#nullable disable
using System;

namespace PlaytestOps.Editor
{
    [Serializable] public sealed class VersionControlWorkspace
    {
        public string name = "", repository = "", branch = "", selectorType = "", workspaceType = "";
        public long currentChangeset = -1, headChangeset = -1;
    }
    [Serializable] public sealed class VersionControlPendingItem
    {
        public string status = "", path = "", oldPath = "";
    }
    [Serializable] public sealed class VersionControlChangeset
    {
        public long number;
        public string dateUtc = "", author = "", comment = "", branch = "";
    }
    [Serializable] public sealed class VersionControlPending
    {
        public string state = "unavailable", errorCode = "";
        public VersionControlPendingItem[] items = new VersionControlPendingItem[0];
        public bool truncated;
    }
    [Serializable] public sealed class VersionControlIncoming
    {
        public string state = "unavailable", errorCode = "";
        public VersionControlChangeset[] changesets = new VersionControlChangeset[0];
        public bool truncated;
    }
    [Serializable] public sealed class VersionControlHistory
    {
        public string state = "unavailable", errorCode = "", scope = "repository";
        public VersionControlChangeset[] changesets = new VersionControlChangeset[0];
        public bool hasMore;
        public int offset;
    }
    [Serializable] public sealed class VersionControlSnapshot
    {
        public int protocolVersion = 1;
        public string capturedAtUtc = "", state = "failed", errorCode = "";
        public VersionControlWorkspace workspace = new VersionControlWorkspace();
        public VersionControlPending pending = new VersionControlPending();
        public VersionControlIncoming incoming = new VersionControlIncoming();
        public VersionControlHistory history = new VersionControlHistory();

        public static VersionControlSnapshot Failure(string state, string code, int offset = 0, string scope = "repository")
        {
            return new VersionControlSnapshot {
                capturedAtUtc = DateTime.UtcNow.ToString("O"), state = state, errorCode = code,
                pending = new VersionControlPending { errorCode = code },
                incoming = new VersionControlIncoming { errorCode = code },
                history = new VersionControlHistory { errorCode = code, offset = offset, scope = scope }
            };
        }
    }
}
