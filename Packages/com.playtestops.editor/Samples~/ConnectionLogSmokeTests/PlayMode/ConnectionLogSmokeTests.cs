using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlaytestOps.LogSmoke
{
    public sealed class PlayModeConnectionTests
    {
        [UnityTest, Description("Placeholder: verifies PlayMode/domain reload and all three dashboard log levels.")]
        public IEnumerator DebugWarningErrorReachDashboard()
        {
            Debug.Log("[PlaytestOps PlayMode] Debug placeholder: connection test started.");
            yield return new WaitForSecondsRealtime(0.75f);
            LogAssert.Expect(LogType.Warning, "[PlaytestOps PlayMode] Expected placeholder warning.");
            Debug.LogWarning("[PlaytestOps PlayMode] Expected placeholder warning.");
            yield return new WaitForSecondsRealtime(0.75f);
            LogAssert.Expect(LogType.Error, "[PlaytestOps PlayMode] Expected placeholder error; this test should pass.");
            Debug.LogError("[PlaytestOps PlayMode] Expected placeholder error; this test should pass.");
            Assert.That(Application.isPlaying, Is.True);
            Debug.Log("[PlaytestOps PlayMode] Debug placeholder: connection test completed.");
        }
    }
}
