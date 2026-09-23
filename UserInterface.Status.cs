using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TCPTunnel
{
    public partial class UserInterface
    {
        private static LocalHubStatusMonitor localStatusMonitor;
        private static long nextLocalStatusCheck;

        private static void CheckLocalHubStatus()
        {
            if (localStatusMonitor == null || Environment.TickCount64 < nextLocalStatusCheck) return;
            nextLocalStatusCheck = Environment.TickCount64 + 500;
            foreach (string message in localStatusMonitor.Observe(ServerInterface.CaptureStatus(), isLocalHubSession))
                WriteSystemChatLine(message);
        }

        private static void ShowHubStatus(Stream stream, HubStatusSession status, IPEndPoint endpoint, string host, CancellationToken token)
        {
            var query = status.QueryAsync((message, ct) => MessageProtocol.WriteStringAsync(stream, message, ct), token);
            var pingTask = endpoint == null ? null : PingAsync(endpoint.Address.ToString(), endpoint.Port);
            RemoteHubStatus remote = query.GetAwaiter().GetResult();
            var ping = pingTask?.GetAwaiter().GetResult() ?? (false, (string)null);
            string unavailable = Lang.Get(TextId.StatusUnavailable);
            var current = new List<string>
            {
                Lang.Get(TextId.StatusCurrent, endpoint == null ? host : host.Contains(':') ? "[" + host.Trim('[', ']') + "]:" + endpoint.Port : host + ":" + endpoint.Port),
                Lang.Get(TextId.StatusAdministrator, String.IsNullOrEmpty(remote?.Owner) ? unavailable : "@" + remote.Owner),
                Lang.Get(connected ? TextId.StatusConnected : TextId.StatusDisconnected),
                ping.Item1 ? Lang.Get(TextId.StatusPing, ping.Item2) : Lang.Get(TextId.StatusPingUnavailable),
                Lang.Get(TextId.StatusParticipants, remote?.Participants.ToString() ?? unavailable)
            };
            List<string> own = null;
            LocalHubSnapshot ownStatus = ServerInterface.CaptureStatus();
            if (ownStatus.Running)
            {
                if (isLocalHubSession)
                    current.Add(Lang.Get(TextId.StatusNat, ownStatus.Nat));
                else
                    own = new List<string>
                    {
                        Lang.Get(TextId.StatusOwn, ownStatus.Port),
                        Lang.Get(TextId.StatusRunning),
                        Lang.Get(TextId.StatusNat, ownStatus.Nat),
                        Lang.Get(TextId.StatusParticipants, ownStatus.Participants)
                    };
            }
            if (token.IsCancellationRequested || !connected) return;
            lock (consoleLock)
            {
                var entry = new ChatHistoryEntry(new StatusCard(current, own));
                chatHistory.Add(entry);
                TrimChatHistoryLocked();
                if (ConsoleGraphic.Enabled) RedrawChatLayoutLocked();
                else WritePlainHistoryEntry(entry);
            }
        }
    }
}
