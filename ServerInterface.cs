using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public class ServerInterface : NetWorker
    {
        private static readonly object serverLock = new object();
        private static TcpListener server;
        private static CancellationTokenSource serverCancellation;
        private static Task acceptTask = Task.CompletedTask;
        private static Task portMappingLifecycle = Task.CompletedTask;
        private static volatile bool isRunning;
        private static string displayAddress = "127.0.0.1";

        public static bool IsRunning => isRunning;
        public static int ListeningPort { get; private set; }
        public static string PortMappingStatus => GetPortMappingStatus();
        public static string DisplayAddress => displayAddress;

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
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ChooseTcpPort), ConsoleColor.DarkGray);
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
                    ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.InvalidPortNumber), ConsoleColor.Red);
                else
                    ConsoleGraphic.WriteContentLine(Lang.Get(TextId.InvalidPortNumber));
                return;
            }

            doCreateServer(port);
        }

        public static bool doCreateServer(int port)
        {
            if (ConsoleGraphic.Enabled)
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.StartingListener), ConsoleColor.Yellow, 0, true, 3);

            string error;
            if (!TryStartServer(port, out error))
            {
                if (ConsoleGraphic.Enabled)
                    ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.CreateHubFailed, error), ConsoleColor.Red);
                else
                    ConsoleGraphic.WriteContentLine(Lang.Get(TextId.CreateHubFailed, error));
                return false;
            }

            if (ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ListenerStarted), ConsoleColor.Green);
                Thread.Sleep(180);
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ConfiguringNat), ConsoleColor.Yellow);
            }
            else
            {
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.HubStarted, port));
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.LocalClientBackground));
            }

            StartPortMapping(port, serverCancellation.Token);

            if (ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.DrawServerEndpointCard(DisplayAddress, port);
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.LocalClientConnecting), ConsoleColor.Yellow, 3);
            }

            return UserInterface.DoConnect("127.0.0.1", port, 1);
        }

        private static string GetDisplayAddress()
        {
            try
            {
                foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                        return address.ToString();
                }
            }
            catch
            {
            }

            return "127.0.0.1";
        }

        public static bool TryStartServer(int port, out string error)
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
                    displayAddress = GetDisplayAddress();
                    ListeningPort = port;
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

            lock (serverLock)
            {
                if (!isRunning)
                    return;

                isRunning = false;
                ListeningPort = 0;
                cancellation = serverCancellation;
                listener = server;
                serverCancellation = null;
                server = null;
            }

            try { cancellation.Cancel(); } catch { }
            try { listener.Stop(); } catch { }
            broadcaster.DisconnectAll();
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
                    if (cancellationToken.IsCancellationRequested)
                    {
                        incoming.Close();
                        break;
                    }

                    Client client = new Client(incoming);
                    broadcaster.AddConnection(client);
                    Task ignored = ServerClientLoopAsync(client, cancellationToken);
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
