using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    internal static class StabilityTests
    {
        public static bool Run()
        {
            try
            {
                return RunLoopbackBroadcastStressAsync().GetAwaiter().GetResult() &&
                       RunAuthenticatedSessionStressAsync().GetAwaiter().GetResult();
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> RunAuthenticatedSessionStressAsync()
        {
            const int clientCount = 3;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            var rawClients = new List<TcpClient>();
            var serverClients = new List<Client>();
            var sessionTasks = new List<Task>();
            NetWorker.broadcaster.DisconnectAll();
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                try
                {
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    for (int index = 0; index < clientCount; index++)
                    {
                        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
                        var rawClient = new TcpClient { NoDelay = true };
                        await rawClient.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                        var serverClient = new Client(await accept.ConfigureAwait(false));
                        rawClients.Add(rawClient);
                        serverClients.Add(serverClient);
                        NetWorker.broadcaster.AddConnection(serverClient);
                        sessionTasks.Add(NetWorker.ServerClientLoopAsync(serverClient, cancellation.Token));

                        NetworkStream stream = rawClient.GetStream();
                        if (await MessageProtocol.ReadStringAsync(stream, cancellation.Token).ConfigureAwait(false) != NetWorker.DO_AUTH_MESSAGE)
                            return false;
                        await MessageProtocol.WriteStringAsync(
                            stream,
                            "REPLY:session" + index,
                            cancellation.Token).ConfigureAwait(false);
                        if (await MessageProtocol.ReadStringAsync(stream, cancellation.Token).ConfigureAwait(false) != NetWorker.AUTH_OK_MESSAGE)
                            return false;

                        for (int participant = 0; participant < index; participant++)
                        {
                            string present = await MessageProtocol.ReadStringAsync(stream, cancellation.Token).ConfigureAwait(false);
                            string localized;
                            string argument;
                            SystemMessageKind kind;
                            if (!SystemMessageProtocol.TryLocalize(present, out localized, out kind, out argument) ||
                                kind != SystemMessageKind.ParticipantPresent || argument != "session" + participant)
                                return false;
                        }

                        for (int recipient = 0; recipient <= index; recipient++)
                        {
                            string joined = await MessageProtocol.ReadStringAsync(
                                rawClients[recipient].GetStream(), cancellation.Token).ConfigureAwait(false);
                            string localized;
                            string argument;
                            SystemMessageKind kind;
                            if (!SystemMessageProtocol.TryLocalize(joined, out localized, out kind, out argument) ||
                                kind != SystemMessageKind.UserJoined || argument != "session" + index)
                                return false;
                        }
                    }

                    await MessageProtocol.WriteStringAsync(
                        rawClients[0].GetStream(),
                        "ordered payload",
                        cancellation.Token).ConfigureAwait(false);
                    for (int recipient = 1; recipient < clientCount; recipient++)
                    {
                        string received = await MessageProtocol.ReadStringAsync(
                            rawClients[recipient].GetStream(), cancellation.Token).ConfigureAwait(false);
                        if (received != "[session0]: ordered payload")
                            return false;
                    }
                    return NetWorker.broadcaster.AuthenticatedClientCount == clientCount;
                }
                finally
                {
                    cancellation.Cancel();
                    listener.Stop();
                    foreach (TcpClient client in rawClients)
                    {
                        try { client.Close(); } catch { }
                    }
                    NetWorker.broadcaster.DisconnectAll();
                    try { await Task.WhenAll(sessionTasks).ConfigureAwait(false); } catch { }
                    foreach (Client client in serverClients)
                        client.Dispose();
                }
            }
        }

        private static async Task<bool> RunLoopbackBroadcastStressAsync()
        {
            const int clientCount = 6;
            const int messageCount = 150;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            var serverClients = new List<Client>();
            var receivingClients = new List<TcpClient>();
            var broadcaster = new Broadcaster();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                try
                {
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    for (int index = 0; index < clientCount; index++)
                    {
                        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
                        var receiver = new TcpClient { NoDelay = true };
                        await receiver.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                        TcpClient accepted = await accept.ConfigureAwait(false);
                        var serverClient = new Client(accepted);
                        broadcaster.AddConnection(serverClient);
                        if (!broadcaster.TryAuthenticate(serverClient, "stress" + index))
                            return false;
                        serverClients.Add(serverClient);
                        receivingClients.Add(receiver);
                    }

                    Task<List<string>>[] readers = receivingClients
                        .Select(client => ReadMessagesAsync(client.GetStream(), messageCount, timeout.Token))
                        .ToArray();
                    for (int index = 0; index < messageCount; index++)
                    {
                        await broadcaster.BroadcastAsync(
                            null,
                            "stress-message-" + index,
                            timeout.Token).ConfigureAwait(false);
                    }

                    List<string>[] received = await Task.WhenAll(readers).ConfigureAwait(false);
                    for (int clientIndex = 0; clientIndex < received.Length; clientIndex++)
                    {
                        if (received[clientIndex].Count != messageCount)
                            return false;
                        for (int messageIndex = 0; messageIndex < messageCount; messageIndex++)
                        {
                            if (received[clientIndex][messageIndex] != "stress-message-" + messageIndex)
                                return false;
                        }
                    }

                    Task<string> kickNotice = MessageProtocol.ReadStringAsync(
                        receivingClients[0].GetStream(),
                        timeout.Token);
                    if (!await broadcaster.KickAsync("stress0", "stress reason", timeout.Token).ConfigureAwait(false))
                        return false;
                    string kickMessage = await kickNotice.ConfigureAwait(false);
                    string localized;
                    string argument;
                    SystemMessageKind kind;
                    return broadcaster.AuthenticatedClientCount == clientCount - 1 &&
                           SystemMessageProtocol.TryLocalize(kickMessage, out localized, out kind, out argument) &&
                           kind == SystemMessageKind.Kicked && argument == "stress reason";
                }
                finally
                {
                    listener.Stop();
                    broadcaster.DisconnectAll();
                    foreach (TcpClient receiver in receivingClients)
                    {
                        try { receiver.Close(); } catch { }
                    }
                    foreach (Client client in serverClients)
                        client.Dispose();
                }
            }
        }

        private static async Task<List<string>> ReadMessagesAsync(
            NetworkStream stream,
            int count,
            CancellationToken cancellationToken)
        {
            var messages = new List<string>(count);
            for (int index = 0; index < count; index++)
                messages.Add(await MessageProtocol.ReadStringAsync(stream, cancellationToken).ConfigureAwait(false));
            return messages;
        }
    }
}
