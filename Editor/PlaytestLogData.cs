using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlaytestLoggerTool
{
    public enum CaptureSource
    {
        GameView,
        SceneView
    }

    [Serializable]
    public class PinMarker
    {
        public Vector2 normalizedPos; // (0..1, 0..1) from bottom-left
        public string label;

        public PinMarker Clone()
        {
            return new PinMarker { normalizedPos = normalizedPos, label = label };
        }
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
        public CaptureSource captureSource;
        public string targetElement; // Legacy (v1.0); migrated into markers on load.
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

    public static class PlaytestCategories
    {
        // Never put '/' in names: Unity popups treat it as a submenu separator.
        public const string UI = "UI & HUD";
        public const string Todo = "Todo";
        public const string Bug = "Bug";
        public const string Physics = "Physics & Controls";
        public const string Camera = "Camera";
        public const string Visuals = "Visuals & Shaders";
        public const string Level = "Level & Map";

        struct Definition
        {
            public string name;
            public Color color;
            public string[] legacyPrefixes;
        }

        static readonly Definition[] Definitions =
        {
            new Definition { name = UI, color = new Color(0.16f, 0.56f, 0.76f), legacyPrefixes = new[] { "UI" } },
            new Definition { name = Todo, color = new Color(0.76f, 0.52f, 0.16f), legacyPrefixes = new[] { "Todo" } },
            new Definition { name = Bug, color = new Color(0.80f, 0.26f, 0.28f), legacyPrefixes = new[] { "Bug" } },
            new Definition { name = Physics, color = new Color(0.52f, 0.34f, 0.78f), legacyPrefixes = new[] { "Physics" } },
            new Definition { name = Camera, color = new Color(0.24f, 0.46f, 0.80f), legacyPrefixes = new[] { "Camera" } },
            new Definition { name = Visuals, color = new Color(0.18f, 0.62f, 0.52f), legacyPrefixes = new[] { "Visuals" } },
            new Definition { name = Level, color = new Color(0.28f, 0.62f, 0.32f), legacyPrefixes = new[] { "Level", "Map" } },
        };

        public static readonly string[] Names = Array.ConvertAll(Definitions, d => d.name);

        public static int IndexOf(string category)
        {
            return Mathf.Max(0, Array.IndexOf(Names, Normalize(category)));
        }

        public static Color ColorOf(string category)
        {
            return Definitions[IndexOf(category)].color;
        }

        /// <summary>Maps older or unknown category strings onto the current set (falls back to Todo).</summary>
        public static string Normalize(string category)
        {
            if (string.IsNullOrEmpty(category)) return Todo;
            foreach (Definition d in Definitions)
            {
                if (d.name == category) return d.name;
            }
            foreach (Definition d in Definitions)
            {
                foreach (string prefix in d.legacyPrefixes)
                {
                    if (category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return d.name;
                }
            }
            return Todo;
        }
    }
}
