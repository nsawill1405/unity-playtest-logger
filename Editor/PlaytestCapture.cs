using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PlaytestLoggerTool
{
    /// <summary>Screenshot capture, pin target detection and marker baking.</summary>
    static class PlaytestCapture
    {
        const int FallbackWidth = 1280;
        const int FallbackHeight = 720;
        const float RaycastDistance = 800f;
        const int MaxPathDepth = 6;

        static readonly Color32 RingColor = new Color32(255, 55, 55, 255);
        static readonly Color32 OutlineColor = new Color32(255, 240, 80, 255);
        static readonly Color32 DigitColor = new Color32(255, 255, 255, 255);

        // The camera drawn last into the Game View (highest depth on Display 1), falling back to Camera.main.
        static Camera GameViewCamera
        {
            get
            {
                Camera top = null;
                foreach (Camera cam in Camera.allCameras)
                {
                    if (cam.targetTexture != null || cam.targetDisplay != 0) continue;
                    if (top == null || cam.depth >= top.depth) top = cam;
                }
                return top != null ? top : Camera.main;
            }
        }

        static Camera SceneViewCamera => SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;

        public static string DisplayName(CaptureSource source)
        {
            return source == CaptureSource.SceneView ? "Scene View" : "Game View";
        }

        /// <summary>Returns the capture source a focused editor window corresponds to, if any.</summary>
        public static CaptureSource? SourceOfWindow(EditorWindow window)
        {
            if (window == null) return null;
            if (window is SceneView) return CaptureSource.SceneView;
            string typeName = window.GetType().FullName;
            if (typeName == "UnityEditor.GameView" || typeName == "UnityEditor.PlayModeView") return CaptureSource.GameView;
            return null;
        }

        /// <summary>
        /// Captures the requested view, falling back to the Scene View camera. <paramref name="actual"/> reports which
        /// view the image came from so pins are resolved against the right camera. Caller owns the returned texture.
        /// </summary>
        public static Texture2D Capture(CaptureSource requested, out CaptureSource actual, out CameraPose pose)
        {
            actual = requested;
            if (requested == CaptureSource.GameView)
            {
                Camera gameCamera = GameViewCamera;
                Texture2D tex = CaptureGameView() ?? CaptureCamera(gameCamera);
                if (tex != null)
                {
                    pose = CameraPose.From(gameCamera);
                    return tex;
                }
            }
            actual = CaptureSource.SceneView;
            pose = CameraPose.From(SceneViewCamera);
            return CaptureCamera(SceneViewCamera);
        }

        /// <summary>Moves the Scene View camera to a recorded pose. Returns false if there is no pose or Scene View.</summary>
        public static bool MoveSceneViewTo(CameraPose pose)
        {
            if (pose == null || !pose.valid) return false;
            SceneView view = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView : EditorWindow.GetWindow<SceneView>();
            if (view == null) return false;

            // 2D mode forces an identity rotation, so leave it when the shot was taken from any other angle.
            if (view.in2DMode && Quaternion.Angle(pose.rotation, Quaternion.identity) > 0.01f) view.in2DMode = false;
            view.orthographic = pose.orthographic;
            // cameraDistance reads the camera's projection, which the Scene View only syncs on its next repaint.
            view.camera.orthographic = pose.orthographic;
            float size = pose.orthographic && pose.orthographicSize > 0f ? pose.orthographicSize : view.size;
            view.size = size;
            // The Scene View orbits a pivot in front of its camera, so place the pivot cameraDistance ahead of the pose.
            Vector3 pivot = pose.position + pose.rotation * Vector3.forward * view.cameraDistance;
            view.LookAtDirect(pivot, pose.rotation, size);
            view.Repaint();
            view.Focus();
            return true;
        }

        // Reads the Game View's own render target so Screen Space - Overlay UI is included.
        static Texture2D CaptureGameView()
        {
            try
            {
                Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
                Object[] views = gameViewType != null ? Resources.FindObjectsOfTypeAll(gameViewType) : null;
                if (views != null && views.Length > 0)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    FieldInfo field = gameViewType.GetField("m_TargetTexture", flags) ?? gameViewType.BaseType?.GetField("m_TargetTexture", flags);
                    PropertyInfo property = gameViewType.GetProperty("targetTexture", flags);
                    RenderTexture target = field?.GetValue(views[0]) as RenderTexture ?? property?.GetValue(views[0], null) as RenderTexture;
                    if (target != null && target.width > 0 && target.height > 0)
                    {
                        RenderTexture temp = RenderTexture.GetTemporary(target.width, target.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                        Graphics.Blit(target, temp);
                        Texture2D tex = ReadPixels(temp);
                        RenderTexture.ReleaseTemporary(temp);
                        return tex;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Playtest Logger] Game View capture failed, falling back to camera render: " + ex.Message);
            }

            return Application.isPlaying ? ScreenCapture.CaptureScreenshotAsTexture() : null;
        }

        static Texture2D CaptureCamera(Camera cam)
        {
            if (cam == null) return null;
            bool hasSize = cam.pixelWidth >= 16 && cam.pixelHeight >= 16;
            int w = hasSize ? cam.pixelWidth : FallbackWidth;
            int h = hasSize ? cam.pixelHeight : FallbackHeight;

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevTarget = cam.targetTexture;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                return ReadPixels(rt);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static Texture2D ReadPixels(RenderTexture source)
        {
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = source;
            Texture2D tex = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            return tex;
        }

        /// <summary>Snapshot of play state and player/camera position for the log entry.</summary>
        public static string DescribeContext()
        {
            string mode = EditorApplication.isPlaying ? "PlayMode" : "EditMode";
            GameObject player = null;
            try
            {
                player = GameObject.FindWithTag("Player");
            }
            catch (UnityException)
            {
                // "Player" tag is not defined in this project.
            }

            if (player != null)
            {
                string speed = TryGetSpeed(player, out float s) ? $" | Speed={s:F1}" : "";
                return $"{mode} | PlayerPos={FormatVector(player.transform.position)}{speed}";
            }
            if (Camera.main != null)
            {
                return $"{mode} | CamPos={FormatVector(Camera.main.transform.position)}";
            }
            return mode;
        }

        static bool TryGetSpeed(GameObject go, out float speed)
        {
            speed = 0f;
            if (go.TryGetComponent(out Rigidbody rb))
            {
#if UNITY_6000_0_OR_NEWER
                speed = rb.linearVelocity.magnitude;
#else
                speed = rb.velocity.magnitude;
#endif
                return true;
            }
            if (go.TryGetComponent(out Rigidbody2D rb2D))
            {
#if UNITY_6000_0_OR_NEWER
                speed = rb2D.linearVelocity.magnitude;
#else
                speed = rb2D.velocity.magnitude;
#endif
                return true;
            }
            return false;
        }

        static string FormatVector(Vector3 v) => $"({v.x:F1}, {v.y:F1}, {v.z:F1})";

        /// <summary>Describes what is under a point of a capture: a UI element, a collider, or just the position.</summary>
        public static string DescribePoint(Vector2 normalized, Vector2 imageSize, CaptureSource source)
        {
            string percent = $"({normalized.x * 100f:F0}%, {normalized.y * 100f:F0}%)";

            if (source == CaptureSource.GameView)
            {
                RectTransform ui = FindSmallestUiElementAt(Vector2.Scale(normalized, imageSize));
                if (ui != null) return $"UI: {GetTransformPath(ui)} {percent}";
            }

            Camera cam = source == CaptureSource.SceneView ? SceneViewCamera : Camera.main;
            if (cam != null)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(normalized.x, normalized.y, 0f));
                if (Physics.Raycast(ray, out RaycastHit hit, RaycastDistance))
                {
                    return $"Object: {GetTransformPath(hit.transform)} at {hit.point:F1}";
                }
            }

            return "Spot " + percent;
        }

        static RectTransform FindSmallestUiElementAt(Vector2 screenPoint)
        {
            RectTransform best = null;
            float smallestArea = float.MaxValue;

            foreach (Canvas canvas in FindCanvases())
            {
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                Camera uiCam = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : (canvas.worldCamera != null ? canvas.worldCamera : Camera.main);

                foreach (RectTransform rt in canvas.GetComponentsInChildren<RectTransform>(false))
                {
                    if (rt.transform == canvas.transform) continue;
                    // Skip pure layout containers; they would always win over their visible children.
                    if (rt.childCount > 0 && !rt.TryGetComponent(out CanvasRenderer _)) continue;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, uiCam)) continue;

                    Vector2 size = Vector2.Scale(rt.rect.size, rt.lossyScale);
                    float area = Mathf.Abs(size.x * size.y);
                    if (area > 1f && area <= smallestArea)
                    {
                        smallestArea = area;
                        best = rt;
                    }
                }
            }
            return best;
        }

        static Canvas[] FindCanvases()
        {
#if UNITY_2022_2_OR_NEWER
            return Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
#else
            return Object.FindObjectsOfType<Canvas>();
#endif
        }

        static string GetTransformPath(Transform t)
        {
            string path = t.name;
            Transform current = t.parent;
            for (int depth = 1; current != null; depth++, current = current.parent)
            {
                if (depth >= MaxPathDepth) return "…/" + path;
                path = current.name + "/" + path;
            }
            return path;
        }

        /// <summary>Encodes a PNG of the clean capture with numbered rings drawn at each marker.</summary>
        public static byte[] BakeMarkers(Texture2D clean, List<PinMarker> markers)
        {
            if (markers == null || markers.Count == 0) return clean.EncodeToPNG();

            int w = clean.width;
            int h = clean.height;
            Color32[] pixels = clean.GetPixels32();
            int outerR = Mathf.Max(14, Mathf.RoundToInt(Mathf.Min(w, h) * 0.025f));

            for (int i = 0; i < markers.Count; i++)
            {
                Vector2 p = markers[i].normalizedPos;
                int cx = Mathf.Clamp(Mathf.RoundToInt(p.x * (w - 1)), 0, w - 1);
                int cy = Mathf.Clamp(Mathf.RoundToInt(p.y * (h - 1)), 0, h - 1);
                DrawRing(pixels, w, h, cx, cy, outerR);
                DrawNumberBadge(pixels, w, h, cx, cy, outerR, i + 1);
            }

            Texture2D baked = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                baked.SetPixels32(pixels);
                baked.Apply();
                return baked.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }

        // Hollow ring (so the pinned element stays visible) with an outline and a centre dot.
        static void DrawRing(Color32[] pixels, int w, int h, int cx, int cy, int outerR)
        {
            int innerR = Mathf.Max(10, outerR - 4);
            int outlineR = outerR + 2;
            for (int dy = -outlineR; dy <= outlineR; dy++)
            {
                for (int dx = -outlineR; dx <= outlineR; dx++)
                {
                    int distSq = dx * dx + dy * dy;
                    if (distSq >= innerR * innerR && distSq <= outerR * outerR) Plot(pixels, w, h, cx + dx, cy + dy, RingColor);
                    else if (distSq > outerR * outerR && distSq <= outlineR * outlineR) Plot(pixels, w, h, cx + dx, cy + dy, OutlineColor);
                    else if (distSq <= 9) Plot(pixels, w, h, cx + dx, cy + dy, RingColor);
                }
            }
        }

        // 3x5 bitmap digits, one row per 3 bits, top row first.
        static readonly int[][] DigitRows =
        {
            new[] { 7, 5, 5, 5, 7 }, new[] { 2, 6, 2, 2, 7 }, new[] { 7, 1, 7, 4, 7 }, new[] { 7, 1, 7, 1, 7 },
            new[] { 5, 5, 7, 1, 1 }, new[] { 7, 4, 7, 1, 7 }, new[] { 7, 4, 7, 5, 7 }, new[] { 7, 1, 1, 1, 1 },
            new[] { 7, 5, 7, 5, 7 }, new[] { 7, 5, 7, 1, 7 }
        };

        // Numbered badge next to the ring (top-right, flipped to stay inside the image).
        static void DrawNumberBadge(Color32[] pixels, int w, int h, int cx, int cy, int outerR, int number)
        {
            string digits = number.ToString();
            int scale = Mathf.Max(2, outerR / 6);
            int textW = digits.Length * 4 * scale - scale;
            int textH = 5 * scale;
            int pad = scale * 2;
            int badgeW = textW + pad * 2;
            int badgeH = textH + pad * 2;

            int left = cx + outerR;
            if (left + badgeW > w) left = cx - outerR - badgeW;
            int bottom = cy + outerR;
            if (bottom + badgeH > h) bottom = cy - outerR - badgeH;

            FillRect(pixels, w, h, left - 2, bottom - 2, left + badgeW + 2, bottom + badgeH + 2, OutlineColor);
            FillRect(pixels, w, h, left, bottom, left + badgeW, bottom + badgeH, RingColor);

            int top = bottom + pad + textH; // texture y grows upwards
            for (int d = 0; d < digits.Length; d++)
            {
                int[] rows = DigitRows[digits[d] - '0'];
                int glyphLeft = left + pad + d * 4 * scale;
                for (int row = 0; row < 5; row++)
                {
                    for (int col = 0; col < 3; col++)
                    {
                        if ((rows[row] & (4 >> col)) == 0) continue;
                        int x0 = glyphLeft + col * scale;
                        int y1 = top - row * scale;
                        FillRect(pixels, w, h, x0, y1 - scale, x0 + scale, y1, DigitColor);
                    }
                }
            }
        }

        static void FillRect(Color32[] pixels, int w, int h, int xMin, int yMin, int xMax, int yMax, Color32 color)
        {
            for (int y = Mathf.Max(0, yMin); y < Mathf.Min(h, yMax); y++)
            {
                for (int x = Mathf.Max(0, xMin); x < Mathf.Min(w, xMax); x++)
                {
                    pixels[y * w + x] = color;
                }
            }
        }

        static void Plot(Color32[] pixels, int w, int h, int x, int y, Color32 color)
        {
            if (x >= 0 && x < w && y >= 0 && y < h) pixels[y * w + x] = color;
        }
    }
}
