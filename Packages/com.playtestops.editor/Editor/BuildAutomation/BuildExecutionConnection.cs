using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlaytestOps.Editor
{
    public static partial class EditorConnection
    {
        private static CancellationTokenSource buildDelivery;
        private static void CancelBuildDelivery()
        {
            buildDelivery?.Cancel(); buildDelivery?.Dispose(); buildDelivery = null;
        }
        private static void StartBuildDelivery(ClientWebSocket connection, int currentGeneration, string origin, CancellationToken ct)
        {
            CancelBuildDelivery();
            WindowsBuildExecution.ResumeDelivery(origin);
            buildDelivery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ct = buildDelivery.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await WindowsBuildExecution.DeliverAsync(origin, async (json, token) =>
                    {
                        var bytes = Encoding.UTF8.GetBytes(json);
                        using (var sending = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            sending.CancelAfter(TimeSpan.FromSeconds(10));
                            await SendLock.WaitAsync(sending.Token).ConfigureAwait(false);
                            try
                            {
                                if (currentGeneration != generation || !ReferenceEquals(socket, connection) || connection.State != WebSocketState.Open) throw new OperationCanceledException();
                                await connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sending.Token).ConfigureAwait(false);
                            }
                            finally { SendLock.Release(); }
                        }
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch { if (currentGeneration == generation && ReferenceEquals(socket, connection)) connection.Abort(); }
            }, ct);
        }
    }
}
