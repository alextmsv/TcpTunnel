using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public class ServerInterface : NetWorker
    {
        private static readonly object serverLock = new object();
        internal const int MaxConnectedClients = 64;
        private static TcpListener server;
        private static CancellationTokenSource serverCancellation;
        private static Task acceptTask = Task.CompletedTask;
        private static readonly List<Task> clientTasks = new List<Task>();
        private static Task portMappingLifecycle = Task.CompletedTask;
        private static volatile bool isRunning;
        private static volatile string displayAddress = "127.0.0.1";
        private static volatile bool displayAddressIsPublic;

        public static bool IsRunning => isRunning;
        public static int ListeningPort { get; private set; }
        public static string PortMappingStatus => GetPortMappingStatus();
        public static string DisplayAddress => displayAddress;
        public static bool DisplayAddressIsPublic => displayAddressIsPublic;
        public static string AdministratorNickname { get; private set; } = String.Empty;
        public static int ConnectedClientCount => broadcaster.AuthenticatedClientCount;

        internal static LocalHubSnapshot CaptureStatus()
        {
            lock (serverLock)
                return new LocalHubSnapshot(isRunning, ListeningPort, PortMappingStatus, ConnectedClientCount);
        }

        internal static async Task<KickCommandResult> KickClientAsync(
            string targetNickname,
            string administratorNickname,
            string reason)
        {
            if (String.Equals(targetNickname, administratorNickname, StringComparison.OrdinalIgnoreCase))
                return KickCommandResult.CannotKickSelf;

            bool kicked = await broadcaster.KickAsync(
                targetNickname,
                reason,
                CancellationToken.None).ConfigureAwait(false);
            return kicked ? KickCommandResult.Success : KickCommandResult.NotFound;
        }

        public static void tryCreateServer()
        {
            if (ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.WriteCenteredLine(
                    Lang.Get(TextId.HubSetup),
                    ConsoleGraphic.ContentTop + 1,
                    ConsoleColor.Cyan,
                    true,
                    4);
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ChooseTcpPort), ConsoleTheme.SystemText);
                ConsoleGraphic.TrySetContentCursor(2, ConsoleGraphic.ContentTop + 3);
                Program.matrix(Lang.Get(TextId.EnterServerPort), 4, ConsoleColor.Yellow, false);
            }
            else
            {
                Program.matrix(Lang.Get(TextId.EnterServerPort));
            }

            string rawPort = Console.ReadLine();
            int port;
            if (String.IsNullOrWhiteSpace(rawPort))
                port = 9091;
            else if (!Int32.TryParse(rawPort, out port) || port < 1 || port > 65535)
            {
                if (ConsoleGraphic.Enabled)
                    ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.InvalidPortNumber), ConsoleTheme.SystemText);
                else
                    ConsoleGraphic.WriteContentLine(Lang.Get(TextId.InvalidPortNumber));
                return;
            }

            doCreateServer(port);
        }

        public static bool doCreateServer(int port)
        {
            if (!UserInterface.EnsureNickname()) return false;
            if (ConsoleGraphic.Enabled)
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.StartingListener), ConsoleTheme.SystemText, 0, true, 3);

            string error;
            if (!TryStartServer(port, out error, nickname))
            {
                if (ConsoleGraphic.Enabled)
                    ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.CreateHubFailed, error), ConsoleTheme.SystemText);
                else
                    ConsoleGraphic.WriteContentLine(Lang.Get(TextId.CreateHubFailed, error));
                return false;
            }

            if (ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ListenerStarted), ConsoleTheme.SystemText);
                Thread.Sleep(180);
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ConfiguringNat), ConsoleTheme.SystemText);
            }
            else
            {
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.HubStarted, port));
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.LocalClientBackground));
            }

            StartPortMapping(port, serverCancellation.Token);

            ResolveDisplayAddress(port, serverCancellation.Token);

            if (ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.DrawServerEndpointCard(DisplayAddress, port);
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.LocalClientConnecting), ConsoleTheme.SystemText, 3);
            }

            return UserInterface.DoConnect("127.0.0.1", port, 1);
        }

        private static void ResolveDisplayAddress(int port, CancellationToken cancellationToken)
        {
            HubAddressResolution resolution;
            try
            {
                resolution = NetworkAddressResolver.ResolveHubAddressAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (serverLock)
            {
                if (!isRunning || ListeningPort != port)
                    return;

                displayAddress = resolution.Address;
                displayAddressIsPublic = resolution.IsPublic;
            }
        }

        public static bool TryStartServer(int port, out string error, string administratorNickname = null)
        {
            if (port < 1 || port > 65535)
            {
                error = Lang.Get(TextId.PortOutOfRange);
                return false;
            }

            lock (serverLock)
            {
                if (isRunning)
                {
                    error = Lang.Get(TextId.HubAlreadyRunning, ListeningPort);
                    return false;
                }

                try
                {
                    TcpListener listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();

                    server = listener;
                    serverCancellation = new CancellationTokenSource();
                    displayAddress = "127.0.0.1";
                    displayAddressIsPublic = false;
                    ListeningPort = port;
                    AdministratorNickname = IsNicknameValid(administratorNickname) ? administratorNickname : String.Empty;
                    isRunning = true;
                    acceptTask = AcceptLoopAsync(listener, serverCancellation.Token);
                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    server = null;
                    serverCancellation = null;
                    isRunning = false;
                    ListeningPort = 0;
                    error = ex.Message;
                    return false;
                }
            }
        }

        public static void StopServer()
        {
            CancellationTokenSource cancellation;
            TcpListener listener;
            Task accepting;
            Task[] clientsToDrain;

            lock (serverLock)
            {
                if (!isRunning)
                    return;

                isRunning = false;
                ListeningPort = 0;
                displayAddressIsPublic = false;
                cancellation = serverCancellation;
                listener = server;
                accepting = acceptTask;
                clientsToDrain = clientTasks.ToArray();
                clientTasks.Clear();
                serverCancellation = null;
                server = null;
            }

            try { cancellation.Cancel(); } catch { }
            try { listener.Stop(); } catch { }
            broadcaster.DisconnectAll();
            try
            {
                var all = new List<Task>(clientsToDrain) { accepting };
                Task.WhenAll(all).Wait(TimeSpan.FromSeconds(3));
            }
            catch { }
            Task mappingCleanup = StopPortMapping();
            try { mappingCleanup.Wait(TimeSpan.FromSeconds(3)); } catch { }
        }

        private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient incoming = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    bool stale;
                    lock (serverLock)
                        stale = cancellationToken.IsCancellationRequested || !isRunning || !Object.ReferenceEquals(server, listener);
                    if (stale)
                    {
                        incoming.Close();
                        break;
                    }

                    Client client = new Client(incoming);
                    if (broadcaster.ConnectionCount >= MaxConnectedClients)
                    {
                        client.Close();
                        continue;
                    }
                    broadcaster.AddConnection(client);
                    Task clientTask = ServerClientLoopAsync(client, cancellationToken);
                    lock (serverLock)
                    {
                        if (isRunning && Object.ReferenceEquals(server, listener))
                            clientTasks.Add(clientTask);
                        else
                            broadcaster.RemoveClient(client);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Listener закрыт при остановке Hub.
            }
            catch (SocketException)
            {
                // Listener закрыт либо сеть стала недоступна.
            }
        }

        private static void StartPortMapping(int port, CancellationToken cancellationToken)
        {
            lock (serverLock)
            {
                Task previousLifecycle = portMappingLifecycle;
                portMappingLifecycle = Task.Run(async () =>
                {
                    try { await previousLifecycle.ConfigureAwait(false); } catch { }
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    SetPortMappingStatus(TextId.NatTrying);
                    await TryOpenPortAsync(port, cancellationToken).ConfigureAwait(false);
                });
            }
        }

        private static Task StopPortMapping()
        {
            lock (serverLock)
            {
                Task previousLifecycle = portMappingLifecycle;
                portMappingLifecycle = Task.Run(async () =>
                {
                    try { await previousLifecycle.ConfigureAwait(false); } catch { }
                    await TryClosePortAsync().ConfigureAwait(false);
                });
                return portMappingLifecycle;
            }
        }
    }
}
