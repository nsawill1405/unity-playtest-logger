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
            public string targetElement;
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

        // NOTE: Never put '/' in Popup strings — Unity treats '/' as nested submenus.
        static readonly string[] Categories =
        {
            "UI & HUD",
            "Todo",
            "Bug",
            "Physics & Controls",
            "Camera",
            "Visuals & Shaders",
            "Level & Map"
        };

        static readonly string[] CaptureSources =
        {
            "Game View (UI)",
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
        bool autoSyncActiveView = true;
        bool includeScreenshot = true;
        bool includeLogs = true;
        bool pauseOnQuickCapture;
        bool hideResolved;
        string newDescription = "";
        string statusBanner = "";
        double statusBannerUntil;

        string expandedEntryId = "";
        string lastExternalViewType = "";

        Texture2D draftCleanTex;
        byte[] draftCleanPng;
        readonly List<PinMarker> draftMarkers = new List<PinMarker>();

        LogEntryCollection collection;
        Vector2 formScroll;
        Vector2 savedScroll;
        readonly Dictionary<string, Texture2D> cleanTexCache = new Dictionary<string, Texture2D>();

        string draggingOwnerKey;
        int draggingMarkerIndex = -1;
        bool dragModifiedSavedEntry;

        GUIStyle cardStyle;
        GUIStyle headerBarStyle;
        GUIStyle primaryBtnStyle;
        GUIStyle subtleBtnStyle;
        GUIStyle tabActiveStyle;
        GUIStyle tabInactiveStyle;
        GUIStyle textAreaStyle;
        GUIStyle pinBadgeStyle;
        GUIStyle categoryPillStyle;
        GUIStyle rowHeaderBtnStyle;
        GUIStyle sectionTitleStyle;
        Texture2D cardBgTex;
        Texture2D cardBorderTex;
        Texture2D headerBgTex;
        Texture2D primaryBtnTex;
        Texture2D primaryBtnHoverTex;
        Texture2D tabActiveTex;
        Texture2D inputBgTex;

        static PlaytestLoggerWindow()
        {
            Application.logMessageReceived -= OnUnityLog;
            Application.logMessageReceived += OnUnityLog;
        }

        [MenuItem("Window/Playtest Logger %#l", false, 1950)]
        public static void OpenWindowMenu()
        {
            PlaytestLoggerWindow win = GetWindow<PlaytestLoggerWindow>("Playtest Logger");
            win.minSize = new Vector2(430f, 500f);
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
            EditorWindow prevFocused = focusedWindow;
            PlaytestLoggerWindow win = GetWindow<PlaytestLoggerWindow>("Playtest Logger");
            win.minSize = new Vector2(430f, 500f);
            if (prevFocused != null)
            {
                string prevType = prevFocused.GetType().FullName ?? "";
                if (prevType == "UnityEditor.SceneView") win.captureSourceIndex = 1;
                else if (prevType == "UnityEditor.GameView") win.captureSourceIndex = 0;
            }
            if (EditorApplication.isPlaying && win.pauseOnQuickCapture)
            {
                EditorApplication.isPaused = true;
            }
            win.actionIndex = 0;
            win.CaptureDraftPreview();
            win.RefreshEntries();
            win.ShowBanner("Captured fresh screenshot (" + CaptureSources[win.captureSourceIndex] + ")");
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
            EditorApplication.update -= OnEditorTick;
            EditorApplication.update += OnEditorTick;
            RefreshEntries();
        }

        void OnFocus()
        {
            RefreshEntries();
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorTick;
            ClearTextureCaches();
            DestroyStyleTextures();
            if (draftCleanTex != null)
            {
                DestroyImmediate(draftCleanTex);
                draftCleanTex = null;
            }
        }

        // Automatically retakes draft screenshot when switching between Scene View and Game View tabs
        void OnEditorTick()
        {
            if (!autoSyncActiveView) return;

            EditorWindow fw = focusedWindow;
            if (fw == null || fw == this) return;

            string fullName = fw.GetType().FullName ?? "";
            bool isGameView = fullName == "UnityEditor.GameView" || fullName == "UnityEditor.PlayModeView";
            bool isSceneView = fullName == "UnityEditor.SceneView";
            if (!isGameView && !isSceneView) return;

            string viewKey = isGameView ? "Game" : "Scene";
            if (viewKey != lastExternalViewType)
            {
                lastExternalViewType = viewKey;
                int targetSrc = isGameView ? 0 : 1;
                captureSourceIndex = targetSrc;
                EditorApplication.delayCall += () =>
                {
                    if (this == null) return;
                    CaptureDraftPreview();
                    Repaint();
                };
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

        void DestroyStyleTextures()
        {
            if (cardBgTex != null) DestroyImmediate(cardBgTex);
            if (cardBorderTex != null) DestroyImmediate(cardBorderTex);
            if (headerBgTex != null) DestroyImmediate(headerBgTex);
            if (primaryBtnTex != null) DestroyImmediate(primaryBtnTex);
            if (primaryBtnHoverTex != null) DestroyImmediate(primaryBtnHoverTex);
            if (tabActiveTex != null) DestroyImmediate(tabActiveTex);
            if (inputBgTex != null) DestroyImmediate(inputBgTex);
            cardStyle = null;
        }

        static Texture2D MakeTex(Color col)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.SetPixel(0, 0, col);
            t.Apply();
            return t;
        }

        void EnsureStyles()
        {
            if (cardStyle != null && cardBgTex != null) return;

            bool pro = EditorGUIUtility.isProSkin;
            cardBgTex = MakeTex(pro ? new Color(0.165f, 0.175f, 0.205f, 1f) : new Color(0.88f, 0.89f, 0.91f, 1f));
            cardBorderTex = MakeTex(pro ? new Color(0.25f, 0.27f, 0.32f, 1f) : new Color(0.72f, 0.74f, 0.78f, 1f));
            headerBgTex = MakeTex(pro ? new Color(0.125f, 0.133f, 0.155f, 1f) : new Color(0.82f, 0.83f, 0.86f, 1f));
            primaryBtnTex = MakeTex(new Color(0.20f, 0.48f, 0.86f, 1f));
            primaryBtnHoverTex = MakeTex(new Color(0.26f, 0.56f, 0.95f, 1f));
            tabActiveTex = MakeTex(pro ? new Color(0.24f, 0.28f, 0.36f, 1f) : new Color(0.75f, 0.80f, 0.88f, 1f));
            inputBgTex = MakeTex(pro ? new Color(0.11f, 0.12f, 0.14f, 1f) : new Color(0.96f, 0.96f, 0.97f, 1f));

            cardStyle = new GUIStyle
            {
                normal = { background = cardBgTex },
                padding = new RectOffset(12, 12, 10, 12),
                margin = new RectOffset(8, 8, 4, 6)
            };

            headerBarStyle = new GUIStyle
            {
                normal = { background = headerBgTex },
                padding = new RectOffset(10, 10, 8, 8)
            };

            primaryBtnStyle = new GUIStyle(GUI.skin.button)
            {
                normal = { background = primaryBtnTex, textColor = Color.white },
                hover = { background = primaryBtnHoverTex, textColor = Color.white },
                active = { background = primaryBtnHoverTex, textColor = Color.white },
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                padding = new RectOffset(12, 12, 6, 6)
            };

            subtleBtnStyle = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 11,
                padding = new RectOffset(8, 8, 3, 3)
            };

            tabActiveStyle = new GUIStyle(EditorStyles.miniButton)
            {
                normal = { background = tabActiveTex, textColor = pro ? Color.white : Color.black },
                hover = { background = tabActiveTex, textColor = pro ? Color.white : Color.black },
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                fixedHeight = 25f
            };

            tabInactiveStyle = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 11,
                fixedHeight = 25f
            };

            textAreaStyle = new GUIStyle(EditorStyles.textArea)
            {
                normal = { background = inputBgTex, textColor = pro ? new Color(0.93f, 0.94f, 0.96f) : Color.black },
                focused = { background = inputBgTex, textColor = pro ? Color.white : Color.black },
                wordWrap = true,
                fontSize = 12,
                padding = new RectOffset(8, 8, 6, 6)
            };

            pinBadgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                normal = { textColor = Color.white }
            };

            categoryPillStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                padding = new RectOffset(6, 6, 2, 2),
                normal = { textColor = Color.white }
            };

            rowHeaderBtnStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };

            sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11
            };
        }

        static Color CategoryColor(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return new Color(0.35f, 0.40f, 0.48f);
            if (cat.StartsWith("UI")) return new Color(0.16f, 0.56f, 0.76f);
            if (cat.StartsWith("Bug")) return new Color(0.80f, 0.26f, 0.28f);
            if (cat.StartsWith("Todo")) return new Color(0.76f, 0.52f, 0.16f);
            if (cat.StartsWith("Physics")) return new Color(0.52f, 0.34f, 0.78f);
            if (cat.StartsWith("Camera")) return new Color(0.24f, 0.46f, 0.80f);
            if (cat.StartsWith("Visuals")) return new Color(0.18f, 0.62f, 0.52f);
            if (cat.StartsWith("Level") || cat.StartsWith("Map")) return new Color(0.28f, 0.62f, 0.32f);
            return new Color(0.38f, 0.42f, 0.50f);
        }

        static string NormalizeLegacyCategory(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return Categories[0];
            for (int i = 0; i < Categories.Length; i++)
            {
                if (Categories[i] == cat) return cat;
            }
            if (cat.StartsWith("UI")) return Categories[0];
            if (cat.StartsWith("Todo")) return Categories[1];
            if (cat.StartsWith("Bug")) return Categories[2];
            if (cat.StartsWith("Physics")) return Categories[3];
            if (cat.StartsWith("Camera")) return Categories[4];
            if (cat.StartsWith("Visuals")) return Categories[5];
            if (cat.StartsWith("Map") || cat.StartsWith("Level")) return Categories[6];
            return Categories[1];
        }

        void ShowBanner(string msg)
        {
            statusBanner = msg;
            statusBannerUntil = EditorApplication.timeSinceStartup + 2.8;
            Repaint();
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
                category = quickScreenshotOnly ? "Visuals & Shaders" : Categories[newCategoryIndex],
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
            expandedEntryId = entry.id;
            PersistCollection(collection);

            if (!quickScreenshotOnly)
            {
                newDescription = "";
                draftMarkers.Clear();
            }
            ShowBanner("Saved entry to PlaytestLogs/changes.md");
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
            ShowBanner("Retook screenshot (" + CaptureSources[captureSourceIndex] + ")");
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
                    e.category = NormalizeLegacyCategory(e.category);
                    if (e.markers == null) e.markers = new List<PinMarker>();
                    if (e.markers.Count == 0 && !string.IsNullOrEmpty(e.targetElement))
                    {
                        e.markers.Add(new PinMarker { normalizedPos = new Vector2(0.5f, 0.5f), label = e.targetElement });
                        e.targetElement = "";
                    }
                }
                if (string.IsNullOrEmpty(expandedEntryId) && collection.entries.Count > 0)
                {
                    expandedEntryId = collection.entries[0].id;
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

        static void DrawCardOutline(Rect r, Texture2D borderTex)
        {
            if (borderTex == null || Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1f), borderTex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 1f, r.width, 1f), borderTex);
            GUI.DrawTexture(new Rect(r.x, r.y, 1f, r.height), borderTex);
            GUI.DrawTexture(new Rect(r.xMax - 1f, r.y, 1f, r.height), borderTex);
        }

        void DrawCategoryBadge(string category, float width = 112f)
        {
            Rect r = GUILayoutUtility.GetRect(width, 18f, GUILayout.Width(width));
            r.y += 1f;
            EditorGUI.DrawRect(r, CategoryColor(category));
            GUI.Label(r, category, categoryPillStyle);
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

            float availW = Mathf.Max(200f, position.width - 44f);
            float aspect = (float)cleanTex.width / Mathf.Max(1, cleanTex.height);
            float drawH = Mathf.Min(maxHeight, availW / aspect);
            float drawW = drawH * aspect;

            Rect rowRect = GUILayoutUtility.GetRect(availW, drawH + 8f);
            EditorGUI.DrawRect(rowRect, new Color(0.08f, 0.09f, 0.11f, 1f));
            DrawCardOutline(rowRect, cardBorderTex);

            Rect imgRect = new Rect(rowRect.x + (rowRect.width - drawW) * 0.5f, rowRect.y + 4f, drawW, drawH);
            GUI.DrawTexture(imgRect, cleanTex, ScaleMode.StretchToFill);
            EditorGUIUtility.AddCursorRect(imgRect, MouseCursor.ArrowPlus);

            Event ev = Event.current;
            const float hitRadius = 12f;

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

            for (int i = 0; i < markers.Count; i++)
            {
                Vector2 pos = MarkerGuiPos(imgRect, markers[i].normalizedPos);
                Rect shadow = new Rect(pos.x - 9f, pos.y - 8f, 20f, 20f);
                Rect outer = new Rect(pos.x - 10f, pos.y - 10f, 20f, 20f);
                Rect inner = new Rect(pos.x - 8f, pos.y - 8f, 16f, 16f);
                EditorGUI.DrawRect(shadow, new Color(0f, 0f, 0f, 0.45f));
                EditorGUI.DrawRect(outer, new Color(1f, 0.92f, 0.28f, 1f));
                EditorGUI.DrawRect(inner, new Color(0.92f, 0.18f, 0.20f, 1f));
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

            GUILayout.Space(3f);
            for (int m = 0; m < markers.Count; m++)
            {
                EditorGUILayout.BeginHorizontal();
                Rect badge = GUILayoutUtility.GetRect(22f, 18f, GUILayout.Width(22f));
                badge.y += 1f;
                EditorGUI.DrawRect(badge, new Color(0.90f, 0.20f, 0.22f, 1f));
                GUI.Label(badge, (m + 1).ToString(), pinBadgeStyle);

                string nextLabel = EditorGUILayout.TextField(markers[m].label ?? "");
                if (nextLabel != markers[m].label)
                {
                    markers[m].label = nextLabel;
                    modified = true;
                }
                if (GUILayout.Button("×", subtleBtnStyle, GUILayout.Width(24f)))
                {
                    markers.RemoveAt(m);
                    modified = true;
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        void ShowMoreActionsMenu()
        {
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("Quick Screenshot Now"), false, () =>
            {
                CaptureDraftPreview();
                SaveDraftEntry(true);
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Auto-Retake When Switching Scene or Game Tab"), autoSyncActiveView, () =>
            {
                autoSyncActiveView = !autoSyncActiveView;
            });
            menu.AddItem(new GUIContent("Pause Play Mode on F1 Capture"), pauseOnQuickCapture, () =>
            {
                pauseOnQuickCapture = !pauseOnQuickCapture;
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Open PlaytestLogs Folder"), false, () =>
            {
                Directory.CreateDirectory(LogsRootDir);
                EditorUtility.RevealInFinder(LogsRootDir);
            });
            menu.AddItem(new GUIContent("Open changes.md Markdown"), false, () =>
            {
                if (!File.Exists(MarkdownFilePath)) PersistCollection(collection);
                UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(MarkdownFilePath, 1);
            });
            menu.ShowAsContext();
        }

        void OnGUI()
        {
            EnsureStyles();

            if (collection == null)
            {
                RefreshEntries();
            }

            int openCount = 0;
            for (int i = 0; i < collection.entries.Count; i++)
            {
                if (!collection.entries[i].resolved) openCount++;
            }

            // Polished Top Navigation Bar (Zero '/' characters so zero unwanted submenus)
            EditorGUILayout.BeginVertical(headerBarStyle);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("+ New Entry", actionIndex == 0 ? tabActiveStyle : tabInactiveStyle, GUILayout.Height(25f)))
            {
                actionIndex = 0;
                if (draftCleanTex == null) CaptureDraftPreview();
            }
            string savedTabLabel = $"Saved Entries ({openCount} open • {collection.entries.Count} total)";
            if (GUILayout.Button(savedTabLabel, actionIndex == 1 ? tabActiveStyle : tabInactiveStyle, GUILayout.Height(25f)))
            {
                actionIndex = 1;
                RefreshEntries();
            }

            if (GUILayout.Button("Actions ▾", subtleBtnStyle, GUILayout.Width(74f), GUILayout.Height(25f)))
            {
                ShowMoreActionsMenu();
            }
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(6f);

            // Capture Source Bar — switching between Game View and Scene View automatically retakes the screenshot
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("View:", EditorStyles.miniBoldLabel, GUILayout.Width(36f));
            int nextSource = GUILayout.Toolbar(captureSourceIndex, CaptureSources, EditorStyles.miniButton);
            if (nextSource != captureSourceIndex)
            {
                captureSourceIndex = nextSource;
                CaptureDraftPreview();
                ShowBanner("Switched to " + CaptureSources[captureSourceIndex] + " & retook screenshot");
            }

            autoSyncActiveView = GUILayout.Toggle(autoSyncActiveView, new GUIContent("Auto", "Automatically retake screenshot when switching between Scene View and Game View tabs"), EditorStyles.miniButton, GUILayout.Width(46f));

            if (GUILayout.Button("Retake (F1)", subtleBtnStyle, GUILayout.Width(82f)))
            {
                CaptureDraftPreview();
                ShowBanner("Captured " + CaptureSources[captureSourceIndex]);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            DrawCardOutline(GUILayoutUtility.GetLastRect(), cardBorderTex);

            if (EditorApplication.timeSinceStartup < statusBannerUntil && !string.IsNullOrEmpty(statusBanner))
            {
                Rect bannerRect = GUILayoutUtility.GetRect(position.width, 24f);
                EditorGUI.DrawRect(bannerRect, new Color(0.16f, 0.34f, 0.24f, 0.95f));
                GUI.Label(new Rect(bannerRect.x + 10f, bannerRect.y + 3f, bannerRect.width - 20f, 18f), "✓  " + statusBanner, EditorStyles.boldLabel);
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
            GUILayout.Space(4f);

            Rect cardRect = EditorGUILayout.BeginVertical(cardStyle);
            DrawCardOutline(cardRect, cardBorderTex);

            // Category Row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Category", sectionTitleStyle, GUILayout.Width(64f));
            DrawCategoryBadge(Categories[newCategoryIndex], 116f);
            newCategoryIndex = EditorGUILayout.Popup(newCategoryIndex, Categories);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(8f);

            // Interactive Screenshot Card
            if (draftCleanTex == null)
            {
                EditorGUILayout.HelpBox("Press F1 or click 'Retake (F1)' above to capture a screenshot.", MessageType.None);
            }
            else
            {
                DrawInteractiveMarkerImage("draft", draftCleanTex, draftMarkers, 235f, out bool markersChanged, out _);
                if (markersChanged && draftMarkers.Count > 0 && draftMarkers[draftMarkers.Count - 1].label.StartsWith("UI:"))
                {
                    newCategoryIndex = 0;
                }

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Click image to add pin  •  Drag pin to adjust  •  Right-click pin to delete", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (draftMarkers.Count > 0 && GUILayout.Button("Clear Pins", subtleBtnStyle, GUILayout.Width(72f)))
                {
                    draftMarkers.Clear();
                }
                EditorGUILayout.EndHorizontal();

                DrawMarkersEditorList(draftMarkers, out _);
            }

            GUILayout.Space(8f);

            // Description
            GUILayout.Label("Description", sectionTitleStyle);
            GUILayout.Space(2f);
            newDescription = EditorGUILayout.TextArea(newDescription, textAreaStyle, GUILayout.MinHeight(78f));

            GUILayout.Space(8f);

            // Options + Primary Save Button
            EditorGUILayout.BeginHorizontal();
            includeScreenshot = EditorGUILayout.ToggleLeft("Attach Screenshot", includeScreenshot, GUILayout.Width(130f));
            includeLogs = EditorGUILayout.ToggleLeft("Attach Console Logs", includeLogs, GUILayout.Width(145f));
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(8f);
            if (GUILayout.Button("Save Log Entry   (Cmd / Ctrl + Enter)", primaryBtnStyle, GUILayout.Height(32f)))
            {
                SaveDraftEntry(false);
            }

            EditorGUILayout.EndVertical();
            GUILayout.Space(6f);
            EditorGUILayout.EndScrollView();
        }

        static string BuildShortSummary(string text, int maxLen = 42)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(No description)";
            string singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length <= maxLen ? singleLine : singleLine.Substring(0, maxLen - 1) + "…";
        }

        void DrawSavedEntriesView()
        {
            // Filter + Quick-Select Entry Dropdown Bar
            EditorGUILayout.BeginVertical(cardStyle);
            List<LogEntry> visibleEntries = new List<LogEntry>();
            for (int i = 0; i < collection.entries.Count; i++)
            {
                if (!hideResolved || !collection.entries[i].resolved)
                {
                    visibleEntries.Add(collection.entries[i]);
                }
            }

            if (visibleEntries.Count > 0)
            {
                string[] dropdownLabels = new string[visibleEntries.Count];
                int selectedDropdownIdx = 0;
                for (int i = 0; i < visibleEntries.Count; i++)
                {
                    LogEntry ve = visibleEntries[i];
                    string statusPrefix = ve.resolved ? "[Done] " : "";
                    // Strip '/' so Unity Popup never splits user descriptions into submenus
                    string safeSummary = BuildShortSummary(ve.description, 38).Replace("/", "⁄");
                    dropdownLabels[i] = $"{i + 1}. {statusPrefix}[{ve.category}] {safeSummary}";
                    if (ve.id == expandedEntryId)
                    {
                        selectedDropdownIdx = i;
                    }
                }

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Select Entry:", sectionTitleStyle, GUILayout.Width(82f));
                int nextSelected = EditorGUILayout.Popup(selectedDropdownIdx, dropdownLabels);
                if (nextSelected != selectedDropdownIdx && nextSelected >= 0 && nextSelected < visibleEntries.Count)
                {
                    expandedEntryId = visibleEntries[nextSelected].id;
                }
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(4f);
            }

            EditorGUILayout.BeginHorizontal();
            hideResolved = EditorGUILayout.ToggleLeft("Hide Done Entries", hideResolved, GUILayout.Width(135f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Collapse All", subtleBtnStyle, GUILayout.Width(84f)))
            {
                expandedEntryId = "";
            }
            if (GUILayout.Button("Open Folder", subtleBtnStyle, GUILayout.Width(86f)))
            {
                Directory.CreateDirectory(LogsRootDir);
                EditorUtility.RevealInFinder(LogsRootDir);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            if (visibleEntries.Count == 0)
            {
                GUILayout.Space(8f);
                EditorGUILayout.HelpBox("No matching saved entries. Switch to '+ New Entry' or press F1 to log an issue.", MessageType.Info);
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

                bool isExpanded = e.id == expandedEntryId;
                Rect cardRect = EditorGUILayout.BeginVertical(cardStyle);
                DrawCardOutline(cardRect, cardBorderTex);

                // Collapsible Dropdown Header Row
                EditorGUILayout.BeginHorizontal();
                bool nextDone = EditorGUILayout.Toggle(e.resolved, GUILayout.Width(16f));
                if (nextDone != e.resolved)
                {
                    e.resolved = nextDone;
                    PersistCollection(collection);
                }

                string arrow = isExpanded ? "▾ " : "▸ ";
                DrawCategoryBadge(e.category, 108f);

                string headerTitle = arrow + BuildShortSummary(e.description, 46);
                if (GUILayout.Button(headerTitle, rowHeaderBtnStyle, GUILayout.ExpandWidth(true), GUILayout.Height(20f)))
                {
                    expandedEntryId = isExpanded ? "" : e.id;
                    isExpanded = !isExpanded;
                }

                string shortTime = !string.IsNullOrEmpty(e.timestamp) && e.timestamp.Length >= 16
                    ? e.timestamp.Substring(11, 5)
                    : e.timestamp;
                GUILayout.Label(shortTime, EditorStyles.miniLabel, GUILayout.Width(38f));
                EditorGUILayout.EndHorizontal();

                // Expanded Dropdown Details
                if (isExpanded)
                {
                    GUILayout.Space(8f);
                    Rect divider = GUILayoutUtility.GetRect(10f, 1f, GUILayout.ExpandWidth(true));
                    EditorGUI.DrawRect(divider, cardBorderTex != null ? new Color(0.28f, 0.30f, 0.36f) : Color.gray);
                    GUILayout.Space(6f);

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Category:", EditorStyles.miniBoldLabel, GUILayout.Width(60f));
                    int curCat = Mathf.Max(0, Array.IndexOf(Categories, e.category));
                    int nextCat = EditorGUILayout.Popup(curCat, Categories, GUILayout.Width(145f));
                    if (nextCat != curCat)
                    {
                        e.category = Categories[nextCat];
                        PersistCollection(collection);
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
                    string nextDesc = EditorGUILayout.TextArea(e.description ?? "", textAreaStyle, GUILayout.MinHeight(56f));
                    if (nextDesc != e.description)
                    {
                        e.description = nextDesc;
                        PersistCollection(collection);
                    }

                    Texture2D cleanTex = GetCleanTextureForEntry(e);
                    if (cleanTex != null)
                    {
                        GUILayout.Space(6f);
                        DrawInteractiveMarkerImage(
                            "entry_" + e.id,
                            cleanTex,
                            e.markers,
                            215f,
                            out _,
                            out bool finishedMarkerChange);

                        EditorGUILayout.LabelField("Click to add pin  •  Drag pin to adjust  •  Right-click pin to remove", EditorStyles.miniLabel);
                        DrawMarkersEditorList(e.markers, out bool labelOrDeleteChanged);

                        if (finishedMarkerChange || labelOrDeleteChanged)
                        {
                            RebakeSavedEntryScreenshot(e);
                        }
                    }

                    GUILayout.Space(8f);
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Retake Screenshot", subtleBtnStyle, GUILayout.Width(122f)))
                    {
                        RetakeSavedEntryScreenshot(e);
                    }
                    if (!string.IsNullOrEmpty(e.screenshotFile) && GUILayout.Button("Open PNG", subtleBtnStyle, GUILayout.Width(76f)))
                    {
                        string shotFull = Path.Combine(LogsRootDir, e.screenshotFile);
                        if (File.Exists(shotFull)) Application.OpenURL("file://" + shotFull);
                    }
                    if (!string.IsNullOrEmpty(e.consoleLogFile) && GUILayout.Button("Console Log", subtleBtnStyle, GUILayout.Width(86f)))
                    {
                        string logPath = Path.Combine(LogsRootDir, e.consoleLogFile);
                        if (File.Exists(logPath))
                        {
                            UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(logPath, 1);
                        }
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Delete", subtleBtnStyle, GUILayout.Width(60f)))
                    {
                        collection.entries.RemoveAt(i);
                        PersistCollection(collection);
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.EndVertical();
                        break;
                    }
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndVertical();
            }
            GUILayout.Space(6f);
            EditorGUILayout.EndScrollView();
        }
    }
}
