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
            string displayed = endpoint == null ? host : host.Contains(':') ? "[" + host.Trim('[', ']') + "]:" + endpoint.Port : host + ":" + endpoint.Port;
            bool encrypted = currentE2E, wasConnected = connected;
            ChatTransport transport = currentTransport; int? signal = CurrentBluetoothSignal;
            Func<string> unavailable = () => Lang.Get(TextId.StatusUnavailable);
            Func<string> link = () => transport switch
            {
                ChatTransport.Bluetooth => signal is int dbm ? Lang.Get(TextId.StatusSignal, dbm) : Lang.Get(TextId.StatusSignalUnavailable),
                ChatTransport.Local => Lang.Get(TextId.StatusLocalLink),
                _ => ping.Item1 ? Lang.Get(TextId.StatusPing, ping.Item2) : Lang.Get(TextId.StatusPingUnavailable)
            };
            var current = new List<Func<string>>
            {
                () => Lang.Get(TextId.StatusCurrent, displayed),
                () => "E2E: " + (encrypted ? "TRUE" : "FALSE"),
                () => Lang.Get(TextId.StatusAdministrator, String.IsNullOrEmpty(remote?.Owner) ? unavailable() : NicknameProtocol.DisplayName(remote.Owner, remote.OriginalNickname)),
                () => Lang.Get(wasConnected ? TextId.StatusConnected : TextId.StatusDisconnected),
                link,
                () => Lang.Get(TextId.StatusParticipants, remote?.Participants.ToString() ?? unavailable())
            };
            List<Func<string>> own = null;
            LocalHubSnapshot ownStatus = ServerInterface.CaptureStatus();
            if (ownStatus.Running)
            {
                HubOptions ownOptions = ServerInterface.Options;
                Func<string> access = () => Lang.Get(TextId.StatusAccess, DescribeHubAccess(ownOptions));
                string service = ServerInterface.SecureHub?.ServiceStatus ?? "DISABLED";
                Func<string> nat = NetWorker.CapturePortMappingStatus();
                bool lanAvailable = ServerInterface.LanDiscoveryAvailable;
                string lanFailure = ServerInterface.LanDiscoveryFailure;
                Func<string> lan = () => Lang.Get(TextId.StatusLanDiscovery, lanAvailable ? Lang.Get(TextId.StatusRunning) : lanFailure ?? unavailable());
                List<Func<string>> target;
                if (isLocalHubSession) { target = current; target.Add(access); }
                else
                {
                    own = new List<Func<string>>
                    {
                        () => ownOptions.UsesTcp ? Lang.Get(TextId.StatusOwn, ownStatus.Port) : Lang.Get(TextId.StatusOwnBluetooth),
                        () => Lang.Get(TextId.StatusRunning), access,
                        () => Lang.Get(TextId.StatusParticipants, ownStatus.Participants)
                    };
                    target = own;
                }
                target.Add(() => "E2E service: " + service);
                if (ownOptions.UsesPortMapping) target.Add(() => Lang.Get(TextId.StatusNat, nat()));
                if (ownOptions.IpMode == HubIpMode.LanOnly) target.Add(lan);
            }
            if (token.IsCancellationRequested || !connected) return;
            lock (consoleLock)
            {
                var entry = new ChatHistoryEntry(new StatusCard(current, own));
                chatHistory.Add(entry);
                TrimChatHistoryLocked();
                if (chatUiSuspended) return;
                if (ConsoleGraphic.Enabled || ScrollbackActive) RedrawChatLayoutLocked();
                else WritePlainHistoryEntry(entry);
            }
        }
    }
}
