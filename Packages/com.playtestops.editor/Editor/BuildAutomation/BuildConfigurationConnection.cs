using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace PlaytestOps.Editor
{
    public static partial class EditorConnection
    {
        private static bool buildSettingsBusy;
        [Serializable] private sealed class BuildSettingsMessage
        {
            public string type = "build.settings.result", requestId = "";
            public BuildConfigurationSnapshot snapshot;
        }
        private static async void ReplyBuildSettings(Envelope request, ClientWebSocket connection, int currentGeneration, CancellationToken ct)
        {
            if (!Guid.TryParseExact(request.requestId, "N", out _)) return;
            var ownsRequest = !buildSettingsBusy;
            var lockedReload = false;
            try
            {
                BuildConfigurationSnapshot result;
                if (!ownsRequest)
                    result = new BuildConfigurationSnapshot { capturedAtUtc = DateTime.UtcNow.ToString("o"), state = "busy", errorCode = "busy" };
                else
                {
                    buildSettingsBusy = true;
                    if (request.type == "build.settings.set")
                    {
                        // A define/backend change must not reload the domain before its
                        // response is sent. Edits are not replayed on a new connection.
                        EditorApplication.LockReloadAssemblies(); lockedReload = true;
                        result = WindowsBuildConfiguration.Apply(request.revision, request.settingId, request.value);
                    }
                    else result = WindowsBuildConfiguration.Read();
                }
                var response = new BuildSettingsMessage { requestId = request.requestId, snapshot = result };
                var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(response));
                using (var sending = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    sending.CancelAfter(TimeSpan.FromSeconds(10));
                    await SendLock.WaitAsync(sending.Token);
                    try
                    {
                        if (currentGeneration != generation || !ReferenceEquals(socket, connection) || connection.State != WebSocketState.Open) return;
                        await connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sending.Token);
                    }
                    finally { SendLock.Release(); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (currentGeneration == generation && ReferenceEquals(socket, connection)) connection.Abort(); }
            finally
            {
                if (lockedReload) EditorApplication.UnlockReloadAssemblies();
                if (ownsRequest) buildSettingsBusy = false;
            }
        }
    }
}
