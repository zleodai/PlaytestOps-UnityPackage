using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PlaytestOps.Editor
{
    // The threaded callback only touches managed data under this lock. Unity APIs,
    // SessionState, report writes and socket snapshots stay on the Editor thread.
    internal static class RunLogCapture
    {
        private const int MaxEntries = 1000;
        private const int MaxCharacters = 192000;
        private const int MaxField = 16000;
        private static readonly object Gate = new object();
        private static readonly List<RunLogEntry> Pending = new List<RunLogEntry>();
        private static bool active;
        private static bool truncated;
        private static int dropped;
        private static int count;
        private static int characters;

        internal static void Begin(RunRecord run)
        {
            lock (Gate)
            {
                Pending.Clear(); truncated = false; dropped = 0;
                run.logs = run.logs ?? new List<RunLogEntry>();
                count = run.logs.Count;
                characters = run.logs.Sum(x => (x.message?.Length ?? 0) + (x.stackTrace?.Length ?? 0));
                active = true;
            }
            Application.logMessageReceivedThreaded -= Capture;
            Application.logMessageReceivedThreaded += Capture;
        }

        private static void Capture(string message, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                if (!active) return;
                if (count >= MaxEntries || characters >= MaxCharacters)
                { truncated = true; if (dropped < int.MaxValue) dropped++; return; }
                var text = Clip(message, Math.Min(MaxField, MaxCharacters - characters));
                characters += text.Length;
                var trace = Clip(stackTrace, Math.Min(MaxField, MaxCharacters - characters));
                characters += trace.Length;
                Pending.Add(new RunLogEntry {
                    sequence = ++count, timestampUtc = DateTime.UtcNow.ToString("O"),
                    level = type == LogType.Log ? "Debug" : type.ToString(),
                    message = text, stackTrace = trace
                });
            }
        }

        private static string Clip(string value, int limit)
        {
            value = value ?? "";
            if (value.Length <= limit) return value;
            truncated = true;
            const string marker = "\n[truncated]";
            var cut = limit >= marker.Length ? limit - marker.Length : limit;
            if (cut > 0 && char.IsHighSurrogate(value[cut - 1])) cut--;
            return value.Substring(0, cut) + (limit >= marker.Length ? marker : "");
        }

        internal static bool Drain(RunRecord run, bool stop = false)
        {
            if (stop) Application.logMessageReceivedThreaded -= Capture;
            lock (Gate)
            {
                if (stop) active = false;
                var changed = Pending.Count > 0 || truncated || dropped > 0;
                run.logs = run.logs ?? new List<RunLogEntry>();
                run.logs.AddRange(Pending); Pending.Clear();
                run.logsTruncated |= truncated; truncated = false;
                run.droppedLogCount = (int)Math.Min(int.MaxValue, (long)run.droppedLogCount + dropped); dropped = 0;
                return changed;
            }
        }
    }
}
