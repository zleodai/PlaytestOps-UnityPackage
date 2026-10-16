using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.TestTools.TestRunner.Api;

namespace PlaytestOps.Editor
{
    [Serializable]
    public sealed class TestCase
    {
        public string uniqueName;
        public string fullName;
        public string name;
        public string description;
        public string assembly;
        public string mode;
        public string runState;
        public string skipReason;

        public bool IsRunnable => runState == "Runnable" || runState == "Explicit";
        public string Key => mode + ":" + uniqueName;
    }

    public static class TestCatalog
    {
        public static List<TestCase> Flatten(ITestAdaptor root, TestMode mode)
        {
            var result = new List<TestCase>();
            Visit(root, mode, "", result);
            return result.OrderBy(x => x.fullName, StringComparer.Ordinal).ToList();
        }

        private static void Visit(ITestAdaptor node, TestMode mode, string assembly, List<TestCase> result)
        {
            if (node == null) return;
            if (node.IsTestAssembly)
                assembly = node.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? node.Name.Substring(0, node.Name.Length - 4) : node.Name;
            if (!node.IsSuite)
                result.Add(new TestCase {
                    uniqueName = node.UniqueName, fullName = node.FullName, name = node.Name,
                    description = node.Description ?? "", assembly = assembly, mode = mode.ToString(),
                    runState = node.RunState.ToString(), skipReason = node.SkipReason ?? ""
                });
            if (node.HasChildren)
                foreach (var child in node.Children) Visit(child, mode, assembly, result);
        }

        // Re-resolve against fresh discovery: an empty or ambiguous filter must never run a suite.
        public static TestCase Resolve(IEnumerable<TestCase> catalog, string key)
        {
            var cases = catalog.ToList();
            var matches = cases.Where(x => x.Key == key).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("The selected test is missing or ambiguous. Refresh discovery.");
            var selected = matches[0];
            if (!selected.IsRunnable) throw new InvalidOperationException("This test cannot run: " + selected.runState + ". " + selected.skipReason);
            if (string.IsNullOrEmpty(selected.fullName) || string.IsNullOrEmpty(selected.assembly) || string.IsNullOrEmpty(selected.uniqueName))
                throw new InvalidOperationException("The test has incomplete selection metadata.");
            if (cases.Count(x => x.mode == selected.mode && x.assembly == selected.assembly && x.fullName == selected.fullName) != 1)
                throw new InvalidOperationException("Multiple tests share this execution selector; selecting one safely is not possible.");
            return selected;
        }
    }
}
