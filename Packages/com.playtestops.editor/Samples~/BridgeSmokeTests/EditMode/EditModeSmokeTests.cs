using NUnit.Framework;

namespace PlaytestOps.Smoke
{
    public sealed class EditModeSmokeTests
    {
        [Test, Description("Placeholder: verifies EditMode discovery and result reporting.")]
        public void PassingTest() => Assert.That(2 + 2, Is.EqualTo(4));

        [TestCase(1), TestCase(2), Description("Placeholder: each parameter must be selectable independently.")]
        public void ParameterizedTest(int value) => Assert.That(value, Is.GreaterThan(0));

        [Test, Explicit("Intentional failure; run only when verifying error reporting.")]
        public void IntentionalFailure() => Assert.Fail("PlaytestOps sample failure: this message is expected.");
    }
}
