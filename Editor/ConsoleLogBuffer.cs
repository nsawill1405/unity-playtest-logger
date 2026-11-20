using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PlaytestLoggerTool
{
    /// <summary>Keeps the most recent Unity Console messages so they can be attached to log entries.</summary>
    [InitializeOnLoad]
    static class ConsoleLogBuffer
    {
        const int Capacity = 80;
        static readonly Queue<string> Lines = new Queue<string>(Capacity);

        static ConsoleLogBuffer()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] [{type}] {condition}";
            if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && !string.IsNullOrEmpty(stackTrace))
            {
                line += "\n" + stackTrace.TrimEnd();
            }

            lock (Lines)
            {
                if (Lines.Count >= Capacity) Lines.Dequeue();
                Lines.Enqueue(line);
            }
        }

        public static string[] Snapshot()
        {
            lock (Lines)
            {
                return Lines.ToArray();
            }
        }
    }
}
