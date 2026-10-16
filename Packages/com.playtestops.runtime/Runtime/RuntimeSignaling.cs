using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlaytestOps.RuntimeStreaming
{
    // No Unity or WebRTC APIs run on this worker. Both queues and every message are bounded.
    internal sealed class RuntimeSignaling : IDisposable
    {
        private const int MaximumMessageBytes = 65536;
        private const int MaximumQueue = 128;
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<byte[]> outgoing = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim available = new SemaphoreSlim(0);
        private int incomingCount;
        private int outgoingCount;
        private int disposed;
        private volatile bool connected;
        private volatile string failure;

        public bool Connected => connected;
        public string Failure => failure;
        public bool CanSendLogs => connected && disposed == 0 && Volatile.Read(ref outgoingCount) < MaximumQueue / 2;

        public void Connect(Uri uri, string token)
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            _ = Run(uri);
        }

        private async Task Run(Uri uri)
        {
            try
            {
                using (var opening = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    opening.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.ConnectAsync(uri, opening.Token).ConfigureAwait(false);
                }
                connected = true;
                var sender = SendLoop();
                var receiver = ReceiveLoop();
                await Task.WhenAny(sender, receiver).ConfigureAwait(false);
                stop.Cancel();
                socket.Abort();
                await Task.WhenAll(sender, receiver).ConfigureAwait(false);
                if (disposed == 0) failure = "signaling.closed";
            }
            catch (Exception)
            {
                // Exception strings can include URLs, headers, or remote data. Never forward them.
                if (disposed == 0) failure = "signaling.connection_failed";
            }
            finally { connected = false; }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[4096];
            var utf8 = new UTF8Encoding(false, true);
            while (!stop.IsCancellationRequested)
            {
                using (var message = new MemoryStream())
                {
                    WebSocketReceiveResult part;
                    do
                    {
                        part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token).ConfigureAwait(false);
                        if (part.MessageType == WebSocketMessageType.Close) return;
                        if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > MaximumMessageBytes)
                            throw new InvalidDataException();
                        message.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);
                    if (Interlocked.Increment(ref incomingCount) > MaximumQueue) throw new InvalidDataException();
                    incoming.Enqueue(utf8.GetString(message.ToArray()));
                }
            }
        }

        private async Task SendLoop()
        {
            while (!stop.IsCancellationRequested)
            {
                await available.WaitAsync(stop.Token).ConfigureAwait(false);
                if (!outgoing.TryDequeue(out var bytes)) continue;
                Interlocked.Decrement(ref outgoingCount);
                using (var sending = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    sending.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sending.Token).ConfigureAwait(false);
                }
            }
        }

        public bool TryReceive(out string json)
        {
            if (!incoming.TryDequeue(out json)) return false;
            Interlocked.Decrement(ref incomingCount);
            return true;
        }

        public bool Send(string json) => Enqueue(json, MaximumQueue, true);
        public bool SendLog(string json) => Enqueue(json, MaximumQueue / 2, false);

        private bool Enqueue(string json, int limit, bool critical)
        {
            if (!connected || disposed != 0 || json == null) return false;
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MaximumMessageBytes) return false;
            if (Interlocked.Increment(ref outgoingCount) > limit)
            {
                Interlocked.Decrement(ref outgoingCount);
                if (critical) failure = "signaling.send_overflow";
                return false;
            }
            outgoing.Enqueue(bytes);
            available.Release();
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            connected = false;
            stop.Cancel();
            socket.Abort();
            socket.Dispose();
            // The worker may still be unwinding waits; do not dispose its synchronization primitives here.
        }
    }
}
