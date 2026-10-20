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
            if (sender.Diagnostics.Supported && sender.Transport == ChatTransport.Bluetooth &&
                WhoisProtocol.TrySignal(message, out int signal))
            {
                sender.Diagnostics.SetSignal(signal);
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
            string reply;
            // WARNING: The hidden path must not call Snapshot; it reads real network addresses.
            if (target != null && (sender.HideClientIpAddresses || target.HideClientIpAddresses || !target.IpDisclosureAllowed))
                reply = WhoisProtocol.HiddenReply(id, SnapshotDiagnostics(target));
            else
                reply = WhoisProtocol.Reply(id, target == null ? null : Snapshot(target));
            await sender.SendAsync(reply, token).ConfigureAwait(false);
            if (target != null && !Object.ReferenceEquals(target, sender) && target.Diagnostics.Supported &&
                target.Diagnostics.ShouldNotify(sender.Nickname))
            {
                try { await target.SendAsync(WhoisProtocol.Notice(sender.Nickname), token).ConfigureAwait(false); }
                catch { target.Close(); }
            }
        }

        internal static WhoisInfo Snapshot(Client target)
        {
            WhoisDiagnosticsSnapshot diagnostics = SnapshotDiagnostics(target);
            string address = target.IpAddress ?? "";
            bool publicUnavailable = false;
            if (String.Equals(target.Nickname, ServerInterface.AdministratorNickname, StringComparison.OrdinalIgnoreCase) &&
                IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip))
            {
                publicUnavailable = !ServerInterface.DisplayAddressIsPublic;
                address = publicUnavailable ? "" : ServerInterface.DisplayAddress;
            }
            return new WhoisInfo(diagnostics.Nickname, address, publicUnavailable, diagnostics.PingMilliseconds,
                diagnostics.WindowWidth, diagnostics.WindowHeight, diagnostics.SnakeEnabled, diagnostics.Paused,
                diagnostics.SnakeDelay, diagnostics.SnakeColor, diagnostics.SnakeGlyph, diagnostics.Messages,
                diagnostics.Transport, diagnostics.SignalDbm, OriginalNickname: diagnostics.OriginalNickname);
        }

        private static WhoisDiagnosticsSnapshot SnapshotDiagnostics(Client target)
        {
            bool hasSnake = target.TryGetSnakeProfile(out SnakeProfile snake);
            var size = target.Diagnostics.GetSize();
            string transport = target.Transport switch
            {
                ChatTransport.Bluetooth => WhoisProtocol.TransportBluetooth,
                ChatTransport.Local => WhoisProtocol.TransportLocal,
                _ => WhoisProtocol.TransportTcp
            };
            return new WhoisDiagnosticsSnapshot(target.Nickname, target.Diagnostics.PingMilliseconds,
                size.Width, size.Height, hasSnake ? snake.Enabled : null, snake.Paused,
                hasSnake ? snake.DelayMilliseconds : 75, hasSnake ? (int)snake.Color : 10,
                hasSnake ? snake.Glyph : '-', target.Diagnostics.Messages,
                transport, target.Transport == ChatTransport.Bluetooth ? target.Diagnostics.SignalDbm : null, target.OriginalNickname);
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
