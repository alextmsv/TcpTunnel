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
        private static readonly object admissionLock = new object();
        internal const int MaxConnectedClients = 64;
        private static TcpListener server;
        private static BluetoothHubHost bluetoothHost;
        private static CancellationTokenSource serverCancellation;
        private static Task acceptTask = Task.CompletedTask;
        private static readonly List<Task> clientTasks = new List<Task>();
        private static Task portMappingLifecycle = Task.CompletedTask;
        private static volatile bool isRunning;
        private static volatile string displayAddress = "127.0.0.1";
        private static volatile bool displayAddressIsPublic;

        public static bool IsRunning => isRunning;
        public static int ListeningPort { get; private set; }
        internal static HubOptions Options { get; private set; } = HubOptions.Default;
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

        public static void tryCreateServer() => tryCreateServer(HubOptions.Default);

        internal static void tryCreateServer(HubOptions options)
        {
            if (!options.UsesTcp)
            {
                doCreateServer(0, options);
                return;
            }

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

            doCreateServer(port, options);
        }

        public static bool doCreateServer(int port) => doCreateServer(port, HubOptions.Default);

        internal static bool doCreateServer(int port, HubOptions options)
        {
            if (!UserInterface.EnsureNickname()) return false;
            ShowHubProgress(Lang.Get(options.UsesTcp ? TextId.StartingListener : TextId.BluetoothHubStarting), true);

            string error;
            bool advertisingBlocked;
            bool started = TryStartServer(port, options, out error, nickname, out advertisingBlocked);
            if (!started && advertisingBlocked)
            {
                ShowHubProgress(Lang.Get(TextId.BluetoothRequestingElevation), false);
                if (BluetoothPolicy.RequestAllowAdvertising(out string elevationError))
                {
                    started = TryStartServer(port, options, out error, nickname, out advertisingBlocked);
                    if (!started && advertisingBlocked)
                        error = Lang.Get(TextId.BluetoothStillBlocked);
                }
                else
                {
                    error = elevationError;
                }
            }

            if (!started)
            {
                ShowHubProgress(Lang.Get(TextId.CreateHubFailed, error), false);
                return false;
            }

            if (!options.UsesTcp)
            {
                if (ConsoleGraphic.Enabled)
                {
                    ConsoleGraphic.DrawServerEndpointCard(Lang.Get(TextId.BluetoothEndpoint), 0);
                    ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.LocalClientConnecting), ConsoleTheme.SystemText, 3);
                }
                else
                {
                    ConsoleGraphic.WriteContentLine(Lang.Get(TextId.BluetoothHubStarted));
                }
                return UserInterface.DoConnectLocal(CreateLocalConnection());
            }

            if (ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ListenerStarted), ConsoleTheme.SystemText);
                if (options.UsesPortMapping)
                {
                    Thread.Sleep(180);
                    ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ConfiguringNat), ConsoleTheme.SystemText);
                }
            }
            else
            {
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.HubStarted, port));
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.LocalClientBackground));
            }

            if (options.UsesPortMapping)
            {
                StartPortMapping(port, serverCancellation.Token);
                ResolveDisplayAddress(port, serverCancellation.Token);
            }
            else
            {
                displayAddress = NetworkAddressResolver.GetLocalIPv4Address();
            }

            if (ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.DrawServerEndpointCard(DisplayAddress, port);
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.LocalClientConnecting), ConsoleTheme.SystemText, 3);
            }

            return UserInterface.DoConnect("127.0.0.1", port, 1);
        }

        private static void ShowHubProgress(string text, bool starting)
        {
            if (ConsoleGraphic.Enabled)
            {
                if (starting)
                    ConsoleGraphic.WriteBottomStatus(text, ConsoleTheme.SystemText, 0, true, 3);
                else
                    ConsoleGraphic.WriteBottomStatus(text, ConsoleTheme.SystemText);
            }
            else
            {
                ConsoleGraphic.WriteContentLine(text);
            }
        }

        internal static IChatConnection CreateLocalConnection()
        {
            var (hubSide, clientSide) = LocalChatConnection.CreatePair();
            AdmitConnection(hubSide);
            return clientSide;
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

        public static bool TryStartServer(int port, out string error, string administratorNickname = null) =>
            TryStartServer(port, HubOptions.Default, out error, administratorNickname, out _);

        internal static bool TryStartServer(
            int port,
            HubOptions options,
            out string error,
            string administratorNickname,
            out bool advertisingBlocked)
        {
            advertisingBlocked = false;
            if (options == null || !options.IsValid)
            {
                error = Lang.Get(TextId.HubOptionsInvalid);
                return false;
            }
            if (options.UsesTcp && (port < 1 || port > 65535))
            {
                error = Lang.Get(TextId.PortOutOfRange);
                return false;
            }
            if (options.Bluetooth && !IsNicknameValid(administratorNickname))
            {
                error = Lang.Get(TextId.HubOptionsInvalid);
                return false;
            }

            CancellationToken token;
            lock (serverLock)
            {
                if (isRunning)
                {
                    error = Lang.Get(TextId.HubAlreadyRunning, ListeningPort);
                    return false;
                }

                TcpListener listener = null;
                try
                {
                    if (options.UsesTcp)
                    {
                        listener = new TcpListener(IPAddress.Any, port);
                        listener.Start();
                    }

                    server = listener;
                    serverCancellation = new CancellationTokenSource();
                    token = serverCancellation.Token;
                    displayAddress = "127.0.0.1";
                    displayAddressIsPublic = false;
                    ListeningPort = options.UsesTcp ? port : 0;
                    Options = options;
                    AdministratorNickname = IsNicknameValid(administratorNickname) ? administratorNickname : String.Empty;
                    isRunning = true;
                    acceptTask = listener == null
                        ? Task.CompletedTask
                        : AcceptLoopAsync(listener, options.IpMode == HubIpMode.LanOnly, token);
                }
                catch (Exception ex)
                {
                    try { listener?.Stop(); } catch { }
                    server = null;
                    serverCancellation = null;
                    isRunning = false;
                    ListeningPort = 0;
                    error = ex.Message;
                    return false;
                }
            }

            if (!options.Bluetooth)
            {
                error = null;
                return true;
            }

            BluetoothHubStartStatus status = BluetoothHubHost.TryStart(
                options.BeaconMode,
                administratorNickname,
                AdmitConnection,
                () => broadcaster.AuthenticatedClientCount,
                out BluetoothHubHost host,
                out string bluetoothError);
            if (status == BluetoothHubStartStatus.Started)
            {
                bool current;
                lock (serverLock)
                {
                    current = isRunning && serverCancellation != null && serverCancellation.Token == token;
                    if (current)
                        bluetoothHost = host;
                }
                if (!current)
                {
                    host.Dispose();
                    error = Lang.Get(TextId.BluetoothHubStartFailed, String.Empty);
                    return false;
                }
                error = null;
                return true;
            }

            StopServer();
            advertisingBlocked = status == BluetoothHubStartStatus.AdvertisingBlockedByPolicy;
            error = advertisingBlocked
                ? Lang.Get(TextId.BluetoothAdvertisingBlocked)
                : Lang.Get(TextId.BluetoothHubStartFailed, bluetoothError);
            return false;
        }

        public static void StopServer()
        {
            CancellationTokenSource cancellation;
            TcpListener listener;
            BluetoothHubHost bluetooth;
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
                bluetooth = bluetoothHost;
                accepting = acceptTask;
                clientsToDrain = clientTasks.ToArray();
                clientTasks.Clear();
                serverCancellation = null;
                server = null;
                bluetoothHost = null;
            }

            try { bluetooth?.Dispose(); } catch { }
            try { cancellation.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
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

        internal static void AdmitConnection(IChatConnection connection)
        {
            CancellationToken token;
            lock (serverLock)
            {
                if (!isRunning || serverCancellation == null)
                {
                    connection.Dispose();
                    return;
                }
                token = serverCancellation.Token;
            }
            Admit(connection, token);
        }

        private static void Admit(IChatConnection connection, CancellationToken cancellationToken)
        {
            Client client = new Client(connection);
            lock (admissionLock)
            {
                if (broadcaster.ConnectionCount >= MaxConnectedClients)
                {
                    client.Close();
                    return;
                }
                broadcaster.AddConnection(client);
            }
            Task clientTask = ServerClientLoopAsync(client, cancellationToken);
            lock (serverLock)
            {
                if (isRunning && serverCancellation != null && serverCancellation.Token == cancellationToken)
                    clientTasks.Add(clientTask);
                else
                    broadcaster.RemoveClient(client);
            }
        }

        private static async Task AcceptLoopAsync(TcpListener listener, bool lanOnly, CancellationToken cancellationToken)
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

                    if (lanOnly && !LanPolicy.IsAllowed((incoming.Client.RemoteEndPoint as IPEndPoint)?.Address))
                    {
                        incoming.Close();
                        continue;
                    }

                    Admit(new TcpChatConnection(incoming), cancellationToken);
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
