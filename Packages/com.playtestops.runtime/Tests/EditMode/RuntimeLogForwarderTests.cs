using NUnit.Framework;
using UnityEngine;

namespace PlaytestOps.RuntimeStreaming.Tests
{
    public sealed class RuntimeLogForwarderTests
    {
        private const string RuntimeToken = "runtime-token-never-forward-0123456789";
        private const string LeaseToken = "lease-token-never-forward-0123456789";
        private const string RedactedMessage = "[Streaming configuration or credentials redacted.]";
        private RuntimeLogForwarder forwarder;

        [SetUp]
        public void SetUp() => forwarder = new RuntimeLogForwarder(RuntimeToken);

        [TearDown]
        public void TearDown() => forwarder.Dispose();

        [TestCase(LogType.Log, "info")]
        [TestCase(LogType.Warning, "warning")]
        [TestCase(LogType.Error, "error")]
        [TestCase(LogType.Exception, "exception")]
        [TestCase(LogType.Assert, "assert")]
        public void PreservesGameLogLevel(LogType type, string expectedLevel)
        {
            forwarder.Receive("Game event", string.Empty, type);

            var entry = Read();
            Assert.That(entry.Level, Is.EqualTo(expectedLevel));
            Assert.That(entry.Message, Is.EqualTo("Game event"));
            Assert.That(forwarder.TryRead(out _), Is.False);
        }

        [Test]
        public void RedactsRuntimeCurrentAndPreviousLeaseTokens()
        {
            const string previousLease = "previous-lease-token-0123456789";
            forwarder.AddSecret(previousLease);
            forwarder.AddSecret(LeaseToken);
            forwarder.Receive("runtime=" + RuntimeToken + "; old=" + previousLease + "; current=" + LeaseToken,
                string.Empty, LogType.Log);

            Assert.That(Read().Message, Is.EqualTo("runtime=[REDACTED]; old=[REDACTED]; current=[REDACTED]"));
        }

        [Test]
        public void RedactsSecretLearnedAfterMessageWasQueued()
        {
            forwarder.Receive("Queued " + LeaseToken, "at Method(" + LeaseToken + ")", LogType.Warning);
            forwarder.AddSecret(LeaseToken);

            var entry = Read();
            Assert.That(entry.Level, Is.EqualTo("warning"));
            Assert.That(entry.Message, Is.EqualTo("Queued [REDACTED]\nat Method([REDACTED])"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConfigurationEnvironmentMentionSuppressesEntireMessage(bool mentionInStack)
        {
            const string sensitive = "PLAYTESTOPS_STREAM_CONFIG contains otherwise-unknown-secret";
            forwarder.Receive(mentionInStack ? "Game message" : sensitive,
                mentionInStack ? sensitive : "Ordinary stack", LogType.Error);

            var entry = Read();
            Assert.That(entry.Level, Is.EqualTo("error"));
            Assert.That(entry.Message, Is.EqualTo(RedactedMessage));
        }

        [Test]
        public void RedactsCredentialsInStackWhilePreservingUsefulStackText()
        {
            forwarder.AddSecret(LeaseToken);
            forwarder.Receive("Game operation failed", "at Connect(" + RuntimeToken + ")\nat Lease(" + LeaseToken + ")",
                LogType.Exception);

            var entry = Read();
            Assert.That(entry.Level, Is.EqualTo("exception"));
            Assert.That(entry.Message, Is.EqualTo("Game operation failed\nat Connect([REDACTED])\nat Lease([REDACTED])"));
        }

        [Test]
        public void CapsForwardedMessageLengthAndMarksTruncation()
        {
            forwarder.Receive(new string('x', 1801), string.Empty, LogType.Log);

            var text = Read().Message;
            Assert.That(text.Length, Is.LessThanOrEqualTo(1800));
            Assert.That(text, Does.EndWith(" [truncated]"));
        }

        [Test]
        public void RedactsSecretCrossingLengthLimitBeforeTruncation()
        {
            forwarder.Receive(new string('x', 1780) + RuntimeToken + new string('x', 100), string.Empty, LogType.Log);

            var text = Read().Message;
            Assert.That(text.Length, Is.LessThanOrEqualTo(1800));
            Assert.That(text, Does.EndWith(" [truncated]"));
            Assert.That(text, Does.Not.Contain("runtime-token"), "Truncation must not expose a credential prefix.");
        }

        [Test]
        public void TruncationDoesNotSplitASurrogatePair()
        {
            forwarder.Receive(new string('x', 1786) + "\U0001F600" + new string('x', 100), string.Empty, LogType.Log);

            var text = Read().Message;
            Assert.That(text.Length, Is.LessThanOrEqualTo(1800));
            Assert.That(text, Does.EndWith(" [truncated]"));
            foreach (var character in text)
                Assert.That(char.IsSurrogate(character), Is.False, "The truncated emoji must not leave a lone surrogate.");
        }

        [TestCase(8193, 0)]
        [TestCase(0, 8193)]
        [TestCase(4096, 4097)]
        public void OversizedMessageOrStackIsOmitted(int messageLength, int stackLength)
        {
            forwarder.Receive(new string('x', messageLength), new string('y', stackLength), LogType.Warning);

            var entry = Read();
            Assert.That(entry.Level, Is.EqualTo("warning"));
            Assert.That(entry.Message, Is.EqualTo("[Game log omitted: source exceeded 8192 characters.]"));
        }

        [Test]
        public void OverflowDropsNewestEntriesAndReportsDropCountOnce()
        {
            for (var index = 0; index < 259; index++)
                forwarder.Receive("event-" + index, string.Empty, LogType.Log);

            Assert.That(forwarder.TakeDropped(), Is.EqualTo(3));
            Assert.That(forwarder.TakeDropped(), Is.Zero);
            for (var index = 0; index < 256; index++)
                Assert.That(Read().Message, Is.EqualTo("event-" + index));
            Assert.That(forwarder.TryRead(out _), Is.False);

            forwarder.Receive("after-drain", string.Empty, LogType.Log);
            Assert.That(Read().Message, Is.EqualTo("after-drain"));
            Assert.That(forwarder.TakeDropped(), Is.Zero);
        }

        [Test]
        public void SuppressesOwnLogPrefixWithoutSuppressingGameMessagesContainingIt()
        {
            forwarder.Receive("PlaytestOps streaming: internal diagnostic", string.Empty, LogType.Error);
            Assert.That(forwarder.TryRead(out _), Is.False);
            Assert.That(forwarder.TakeDropped(), Is.Zero);

            forwarder.Receive("Game mentions PlaytestOps streaming: status", string.Empty, LogType.Log);
            Assert.That(Read().Message, Is.EqualTo("Game mentions PlaytestOps streaming: status"));
        }

        [Test]
        public void DisposalDiscardsQueuedLogsAndRejectsLateLogs()
        {
            forwarder.Receive("queued-before-disposal", string.Empty, LogType.Log);
            forwarder.Dispose();
            Assert.That(forwarder.TryRead(out _), Is.False);

            forwarder.Receive("late-after-disposal", "late-stack", LogType.Exception);
            Assert.That(forwarder.TryRead(out _), Is.False);
            forwarder.Dispose();
            Assert.That(forwarder.TryRead(out _), Is.False);
        }

        [Test]
        public void ShortSecretsDoNotRecursivelyExpandRedactionMarkers()
        {
            forwarder.AddSecret("A");
            forwarder.AddSecret("E");
            forwarder.Receive("AEA", string.Empty, LogType.Log);

            Assert.That(Read().Message, Is.EqualTo("[REDACTED][REDACTED][REDACTED]"));
        }

        [Test]
        public void OverlappingSecretsAreRedactedAsWholeCredentials()
        {
            forwarder.AddSecret("credential");
            forwarder.AddSecret("credential-longer");
            forwarder.Receive("credential-longer credential", string.Empty, LogType.Log);

            Assert.That(Read().Message, Is.EqualTo("[REDACTED] [REDACTED]"));
        }

        [Test]
        public void MoreThan128DistinctSecretsSuppressesQueuedAndFutureGameText()
        {
            // The runtime token is the first of the 128 retained credentials.
            for (var index = 0; index < 127; index++) forwarder.AddSecret("unique-lease-secret-" + index);
            forwarder.Receive("At capacity, ordinary game text remains available", string.Empty, LogType.Log);
            Assert.That(Read().Message, Is.EqualTo("At capacity, ordinary game text remains available"));

            forwarder.Receive("Queued before secret capacity was exceeded", string.Empty, LogType.Warning);
            forwarder.AddSecret("the-129th-distinct-secret");
            forwarder.Receive("New game text after secret capacity was exceeded", string.Empty, LogType.Error);

            var queued = Read();
            var later = Read();
            Assert.That(queued.Message, Is.EqualTo(RedactedMessage));
            Assert.That(queued.Level, Is.EqualTo("warning"));
            Assert.That(later.Message, Is.EqualTo(RedactedMessage));
            Assert.That(later.Level, Is.EqualTo("error"));
        }

        [Test]
        public void RepeatedKnownSecretDoesNotConsumeSecretCapacity()
        {
            for (var index = 0; index < 200; index++) forwarder.AddSecret(RuntimeToken);
            forwarder.Receive("Ordinary game text", string.Empty, LogType.Log);

            Assert.That(Read().Message, Is.EqualTo("Ordinary game text"));
        }

        private RuntimeLogForwarder.Entry Read()
        {
            Assert.That(forwarder.TryRead(out var entry), Is.True, "Expected one forwarded game log.");
            return entry;
        }
    }
}
