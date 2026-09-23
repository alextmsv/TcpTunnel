using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    internal static class WhoisServer
    {
        internal static async Task HandleAsync(Client sender, string message, CancellationToken token)
        {
            if (WhoisProtocol.TryProbe(message, true, out string probe))
            {
                if (sender.Diagnostics.Supported) sender.Diagnostics.ReceivePong(probe);
                return;
            }
            if (sender.Diagnostics.Supported && WhoisProtocol.TrySize(message, out int width, out int height))
            {
                sender.Diagnostics.SetSize(width, height);
                return;
            }
            if (!sender.TryConsumeControlToken()) return;
            if (message == WhoisProtocol.Hello)
            {
                sender.Diagnostics.Supported = true;
                await sender.SendAsync(WhoisProtocol.Capabilities, token).ConfigureAwait(false);
                return;
            }
            if (!sender.Diagnostics.Supported) return;
            if (!WhoisProtocol.TryRequest(message, out string id, out string nick)) return;
            Client target = NetWorker.broadcaster.GetAuthenticatedClients()
                .FirstOrDefault(client => String.Equals(client.Nickname, nick, StringComparison.OrdinalIgnoreCase));
            WhoisInfo info = target == null ? null : Snapshot(target);
            await sender.SendAsync(WhoisProtocol.Reply(id, info), token).ConfigureAwait(false);
            if (target != null && !Object.ReferenceEquals(target, sender) && target.Diagnostics.Supported &&
                target.Diagnostics.ShouldNotify(sender.Nickname))
            {
                try { await target.SendAsync(WhoisProtocol.Notice(sender.Nickname), token).ConfigureAwait(false); }
                catch { target.Close(); }
            }
        }

        internal static WhoisInfo Snapshot(Client target)
        {
            bool hasSnake = target.TryGetSnakeProfile(out SnakeProfile snake);
            var size = target.Diagnostics.GetSize();
            string address = target.IpAddress ?? "";
            bool publicUnavailable = false;
            if (String.Equals(target.Nickname, ServerInterface.AdministratorNickname, StringComparison.OrdinalIgnoreCase) &&
                IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip))
            {
                publicUnavailable = !ServerInterface.DisplayAddressIsPublic;
                address = publicUnavailable ? "" : ServerInterface.DisplayAddress;
            }
            return new WhoisInfo(target.Nickname, address, publicUnavailable, target.Diagnostics.PingMilliseconds,
                size.Width, size.Height, hasSnake ? snake.Enabled : null, snake.Paused,
                hasSnake ? snake.DelayMilliseconds : 75, hasSnake ? (int)snake.Color : 10,
                hasSnake ? snake.Glyph : '-', target.Diagnostics.Messages);
        }

        internal static async Task HeartbeatAsync(Client client, CancellationToken token)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                do
                {
                    await client.SendAsync(WhoisProtocol.Ping(client.Diagnostics.BeginProbe()), token).ConfigureAwait(false);
                } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { client.Close(); }
        }
    }
}
