using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace PlaytestOps.Smoke
{
    public sealed class PlayModeSmokeTests
    {
        [UnityTest, Description("Placeholder: yields a frame and verifies PlayMode result reporting.")]
        public IEnumerator PassingTest()
        {
            yield return null;
            Assert.Pass("PlaytestOps PlayMode smoke test passed.");
        }

        [UnityTest, Description("Placeholder: must not run when the other PlayMode case is selected.")]
        public IEnumerator OtherPassingTest()
        {
            yield return null;
            Assert.Pass();
        }
    }
}
