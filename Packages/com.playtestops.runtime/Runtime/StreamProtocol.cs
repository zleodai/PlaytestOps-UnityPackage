using System;
using UnityEngine;

namespace PlaytestOps.RuntimeStreaming
{
    [Serializable]
    internal sealed class StreamConfig
    {
        public string sessionId;
        public string instanceId;
        public string origin;
        public string token;
        public int width;
        public int height;
        public int frameRate;
        public int maxBitrateKbps;
        public bool diagnostics;

        internal static bool TryRead(string json, out StreamConfig config)
        {
            config = null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) return false;
            try { config = JsonUtility.FromJson<StreamConfig>(json); }
            catch { return false; }
            return config != null && Identifier(config.sessionId) && Identifier(config.instanceId)
                && !string.IsNullOrEmpty(config.token) && config.token.Length >= 32 && config.token.Length <= 512
                && !config.token.Contains("\r") && !config.token.Contains("\n")
                && config.width >= 64 && config.width <= 4096 && config.height >= 64 && config.height <= 4096
                && config.frameRate >= 1 && config.frameRate <= 60
                && config.maxBitrateKbps >= 100 && config.maxBitrateKbps <= 50000
                && Uri.TryCreate(config.origin, UriKind.Absolute, out var uri)
                && (uri.Scheme == "http" || uri.Scheme == "https") && uri.IsLoopback
                && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/";
        }

        internal Uri SocketUri => new UriBuilder(origin)
        {
            Scheme = new Uri(origin).Scheme == "https" ? "wss" : "ws",
            Path = "/api/streaming/runtime/" + Uri.EscapeDataString(sessionId) + "/" + Uri.EscapeDataString(instanceId)
        }.Uri;

        private static bool Identifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (var c in value) if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
            return true;
        }
    }

    [Serializable]
    internal sealed class SignalMessage
    {
        public string type;
        public string sessionId;
        public string instanceId;
        public string sdp;
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
        public string leaseId;
        public string leaseToken;
        public string expiresUtc;
        public int timeoutMs;
        public string reason;
        public string level;
        public string message;
        public int width;
        public int height;
        public long framesCaptured;
        public long lastInputSeq;
        public long lastDiagnosticId;
    }

    [Serializable]
    internal sealed class InputMessage
    {
        public string type;
        public string sessionId;
        public string instanceId;
        public string leaseId;
        public string leaseToken;
        public long seq;
        public double clientSentMs;
        public string kind;
        public string code;
        public bool down;
        public float x;
        public float y;
        public float dx;
        public float dy;
        public float wheelX;
        public float wheelY;
        public int buttons;
        public string[] keys;
        public long diagnosticId;
        [NonSerialized] public double receivedMs;
        [NonSerialized] public double keyboardEventTime;
        [NonSerialized] public double mouseEventTime;
    }

    [Serializable]
    internal sealed class InputReply
    {
        public string type;
        public string sessionId;
        public string instanceId;
        public long seq;
        public double clientSentMs;
        public long diagnosticId;
        public bool duplicate;
        public double runtimeReceivedMs;
        public double runtimeAppliedMs;
        public string reason;
        public long expectedSeq;
    }
}
