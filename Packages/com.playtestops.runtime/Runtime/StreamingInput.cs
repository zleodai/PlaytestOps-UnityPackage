using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.Scripting;

namespace PlaytestOps.RuntimeStreaming
{
    [Preserve, InputControlLayout(displayName = "PlaytestOps Remote Keyboard", canRunInBackground = true)]
    internal sealed class RemoteKeyboard : Keyboard { }

    [Preserve, InputControlLayout(displayName = "PlaytestOps Remote Mouse", canRunInBackground = true)]
    internal sealed class RemoteMouse : Mouse { }

    /// <summary>Optional game-facing adapter. These are dedicated virtual devices, never OS-global input.</summary>
    public static class StreamingInput
    {
        public static Keyboard Keyboard { get; internal set; }
        public static Mouse Mouse { get; internal set; }
        public static bool HasControl { get; internal set; }
        public static long LastAppliedSequence { get; internal set; }
        public static uint LastAppliedDiagnosticId { get; internal set; }
        /// <summary>Runs after an Input System update; game code may pair actions to the exposed devices.</summary>
        public static event Action<long, uint> Applied;
        internal static void Notify(long sequence, uint diagnosticId)
        {
            LastAppliedSequence = sequence;
            LastAppliedDiagnosticId = diagnosticId;
            // An optional game adapter must not prevent safety cleanup or acknowledgments.
            if (Applied == null) return;
            foreach (Action<long, uint> callback in Applied.GetInvocationList())
                try { callback(sequence, diagnosticId); } catch { }
        }
    }

    internal sealed class RuntimeInput : IDisposable
    {
        private const int MaximumPending = 256;
        private const int MaximumPacketBytes = 16384;
        private const long MaximumSequence = 9007199254740991;
        private readonly StreamConfig config;
        private readonly Action<InputReply> reply;
        private readonly ConcurrentQueue<ReceivedPacket> pending = new ConcurrentQueue<ReceivedPacket>();
        private readonly List<InputMessage> appliedThisUpdate = new List<InputMessage>();
        private readonly Dictionary<long, InputReply> acknowledgments = new Dictionary<long, InputReply>();
        private readonly Queue<long> acknowledgmentOrder = new Queue<long>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly int mainThread = Thread.CurrentThread.ManagedThreadId;
        private KeyboardState keyboard;
        private MouseState mouse;
        private string leaseId;
        private string leaseToken;
        private double leaseDeadline;
        private double lastInput;
        private int timeoutMs = 1500;
        private int pendingCount;
        private int overflow;
        private long lastAccepted;
        private double lastEventTime;
        private bool requireSnapshot = true;
        private volatile bool disposed;

        private struct ReceivedPacket { public string Json; public double ReceivedMs; }

        internal RuntimeInput(StreamConfig config, Action<InputReply> reply)
        {
            this.config = config;
            this.reply = reply;
            try
            {
                InputSystem.RegisterLayout<RemoteKeyboard>();
                InputSystem.RegisterLayout<RemoteMouse>();
                StreamingInput.Keyboard = InputSystem.AddDevice<RemoteKeyboard>("PlaytestOps Remote Keyboard");
                StreamingInput.Mouse = InputSystem.AddDevice<RemoteMouse>("PlaytestOps Remote Mouse");
                InputSystem.SetDeviceUsage(StreamingInput.Keyboard, new InternedString("PlaytestOpsRemote"));
                InputSystem.SetDeviceUsage(StreamingInput.Mouse, new InternedString("PlaytestOpsRemote"));
                InputSystem.onBeforeUpdate += BeforeInputUpdate;
                InputSystem.onAfterUpdate += AfterInputUpdate;
            }
            catch
            {
                if (StreamingInput.Keyboard != null && StreamingInput.Keyboard.added) InputSystem.RemoveDevice(StreamingInput.Keyboard);
                if (StreamingInput.Mouse != null && StreamingInput.Mouse.added) InputSystem.RemoveDevice(StreamingInput.Mouse);
                StreamingInput.Keyboard = null;
                StreamingInput.Mouse = null;
                throw;
            }
        }

        internal long LastAppliedSequence => StreamingInput.LastAppliedSequence;
        internal uint LastDiagnosticId => StreamingInput.LastAppliedDiagnosticId;

        internal bool Grant(SignalMessage message)
        {
            if (string.IsNullOrEmpty(message.leaseId) || message.leaseId.Length > 128
                || string.IsNullOrEmpty(message.leaseToken) || message.leaseToken.Length > 512
                || message.timeoutMs < 250 || message.timeoutMs > 1500
                || !TryDeadline(message.expiresUtc, out var deadline))
            {
                Revoke();
                return false;
            }
            Revoke();
            leaseId = message.leaseId;
            leaseToken = message.leaseToken;
            timeoutMs = message.timeoutMs;
            leaseDeadline = deadline;
            lastInput = clock.Elapsed.TotalMilliseconds;
            StreamingInput.HasControl = true;
            return true;
        }

        internal void Renew(SignalMessage message)
        {
            if (leaseId != null && message.leaseId == leaseId && TryDeadline(message.expiresUtc, out var deadline))
                leaseDeadline = deadline;
        }

        private bool TryDeadline(string utc, out double deadline)
        {
            deadline = 0;
            if (!DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires)) return false;
            var remaining = (expires - DateTimeOffset.UtcNow).TotalMilliseconds;
            if (remaining <= 0 || remaining > 10000) return false;
            deadline = clock.Elapsed.TotalMilliseconds + remaining;
            return true;
        }

        internal void Receive(byte[] bytes)
        {
            if (disposed) return;
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumPacketBytes)
            {
                Interlocked.Exchange(ref overflow, 1);
                return;
            }
            if (Interlocked.Increment(ref pendingCount) > MaximumPending)
            {
                Interlocked.Decrement(ref pendingCount);
                Interlocked.Exchange(ref overflow, 1);
                return;
            }
            try
            {
                pending.Enqueue(new ReceivedPacket
                {
                    Json = new UTF8Encoding(false, true).GetString(bytes),
                    ReceivedMs = clock.Elapsed.TotalMilliseconds
                });
            }
            catch
            {
                Interlocked.Decrement(ref pendingCount);
                Interlocked.Exchange(ref overflow, 1);
            }
        }

        internal void Tick()
        {
            if (disposed) return;
            if (Interlocked.Exchange(ref overflow, 0) != 0)
            {
                Revoke();
                Error("queue_overflow", 0);
            }
            var now = clock.Elapsed.TotalMilliseconds;
            if (appliedThisUpdate.Count != 0 && now - appliedThisUpdate[0].receivedMs >= timeoutMs)
            {
                ReleaseAll();
                ClearPending();
                Error("application_timeout", lastAccepted + 1);
            }
            if (leaseId != null && now >= leaseDeadline)
            {
                Revoke();
                Error("lease_expired", 0);
            }
            else if (leaseId != null && now - lastInput >= timeoutMs && !requireSnapshot)
            {
                ReleaseAll();
                ClearPending();
                Error("input_timeout", lastAccepted + 1);
            }
        }

        private void BeforeInputUpdate()
        {
            if (disposed || Thread.CurrentThread.ManagedThreadId != mainThread
                || !InputPhase()) return;
            Tick();
            if (!StreamingInput.Keyboard.enabled || !StreamingInput.Mouse.enabled)
            {
                if (leaseId != null) { Revoke(); Error("input_devices_disabled", 0); }
                return;
            }
            for (var count = 0; count < 64 && pending.TryDequeue(out var packet); count++)
            {
                Interlocked.Decrement(ref pendingCount);
                if (appliedThisUpdate.Count >= MaximumPending)
                {
                    Revoke();
                    Error("application_backpressure", 0);
                    break;
                }
                InputMessage message;
                try { message = JsonUtility.FromJson<InputMessage>(packet.Json); }
                catch { message = null; }
                if (message == null || message.type != "input" || message.sessionId != config.sessionId
                    || message.instanceId != config.instanceId || leaseId == null || message.leaseId != leaseId
                    || !SameSecret(message.leaseToken, leaseToken))
                {
                    // Invalid credentials never renew a lease or affect the authenticated controller's state.
                    Error("unauthorized_input", lastAccepted + 1);
                    continue;
                }
                if (clock.Elapsed.TotalMilliseconds - packet.ReceivedMs > timeoutMs)
                {
                    ReleaseAll();
                    ClearPending();
                    Error("stale_input", lastAccepted + 1);
                    break;
                }
                message.receivedMs = packet.ReceivedMs;
                message.keyboardEventTime = message.mouseEventTime = -1;
                if (!Validate(message))
                {
                    ReleaseAll();
                    Error("invalid_input", lastAccepted + 1);
                    continue;
                }
                if (message.seq <= lastAccepted)
                {
                    if (acknowledgments.TryGetValue(message.seq, out var prior))
                    {
                        reply(new InputReply { type = "input.ack", seq = prior.seq, duplicate = true,
                            clientSentMs = prior.clientSentMs, diagnosticId = prior.diagnosticId,
                            runtimeReceivedMs = prior.runtimeReceivedMs, runtimeAppliedMs = prior.runtimeAppliedMs });
                    }
                    // Duplicates of this update's pending events get the original ack after the update.
                    else if (message.seq <= StreamingInput.LastAppliedSequence) Error("stale_sequence", lastAccepted + 1);
                    continue;
                }
                if (message.seq != lastAccepted + 1)
                {
                    ReleaseAll();
                    if (message.kind != "state" && message.kind != "reset")
                    {
                        Error("sequence_gap", lastAccepted + 1);
                        continue;
                    }
                }
                if (requireSnapshot && message.kind != "state" && message.kind != "reset" && message.kind != "ping")
                {
                    Error("state_required", lastAccepted + 1);
                    continue;
                }
                Apply(message);
                lastAccepted = message.seq;
                lastInput = clock.Elapsed.TotalMilliseconds;
                appliedThisUpdate.Add(message);
            }
        }

        private void Apply(InputMessage message)
        {
            switch (message.kind)
            {
                case "key":
                    keyboard.Set(Keys[message.code], message.down);
                    message.keyboardEventTime = QueueKeyboard();
                    break;
                case "pointer":
                    SetPointer(message);
                    message.mouseEventTime = QueueMouse();
                    break;
                case "state":
                    keyboard = default;
                    if (message.keys != null) foreach (var code in message.keys) keyboard.Set(Keys[code], true);
                    SetPointer(message);
                    mouse.delta = Vector2.zero;
                    mouse.scroll = Vector2.zero;
                    message.keyboardEventTime = QueueKeyboard();
                    message.mouseEventTime = QueueMouse();
                    requireSnapshot = false;
                    break;
                case "reset":
                    keyboard = default;
                    mouse.buttons = 0;
                    mouse.delta = Vector2.zero;
                    mouse.scroll = Vector2.zero;
                    message.keyboardEventTime = QueueKeyboard();
                    message.mouseEventTime = QueueMouse();
                    requireSnapshot = false;
                    break;
            }
        }

        private void SetPointer(InputMessage message)
        {
            mouse.position = new Vector2(Mathf.Clamp01(message.x) * (Screen.width - 1),
                (1f - Mathf.Clamp01(message.y)) * (Screen.height - 1));
            mouse.delta = new Vector2(Mathf.Clamp(message.dx, -4096, 4096), -Mathf.Clamp(message.dy, -4096, 4096));
            // Browser wheels use downward-positive CSS pixels; Unity scroll uses upward-positive units.
            mouse.scroll = new Vector2(Mathf.Clamp(message.wheelX, -1200, 1200), -Mathf.Clamp(message.wheelY, -1200, 1200));
            // DOM back/forward bits are reversed relative to Input System MouseButton.
            mouse.buttons = (ushort)((message.buttons & 7) | ((message.buttons & 8) << 1) | ((message.buttons & 16) >> 1));
        }

        private void AfterInputUpdate()
        {
            if (disposed || Thread.CurrentThread.ManagedThreadId != mainThread
                || !InputPhase()) return;
            if (!StreamingInput.Keyboard.enabled || !StreamingInput.Mouse.enabled)
            {
                Revoke();
                Error("input_devices_disabled", 0);
                return;
            }
            var appliedCount = 0;
            foreach (var message in appliedThisUpdate)
            {
                // Fixed updates can defer current-time events into a later simulation step.
                // A post-update callback alone is not proof that those events were consumed.
                if ((message.keyboardEventTime >= 0 && StreamingInput.Keyboard.lastUpdateTime + 1e-9 < message.keyboardEventTime)
                    || (message.mouseEventTime >= 0 && StreamingInput.Mouse.lastUpdateTime + 1e-9 < message.mouseEventTime)) break;
                var appliedMs = clock.Elapsed.TotalMilliseconds;
                var diagnostic = message.diagnosticId == 0 ? StreamingInput.LastAppliedDiagnosticId : (uint)message.diagnosticId;
                StreamingInput.Notify(message.seq, diagnostic);
                var ack = new InputReply { type = "input.ack", seq = message.seq, clientSentMs = message.clientSentMs,
                    diagnosticId = message.diagnosticId, runtimeReceivedMs = message.receivedMs, runtimeAppliedMs = appliedMs };
                acknowledgments[message.seq] = ack;
                acknowledgmentOrder.Enqueue(message.seq);
                while (acknowledgmentOrder.Count > MaximumPending) acknowledgments.Remove(acknowledgmentOrder.Dequeue());
                reply(ack);
                appliedCount++;
            }
            if (appliedCount != 0) appliedThisUpdate.RemoveRange(0, appliedCount);
        }

        internal void Revoke()
        {
            ReleaseAll();
            ClearPending();
            leaseId = leaseToken = null;
            lastAccepted = 0;
            StreamingInput.LastAppliedSequence = 0;
            StreamingInput.LastAppliedDiagnosticId = 0;
            acknowledgments.Clear();
            acknowledgmentOrder.Clear();
            StreamingInput.HasControl = false;
        }

        private void ReleaseAll()
        {
            keyboard = default;
            mouse = default;
            requireSnapshot = true;
            appliedThisUpdate.Clear();
            if (StreamingInput.Keyboard != null && StreamingInput.Keyboard.added) InputSystem.ResetDevice(StreamingInput.Keyboard);
            if (StreamingInput.Mouse != null && StreamingInput.Mouse.added) InputSystem.ResetDevice(StreamingInput.Mouse);
            // ResetDevice does not remove events already queued in this update. Neutral tail events
            // prevent an earlier queued key/button press from becoming held again after a revoke/gap.
            if (StreamingInput.Keyboard != null && StreamingInput.Keyboard.added) QueueKeyboard();
            if (StreamingInput.Mouse != null && StreamingInput.Mouse.added) QueueMouse();
        }

        private double QueueKeyboard()
        {
            var time = EventTime();
            InputSystem.QueueStateEvent(StreamingInput.Keyboard, keyboard, time);
            return time;
        }

        private double QueueMouse()
        {
            var time = EventTime();
            InputSystem.QueueStateEvent(StreamingInput.Mouse, mouse, time);
            return time;
        }

        private double EventTime()
        {
            lastEventTime = Math.Max(Time.realtimeSinceStartupAsDouble, lastEventTime + 0.000001d);
            return lastEventTime;
        }

        private void ClearPending()
        {
            while (pending.TryDequeue(out _)) Interlocked.Decrement(ref pendingCount);
        }

        private void Error(string reason, long expected) => reply(new InputReply
        {
            type = "input.error", reason = reason, expectedSeq = expected,
            runtimeAppliedMs = clock.Elapsed.TotalMilliseconds
        });

        private static bool Validate(InputMessage message)
        {
            if (message.seq < 1 || message.seq > MaximumSequence || message.diagnosticId < 0 || message.diagnosticId > uint.MaxValue
                || double.IsNaN(message.clientSentMs) || double.IsInfinity(message.clientSentMs) || message.clientSentMs < 0
                || !Finite(message.x) || !Finite(message.y) || !Finite(message.dx) || !Finite(message.dy)
                || !Finite(message.wheelX) || !Finite(message.wheelY) || message.buttons < 0 || message.buttons > 31) return false;
            switch (message.kind)
            {
                case "key": return message.code != null && Keys.ContainsKey(message.code);
                case "pointer": case "reset": case "ping": return true;
                case "state":
                    if (message.keys == null) return true;
                    if (message.keys.Length > 64) return false;
                    foreach (var code in message.keys) if (code == null || !Keys.ContainsKey(code)) return false;
                    return true;
                default: return false;
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool InputPhase() => InputState.currentUpdateType == InputUpdateType.Dynamic
            || InputState.currentUpdateType == InputUpdateType.Fixed || InputState.currentUpdateType == InputUpdateType.Manual;
        private static bool SameSecret(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            var difference = 0;
            for (var i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }

        private static readonly Dictionary<string, Key> Keys = CreateKeys();
        private static Dictionary<string, Key> CreateKeys()
        {
            var keys = new Dictionary<string, Key>(StringComparer.Ordinal);
            for (var c = 'A'; c <= 'Z'; c++) keys.Add("Key" + c, (Key)Enum.Parse(typeof(Key), c.ToString()));
            for (var i = 0; i <= 9; i++)
            {
                keys.Add("Digit" + i, (Key)Enum.Parse(typeof(Key), "Digit" + i));
                keys.Add("Numpad" + i, (Key)Enum.Parse(typeof(Key), "Numpad" + i));
            }
            for (var i = 1; i <= 12; i++) keys.Add("F" + i, (Key)Enum.Parse(typeof(Key), "F" + i));
            var codes = new[] { "Space", "Enter", "Tab", "Backquote", "Quote", "Semicolon", "Comma", "Period", "Slash",
                "Backslash", "BracketLeft", "BracketRight", "Minus", "Equal", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown",
                "ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight",
                "Escape", "Backspace", "CapsLock", "NumLock", "PrintScreen", "ScrollLock", "Pause", "Home", "End", "PageUp",
                "PageDown", "Insert", "Delete", "NumpadEnter", "NumpadDivide", "NumpadMultiply", "NumpadAdd", "NumpadSubtract", "NumpadDecimal" };
            var values = new[] { Key.Space, Key.Enter, Key.Tab, Key.Backquote, Key.Quote, Key.Semicolon, Key.Comma, Key.Period, Key.Slash,
                Key.Backslash, Key.LeftBracket, Key.RightBracket, Key.Minus, Key.Equals, Key.LeftArrow, Key.RightArrow, Key.UpArrow, Key.DownArrow,
                Key.LeftShift, Key.RightShift, Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftMeta, Key.RightMeta,
                Key.Escape, Key.Backspace, Key.CapsLock, Key.NumLock, Key.PrintScreen, Key.ScrollLock, Key.Pause, Key.Home, Key.End,
                Key.PageUp, Key.PageDown, Key.Insert, Key.Delete, Key.NumpadEnter, Key.NumpadDivide, Key.NumpadMultiply, Key.NumpadPlus, Key.NumpadMinus, Key.NumpadPeriod };
            for (var i = 0; i < codes.Length; i++) keys.Add(codes[i], values[i]);
            return keys;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            InputSystem.onBeforeUpdate -= BeforeInputUpdate;
            InputSystem.onAfterUpdate -= AfterInputUpdate;
            Revoke();
            if (StreamingInput.Keyboard != null && StreamingInput.Keyboard.added) InputSystem.RemoveDevice(StreamingInput.Keyboard);
            if (StreamingInput.Mouse != null && StreamingInput.Mouse.added) InputSystem.RemoveDevice(StreamingInput.Mouse);
            StreamingInput.Keyboard = null;
            StreamingInput.Mouse = null;
        }
    }
}
