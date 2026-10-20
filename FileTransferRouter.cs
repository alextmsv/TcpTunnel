using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel;

internal static class FileTransferRouter
{
    internal static Task RouteAsync(Client sender, string frame, CancellationToken token)
    {
        if (!sender.SupportsFileTransfer || !sender.TryConsumeFileToken() ||
            !FileTransferProtocol.TryBind(frame, sender.Nickname, out string bound) ||
            !FileTransferProtocol.TryParse(bound, out var packet)) return Task.CompletedTask;
        var recipients = NetWorker.broadcaster.GetAuthenticatedClients(sender)
            .Where(peer => peer.SupportsFileTransfer && (packet.Target.Length == 0 || String.Equals(peer.Nickname, packet.Target, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (Client peer in recipients) _ = DeliverAsync(peer, bound, token);
        return Task.CompletedTask;
    }
    private static async Task DeliverAsync(Client peer, string frame, CancellationToken token)
    {
        try { await peer.SendAsync(frame, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { peer.Close(); }
    }
}
