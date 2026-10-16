using NUnit.Framework;
using UnityEngine;

namespace PlaytestOps.RuntimeStreaming.Tests
{
    public sealed class StreamConfigTests
    {
        [TestCase("http://127.0.0.1:5282", "ws")]
        [TestCase("https://localhost:5282", "wss")]
        [TestCase("http://[::1]:5282", "ws")]
        public void ValidLoopbackConfigurationCreatesTokenFreeSocketUri(string origin, string scheme)
        {
            var config = ValidConfig();
            config.origin = origin;

            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out var parsed), Is.True);
            Assert.That(parsed.SocketUri.Scheme, Is.EqualTo(scheme));
            Assert.That(parsed.SocketUri.AbsolutePath, Is.EqualTo("/api/streaming/runtime/test-session/test-instance"));
            Assert.That(parsed.SocketUri.Query, Is.Empty);
            Assert.That(parsed.SocketUri.UserInfo, Is.Empty);
            Assert.That(parsed.SocketUri.AbsoluteUri, Does.Not.Contain(config.token));
        }

        [TestCase("http://192.0.2.1:5282")]
        [TestCase("http://user:password@localhost:5282")]
        [TestCase("http://localhost:5282/unexpected-path")]
        [TestCase("http://localhost:5282/?token=secret")]
        [TestCase("http://localhost:5282/#fragment")]
        [TestCase("file:///localhost")]
        public void UnsafeOriginIsRejected(string origin)
        {
            var config = ValidConfig();
            config.origin = origin;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        [TestCase(64, 64, 1, 100)]
        [TestCase(4096, 4096, 60, 50000)]
        public void InclusiveCaptureLimitsAreAccepted(int width, int height, int frameRate, int bitrate)
        {
            var config = ValidConfig();
            config.width = width;
            config.height = height;
            config.frameRate = frameRate;
            config.maxBitrateKbps = bitrate;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.True);
        }

        [TestCase("width", 63)]
        [TestCase("width", 4097)]
        [TestCase("height", 63)]
        [TestCase("height", 4097)]
        [TestCase("frameRate", 0)]
        [TestCase("frameRate", 61)]
        [TestCase("bitrate", 99)]
        [TestCase("bitrate", 50001)]
        public void OutOfRangeCaptureSettingsAreRejected(string setting, int value)
        {
            var config = ValidConfig();
            switch (setting)
            {
                case "width": config.width = value; break;
                case "height": config.height = value; break;
                case "frameRate": config.frameRate = value; break;
                case "bitrate": config.maxBitrateKbps = value; break;
            }
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("too-short")]
        [TestCase("01234567890123456789012345678901\r\nInjected: value")]
        public void MissingShortOrHeaderInjectingTokenIsRejected(string token)
        {
            var config = ValidConfig();
            config.token = token;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("../other")]
        [TestCase("session?token=secret")]
        public void InvalidRoutingIdentifierIsRejected(string identifier)
        {
            var config = ValidConfig();
            config.sessionId = identifier;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
            config = ValidConfig();
            config.instanceId = identifier;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        [Test]
        public void OversizedConfigurationOrTokenIsRejected()
        {
            var validJson = JsonUtility.ToJson(ValidConfig());
            Assert.That(StreamConfig.TryRead(validJson + new string(' ', 8193 - validJson.Length), out _), Is.False);
            var config = ValidConfig();
            config.token = new string('t', 513);
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        private static StreamConfig ValidConfig() => new StreamConfig
        {
            sessionId = "test-session", instanceId = "test-instance", origin = "http://127.0.0.1:5282",
            token = new string('t', 32), width = 1280, height = 720, frameRate = 30, maxBitrateKbps = 6000
        };
    }
}
