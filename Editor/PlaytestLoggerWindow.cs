using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlaytestLoggerTool
{
    [InitializeOnLoad]
    public class PlaytestLoggerWindow : EditorWindow
    {
        [Serializable]
        public class PinMarker
        {
            public Vector2 normalizedPos; // (0..1, 0..1) from bottom-left
            public string label;
        }

        [Serializable]
        public class LogEntry
        {
            public string id;
            public string timestamp;
            public string category;
            public string description;
            public string scene;
            public string contextInfo;
            public string targetElement; // legacy single-pin field preserved for compatibility
            public string cleanScreenshotFile;
            public string screenshotFile;
            public string consoleLogFile;
            public bool resolved;
            public List<PinMarker> markers = new List<PinMarker>();
        }

        [Serializable]
        public class LogEntryCollection
        {
            public List<LogEntry> entries = new List<LogEntry>();
        }

        static readonly string[] Categories =
        {
            "UI / HUD",
            "Todo / Change",
            "Bug",
            "Physics / Controls",
            "Camera",
            "Visuals / Shader",
            "Map / Level"
        };

        static readonly string[] ActionOptions =
        {
            "New Log Entry",
            "Saved Entries",
            "Quick Screenshot",
            "Open PlaytestLogs Folder",
            "Open changes.md"
        };

        static readonly string[] CaptureSources =
        {
            "Game View (includes UI)",
            "Scene View"
        };

        const int MaxConsoleBuffer = 80;
        static readonly List<string> consoleBuffer = new List<string>(MaxConsoleBuffer);

        public static string LogsRootDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "PlaytestLogs"));
        public static string ScreenshotsDir => Path.Combine(LogsRootDir, "Screenshots");
        public static string ConsoleLogsDir => Path.Combine(LogsRootDir, "ConsoleLogs");
        public static string JsonFilePath => Path.Combine(LogsRootDir, "entries.json");
        public static string MarkdownFilePath => Path.Combine(LogsRootDir, "changes.md");

        int actionIndex;
        int captureSourceIndex;
        int newCategoryIndex;
        bool includeScreenshot = true;
        bool includeLogs = true;
        bool pauseOnQuickCapture;
        bool hideResolved;
        string newDescription = "";
        string statusBanner = "";
        double statusBannerUntil;

        Texture2D draftCleanTex;
        byte[] draftCleanPng;
        readonly List<PinMarker> draftMarkers = new List<PinMarker>();

        LogEntryCollection collection;
        Vector2 formScroll;
        Vector2 savedScroll;
        readonly Dictionary<string, Texture2D> cleanTexCache = new Dictionary<string, Texture2D>();

        // Interactive marker drag state (shared via unique owner key: "draft" or entry.id)
        string draggingOwnerKey;
        int draggingMarkerIndex = -1;
        bool dragModifiedSavedEntry;

        GUIStyle pinBadgeStyle;

        static PlaytestLoggerWindow()
        {
            Application.logMessageReceived -= OnUnityLog;
            Application.logMessageReceived += OnUnityLog;
        }

        [MenuItem("Window/Playtest Logger %#l", false, 1950)]
        public static void OpenWindowMenu()
        {
            PlaytestLoggerWindow win = GetWindow<PlaytestLoggerWindow>("Playtest Logger");
            win.minSize = new Vector2(440f, 480f);
            win.RefreshEntries();
            if (win.draftCleanTex == null)
            {
                win.CaptureDraftPreview();
            }
            win.Focus();
        }

        [MenuItem("Window/Playtest Logger Quick Capture _F1", false, 1951)]
        public static void QuickCaptureAndFocus()
        {
            PlaytestLoggerWindow win = GetWindow<PlaytestLoggerWindow>("Playtest Logger");
            win.minSize = new Vector2(440f, 480f);
            if (EditorApplication.isPlaying && win.pauseOnQuickCapture)
            {
                EditorApplication.isPaused = true;
            }
            win.actionIndex = 0;
            win.CaptureDraftPreview();
            win.RefreshEntries();
            win.ShowBanner("Captured fresh screenshot");
            win.Focus();
            win.Repaint();
        }

        static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string line = $"[{time}] [{type}] {condition}";
            if ((type == LogType.Error || type == LogType.Exception) && !string.IsNullOrEmpty(stackTrace))
            {
                line += "\n" + stackTrace.TrimEnd();
            }

            lock (consoleBuffer)
            {
                if (consoleBuffer.Count >= MaxConsoleBuffer)
                {
                    consoleBuffer.RemoveAt(0);
                }
                consoleBuffer.Add(line);
            }
        }

        void OnEnable()
        {
            RefreshEntries();
        }

        void OnFocus()
        {
            RefreshEntries();
        }

        void OnDisable()
        {
            ClearTextureCaches();
            if (draftCleanTex != null)
            {
                DestroyImmediate(draftCleanTex);
                draftCleanTex = null;
            }
        }

        void ClearTextureCaches()
        {
            foreach (Texture2D tex in cleanTexCache.Values)
            {
                if (tex != null) DestroyImmediate(tex);
            }
            cleanTexCache.Clear();
        }

        void ShowBanner(string msg)
        {
            statusBanner = msg;
            statusBannerUntil = EditorApplication.timeSinceStartup + 3.0;
            Repaint();
        }

        void EnsureStyles()
        {
            if (pinBadgeStyle != null) return;
            pinBadgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
        }

        static Texture2D CaptureGameViewWithUI()
        {
            try
            {
                Type gvType = Type.GetType("UnityEditor.GameView,UnityEditor");
                if (gvType != null)
                {
                    UnityEngine.Object[] views = Resources.FindObjectsOfTypeAll(gvType);
                    if (views != null && views.Length > 0)
                    {
                        EditorWindow gv = views[0] as EditorWindow;
                        if (gv != null)
                        {
                            gv.Repaint();
                        }

                        FieldInfo rtField = gvType.GetField("m_TargetTexture", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? gvType.BaseType?.GetField("m_TargetTexture", BindingFlags.Instance | BindingFlags.NonPublic);
                        PropertyInfo rtProp = gvType.GetProperty("targetTexture", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                        RenderTexture targetRt = (rtField != null ? rtField.GetValue(views[0]) as RenderTexture : null)
                            ?? (rtProp != null ? rtProp.GetValue(views[0], null) as RenderTexture : null);

                        if (targetRt != null && targetRt.width > 0 && targetRt.height > 0)
                        {
                            RenderTexture temp = RenderTexture.GetTemporary(targetRt.width, targetRt.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                            Graphics.Blit(targetRt, temp);
                            RenderTexture prev = RenderTexture.active;
                            RenderTexture.active = temp;
                            Texture2D tex = new Texture2D(temp.width, temp.height, TextureFormat.RGB24, false);
                            tex.ReadPixels(new Rect(0, 0, temp.width, temp.height), 0, 0);
                            tex.Apply();
                            RenderTexture.active = prev;
                            RenderTexture.ReleaseTemporary(temp);
                            return tex;
                        }
                    }
                }
            }
            catch
            {
            }

            if (Application.isPlaying)
            {
                Texture2D screenTex = ScreenCapture.CaptureScreenshotAsTexture();
                if (screenTex != null) return screenTex;
            }
            return null;
        }

        static Texture2D CaptureCameraDirect(Camera cam, int w = 1280, int h = 720)
        {
            if (cam == null) return null;
            RenderTexture rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            DestroyImmediate(rt);
            return tex;
        }

        static Texture2D CaptureSelectedSource(int sourceIdx)
        {
            Texture2D captured = null;
            if (sourceIdx == 0)
            {
                captured = CaptureGameViewWithUI();
                if (captured == null)
                {
                    captured = CaptureCameraDirect(Camera.main);
                }
            }
            else if (SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView.camera != null)
            {
                captured = CaptureCameraDirect(SceneView.lastActiveSceneView.camera);
            }

            if (captured == null && SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView.camera != null)
            {
                captured = CaptureCameraDirect(SceneView.lastActiveSceneView.camera);
            }
            return captured;
        }

        void CaptureDraftPreview()
        {
            Texture2D captured = CaptureSelectedSource(captureSourceIndex);
            if (captured != null)
            {
                if (draftCleanTex != null) DestroyImmediate(draftCleanTex);
                draftCleanTex = captured;
                draftCleanPng = draftCleanTex.EncodeToPNG();
            }
        }

        static string GetTransformPath(Transform t)
        {
            if (t == null) return "";
            string path = t.name;
            Transform cur = t.parent;
            int depth = 0;
            while (cur != null && depth < 5)
            {
                path = cur.name + "/" + path;
                cur = cur.parent;
                depth++;
            }
            return path;
        }

        static string DetectTargetAtNormalizedPoint(Vector2 normBottomLeft, Vector2 referencePixelSize, bool isSceneViewSource)
        {
            if (!isSceneViewSource)
            {
                Vector2 screenPt = new Vector2(normBottomLeft.x * referencePixelSize.x, normBottomLeft.y * referencePixelSize.y);
#if UNITY_6000_0_OR_NEWER
                Canvas[] canvases = FindObjectsByType<Canvas>();
#else
                Canvas[] canvases = FindObjectsOfType<Canvas>();
#endif
                RectTransform bestUi = null;
                float smallestArea = float.MaxValue;

                for (int c = 0; c < canvases.Length; c++)
                {
                    Canvas canvas = canvases[c];
                    if (canvas == null || !canvas.isActiveAndEnabled) continue;

                    Camera uiCam = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                        ? null
                        : (canvas.worldCamera != null ? canvas.worldCamera : Camera.main);

                    RectTransform[] rects = canvas.GetComponentsInChildren<RectTransform>(false);
                    for (int r = 0; r < rects.Length; r++)
                    {
                        RectTransform rt = rects[r];
                        if (rt == null || rt.transform == canvas.transform) continue;
                        CanvasRenderer cr = rt.GetComponent<CanvasRenderer>();
                        if (cr == null && rt.childCount > 0) continue;

                        if (RectTransformUtility.RectangleContainsScreenPoint(rt, screenPt, uiCam))
                        {
                            Vector2 size = Vector2.Scale(rt.rect.size, rt.lossyScale);
                            float area = Mathf.Abs(size.x * size.y);
                            if (area > 1f && area <= smallestArea)
                            {
                                smallestArea = area;
                                bestUi = rt;
                            }
                        }
                    }
                }

                if (bestUi != null)
                {
                    return $"UI: {GetTransformPath(bestUi)} ({normBottomLeft.x * 100f:F0}%, {normBottomLeft.y * 100f:F0}%)";
                }
            }

            Camera rayCam = isSceneViewSource && SceneView.lastActiveSceneView != null
                ? SceneView.lastActiveSceneView.camera
                : Camera.main;
            if (rayCam != null)
            {
                Ray ray = rayCam.ViewportPointToRay(new Vector3(normBottomLeft.x, normBottomLeft.y, 0f));
                if (Physics.Raycast(ray, out RaycastHit hit, 800f))
                {
                    return $"Object: {GetTransformPath(hit.transform)} at {hit.point:F1}";
                }
            }

            return $"Spot ({normBottomLeft.x * 100f:F0}%, {normBottomLeft.y * 100f:F0}%)";
        }

        static byte[] BakeMarkersOntoPng(Texture2D cleanTex, byte[] cleanPngFallback, List<PinMarker> markers)
        {
            if (cleanTex == null) return cleanPngFallback;
            if (markers == null || markers.Count == 0)
            {
                return cleanPngFallback ?? cleanTex.EncodeToPNG();
            }

            Texture2D copy = new Texture2D(cleanTex.width, cleanTex.height, TextureFormat.RGB24, false);
            copy.SetPixels32(cleanTex.GetPixels32());

            int outerR = Mathf.Max(14, Mathf.RoundToInt(Mathf.Min(copy.width, copy.height) * 0.025f));
            int innerR = Mathf.Max(10, outerR - 4);
            Color32 ringColor = new Color32(255, 55, 55, 255);
            Color32 outlineColor = new Color32(255, 240, 80, 255);

            for (int m = 0; m < markers.Count; m++)
            {
                Vector2 p = markers[m].normalizedPos;
                int cx = Mathf.Clamp(Mathf.RoundToInt(p.x * (copy.width - 1)), 0, copy.width - 1);
                int cy = Mathf.Clamp(Mathf.RoundToInt(p.y * (copy.height - 1)), 0, copy.height - 1);

                for (int dy = -outerR - 2; dy <= outerR + 2; dy++)
                {
                    int py = cy + dy;
                    if (py < 0 || py >= copy.height) continue;
                    for (int dx = -outerR - 2; dx <= outerR + 2; dx++)
                    {
                        int px = cx + dx;
                        if (px < 0 || px >= copy.width) continue;
                        int distSq = dx * dx + dy * dy;
                        if (distSq >= innerR * innerR && distSq <= outerR * outerR)
                        {
                            copy.SetPixel(px, py, ringColor);
                        }
                        else if (distSq > outerR * outerR && distSq <= (outerR + 2) * (outerR + 2))
                        {
                            copy.SetPixel(px, py, outlineColor);
                        }
                        else if (distSq <= 9)
                        {
                            copy.SetPixel(px, py, ringColor);
                        }
                    }
                }
            }

            copy.Apply();
            byte[] bytes = copy.EncodeToPNG();
            DestroyImmediate(copy);
            return bytes;
        }

        static string CaptureSceneContextString()
        {
            string mode = EditorApplication.isPlaying ? "PlayMode" : "EditMode";
            GameObject player = null;
            try
            {
                player = GameObject.FindWithTag("Player");
            }
            catch
            {
            }

            if (player != null)
            {
                Vector3 p = player.transform.position;
                Rigidbody rb = player.GetComponent<Rigidbody>();
                string velInfo = rb != null
                    ? $" | Speed={rb.linearVelocity.magnitude:F1}"
                    : "";
                return $"{mode} | PlayerPos=({p.x:F1}, {p.y:F1}, {p.z:F1}){velInfo}";
            }

            if (Camera.main != null)
            {
                Vector3 cp = Camera.main.transform.position;
                return $"{mode} | CamPos=({cp.x:F1}, {cp.y:F1}, {cp.z:F1})";
            }

            return mode;
        }

        static string BuildConsoleSnapshot(string sceneName, string contextInfo, List<PinMarker> markers)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Playtest Console Snapshot ===");
            sb.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Scene: " + sceneName);
            sb.AppendLine("Context: " + contextInfo);
            if (markers != null && markers.Count > 0)
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    sb.AppendLine($"Marker #{i + 1}: {markers[i].label}");
                }
            }
            sb.AppendLine("---------------------------------");
            lock (consoleBuffer)
            {
                for (int i = 0; i < consoleBuffer.Count; i++)
                {
                    sb.AppendLine(consoleBuffer[i]);
                }
            }
            return sb.ToString();
        }

        void SaveDraftEntry(bool quickScreenshotOnly)
        {
            Directory.CreateDirectory(LogsRootDir);
            Directory.CreateDirectory(ScreenshotsDir);
            Directory.CreateDirectory(ConsoleLogsDir);

            if (draftCleanTex == null && (includeScreenshot || quickScreenshotOnly))
            {
                CaptureDraftPreview();
            }

            string id = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string sceneName = SceneManager.GetActiveScene().name;
            string contextInfo = CaptureSceneContextString();
            string cleanRel = "";
            string finalRel = "";
            string consoleRel = "";

            List<PinMarker> markersCopy = new List<PinMarker>();
            if (!quickScreenshotOnly)
            {
                for (int i = 0; i < draftMarkers.Count; i++)
                {
                    markersCopy.Add(new PinMarker
                    {
                        normalizedPos = draftMarkers[i].normalizedPos,
                        label = draftMarkers[i].label
                    });
                }
            }

            if ((includeScreenshot || quickScreenshotOnly) && draftCleanTex != null)
            {
                cleanRel = "Screenshots/" + id + "_clean.png";
                finalRel = "Screenshots/" + id + ".png";
                byte[] rawBytes = draftCleanPng ?? draftCleanTex.EncodeToPNG();
                File.WriteAllBytes(Path.Combine(LogsRootDir, cleanRel), rawBytes);
                byte[] bakedBytes = BakeMarkersOntoPng(draftCleanTex, rawBytes, markersCopy);
                File.WriteAllBytes(Path.Combine(LogsRootDir, finalRel), bakedBytes);
            }

            if (includeLogs)
            {
                consoleRel = "ConsoleLogs/" + id + ".log";
                File.WriteAllText(Path.Combine(LogsRootDir, consoleRel), BuildConsoleSnapshot(sceneName, contextInfo, markersCopy));
            }

            LogEntry entry = new LogEntry
            {
                id = id,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                category = quickScreenshotOnly ? "Screenshot" : Categories[newCategoryIndex],
                description = quickScreenshotOnly
                    ? (string.IsNullOrWhiteSpace(newDescription) ? "Quick screenshot capture" : newDescription.Trim())
                    : (string.IsNullOrWhiteSpace(newDescription) ? "(No description)" : newDescription.Trim()),
                scene = sceneName,
                contextInfo = contextInfo,
                cleanScreenshotFile = cleanRel,
                screenshotFile = finalRel,
                consoleLogFile = consoleRel,
                resolved = false,
                markers = markersCopy
            };

            RefreshEntries();
            collection.entries.Insert(0, entry);
            PersistCollection(collection);

            if (!quickScreenshotOnly)
            {
                newDescription = "";
                draftMarkers.Clear();
            }
            ShowBanner("Saved -> PlaytestLogs/" + (string.IsNullOrEmpty(finalRel) ? "changes.md" : finalRel));
        }

        void RebakeSavedEntryScreenshot(LogEntry entry)
        {
            Texture2D cleanTex = GetCleanTextureForEntry(entry);
            if (cleanTex == null) return;

            Directory.CreateDirectory(ScreenshotsDir);
            if (string.IsNullOrEmpty(entry.screenshotFile))
            {
                entry.screenshotFile = "Screenshots/" + entry.id + ".png";
            }
            byte[] baked = BakeMarkersOntoPng(cleanTex, null, entry.markers);
            File.WriteAllBytes(Path.Combine(LogsRootDir, entry.screenshotFile), baked);
            PersistCollection(collection);
        }

        void RetakeSavedEntryScreenshot(LogEntry entry)
        {
            Texture2D fresh = CaptureSelectedSource(captureSourceIndex);
            if (fresh == null)
            {
                ShowBanner("No active Game View or Scene View camera found");
                return;
            }

            Directory.CreateDirectory(ScreenshotsDir);
            if (string.IsNullOrEmpty(entry.id))
            {
                entry.id = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            }

            entry.cleanScreenshotFile = "Screenshots/" + entry.id + "_clean.png";
            entry.screenshotFile = "Screenshots/" + entry.id + ".png";

            byte[] cleanBytes = fresh.EncodeToPNG();
            File.WriteAllBytes(Path.Combine(LogsRootDir, entry.cleanScreenshotFile), cleanBytes);

            if (cleanTexCache.TryGetValue(entry.id, out Texture2D oldTex) && oldTex != null)
            {
                DestroyImmediate(oldTex);
            }
            cleanTexCache[entry.id] = fresh;

            byte[] baked = BakeMarkersOntoPng(fresh, cleanBytes, entry.markers);
            File.WriteAllBytes(Path.Combine(LogsRootDir, entry.screenshotFile), baked);

            entry.contextInfo = CaptureSceneContextString();
            PersistCollection(collection);
            ShowBanner("Updated screenshot for entry " + entry.timestamp);
        }

        Texture2D GetCleanTextureForEntry(LogEntry entry)
        {
            if (entry == null) return null;
            string cacheKey = !string.IsNullOrEmpty(entry.id) ? entry.id : entry.screenshotFile;
            if (!string.IsNullOrEmpty(cacheKey) && cleanTexCache.TryGetValue(cacheKey, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            string rel = !string.IsNullOrEmpty(entry.cleanScreenshotFile) ? entry.cleanScreenshotFile : entry.screenshotFile;
            if (string.IsNullOrEmpty(rel)) return null;

            string full = Path.Combine(LogsRootDir, rel);
            if (!File.Exists(full) && !string.IsNullOrEmpty(entry.screenshotFile))
            {
                full = Path.Combine(LogsRootDir, entry.screenshotFile);
            }
            if (!File.Exists(full)) return null;

            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(File.ReadAllBytes(full));
            if (!string.IsNullOrEmpty(cacheKey))
            {
                cleanTexCache[cacheKey] = tex;
            }
            return tex;
        }

        void RefreshEntries()
        {
            if (!File.Exists(JsonFilePath))
            {
                collection = new LogEntryCollection();
                return;
            }
            try
            {
                collection = JsonUtility.FromJson<LogEntryCollection>(File.ReadAllText(JsonFilePath)) ?? new LogEntryCollection();
                for (int i = 0; i < collection.entries.Count; i++)
                {
                    LogEntry e = collection.entries[i];
                    if (e.markers == null) e.markers = new List<PinMarker>();
                    if (e.markers.Count == 0 && !string.IsNullOrEmpty(e.targetElement))
                    {
                        e.markers.Add(new PinMarker { normalizedPos = new Vector2(0.5f, 0.5f), label = e.targetElement });
                        e.targetElement = "";
                    }
                }
            }
            catch
            {
                collection = new LogEntryCollection();
            }
        }

        static void PersistCollection(LogEntryCollection data)
        {
            Directory.CreateDirectory(LogsRootDir);
            File.WriteAllText(JsonFilePath, JsonUtility.ToJson(data, true));

            StringBuilder md = new StringBuilder();
            md.AppendLine("# Playtest Change Log");
            md.AppendLine();
            for (int i = 0; i < data.entries.Count; i++)
            {
                LogEntry e = data.entries[i];
                string check = e.resolved ? "x" : " ";
                md.AppendLine($"- [{check}] **[{e.category}]** {e.description}");
                md.AppendLine($"  - Time: `{e.timestamp}` | Scene: `{e.scene}` | `{e.contextInfo}`");
                if (e.markers != null && e.markers.Count > 0)
                {
                    for (int m = 0; m < e.markers.Count; m++)
                    {
                        md.AppendLine($"  - Marker #{m + 1}: `{e.markers[m].label}`");
                    }
                }
                if (!string.IsNullOrEmpty(e.screenshotFile))
                {
                    md.AppendLine($"  - Screenshot: `{e.screenshotFile}`");
                }
                if (!string.IsNullOrEmpty(e.consoleLogFile))
                {
                    md.AppendLine($"  - Console Log: `{e.consoleLogFile}`");
                }
                md.AppendLine();
            }
            File.WriteAllText(MarkdownFilePath, md.ToString());
        }

        bool DrawInteractiveMarkerImage(
            string ownerKey,
            Texture2D cleanTex,
            List<PinMarker> markers,
            float maxHeight,
            out bool markersChanged,
            out bool dragFinished)
        {
            markersChanged = false;
            dragFinished = false;
            if (cleanTex == null) return false;

            EnsureStyles();

            float availW = Mathf.Max(220f, EditorGUIUtility.currentViewWidth - 38f);
            float aspect = (float)cleanTex.width / Mathf.Max(1, cleanTex.height);
            float drawH = Mathf.Min(maxHeight, availW / aspect);
            float drawW = drawH * aspect;

            Rect rowRect = GUILayoutUtility.GetRect(availW, drawH);
            Rect imgRect = new Rect(rowRect.x + (rowRect.width - drawW) * 0.5f, rowRect.y, drawW, drawH);

            GUI.DrawTexture(imgRect, cleanTex, ScaleMode.StretchToFill);
            EditorGUIUtility.AddCursorRect(imgRect, MouseCursor.ArrowPlus);

            Event ev = Event.current;
            const float hitRadius = 11f;

            // Handle mouse input for adding, dragging, or right-click deleting markers
            if (ev.type == EventType.MouseDown && imgRect.Contains(ev.mousePosition))
            {
                int hitIdx = -1;
                float bestDist = hitRadius;
                for (int i = 0; i < markers.Count; i++)
                {
                    Vector2 mp = MarkerGuiPos(imgRect, markers[i].normalizedPos);
                    float d = Vector2.Distance(ev.mousePosition, mp);
                    if (d <= bestDist)
                    {
                        bestDist = d;
                        hitIdx = i;
                    }
                }

                if (ev.button == 1)
                {
                    if (hitIdx >= 0)
                    {
                        markers.RemoveAt(hitIdx);
                        markersChanged = true;
                        dragFinished = true;
                        ev.Use();
                        Repaint();
                    }
                }
                else if (ev.button == 0)
                {
                    if (hitIdx >= 0)
                    {
                        draggingOwnerKey = ownerKey;
                        draggingMarkerIndex = hitIdx;
                        dragModifiedSavedEntry = false;
                        ev.Use();
                    }
                    else
                    {
                        Vector2 norm = GuiToNormalized(imgRect, ev.mousePosition);
                        string detected = DetectTargetAtNormalizedPoint(norm, new Vector2(cleanTex.width, cleanTex.height), captureSourceIndex == 1);
                        markers.Add(new PinMarker { normalizedPos = norm, label = detected });
                        draggingOwnerKey = ownerKey;
                        draggingMarkerIndex = markers.Count - 1;
                        dragModifiedSavedEntry = true;
                        markersChanged = true;
                        ev.Use();
                        Repaint();
                    }
                }
            }
            else if (ev.type == EventType.MouseDrag && draggingOwnerKey == ownerKey && draggingMarkerIndex >= 0 && draggingMarkerIndex < markers.Count)
            {
                Vector2 norm = GuiToNormalized(imgRect, ev.mousePosition);
                markers[draggingMarkerIndex].normalizedPos = norm;
                markers[draggingMarkerIndex].label = DetectTargetAtNormalizedPoint(norm, new Vector2(cleanTex.width, cleanTex.height), captureSourceIndex == 1);
                dragModifiedSavedEntry = true;
                markersChanged = true;
                ev.Use();
                Repaint();
            }
            else if (ev.type == EventType.MouseUp && draggingOwnerKey == ownerKey)
            {
                if (dragModifiedSavedEntry)
                {
                    dragFinished = true;
                }
                draggingOwnerKey = null;
                draggingMarkerIndex = -1;
                dragModifiedSavedEntry = false;
                ev.Use();
                Repaint();
            }

            // Draw numbered callout markers live over the clean texture
            for (int i = 0; i < markers.Count; i++)
            {
                Vector2 pos = MarkerGuiPos(imgRect, markers[i].normalizedPos);
                Rect outer = new Rect(pos.x - 9f, pos.y - 9f, 18f, 18f);
                Rect inner = new Rect(pos.x - 7f, pos.y - 7f, 14f, 14f);
                EditorGUI.DrawRect(outer, new Color(1f, 0.94f, 0.3f, 0.95f));
                EditorGUI.DrawRect(inner, new Color(0.95f, 0.18f, 0.18f, 0.98f));
                GUI.Label(outer, (i + 1).ToString(), pinBadgeStyle);
                EditorGUIUtility.AddCursorRect(outer, MouseCursor.MoveArrow);
            }

            return true;
        }

        static Vector2 MarkerGuiPos(Rect imgRect, Vector2 normBottomLeft)
        {
            return new Vector2(
                imgRect.x + normBottomLeft.x * imgRect.width,
                imgRect.y + (1f - normBottomLeft.y) * imgRect.height
            );
        }

        static Vector2 GuiToNormalized(Rect imgRect, Vector2 mousePos)
        {
            return new Vector2(
                Mathf.Clamp01((mousePos.x - imgRect.x) / Mathf.Max(1f, imgRect.width)),
                Mathf.Clamp01(1f - (mousePos.y - imgRect.y) / Mathf.Max(1f, imgRect.height))
            );
        }

        void DrawMarkersEditorList(List<PinMarker> markers, out bool modified)
        {
            modified = false;
            if (markers == null || markers.Count == 0) return;

            for (int m = 0; m < markers.Count; m++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label($"#{m + 1}", EditorStyles.miniBoldLabel, GUILayout.Width(22f));
                string nextLabel = EditorGUILayout.TextField(markers[m].label ?? "");
                if (nextLabel != markers[m].label)
                {
                    markers[m].label = nextLabel;
                    modified = true;
                }
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22f)))
                {
                    markers.RemoveAt(m);
                    modified = true;
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        void OnGUI()
        {
            if (collection == null)
            {
                RefreshEntries();
            }

            int openCount = 0;
            for (int i = 0; i < collection.entries.Count; i++)
            {
                if (!collection.entries[i].resolved) openCount++;
            }

            // Top Toolbar + Action Dropdown
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            string[] dynamicActions =
            {
                "New Log Entry",
                $"Saved Entries ({openCount} open / {collection.entries.Count} total)",
                "Quick Screenshot",
                "Open PlaytestLogs Folder",
                "Open changes.md"
            };
            int nextAction = EditorGUILayout.Popup(actionIndex, dynamicActions, EditorStyles.toolbarPopup, GUILayout.Width(220f));
            if (nextAction != actionIndex)
            {
                if (nextAction == 2)
                {
                    CaptureDraftPreview();
                    SaveDraftEntry(true);
                }
                else if (nextAction == 3)
                {
                    Directory.CreateDirectory(LogsRootDir);
                    EditorUtility.RevealInFinder(LogsRootDir);
                }
                else if (nextAction == 4)
                {
                    if (!File.Exists(MarkdownFilePath)) PersistCollection(collection);
                    UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(MarkdownFilePath, 1);
                }
                else
                {
                    actionIndex = nextAction;
                    if (actionIndex == 0 && draftCleanTex == null) CaptureDraftPreview();
                    if (actionIndex == 1) RefreshEntries();
                }
            }

            captureSourceIndex = EditorGUILayout.Popup(captureSourceIndex, CaptureSources, EditorStyles.toolbarPopup, GUILayout.Width(155f));
            pauseOnQuickCapture = GUILayout.Toggle(pauseOnQuickCapture, "Pause on F1", EditorStyles.toolbarButton, GUILayout.Width(82f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Folder", EditorStyles.toolbarButton, GUILayout.Width(50f)))
            {
                Directory.CreateDirectory(LogsRootDir);
                EditorUtility.RevealInFinder(LogsRootDir);
            }
            EditorGUILayout.EndHorizontal();

            if (EditorApplication.timeSinceStartup < statusBannerUntil && !string.IsNullOrEmpty(statusBanner))
            {
                EditorGUILayout.HelpBox(statusBanner, MessageType.Info);
            }

            if (actionIndex == 0)
            {
                DrawNewEntryView();
            }
            else
            {
                DrawSavedEntriesView();
            }
        }

        void DrawNewEntryView()
        {
            Event ev = Event.current;
            if (ev.type == EventType.KeyDown && (ev.command || ev.control) && ev.keyCode == KeyCode.Return)
            {
                SaveDraftEntry(false);
                ev.Use();
                return;
            }

            formScroll = EditorGUILayout.BeginScrollView(formScroll);
            EditorGUILayout.Space(4f);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Category:", GUILayout.Width(65f));
            newCategoryIndex = EditorGUILayout.Popup(newCategoryIndex, Categories);
            if (GUILayout.Button("Capture / Retake (F1)", GUILayout.Width(145f)))
            {
                CaptureDraftPreview();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4f);

            if (draftCleanTex == null)
            {
                EditorGUILayout.HelpBox("Click 'Capture / Retake (F1)' or press F1 while playing/editing to grab a screenshot.", MessageType.None);
            }
            else
            {
                DrawInteractiveMarkerImage("draft", draftCleanTex, draftMarkers, 240f, out bool markersChanged, out _);
                if (markersChanged && draftMarkers.Count > 0 && draftMarkers[draftMarkers.Count - 1].label.StartsWith("UI:"))
                {
                    newCategoryIndex = 0;
                }

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Click image to add pin • Drag pin to move • Right-click pin to delete", EditorStyles.miniLabel);
                if (draftMarkers.Count > 0 && GUILayout.Button("Clear Pins", EditorStyles.miniButton, GUILayout.Width(72f)))
                {
                    draftMarkers.Clear();
                }
                EditorGUILayout.EndHorizontal();

                DrawMarkersEditorList(draftMarkers, out _);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginHorizontal();
            includeScreenshot = EditorGUILayout.ToggleLeft("Save Screenshot", includeScreenshot, GUILayout.Width(125f));
            includeLogs = EditorGUILayout.ToggleLeft("Save Console Logs", includeLogs, GUILayout.Width(135f));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("Description (what needs changing):");
            newDescription = EditorGUILayout.TextArea(newDescription, EditorStyles.textArea, GUILayout.MinHeight(72f));

            EditorGUILayout.Space(6f);
            if (GUILayout.Button("Save Log Entry (Cmd/Ctrl + Enter)", GUILayout.Height(30f)))
            {
                SaveDraftEntry(false);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawSavedEntriesView()
        {
            EditorGUILayout.BeginHorizontal();
            hideResolved = EditorGUILayout.ToggleLeft("Hide Done Entries", hideResolved, GUILayout.Width(140f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh List", EditorStyles.miniButton, GUILayout.Width(85f)))
            {
                ClearTextureCaches();
                RefreshEntries();
            }
            EditorGUILayout.EndHorizontal();

            if (collection.entries.Count == 0)
            {
                EditorGUILayout.HelpBox("No saved entries yet. Switch to 'New Log Entry' or press F1 to capture one.", MessageType.Info);
                return;
            }

            savedScroll = EditorGUILayout.BeginScrollView(savedScroll);
            for (int i = 0; i < collection.entries.Count; i++)
            {
                LogEntry e = collection.entries[i];
                if (hideResolved && e.resolved)
                {
                    continue;
                }

                EditorGUILayout.BeginVertical("box");

                // Header row: Done toggle, Category dropdown, Timestamp, Retake Screenshot, Log, Delete
                EditorGUILayout.BeginHorizontal();
                bool nextDone = EditorGUILayout.ToggleLeft("Done", e.resolved, EditorStyles.boldLabel, GUILayout.Width(54f));
                if (nextDone != e.resolved)
                {
                    e.resolved = nextDone;
                    PersistCollection(collection);
                }

                int curCat = Mathf.Max(0, Array.IndexOf(Categories, e.category));
                int nextCat = EditorGUILayout.Popup(curCat, Categories, GUILayout.Width(130f));
                if (nextCat != curCat)
                {
                    e.category = Categories[nextCat];
                    PersistCollection(collection);
                }

                EditorGUILayout.LabelField($"{e.timestamp} • {e.scene}", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Retake Shot", EditorStyles.miniButton, GUILayout.Width(82f)))
                {
                    RetakeSavedEntryScreenshot(e);
                }
                if (!string.IsNullOrEmpty(e.screenshotFile) && GUILayout.Button("Open PNG", EditorStyles.miniButton, GUILayout.Width(68f)))
                {
                    string shotFull = Path.Combine(LogsRootDir, e.screenshotFile);
                    if (File.Exists(shotFull)) Application.OpenURL("file://" + shotFull);
                }
                if (!string.IsNullOrEmpty(e.consoleLogFile) && GUILayout.Button("Log", EditorStyles.miniButton, GUILayout.Width(38f)))
                {
                    string logPath = Path.Combine(LogsRootDir, e.consoleLogFile);
                    if (File.Exists(logPath))
                    {
                        UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(logPath, 1);
                    }
                }
                if (GUILayout.Button("Delete", EditorStyles.miniButton, GUILayout.Width(52f)))
                {
                    collection.entries.RemoveAt(i);
                    PersistCollection(collection);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    break;
                }
                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(e.contextInfo))
                {
                    EditorGUILayout.LabelField(e.contextInfo, EditorStyles.miniLabel);
                }

                // Editable Description
                string nextDesc = EditorGUILayout.TextArea(e.description ?? "", EditorStyles.textArea, GUILayout.MinHeight(42f));
                if (nextDesc != e.description)
                {
                    e.description = nextDesc;
                    PersistCollection(collection);
                }

                // Interactive Screenshot with Draggable / Adjustable Markers
                Texture2D cleanTex = GetCleanTextureForEntry(e);
                if (cleanTex != null)
                {
                    GUILayout.Space(3f);
                    DrawInteractiveMarkerImage(
                        "entry_" + e.id,
                        cleanTex,
                        e.markers,
                        200f,
                        out _,
                        out bool finishedMarkerChange);

                    DrawMarkersEditorList(e.markers, out bool labelOrDeleteChanged);

                    if (finishedMarkerChange || labelOrDeleteChanged)
                    {
                        RebakeSavedEntryScreenshot(e);
                    }
                }

                EditorGUILayout.EndVertical();
                GUILayout.Space(5f);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
