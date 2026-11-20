using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PlaytestLoggerTool
{
    /// <summary>GUI styles for the Playtest Logger window. Must be created inside OnGUI.</summary>
    sealed class PlaytestLoggerStyles
    {
        public readonly GUIStyle Card;
        public readonly GUIStyle HeaderBar;
        public readonly GUIStyle PrimaryButton;
        public readonly GUIStyle SubtleButton;
        public readonly GUIStyle TabActive;
        public readonly GUIStyle TabInactive;
        public readonly GUIStyle TextArea;
        public readonly GUIStyle PinBadge;
        public readonly GUIStyle CategoryPill;
        public readonly GUIStyle RowHeader;
        public readonly GUIStyle SectionTitle;

        public readonly Color BorderColor;
        public readonly Color ImageBackground = new Color(0.08f, 0.09f, 0.11f, 1f);
        public readonly Color BannerColor = new Color(0.16f, 0.34f, 0.24f, 0.95f);
        public readonly Color PinOutline = new Color(1f, 0.92f, 0.28f, 1f);
        public readonly Color PinFill = new Color(0.92f, 0.18f, 0.20f, 1f);
        public readonly Color PinShadow = new Color(0f, 0f, 0f, 0.45f);

        readonly bool builtForProSkin;
        readonly List<Texture2D> ownedTextures = new List<Texture2D>();

        public PlaytestLoggerStyles()
        {
            bool pro = builtForProSkin = EditorGUIUtility.isProSkin;
            Color text = pro ? Color.white : Color.black;
            BorderColor = pro ? new Color(0.25f, 0.27f, 0.32f, 1f) : new Color(0.72f, 0.74f, 0.78f, 1f);

            Texture2D cardBg = Tex(pro ? new Color(0.165f, 0.175f, 0.205f) : new Color(0.88f, 0.89f, 0.91f));
            Texture2D headerBg = Tex(pro ? new Color(0.125f, 0.133f, 0.155f) : new Color(0.82f, 0.83f, 0.86f));
            Texture2D primary = Tex(new Color(0.20f, 0.48f, 0.86f));
            Texture2D primaryHover = Tex(new Color(0.26f, 0.56f, 0.95f));
            Texture2D tabActive = Tex(pro ? new Color(0.24f, 0.28f, 0.36f) : new Color(0.75f, 0.80f, 0.88f));
            Texture2D inputBg = Tex(pro ? new Color(0.11f, 0.12f, 0.14f) : new Color(0.96f, 0.96f, 0.97f));

            Card = new GUIStyle
            {
                normal = { background = cardBg },
                padding = new RectOffset(12, 12, 10, 12),
                margin = new RectOffset(8, 8, 4, 6)
            };
            HeaderBar = new GUIStyle
            {
                normal = { background = headerBg },
                padding = new RectOffset(10, 10, 8, 8)
            };
            PrimaryButton = new GUIStyle(GUI.skin.button)
            {
                normal = { background = primary, textColor = Color.white },
                hover = { background = primaryHover, textColor = Color.white },
                active = { background = primaryHover, textColor = Color.white },
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                padding = new RectOffset(12, 12, 6, 6)
            };
            SubtleButton = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 11,
                padding = new RectOffset(8, 8, 3, 3)
            };
            TabActive = new GUIStyle(EditorStyles.miniButton)
            {
                normal = { background = tabActive, textColor = text },
                hover = { background = tabActive, textColor = text },
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                fixedHeight = 25f
            };
            TabInactive = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 11,
                fixedHeight = 25f
            };
            TextArea = new GUIStyle(EditorStyles.textArea)
            {
                normal = { background = inputBg, textColor = pro ? new Color(0.93f, 0.94f, 0.96f) : Color.black },
                focused = { background = inputBg, textColor = text },
                wordWrap = true,
                fontSize = 12,
                padding = new RectOffset(8, 8, 6, 6)
            };
            PinBadge = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                normal = { textColor = Color.white }
            };
            CategoryPill = new GUIStyle(PinBadge)
            {
                padding = new RectOffset(6, 6, 2, 2)
            };
            RowHeader = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            SectionTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11
            };
        }

        /// <summary>False once the editor skin changed or the textures were lost (e.g. after a domain reload).</summary>
        public bool IsValid => builtForProSkin == EditorGUIUtility.isProSkin && ownedTextures.TrueForAll(t => t != null);

        public void Dispose()
        {
            foreach (Texture2D t in ownedTextures)
            {
                if (t != null) Object.DestroyImmediate(t);
            }
            ownedTextures.Clear();
        }

        Texture2D Tex(Color color)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, color);
            t.Apply();
            ownedTextures.Add(t);
            return t;
        }
    }
}
