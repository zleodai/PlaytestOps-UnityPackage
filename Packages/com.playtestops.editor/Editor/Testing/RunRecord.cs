using System;
using System.Collections.Generic;

namespace PlaytestOps.Editor
{
    [Serializable]
    public sealed class RunRecord
    {
        public string id;
        public string dashboardRunId;
        public int revision;
        public string unityJobId;
        public TestCase test;
        public string lifecycle;
        public string outcome;
        public string startedUtc;
        public string finishedUtc;
        public double durationSeconds;
        public string message;
        public string stackTrace;
        public string output;
        public string rootId;
        public bool leafReceived;
        public List<RunLogEntry> logs = new List<RunLogEntry>();
        public bool logsTruncated;
        public int droppedLogCount;
        public bool IsActive => lifecycle == "Starting" || lifecycle == "Running";
    }

    [Serializable]
    public sealed class RunLogEntry
    {
        public int sequence;
        public string timestampUtc;
        public string level;
        public string message;
        public string stackTrace;
    }
}
