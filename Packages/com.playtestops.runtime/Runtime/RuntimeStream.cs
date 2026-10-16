using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.WebRTC;

namespace PlaytestOps.RuntimeStreaming
{
    [DefaultExecutionOrder(-10000)]
    internal sealed class RuntimeStream : MonoBehaviour
    {
        private const string ConfigVariable = "PLAYTESTOPS_STREAM_CONFIG";
        private StreamConfig config;
        private RuntimeSignaling signaling;
        private RuntimeInput input;
        private RuntimeLogForwarder gameLogs;
        private RTCPeerConnection peer;
        private RTCDataChannel channel;
        private VideoStreamTrack video;
        private RTCRtpSender sender;
        private RenderTexture framebuffer;
        private readonly Queue<SignalMessage> remoteCandidates = new Queue<SignalMessage>();
        private bool started;
        private bool stopping;
        private bool cleaned;
        private bool hasAnswer;
        private bool answerPending;
        private bool oldRunInBackground;
        private bool changedBackgroundPolicy;
        private int peerGeneration;
        private double nextHeartbeat;
        private double peerDeadline;
        private double nextStats;
        private double nextLogDrain;
        private long framesCaptured;
        private bool statsPending;
        private string deferredFailure;
        private string deferredPeerFailure;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Application.isEditor) return;
            var json = Environment.GetEnvironmentVariable(ConfigVariable);
            if (string.IsNullOrEmpty(json)) return;
            Environment.SetEnvironmentVariable(ConfigVariable, null);
            if (!StreamConfig.TryRead(json, out var configuration))
            {
                Debug.LogError("PlaytestOps streaming: invalid launch configuration.");
                return;
            }
            var owner = new GameObject("PlaytestOps Runtime Streaming");
            DontDestroyOnLoad(owner);
            owner.AddComponent<RuntimeStream>().config = configuration;
        }

        private void Start()
        {
            try
            {
#if !ENABLE_INPUT_SYSTEM
                throw new InvalidOperationException();
#else
                if (Application.platform != RuntimePlatform.WindowsPlayer || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException();
                oldRunInBackground = Application.runInBackground;
                Application.runInBackground = true;
                changedBackgroundPolicy = true;
                gameLogs = new RuntimeLogForwarder(config.token);
                signaling = new RuntimeSignaling();
                signaling.Connect(config.SocketUri, config.token);
                config.token = null;
                input = new RuntimeInput(config, SendInputReply);
                StartCoroutine(Guard(InitializeCapture(), "capture.initialization_failed"));
#endif
            }
            catch { Fail("runtime.unsupported_or_initialization_failed"); }
        }

        private IEnumerator InitializeCapture()
        {
            // Let an externally configured player window settle before validating its native framebuffer.
            yield return new WaitForEndOfFrame();
            if (Screen.width != config.width || Screen.height != config.height)
            {
                Fail("capture.resolution_mismatch");
                yield break;
            }
            var descriptor = new RenderTextureDescriptor(Screen.width, Screen.height)
            {
                graphicsFormat = WebRTC.GetSupportedGraphicsFormat(SystemInfo.graphicsDeviceType),
                depthBufferBits = 0, msaaSamples = 1, useMipMap = false, autoGenerateMips = false
            };
            framebuffer = new RenderTexture(descriptor) { name = "PlaytestOps Native Framebuffer" };
            if (!framebuffer.Create()) throw new InvalidOperationException();
            // ScreenCapture already yields the required orientation. WebRTC's default copier flips
            // camera textures vertically; copying this final framebuffer unchanged keeps UI, pixels,
            // diagnostic coordinates, and normalized input coordinates in the same top-left space.
            video = new VideoStreamTrack(framebuffer, (source, destination) => Graphics.Blit(source, destination));
            StartCoroutine(Guard(CaptureFrames(), "capture.failed"));
        }

        private IEnumerator CaptureFrames()
        {
            // WebRTC.Update waits for end of frame itself. Advance its public enumerator immediately
            // AFTER capture to enforce capture -> track copy ordering, avoiding a coroutine-order race.
            var pump = WebRTC.Update();
            pump.MoveNext();
            var nextCapture = 0d;
            try
            {
                while (!stopping)
                {
                    yield return pump.Current;
                    if (Screen.width != config.width || Screen.height != config.height)
                    {
                        Fail("capture.resolution_changed");
                        yield break;
                    }
                    var now = Time.realtimeSinceStartupAsDouble;
                    if (now < nextCapture) continue;
                    var interval = 1d / config.frameRate;
                    nextCapture += interval;
                    if (nextCapture <= now) nextCapture = now + interval;
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(framebuffer);
                    framesCaptured++;
                    var active = RenderTexture.active;
                    try { if (!pump.MoveNext()) throw new InvalidOperationException(); }
                    finally { RenderTexture.active = active; }
                }
            }
            finally { (pump as IDisposable)?.Dispose(); }
        }

        private void Update()
        {
            if (stopping) return;
            try
            {
                input?.Tick();
                if (deferredFailure != null) { Fail(deferredFailure); return; }
                if (deferredPeerFailure != null)
                {
                    input.Revoke();
                    ClosePeer();
                    Log("warning", deferredPeerFailure);
                    deferredPeerFailure = null;
                }
                if (signaling == null) return;
                if (signaling.Failure != null) { Fail(signaling.Failure); return; }
                if (!signaling.Connected || video == null || framesCaptured == 0) return;
                if (!started)
                {
                    started = true;
                    Send(new SignalMessage { type = "runtime.ready", width = Screen.width, height = Screen.height });
                    Log("info", "Runtime ready: framebuffer capture; WebRTC 3.0.0; dedicated Input System devices.");
                }
                for (var i = 0; i < 64 && signaling.TryReceive(out var json); i++)
                {
                    SignalMessage message;
                    try { message = JsonUtility.FromJson<SignalMessage>(json); }
                    catch { Fail("signaling.invalid_message"); return; }
                    if (message == null || message.sessionId != config.sessionId || message.instanceId != config.instanceId)
                    { Fail("signaling.identity_mismatch"); return; }
                    HandleSignal(message);
                    if (stopping) return;
                }
                var now = Time.realtimeSinceStartupAsDouble;
                if (now >= nextLogDrain)
                {
                    nextLogDrain = now + 0.5;
                    var budget = 8;
                    if (signaling.CanSendLogs)
                    {
                        var dropped = gameLogs.TakeDropped();
                        if (dropped != 0) { Log("warning", "Game log queue dropped " + dropped + " messages while full."); budget--; }
                    }
                    for (var i = 0; i < budget && signaling.CanSendLogs && gameLogs.TryRead(out var entry); i++)
                        Log(entry.Level, entry.Message);
                }
                if (now >= nextHeartbeat)
                {
                    nextHeartbeat = now + 1;
                    Send(new SignalMessage { type = "runtime.heartbeat", width = Screen.width, height = Screen.height,
                        framesCaptured = framesCaptured, lastInputSeq = input.LastAppliedSequence, lastDiagnosticId = input.LastDiagnosticId });
                }
                if (peer != null && peer.ConnectionState != RTCPeerConnectionState.Connected && now > peerDeadline)
                { input.Revoke(); ClosePeer(); Log("warning", "peer.negotiation_timeout"); return; }
                if (peer != null && peer.ConnectionState == RTCPeerConnectionState.Connected && !statsPending && now >= nextStats)
                {
                    nextStats = now + 2;
                    StartCoroutine(Guard(CheckStats(peer, peerGeneration), "peer.stats_failed"));
                }
            }
            catch { Fail("runtime.processing_failed"); }
        }

        private void HandleSignal(SignalMessage message)
        {
            switch (message.type)
            {
                case "viewer.ready":
                    StartPeer();
                    break;
                case "signal.answer":
                    if (peer == null || hasAnswer || answerPending || string.IsNullOrEmpty(message.sdp) || message.sdp.Length > 60000)
                    { Fail("peer.invalid_answer"); return; }
                    answerPending = true;
                    StartCoroutine(Guard(ApplyAnswer(message.sdp, peer, peerGeneration), "peer.answer_failed"));
                    break;
                case "signal.ice":
                    if (peer == null || string.IsNullOrEmpty(message.candidate)) return;
                    if (message.candidate.Length > 4096 || message.sdpMid?.Length > 256 || message.sdpMLineIndex < 0 || message.sdpMLineIndex > 16)
                    { Fail("peer.invalid_ice"); return; }
                    if (hasAnswer) AddCandidate(message);
                    else if (remoteCandidates.Count < 128) remoteCandidates.Enqueue(message);
                    else Fail("peer.ice_overflow");
                    break;
                case "control.granted":
                    gameLogs.AddSecret(message.leaseToken);
                    // The browser receives its grant only after this main-thread installation ack,
                    // so its data channel cannot race the runtime's signaling socket at lease startup.
                    if (input.Grant(message))
                        Send(new SignalMessage { type = "control.ready", leaseId = message.leaseId });
                    break;
                case "control.renewed": input.Renew(message); break;
                case "control.revoked": input.Revoke(); break;
                case "viewer.closed": input.Revoke(); ClosePeer(); break;
                case "session.stop":
                    stopping = true;
                    input.Revoke();
                    StartCoroutine(FinishStop(true));
                    break;
            }
        }

        private void StartPeer()
        {
            ClosePeer();
            input.Revoke();
            hasAnswer = answerPending = false;
            peer = new RTCPeerConnection(); // Local-only MVP: no STUN/TURN credentials or external candidate servers.
            var local = peer;
            var generation = peerGeneration;
            peerDeadline = Time.realtimeSinceStartupAsDouble + 20;
            peer.OnIceCandidate = candidate =>
            {
                if (candidate == null) return;
                using (candidate)
                {
                    if (stopping || local != peer || generation != peerGeneration) return;
                    Send(new SignalMessage { type = "signal.ice", candidate = candidate.Candidate,
                        sdpMid = candidate.SdpMid, sdpMLineIndex = candidate.SdpMLineIndex ?? 0 });
                }
            };
            peer.OnConnectionStateChange = state =>
            {
                if (local != peer || stopping) return;
                if (state == RTCPeerConnectionState.Disconnected || state == RTCPeerConnectionState.Failed
                    || state == RTCPeerConnectionState.Closed)
                {
                    input.Revoke();
                    deferredPeerFailure = "peer.disconnected";
                }
            };
            sender = peer.AddTrack(video);
            channel = peer.CreateDataChannel("playtestops-input", new RTCDataChannelInit { ordered = true });
            channel.OnMessage = bytes => { if (!stopping && local == peer) input.Receive(bytes); };
            channel.OnClose = () => { if (!stopping && local == peer) { input.Revoke(); deferredPeerFailure = "input.channel_closed"; } };
            channel.OnError = _ => { if (!stopping && local == peer) deferredPeerFailure = "input.channel_failed"; };
            StartCoroutine(Guard(CreateOffer(local, generation), "peer.offer_failed"));
        }

        private IEnumerator CreateOffer(RTCPeerConnection local, int generation)
        {
            var offer = local.CreateOffer();
            var deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (!offer.IsDone && Current(local, generation) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            if (!Current(local, generation)) yield break;
            if (!offer.IsDone || offer.IsError) throw new InvalidOperationException();
            var description = offer.Desc;
            var setting = local.SetLocalDescription(ref description);
            deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (!setting.IsDone && Current(local, generation) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            if (!Current(local, generation)) yield break;
            if (!setting.IsDone || setting.IsError) throw new InvalidOperationException();
            Send(new SignalMessage { type = "signal.offer", sdp = description.sdp });
        }

        private IEnumerator ApplyAnswer(string sdp, RTCPeerConnection local, int generation)
        {
            var description = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
            var setting = local.SetRemoteDescription(ref description);
            var deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (!setting.IsDone && Current(local, generation) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            if (!Current(local, generation)) yield break;
            if (!setting.IsDone || setting.IsError) throw new InvalidOperationException();
            hasAnswer = true;
            answerPending = false;
            var parameters = sender.GetParameters();
            if (parameters.encodings == null || parameters.encodings.Length == 0) throw new InvalidOperationException();
            foreach (var encoding in parameters.encodings)
            {
                encoding.maxBitrate = (ulong)config.maxBitrateKbps * 1000;
                encoding.maxFramerate = (uint)config.frameRate;
                encoding.scaleResolutionDownBy = 1d;
            }
            if (sender.SetParameters(parameters).errorType != RTCErrorType.None) throw new InvalidOperationException();
            while (remoteCandidates.Count != 0) AddCandidate(remoteCandidates.Dequeue());
        }

        private void AddCandidate(SignalMessage message)
        {
            using (var candidate = new RTCIceCandidate(new RTCIceCandidateInit
            {
                candidate = message.candidate, sdpMid = message.sdpMid, sdpMLineIndex = message.sdpMLineIndex
            }))
                if (!peer.AddIceCandidate(candidate)) Fail("peer.ice_rejected");
        }

        private IEnumerator CheckStats(RTCPeerConnection local, int generation)
        {
            statsPending = true;
            try
            {
                var operation = local.GetStats();
                var deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (!operation.IsDone && Current(local, generation) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                if (!Current(local, generation))
                {
                    if (operation.IsDone && !operation.IsError) operation.Value?.Dispose();
                    yield break;
                }
                if (!operation.IsDone || operation.IsError) throw new InvalidOperationException();
                using (var report = operation.Value)
                {
                    if (!Current(local, generation)) yield break;
                    foreach (var statistic in report.Stats.Values)
                    {
                        if (!(statistic is RTCOutboundRTPStreamStats outbound) || outbound.kind != "video") continue;
                        if ((outbound.frameWidth != 0 && outbound.frameWidth != config.width)
                            || (outbound.frameHeight != 0 && outbound.frameHeight != config.height))
                        { Fail("video.encoded_resolution_changed"); yield break; }
                        if (config.diagnostics)
                            Log("info", "Video: " + outbound.frameWidth + "x" + outbound.frameHeight
                                + "; encoded=" + outbound.framesEncoded + "; sent=" + outbound.framesSent
                                + "; bytes=" + outbound.bytesSent + "; inputSeq=" + input.LastAppliedSequence
                                + "; diagnosticId=" + input.LastDiagnosticId + ".");
                    }
                }
            }
            finally { if (generation == peerGeneration) statsPending = false; }
        }

        private bool Current(RTCPeerConnection local, int generation) => !stopping && local == peer && generation == peerGeneration;

        private void SendInputReply(InputReply message)
        {
            if (stopping || channel == null) return;
            try
            {
                if (channel.ReadyState != RTCDataChannelState.Open) return;
                if (channel.BufferedAmount > 262144) { deferredFailure = "input.ack_backpressure"; return; }
                message.sessionId = config.sessionId;
                message.instanceId = config.instanceId;
                channel.Send(JsonUtility.ToJson(message));
            }
            catch { deferredFailure = "input.ack_failed"; }
        }

        private void Send(SignalMessage message)
        {
            message.sessionId = config.sessionId;
            message.instanceId = config.instanceId;
            if (signaling != null && signaling.Connected && !signaling.Send(JsonUtility.ToJson(message)))
                deferredFailure = "signaling.send_failed";
        }

        private void Log(string level, string message)
        {
            if (signaling == null || !signaling.Connected) return;
            signaling.SendLog(JsonUtility.ToJson(new SignalMessage { type = "runtime.log", sessionId = config.sessionId,
                instanceId = config.instanceId, level = level, message = message }));
        }

        private IEnumerator Guard(IEnumerator routine, string error)
        {
            try
            {
                while (!stopping)
                {
                    bool next;
                    try { next = routine.MoveNext(); }
                    catch { Fail(error); yield break; }
                    if (!next) yield break;
                    yield return routine.Current;
                }
            }
            finally { (routine as IDisposable)?.Dispose(); }
        }

        private void Fail(string reason)
        {
            if (stopping) return;
            stopping = true;
            input?.Revoke();
            Send(new SignalMessage { type = "stream.error", message = reason });
            Debug.LogError("PlaytestOps streaming: " + reason);
            StartCoroutine(FinishStop(true));
        }

        private IEnumerator FinishStop(bool quit)
        {
            ClosePeer();
            yield return new WaitForSecondsRealtime(0.2f);
            Cleanup();
            if (quit) Application.Quit(0);
            Destroy(gameObject);
        }

        private void ClosePeer()
        {
            peerGeneration++;
            var oldPeer = peer;
            var oldChannel = channel;
            peer = null;
            channel = null;
            statsPending = false;
            if (oldChannel != null)
            {
                oldChannel.OnMessage = null; oldChannel.OnClose = null; oldChannel.OnError = null;
                try { oldChannel.Close(); } catch { }
                try { oldChannel.Dispose(); } catch { }
            }
            if (oldPeer != null)
            {
                oldPeer.OnIceCandidate = null; oldPeer.OnConnectionStateChange = null;
                try { oldPeer.Close(); } catch { }
                try { oldPeer.Dispose(); } catch { }
            }
            sender = null;
            remoteCandidates.Clear();
        }

        private void OnGUI()
        {
            if (config != null && config.diagnostics && !stopping) DiagnosticMarker.Draw(StreamingInput.LastAppliedDiagnosticId);
        }

        private void OnApplicationQuit() => Cleanup();
        private void OnDestroy() => Cleanup();
        private void Cleanup()
        {
            if (cleaned) return;
            cleaned = stopping = true;
            try { gameLogs?.Dispose(); } catch { }
            gameLogs = null;
            StopAllCoroutines();
            try { input?.Dispose(); } catch { }
            input = null;
            ClosePeer();
            try { video?.Dispose(); } catch { }
            video = null;
            if (framebuffer != null) { framebuffer.Release(); Destroy(framebuffer); framebuffer = null; }
            try { signaling?.Dispose(); } catch { }
            signaling = null;
            if (config != null) config.token = null;
            if (changedBackgroundPolicy) Application.runInBackground = oldRunInBackground;
        }
    }
}
