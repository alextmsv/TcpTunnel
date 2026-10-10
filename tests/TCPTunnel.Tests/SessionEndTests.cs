using TCPTunnel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

internal static class SessionEndTests
{
    public static async Task<bool> PendingKickOverTcpSelfTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var accepted = listener.AcceptTcpClientAsync(timeout.Token);
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var server = await accepted;
            var end = new SessionEndState();
            NetWorker.connected = true;
            var method = typeof(UserInterface).GetMethod("ReceiveMessagesAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            Task receiver = (Task)method.Invoke(null, new object[] { client, client.GetStream(), timeout.Token, end, new HubStatusSession() })!;
            end.RecordFailure(SessionEndKind.ConnectionLost);
            Task drain = end.DrainReceiverAsync(receiver);
            await MessageProtocol.WriteStringAsync(server.GetStream(),
                SystemMessageProtocol.Create(SystemMessageKind.Kicked, "pending kick"), timeout.Token);
            await drain.WaitAsync(timeout.Token);
            await receiver.WaitAsync(timeout.Token);
            return end.Kind == SessionEndKind.ServerReason && end.Message.Contains("pending kick") && !NetWorker.connected;
        }
        finally { listener.Stop(); NetWorker.connected = false; }
    }

    public static async Task<bool> PendingServerReasonSelfTest()
    {
        var end = new SessionEndState();
        end.RecordFailure(SessionEndKind.ConnectionLost);
        var receiver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task drain = end.DrainReceiverAsync(receiver.Task);
        if (drain.IsCompleted) return false;
        end.RecordServerReason(SystemMessageKind.Kicked, "pending kick");
        receiver.SetResult();
        await drain;
        return end.Message == "pending kick";
    }

    public static bool DisconnectReasonPrioritySelfTest()
    {
        var end = new SessionEndState();
        end.RecordFailure(SessionEndKind.ConnectionLost);
        end.RecordServerReason(SystemMessageKind.Kicked, "reason");
        end.RecordFailure(SessionEndKind.ReadTimedOut);
        end.RecordFailure(SessionEndKind.ConnectionLost);
        return end.RequiresAcknowledgement && end.Kind == SessionEndKind.ServerReason &&
               end.Message == "reason";
    }

    public static bool VoluntaryDisconnectSelfTest()
    {
        var end = new SessionEndState();
        end.Leave();
        end.RecordFailure(SessionEndKind.ConnectionLost);
        end.RecordServerReason(SystemMessageKind.Kicked, "late");
        var other = new SessionEndState();
        other.RecordServerReason(SystemMessageKind.TooManyMessages, "limit");
        other.Leave();
        return !end.RequiresAcknowledgement && end.Kind == SessionEndKind.Voluntary &&
               other.RequiresAcknowledgement && other.Message == "limit";
    }

    public static bool NonTerminalImageWarningSelfTest()
    {
        var end = new SessionEndState();
        end.RecordServerReason(SystemMessageKind.InvalidImage, "bad image");
        end.RecordServerReason(SystemMessageKind.TooManyImages, "image rate");
        return end.Kind == SessionEndKind.Active && !end.RequiresAcknowledgement;
    }

    public static bool EncryptionErrorReasonSelfTest()
    {
        var end = new SessionEndState();
        end.RecordFailure(SessionEndKind.ConnectionLost);
        string message = Lang.Get(TextId.CryptoInvalidPacket);
        end.RecordServerReason(SystemMessageKind.EncryptionError, message);
        end.RecordFailure(SessionEndKind.ReadTimedOut);
        return end.Kind == SessionEndKind.ServerReason && end.RequiresAcknowledgement && end.Message == message;
    }
}
