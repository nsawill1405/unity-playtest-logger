using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PlaytestLoggerTool
{
    /// <summary>Reads and writes everything under &lt;ProjectRoot&gt;/PlaytestLogs.</summary>
    static class PlaytestLogStore
    {
        public static string RootDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "PlaytestLogs"));
        public static string JsonPath => Path.Combine(RootDir, "entries.json");
        public static string MarkdownPath => Path.Combine(RootDir, "changes.md");

        public static string NewId(DateTime time) => time.ToString("yyyyMMdd_HHmmss_fff");
        public static string FormatTimestamp(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss");

        // Relative paths (forward slashes) are what gets stored in entries.json and changes.md.
        public static string CleanScreenshotPath(string id) => "Screenshots/" + id + "_clean.png";
        public static string ScreenshotPath(string id) => "Screenshots/" + id + ".png";
        public static string ConsoleLogPath(string id) => "ConsoleLogs/" + id + ".log";

        public static string FullPath(string relativePath) => Path.Combine(RootDir, relativePath);

        public static bool Exists(string relativePath)
        {
            return !string.IsNullOrEmpty(relativePath) && File.Exists(FullPath(relativePath));
        }

        public static void WriteBytes(string relativePath, byte[] bytes)
        {
            string full = FullPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllBytes(full, bytes);
        }

        public static void WriteText(string relativePath, string text)
        {
            string full = FullPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, text);
        }

        public static LogEntryCollection Load()
        {
            if (!File.Exists(JsonPath)) return new LogEntryCollection();

            LogEntryCollection data;
            try
            {
                data = JsonUtility.FromJson<LogEntryCollection>(File.ReadAllText(JsonPath));
            }
            catch (Exception ex)
            {
                // Never overwrite a log we failed to parse: move it aside so the next save can't clobber it.
                string backup = JsonPath + "." + NewId(DateTime.Now) + ".bak";
                File.Move(JsonPath, backup);
                Debug.LogWarning($"[Playtest Logger] Could not read entries.json ({ex.Message}). Moved it to {backup} and started a new log.");
                return new LogEntryCollection();
            }

            if (data == null) data = new LogEntryCollection();
            if (data.entries == null) data.entries = new List<LogEntry>();
            if (Migrate(data)) Save(data);
            return data;
        }

        static bool Migrate(LogEntryCollection data)
        {
            bool changed = false;
            for (int i = 0; i < data.entries.Count; i++)
            {
                LogEntry e = data.entries[i];
                if (string.IsNullOrEmpty(e.id))
                {
                    e.id = "legacy_" + i + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    changed = true;
                }

                string category = PlaytestCategories.Normalize(e.category);
                if (category != e.category)
                {
                    e.category = category;
                    changed = true;
                }

                if (e.markers == null) e.markers = new List<PinMarker>();
                if (!string.IsNullOrEmpty(e.targetElement))
                {
                    if (e.markers.Count == 0)
                    {
                        e.markers.Add(new PinMarker { normalizedPos = new Vector2(0.5f, 0.5f), label = e.targetElement });
                    }
                    e.targetElement = "";
                    changed = true;
                }
            }
            return changed;
        }

        public static void Save(LogEntryCollection data)
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(JsonPath, JsonUtility.ToJson(data, true));
            File.WriteAllText(MarkdownPath, BuildMarkdown(data));
        }

        public static void DeleteEntryFiles(LogEntry entry)
        {
            foreach (string rel in new[] { entry.cleanScreenshotFile, entry.screenshotFile, entry.consoleLogFile })
            {
                if (Exists(rel)) File.Delete(FullPath(rel));
            }
        }

        static string BuildMarkdown(LogEntryCollection data)
        {
            StringBuilder md = new StringBuilder();
            md.AppendLine("# Playtest Change Log");
            md.AppendLine();
            foreach (LogEntry e in data.entries)
            {
                string[] descriptionLines = (string.IsNullOrWhiteSpace(e.description) ? "(No description)" : e.description.Trim())
                    .Replace("\r\n", "\n")
                    .Split('\n');

                md.AppendLine($"- [{(e.resolved ? "x" : " ")}] **[{e.category}]** {descriptionLines[0]}");
                for (int i = 1; i < descriptionLines.Length; i++)
                {
                    md.AppendLine("  " + descriptionLines[i]);
                }

                string details = $"  - Time: `{e.timestamp}` | Scene: `{e.scene}`";
                if (!string.IsNullOrEmpty(e.contextInfo)) details += $" | `{e.contextInfo}`";
                md.AppendLine(details);

                for (int m = 0; m < e.markers.Count; m++)
                {
                    md.AppendLine($"  - Marker #{m + 1}: `{e.markers[m].label}`");
                }
                if (!string.IsNullOrEmpty(e.screenshotFile))
                {
                    md.AppendLine($"  - Screenshot: [{e.screenshotFile}]({e.screenshotFile})");
                }
                if (!string.IsNullOrEmpty(e.consoleLogFile))
                {
                    md.AppendLine($"  - Console Log: [{e.consoleLogFile}]({e.consoleLogFile})");
                }
                md.AppendLine();
            }
            return md.ToString();
        }

        public static string BuildConsoleSnapshot(LogEntry entry)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Playtest Console Snapshot ===");
            sb.AppendLine("Timestamp: " + entry.timestamp);
            sb.AppendLine("Scene: " + entry.scene);
            sb.AppendLine("Context: " + entry.contextInfo);
            for (int i = 0; i < entry.markers.Count; i++)
            {
                sb.AppendLine($"Marker #{i + 1}: {entry.markers[i].label}");
            }
            sb.AppendLine("---------------------------------");
            foreach (string line in ConsoleLogBuffer.Snapshot())
            {
                sb.AppendLine(line);
            }
            return sb.ToString();
        }
    }
}
