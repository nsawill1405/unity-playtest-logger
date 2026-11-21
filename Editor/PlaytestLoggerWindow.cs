using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PlaytestLoggerTool
{
    public class PlaytestLoggerWindow : EditorWindow
    {
        enum Tab
        {
            NewEntry,
            SavedEntries
        }

        enum MarkerEdit
        {
            None,
            Added,
            Moved,
            Removed
        }

        /// <summary>A bool stored in EditorPrefs, read lazily (EditorPrefs can't be used from static initializers).</summary>
        sealed class EditorPrefBool
        {
            readonly string key;
            readonly bool defaultValue;
            bool? cached;

            public EditorPrefBool(string key, bool defaultValue)
            {
                this.key = "PlaytestLogger." + key;
                this.defaultValue = defaultValue;
            }

            public bool Value
            {
                get => cached ??= EditorPrefs.GetBool(key, defaultValue);
                set
                {
                    if (Value == value) return;
                    cached = value;
                    EditorPrefs.SetBool(key, value);
                }
            }
        }

        const string WindowTitle = "Playtest Logger";
        const string QuickScreenshotCategory = PlaytestCategories.Visuals;
        const float ImageSidePadding = 44f;
        const float PinHitRadius = 12f;
        const double BannerSeconds = 2.8;
        static readonly Vector2 MinWindowSize = new Vector2(430f, 500f);
        static readonly string[] CaptureSourceLabels = { "Game View (UI)", "Scene View" };

        static readonly EditorPrefBool AutoSyncActiveView = new EditorPrefBool("AutoSyncActiveView", true);
        static readonly EditorPrefBool AttachScreenshot = new EditorPrefBool("AttachScreenshot", true);
        static readonly EditorPrefBool AttachConsoleLogs = new EditorPrefBool("AttachConsoleLogs", true);
        static readonly EditorPrefBool PauseOnQuickCapture = new EditorPrefBool("PauseOnQuickCapture", false);
        static readonly EditorPrefBool HideResolved = new EditorPrefBool("HideResolved", false);
        static readonly EditorPrefBool ConfirmDelete = new EditorPrefBool("ConfirmDelete", true);

        static string DraftImagePath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "PlaytestLoggerDraft.png"));

        // Serialized so the draft survives domain reloads (e.g. entering Play Mode). The draft image itself
        // is stashed in Library/ by OnDisable rather than serialized into the window layout.
        [SerializeField] Tab tab;
        [SerializeField] CaptureSource captureSource;
        [SerializeField] CaptureSource draftSource;
        [SerializeField] CameraPose draftPose = new CameraPose();
        bool draftNeedsRetake;
        [SerializeField] int newCategoryIndex;
        [SerializeField] bool categoryPickedManually;
        [SerializeField] string newDescription = "";
        [SerializeField] int descriptionGeneration;
        [SerializeField] List<PinMarker> draftMarkers = new List<PinMarker>();
        [SerializeField] string expandedEntryId = "";
        [SerializeField] Vector2 formScroll;
        [SerializeField] Vector2 savedScroll;

        Texture2D draftTex;
        byte[] draftPng;
        LogEntryCollection collection;
        readonly Dictionary<string, Texture2D> entryTexCache = new Dictionary<string, Texture2D>();
        PlaytestLoggerStyles styles;

        CaptureSource? lastFocusedViewSource;
        int draggingMarkerIndex = -1;
        bool dragMoved;
        bool scrollToExpandedEntry;
        string banner;
        double bannerUntil;

        [MenuItem("Window/Playtest Logger %#l", false, 1950)]
        public static void OpenWindowMenu()
        {
            PlaytestLoggerWindow win = ShowWindow();
            if (win.draftTex == null) win.CaptureDraft();
        }

        [MenuItem("Window/Playtest Logger Quick Capture _F1", false, 1951)]
        public static void QuickCaptureAndFocus()
        {
            // Capture before focusing our window: if it is docked over the Game/Scene View, focusing it hides that view.
            PlaytestLoggerWindow existing = FindExistingWindow();
            CaptureSource source = PlaytestCapture.SourceOfWindow(focusedWindow)
                ?? (existing != null ? existing.captureSource : CaptureSource.GameView);

            if (EditorApplication.isPlaying && PauseOnQuickCapture.Value)
            {
                EditorApplication.isPaused = true;
            }
            Texture2D shot = PlaytestCapture.Capture(source, out CaptureSource actual, out CameraPose pose);

            PlaytestLoggerWindow win = ShowWindow();
            win.tab = Tab.NewEntry;
            win.captureSource = source;
            if (win.SetDraft(shot, actual, pose))
            {
                win.ShowBanner("Captured fresh screenshot (" + PlaytestCapture.DisplayName(actual) + ")");
            }
        }

        static PlaytestLoggerWindow FindExistingWindow()
        {
            PlaytestLoggerWindow[] windows = Resources.FindObjectsOfTypeAll<PlaytestLoggerWindow>();
            return windows.Length > 0 ? windows[0] : null;
        }

        static PlaytestLoggerWindow ShowWindow()
        {
            PlaytestLoggerWindow win = GetWindow<PlaytestLoggerWindow>(WindowTitle);
            win.minSize = MinWindowSize;
            win.Focus();
            return win;
        }

        #region Lifecycle

        void OnEnable()
        {
            EditorApplication.update -= OnEditorTick;
            EditorApplication.update += OnEditorTick;
            newCategoryIndex = Mathf.Clamp(newCategoryIndex, 0, PlaytestCategories.Names.Length - 1);
            RestoreDraftImage();
            ReloadEntries();
        }

        void OnFocus()
        {
            ReloadEntries();
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorTick;
            StashDraftImage();
            ClearEntryTextureCache();
            DestroyDraftTexture();
            styles?.Dispose();
            styles = null;
        }

        void OnDestroy()
        {
            // OnDestroy only runs when the window is closed (not on domain reload), so drop the stashed draft.
            if (File.Exists(DraftImagePath)) File.Delete(DraftImagePath);
        }

        void OnEditorTick()
        {
            if (banner != null && EditorApplication.timeSinceStartup >= bannerUntil)
            {
                banner = null;
                Repaint();
            }
            AutoSyncCaptureSource();
        }

        // Follows the user between Scene View and Game View tabs and retakes the draft screenshot.
        void AutoSyncCaptureSource()
        {
            if (!AutoSyncActiveView.Value) return;
            CaptureSource? view = PlaytestCapture.SourceOfWindow(focusedWindow);
            if (view == null || view == lastFocusedViewSource) return;
            lastFocusedViewSource = view;

            // Never swap the screenshot out from under pins the user already placed.
            if (draftMarkers.Count > 0) return;

            captureSource = view.Value;
            if (tab != Tab.NewEntry)
            {
                // Retake when the user comes back to the New Entry tab.
                draftNeedsRetake = true;
                return;
            }
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                CaptureDraft();
                Repaint();
            };
        }

        void ShowBanner(string message)
        {
            banner = message;
            bannerUntil = EditorApplication.timeSinceStartup + BannerSeconds;
            Repaint();
        }

        #endregion

        #region Draft & entries

        void CaptureDraft()
        {
            SetDraft(PlaytestCapture.Capture(captureSource, out CaptureSource actual, out CameraPose pose), actual, pose);
        }

        bool SetDraft(Texture2D capture, CaptureSource source, CameraPose pose)
        {
            if (capture == null)
            {
                ShowBanner("No Game View or Scene View camera to capture");
                return false;
            }
            DestroyDraftTexture();
            draftTex = capture;
            draftPng = capture.EncodeToPNG();
            draftSource = source;
            draftPose = pose;
            draftNeedsRetake = false;
            return true;
        }

        void DestroyDraftTexture()
        {
            if (draftTex != null) DestroyImmediate(draftTex);
            draftTex = null;
            draftPng = null;
        }

        void StashDraftImage()
        {
            if (draftPng == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(DraftImagePath));
            File.WriteAllBytes(DraftImagePath, draftPng);
        }

        void RestoreDraftImage()
        {
            if (draftTex != null || !File.Exists(DraftImagePath)) return;
            byte[] png = File.ReadAllBytes(DraftImagePath);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(png))
            {
                draftTex = tex;
                draftPng = png;
            }
            else
            {
                DestroyImmediate(tex);
            }
        }

        void ReloadEntries()
        {
            collection = PlaytestLogStore.Load();
        }

        void SaveDraftEntry()
        {
            if (AttachScreenshot.Value && draftTex == null) CaptureDraft();
            Texture2D shot = AttachScreenshot.Value ? draftTex : null;
            AddEntry(PlaytestCategories.Names[newCategoryIndex], newDescription, shot, draftPng, draftSource, draftPose, draftMarkers);

            ResetDescriptionField();
            draftMarkers.Clear();
            categoryPickedManually = false;
            ShowBanner("Saved entry to PlaytestLogs/changes.md");
        }

        // IMGUI keeps the text being edited in a shared editor tied to the text area's control ID, and can write it back
        // into the field after we clear it. Dropping focus and giving the next draft's text area a new control ID means
        // the old text can never come back.
        void ResetDescriptionField()
        {
            newDescription = "";
            descriptionGeneration++;
            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;
            EditorGUIUtility.editingTextField = false;
            Repaint();
        }

        // Saves a separate capture without touching the draft the user may be annotating.
        void SaveQuickScreenshot()
        {
            Texture2D shot = PlaytestCapture.Capture(captureSource, out CaptureSource actual, out CameraPose pose);
            if (shot == null)
            {
                ShowBanner("No Game View or Scene View camera to capture");
                return;
            }
            try
            {
                AddEntry(QuickScreenshotCategory, "Quick screenshot capture", shot, null, actual, pose, new List<PinMarker>());
                ShowBanner("Saved quick screenshot");
            }
            finally
            {
                DestroyImmediate(shot);
            }
        }

        void AddEntry(string category, string description, Texture2D shot, byte[] shotPng, CaptureSource source, CameraPose pose, List<PinMarker> markers)
        {
            DateTime now = DateTime.Now;
            LogEntry entry = new LogEntry
            {
                id = PlaytestLogStore.NewId(now),
                timestamp = PlaytestLogStore.FormatTimestamp(now),
                category = category,
                description = string.IsNullOrWhiteSpace(description) ? "(No description)" : description.Trim(),
                scene = SceneManager.GetActiveScene().name,
                contextInfo = PlaytestCapture.DescribeContext(),
                captureSource = source,
                cameraPose = shot != null ? pose : new CameraPose(),
                markers = markers.ConvertAll(m => m.Clone())
            };

            if (shot != null)
            {
                WriteScreenshots(entry, shot, shotPng ?? shot.EncodeToPNG());
            }
            if (AttachConsoleLogs.Value)
            {
                entry.consoleLogFile = PlaytestLogStore.ConsoleLogPath(entry.id);
                PlaytestLogStore.WriteText(entry.consoleLogFile, PlaytestLogStore.BuildConsoleSnapshot(entry));
            }

            ReloadEntries();
            collection.entries.Insert(0, entry);
            PlaytestLogStore.Save(collection);
            expandedEntryId = entry.id;
        }

        static void WriteScreenshots(LogEntry entry, Texture2D clean, byte[] cleanPng)
        {
            entry.cleanScreenshotFile = PlaytestLogStore.CleanScreenshotPath(entry.id);
            entry.screenshotFile = PlaytestLogStore.ScreenshotPath(entry.id);
            PlaytestLogStore.WriteBytes(entry.cleanScreenshotFile, cleanPng);
            PlaytestLogStore.WriteBytes(entry.screenshotFile, PlaytestCapture.BakeMarkers(clean, entry.markers));
        }

        void RebakeEntryScreenshot(LogEntry entry)
        {
            Texture2D clean = GetEntryTexture(entry);
            if (clean == null) return;

            // Entries without a clean capture only have the annotated PNG. Keep a copy of it as the base image,
            // otherwise every rebake would draw new pins on top of the previously baked ones.
            if (!PlaytestLogStore.Exists(entry.cleanScreenshotFile))
            {
                entry.cleanScreenshotFile = PlaytestLogStore.CleanScreenshotPath(entry.id);
                PlaytestLogStore.WriteBytes(entry.cleanScreenshotFile, clean.EncodeToPNG());
            }
            if (string.IsNullOrEmpty(entry.screenshotFile))
            {
                entry.screenshotFile = PlaytestLogStore.ScreenshotPath(entry.id);
            }
            PlaytestLogStore.WriteBytes(entry.screenshotFile, PlaytestCapture.BakeMarkers(clean, entry.markers));
            PlaytestLogStore.Save(collection);
        }

        void RetakeEntryScreenshot(LogEntry entry)
        {
            Texture2D fresh = PlaytestCapture.Capture(captureSource, out CaptureSource actual, out CameraPose pose);
            if (fresh == null)
            {
                ShowBanner("No Game View or Scene View camera to capture");
                return;
            }

            entry.captureSource = actual;
            entry.cameraPose = pose;
            WriteScreenshots(entry, fresh, fresh.EncodeToPNG());
            EvictEntryTexture(entry.id);
            entryTexCache[entry.id] = fresh;
            PlaytestLogStore.Save(collection);
            ShowBanner("Retook screenshot (" + PlaytestCapture.DisplayName(actual) + ")");
        }

        void DeleteEntry(string entryId)
        {
            ReloadEntries();
            LogEntry entry = collection.entries.Find(e => e.id == entryId);
            if (entry == null) return;
            if (ConfirmDelete.Value)
            {
                int choice = EditorUtility.DisplayDialogComplex("Delete log entry?",
                    "This removes the entry from changes.md and entries.json and deletes its screenshots and console log.\n\n" +
                    "You can turn this prompt back on from Actions > Ask Before Deleting Entries.",
                    "Delete", "Cancel", "Delete, Don't Ask Again");
                if (choice == 1) return;
                if (choice == 2) ConfirmDelete.Value = false;
            }

            collection.entries.Remove(entry);
            PlaytestLogStore.Save(collection);
            PlaytestLogStore.DeleteEntryFiles(entry);
            EvictEntryTexture(entryId);
            Repaint();
        }

        Texture2D GetEntryTexture(LogEntry entry)
        {
            if (entryTexCache.TryGetValue(entry.id, out Texture2D cached) && cached != null) return cached;

            string rel = PlaytestLogStore.Exists(entry.cleanScreenshotFile) ? entry.cleanScreenshotFile : entry.screenshotFile;
            if (!PlaytestLogStore.Exists(rel)) return null;

            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(PlaytestLogStore.FullPath(rel))))
            {
                DestroyImmediate(tex);
                return null;
            }
            entryTexCache[entry.id] = tex;
            return tex;
        }

        void EvictEntryTexture(string entryId)
        {
            if (entryTexCache.TryGetValue(entryId, out Texture2D tex) && tex != null) DestroyImmediate(tex);
            entryTexCache.Remove(entryId);
        }

        void ClearEntryTextureCache()
        {
            foreach (Texture2D tex in entryTexCache.Values)
            {
                if (tex != null) DestroyImmediate(tex);
            }
            entryTexCache.Clear();
        }

        static void OpenLogsFolder()
        {
            Directory.CreateDirectory(PlaytestLogStore.RootDir);
            EditorUtility.RevealInFinder(PlaytestLogStore.RootDir);
        }

        void OpenMarkdown()
        {
            if (!File.Exists(PlaytestLogStore.MarkdownPath)) PlaytestLogStore.Save(collection);
            InternalEditorUtility.OpenFileAtLineExternal(PlaytestLogStore.MarkdownPath, 1);
        }

        void OpenEntryFile(string relativePath, bool textFile)
        {
            if (!PlaytestLogStore.Exists(relativePath))
            {
                ShowBanner("File not found: " + relativePath);
                return;
            }
            string full = PlaytestLogStore.FullPath(relativePath);
            if (textFile) InternalEditorUtility.OpenFileAtLineExternal(full, 1);
            else Application.OpenURL(new Uri(full).AbsoluteUri);
        }

        #endregion

        #region GUI

        void OnGUI()
        {
            if (styles == null || !styles.IsValid)
            {
                styles?.Dispose();
                styles = new PlaytestLoggerStyles();
            }
            if (collection == null) ReloadEntries();

            DrawHeader();

            if (banner != null)
            {
                Rect bannerRect = GUILayoutUtility.GetRect(position.width, 24f);
                EditorGUI.DrawRect(bannerRect, styles.BannerColor);
                GUI.Label(new Rect(bannerRect.x + 10f, bannerRect.y + 3f, bannerRect.width - 20f, 18f), "✓  " + banner, EditorStyles.boldLabel);
            }

            if (tab == Tab.NewEntry) DrawNewEntryTab();
            else DrawSavedEntriesTab();
        }

        void DrawHeader()
        {
            int openCount = collection.entries.FindAll(e => !e.resolved).Count;

            EditorGUILayout.BeginVertical(styles.HeaderBar);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ New Entry", tab == Tab.NewEntry ? styles.TabActive : styles.TabInactive))
            {
                tab = Tab.NewEntry;
                bool retake = draftTex == null || (draftNeedsRetake && draftMarkers.Count == 0);
                draftNeedsRetake = false;
                if (retake) CaptureDraft();
            }
            string savedLabel = $"Saved Entries ({openCount} open • {collection.entries.Count} total)";
            if (GUILayout.Button(savedLabel, tab == Tab.SavedEntries ? styles.TabActive : styles.TabInactive))
            {
                tab = Tab.SavedEntries;
                ReloadEntries();
            }
            if (GUILayout.Button("Actions ▾", styles.SubtleButton, GUILayout.Width(74f), GUILayout.Height(25f)))
            {
                ShowActionsMenu();
            }
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(6f);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("View:", EditorStyles.miniBoldLabel, GUILayout.Width(36f));
            CaptureSource nextSource = (CaptureSource)GUILayout.Toolbar((int)captureSource, CaptureSourceLabels, EditorStyles.miniButton);
            if (nextSource != captureSource)
            {
                captureSource = nextSource;
                CaptureDraft();
                ShowBanner("Switched to " + PlaytestCapture.DisplayName(captureSource) + " & retook screenshot");
            }
            AutoSyncActiveView.Value = GUILayout.Toggle(AutoSyncActiveView.Value,
                new GUIContent("Auto", "Automatically retake the screenshot when switching between Scene View and Game View tabs (skipped once the draft has pins)"),
                EditorStyles.miniButton, GUILayout.Width(46f));
            if (GUILayout.Button("Retake (F1)", styles.SubtleButton, GUILayout.Width(82f)))
            {
                if (SetDraft(PlaytestCapture.Capture(captureSource, out CaptureSource actual, out CameraPose pose), actual, pose))
                {
                    ShowBanner("Captured " + PlaytestCapture.DisplayName(actual));
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            DrawOutline(GUILayoutUtility.GetLastRect());
        }

        void ShowActionsMenu()
        {
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("Quick Screenshot Now"), false, SaveQuickScreenshot);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Pause Play Mode on F1 Capture"), PauseOnQuickCapture.Value,
                () => PauseOnQuickCapture.Value = !PauseOnQuickCapture.Value);
            menu.AddItem(new GUIContent("Ask Before Deleting Entries"), ConfirmDelete.Value,
                () => ConfirmDelete.Value = !ConfirmDelete.Value);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Open PlaytestLogs Folder"), false, OpenLogsFolder);
            menu.AddItem(new GUIContent("Open changes.md Markdown"), false, OpenMarkdown);
            menu.ShowAsContext();
        }

        void DrawNewEntryTab()
        {
            Event ev = Event.current;
            if (ev.type == EventType.KeyDown && (ev.command || ev.control) && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter))
            {
                ev.Use();
                SaveDraftEntry();
                return;
            }

            formScroll = EditorGUILayout.BeginScrollView(formScroll);
            GUILayout.Space(4f);
            Rect cardRect = EditorGUILayout.BeginVertical(styles.Card);
            DrawOutline(cardRect);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Category", styles.SectionTitle, GUILayout.Width(64f));
            DrawCategoryBadge(PlaytestCategories.Names[newCategoryIndex], 116f);
            int nextCategory = EditorGUILayout.Popup(newCategoryIndex, PlaytestCategories.Names);
            if (nextCategory != newCategoryIndex)
            {
                newCategoryIndex = nextCategory;
                categoryPickedManually = true;
            }
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(8f);

            if (draftTex == null)
            {
                EditorGUILayout.HelpBox("Press F1 or click 'Retake (F1)' above to capture a screenshot.", MessageType.None);
            }
            else
            {
                MarkerEdit edit = DrawMarkerImage(draftTex, draftMarkers, draftSource, 235f);
                bool pinnedUi = edit == MarkerEdit.Added && draftMarkers[draftMarkers.Count - 1].label.StartsWith("UI:");
                if (pinnedUi && !categoryPickedManually)
                {
                    newCategoryIndex = PlaytestCategories.IndexOf(PlaytestCategories.UI);
                }

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Click image to add pin  •  Drag pin to adjust  •  Right-click pin to delete", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (draftMarkers.Count > 0 && GUILayout.Button("Clear Pins", styles.SubtleButton, GUILayout.Width(72f)))
                {
                    draftMarkers.Clear();
                }
                EditorGUILayout.EndHorizontal();

                DrawMarkerList(draftMarkers);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Description", styles.SectionTitle);
            GUILayout.Space(2f);
            if (descriptionGeneration % 2 == 1) GUIUtility.GetControlID(FocusType.Passive); // shifts the text area's control ID per draft
            newDescription = EditorGUILayout.TextArea(newDescription, styles.TextArea, GUILayout.MinHeight(78f));

            GUILayout.Space(8f);
            EditorGUILayout.BeginHorizontal();
            AttachScreenshot.Value = EditorGUILayout.ToggleLeft("Attach Screenshot", AttachScreenshot.Value, GUILayout.Width(130f));
            AttachConsoleLogs.Value = EditorGUILayout.ToggleLeft("Attach Console Logs", AttachConsoleLogs.Value, GUILayout.Width(145f));
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(8f);
            if (GUILayout.Button("Save Log Entry   (Cmd / Ctrl + Enter)", styles.PrimaryButton, GUILayout.Height(32f)))
            {
                SaveDraftEntry();
            }

            EditorGUILayout.EndVertical();
            GUILayout.Space(6f);
            EditorGUILayout.EndScrollView();
        }

        void DrawSavedEntriesTab()
        {
            List<LogEntry> visible = HideResolved.Value ? collection.entries.FindAll(e => !e.resolved) : collection.entries;

            EditorGUILayout.BeginVertical(styles.Card);
            if (visible.Count > 0) DrawEntryPicker(visible);

            EditorGUILayout.BeginHorizontal();
            HideResolved.Value = EditorGUILayout.ToggleLeft("Hide Done Entries", HideResolved.Value, GUILayout.Width(135f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Collapse All", styles.SubtleButton, GUILayout.Width(84f)))
            {
                expandedEntryId = "";
            }
            if (GUILayout.Button("Open Folder", styles.SubtleButton, GUILayout.Width(86f)))
            {
                OpenLogsFolder();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            if (visible.Count == 0)
            {
                GUILayout.Space(8f);
                EditorGUILayout.HelpBox("No matching saved entries. Switch to '+ New Entry' or press F1 to log an issue.", MessageType.Info);
                return;
            }

            savedScroll = EditorGUILayout.BeginScrollView(savedScroll);
            foreach (LogEntry entry in visible)
            {
                DrawEntryCard(entry);
            }
            GUILayout.Space(6f);
            EditorGUILayout.EndScrollView();
        }

        void DrawEntryPicker(List<LogEntry> visible)
        {
            string[] labels = new string[visible.Count];
            int selected = -1;
            for (int i = 0; i < visible.Count; i++)
            {
                LogEntry e = visible[i];
                // Swap '/' for a look-alike so Unity's popup doesn't split descriptions into submenus.
                string summary = Summarize(e.description, 38).Replace("/", "⁄");
                labels[i] = $"{i + 1}. {(e.resolved ? "[Done] " : "")}[{e.category}] {summary}";
                if (e.id == expandedEntryId) selected = i;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Select Entry:", styles.SectionTitle, GUILayout.Width(82f));
            int next = EditorGUILayout.Popup(selected, labels);
            if (next != selected && next >= 0)
            {
                expandedEntryId = visible[next].id;
                scrollToExpandedEntry = true;
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4f);
        }

        void DrawEntryCard(LogEntry e)
        {
            bool isExpanded = e.id == expandedEntryId;
            Rect cardRect = EditorGUILayout.BeginVertical(styles.Card);
            DrawOutline(cardRect);
            if (isExpanded && scrollToExpandedEntry && Event.current.type == EventType.Repaint)
            {
                scrollToExpandedEntry = false;
                savedScroll.y = cardRect.y;
                Repaint();
            }

            EditorGUILayout.BeginHorizontal();
            bool resolved = EditorGUILayout.Toggle(e.resolved, GUILayout.Width(16f));
            if (resolved != e.resolved)
            {
                e.resolved = resolved;
                PlaytestLogStore.Save(collection);
            }
            DrawCategoryBadge(e.category, 108f);
            string title = (isExpanded ? "▾ " : "▸ ") + Summarize(e.description, 46);
            if (GUILayout.Button(title, styles.RowHeader, GUILayout.ExpandWidth(true), GUILayout.Height(20f)))
            {
                expandedEntryId = isExpanded ? "" : e.id;
                isExpanded = !isExpanded;
            }
            string shortTime = e.timestamp != null && e.timestamp.Length >= 16 ? e.timestamp.Substring(11, 5) : e.timestamp;
            GUILayout.Label(shortTime, EditorStyles.miniLabel, GUILayout.Width(38f));
            EditorGUILayout.EndHorizontal();

            if (isExpanded) DrawEntryDetails(e);

            EditorGUILayout.EndVertical();
        }

        void DrawEntryDetails(LogEntry e)
        {
            GUILayout.Space(8f);
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(10f, 1f, GUILayout.ExpandWidth(true)), styles.BorderColor);
            GUILayout.Space(6f);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Category:", EditorStyles.miniBoldLabel, GUILayout.Width(60f));
            int category = PlaytestCategories.IndexOf(e.category);
            int nextCategory = EditorGUILayout.Popup(category, PlaytestCategories.Names, GUILayout.Width(145f));
            if (nextCategory != category)
            {
                e.category = PlaytestCategories.Names[nextCategory];
                PlaytestLogStore.Save(collection);
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{e.timestamp}  •  {e.scene}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(e.contextInfo))
            {
                EditorGUILayout.LabelField(e.contextInfo, EditorStyles.miniLabel);
            }

            GUILayout.Space(4f);
            GUILayout.Label("Description", EditorStyles.miniBoldLabel);
            string description = EditorGUILayout.TextArea(e.description ?? "", styles.TextArea, GUILayout.MinHeight(56f));
            if (description != e.description)
            {
                e.description = description;
                PlaytestLogStore.Save(collection);
            }

            Texture2D tex = GetEntryTexture(e);
            if (tex != null)
            {
                GUILayout.Space(6f);
                bool pinsChanged = DrawMarkerImage(tex, e.markers, e.captureSource, 215f) != MarkerEdit.None;
                EditorGUILayout.LabelField("Click to add pin  •  Drag pin to adjust  •  Right-click pin to remove", EditorStyles.miniLabel);
                MarkerEdit listEdit = DrawMarkerList(e.markers);

                // Labels aren't drawn on the PNG, so label edits only need the JSON/Markdown updated.
                if (pinsChanged || listEdit == MarkerEdit.Removed) RebakeEntryScreenshot(e);
                else if (listEdit != MarkerEdit.None) PlaytestLogStore.Save(collection);
            }

            GUILayout.Space(8f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Retake Screenshot", styles.SubtleButton, GUILayout.Width(122f)))
            {
                RetakeEntryScreenshot(e);
            }
            if (e.cameraPose != null && e.cameraPose.valid
                && GUILayout.Button(new GUIContent("Go to Camera", "Move the Scene View camera to where this screenshot was taken"), styles.SubtleButton, GUILayout.Width(96f)))
            {
                PlaytestCapture.MoveSceneViewTo(e.cameraPose);
            }
            if (!string.IsNullOrEmpty(e.screenshotFile) && GUILayout.Button("Open PNG", styles.SubtleButton, GUILayout.Width(76f)))
            {
                OpenEntryFile(e.screenshotFile, false);
            }
            if (!string.IsNullOrEmpty(e.consoleLogFile) && GUILayout.Button("Console Log", styles.SubtleButton, GUILayout.Width(86f)))
            {
                OpenEntryFile(e.consoleLogFile, true);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Delete", styles.SubtleButton, GUILayout.Width(60f)))
            {
                // Deferred: removing entries (and showing a modal) mid-layout breaks IMGUI's layout pass.
                string id = e.id;
                EditorApplication.delayCall += () =>
                {
                    if (this != null) DeleteEntry(id);
                };
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Draws a capture with its pins and handles add/drag/remove. Returns the committed edit, if any.</summary>
        MarkerEdit DrawMarkerImage(Texture2D tex, List<PinMarker> markers, CaptureSource source, float maxHeight)
        {
            float availableWidth = Mathf.Max(200f, position.width - ImageSidePadding);
            float aspect = (float)tex.width / Mathf.Max(1, tex.height);
            float drawH = Mathf.Min(maxHeight, availableWidth / aspect);
            float drawW = drawH * aspect;

            Rect rowRect = GUILayoutUtility.GetRect(availableWidth, drawH + 8f);
            EditorGUI.DrawRect(rowRect, styles.ImageBackground);
            DrawOutline(rowRect);
            Rect imgRect = new Rect(rowRect.x + (rowRect.width - drawW) * 0.5f, rowRect.y + 4f, drawW, drawH);
            GUI.DrawTexture(imgRect, tex, ScaleMode.StretchToFill);
            EditorGUIUtility.AddCursorRect(imgRect, MouseCursor.ArrowPlus);

            MarkerEdit edit = HandleMarkerInput(imgRect, new Vector2(tex.width, tex.height), markers, source);

            for (int i = 0; i < markers.Count; i++)
            {
                Vector2 pos = NormalizedToGui(imgRect, markers[i].normalizedPos);
                Rect outer = new Rect(pos.x - 10f, pos.y - 10f, 20f, 20f);
                EditorGUI.DrawRect(new Rect(pos.x - 9f, pos.y - 8f, 20f, 20f), styles.PinShadow);
                EditorGUI.DrawRect(outer, styles.PinOutline);
                EditorGUI.DrawRect(new Rect(pos.x - 8f, pos.y - 8f, 16f, 16f), styles.PinFill);
                GUI.Label(outer, (i + 1).ToString(), styles.PinBadge);
                EditorGUIUtility.AddCursorRect(outer, MouseCursor.MoveArrow);
            }
            return edit;
        }

        MarkerEdit HandleMarkerInput(Rect imgRect, Vector2 imageSize, List<PinMarker> markers, CaptureSource source)
        {
            MarkerEdit edit = ProcessMarkerEvent(imgRect, imageSize, markers, source);
            if (Event.current.type == EventType.Used) Repaint();
            return edit;
        }

        MarkerEdit ProcessMarkerEvent(Rect imgRect, Vector2 imageSize, List<PinMarker> markers, CaptureSource source)
        {
            // hotControl keeps drag/up events coming to us even when the mouse leaves the image or window.
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            Event ev = Event.current;

            switch (ev.GetTypeForControl(controlId))
            {
                case EventType.MouseDown when imgRect.Contains(ev.mousePosition):
                {
                    int hit = HitTestMarker(imgRect, markers, ev.mousePosition);
                    if (ev.button == 1 && hit >= 0)
                    {
                        markers.RemoveAt(hit);
                        ev.Use();
                        return MarkerEdit.Removed;
                    }
                    if (ev.button != 0) return MarkerEdit.None;

                    MarkerEdit edit = MarkerEdit.None;
                    if (hit < 0)
                    {
                        Vector2 norm = GuiToNormalized(imgRect, ev.mousePosition);
                        markers.Add(new PinMarker { normalizedPos = norm, label = PlaytestCapture.DescribePoint(norm, imageSize, source) });
                        hit = markers.Count - 1;
                        edit = MarkerEdit.Added;
                    }
                    GUIUtility.hotControl = controlId;
                    draggingMarkerIndex = hit;
                    dragMoved = false;
                    ev.Use();
                    return edit;
                }
                case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                    if (draggingMarkerIndex >= 0 && draggingMarkerIndex < markers.Count)
                    {
                        markers[draggingMarkerIndex].normalizedPos = GuiToNormalized(imgRect, ev.mousePosition);
                        dragMoved = true;
                    }
                    ev.Use();
                    return MarkerEdit.None;
                case EventType.MouseUp when GUIUtility.hotControl == controlId:
                {
                    GUIUtility.hotControl = 0;
                    ev.Use();
                    bool moved = dragMoved && draggingMarkerIndex >= 0 && draggingMarkerIndex < markers.Count;
                    if (moved)
                    {
                        // Re-detect only once the pin is dropped; detection walks every canvas in the scene.
                        PinMarker marker = markers[draggingMarkerIndex];
                        marker.label = PlaytestCapture.DescribePoint(marker.normalizedPos, imageSize, source);
                    }
                    draggingMarkerIndex = -1;
                    dragMoved = false;
                    return moved ? MarkerEdit.Moved : MarkerEdit.None;
                }
                default:
                    return MarkerEdit.None;
            }
        }

        static int HitTestMarker(Rect imgRect, List<PinMarker> markers, Vector2 mousePos)
        {
            int hit = -1;
            float best = PinHitRadius;
            for (int i = 0; i < markers.Count; i++)
            {
                float d = Vector2.Distance(mousePos, NormalizedToGui(imgRect, markers[i].normalizedPos));
                if (d <= best)
                {
                    best = d;
                    hit = i;
                }
            }
            return hit;
        }

        static Vector2 NormalizedToGui(Rect imgRect, Vector2 normalized)
        {
            return new Vector2(imgRect.x + normalized.x * imgRect.width, imgRect.y + (1f - normalized.y) * imgRect.height);
        }

        static Vector2 GuiToNormalized(Rect imgRect, Vector2 guiPos)
        {
            return new Vector2(
                Mathf.Clamp01((guiPos.x - imgRect.x) / Mathf.Max(1f, imgRect.width)),
                Mathf.Clamp01(1f - (guiPos.y - imgRect.y) / Mathf.Max(1f, imgRect.height)));
        }

        /// <summary>Editable label list for pins. Returns Removed if a pin was deleted, Moved (label edit) otherwise.</summary>
        MarkerEdit DrawMarkerList(List<PinMarker> markers)
        {
            if (markers.Count == 0) return MarkerEdit.None;

            MarkerEdit edit = MarkerEdit.None;
            int removeIndex = -1;
            GUILayout.Space(3f);
            for (int i = 0; i < markers.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                Rect badge = GUILayoutUtility.GetRect(22f, 18f, GUILayout.Width(22f));
                badge.y += 1f;
                EditorGUI.DrawRect(badge, styles.PinFill);
                GUI.Label(badge, (i + 1).ToString(), styles.PinBadge);

                string label = EditorGUILayout.TextField(markers[i].label ?? "");
                if (label != markers[i].label)
                {
                    markers[i].label = label;
                    edit = MarkerEdit.Moved;
                }
                if (GUILayout.Button("×", styles.SubtleButton, GUILayout.Width(24f)))
                {
                    removeIndex = i;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (removeIndex < 0) return edit;
            markers.RemoveAt(removeIndex);
            return MarkerEdit.Removed;
        }

        void DrawCategoryBadge(string category, float width)
        {
            Rect r = GUILayoutUtility.GetRect(width, 18f, GUILayout.Width(width));
            r.y += 1f;
            EditorGUI.DrawRect(r, PlaytestCategories.ColorOf(category));
            GUI.Label(r, category, styles.CategoryPill);
        }

        void DrawOutline(Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color c = styles.BorderColor;
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1f, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
        }

        static string Summarize(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(No description)";
            string singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length <= maxLength ? singleLine : singleLine.Substring(0, maxLength - 1) + "…";
        }

        #endregion
    }
}
