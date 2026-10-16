using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace PlaytestOps.RuntimeStreaming
{
    internal sealed class RuntimeLogForwarder : IDisposable
    {
        private const int MaximumQueued = 256;
        private const int MaximumSourceLength = 8192;
        private const int MaximumForwardedLength = 1800;
        private readonly Queue<Entry> entries = new Queue<Entry>();
        private readonly object queueLock = new object();
        private readonly List<string> secrets = new List<string>();
        private readonly object secretLock = new object();
        private int dropped;
        private bool redactAll;
        private volatile bool disposed;

        internal struct Entry { internal string Level; internal string Message; }

        internal RuntimeLogForwarder(string runtimeToken)
        {
            AddSecret(runtimeToken);
            Application.logMessageReceivedThreaded += Receive;
        }

        internal void AddSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return;
            lock (secretLock)
            {
                if (secret.Length > 512) { redactAll = true; return; }
                if (secrets.Contains(secret)) return;
                // Keep old lease tokens protected too. If unusually frequent lease changes exhaust
                // the bound, suppress game text rather than forget a credential and risk exposing it.
                if (secrets.Count >= 128) redactAll = true;
                else { secrets.Add(secret); secrets.Sort((a, b) => b.Length.CompareTo(a.Length)); }
            }
        }

        internal void Receive(string message, string stackTrace, LogType type)
        {
            if (disposed || (message != null && message.StartsWith("PlaytestOps streaming:", StringComparison.Ordinal))) return;
            message = message ?? string.Empty;
            stackTrace = stackTrace ?? string.Empty;
            var text = message.Length > MaximumSourceLength || stackTrace.Length > MaximumSourceLength
                || message.Length + stackTrace.Length > MaximumSourceLength
                ? "[Game log omitted: source exceeded 8192 characters.]"
                : string.IsNullOrEmpty(stackTrace) ? message : message + "\n" + stackTrace;
            var level = type == LogType.Warning ? "warning" : type == LogType.Error ? "error"
                : type == LogType.Exception ? "exception" : type == LogType.Assert ? "assert" : "info";
            lock (queueLock)
            {
                if (disposed) return;
                if (entries.Count >= MaximumQueued) { Interlocked.Increment(ref dropped); return; }
                entries.Enqueue(new Entry { Level = level, Message = text });
            }
        }

        internal bool TryRead(out Entry entry)
        {
            lock (queueLock)
            {
                if (disposed || entries.Count == 0) { entry = default; return false; }
                entry = entries.Dequeue();
            }
            var text = entry.Message;
            // Redact at the send boundary, including credentials learned after this log was queued.
            lock (secretLock)
            {
                if (redactAll || text.IndexOf("PLAYTESTOPS_STREAM_CONFIG", StringComparison.Ordinal) >= 0)
                    text = "[Streaming configuration or credentials redacted.]";
                else
                    foreach (var secret in secrets) text = text.Replace(secret, "\u0001");
            }
            // A one-character placeholder cannot amplify repeated replacements of short secrets.
            text = text.Replace("\u0001", "[REDACTED]");
            // Sanitize first: truncating before replacement could expose a credential prefix.
            if (text.Length > MaximumForwardedLength)
            {
                var length = MaximumForwardedLength - 13;
                if (char.IsHighSurrogate(text[length - 1])) length--;
                text = text.Substring(0, length) + " [truncated]";
            }
            entry.Message = text;
            return true;
        }

        internal int TakeDropped() => Interlocked.Exchange(ref dropped, 0);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Application.logMessageReceivedThreaded -= Receive;
            lock (queueLock) entries.Clear();
            lock (secretLock) { secrets.Clear(); redactAll = true; }
        }
    }
}
