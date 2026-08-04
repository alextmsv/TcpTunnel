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

        public static bool IsRunning => isRunning;
        public static int ListeningPort { get; private set; }
        public static string PortMappingStatus { get; private set; } = "UPnP ещё не запускался.";

        public static void tryCreateServer()
        {
            Program.matrix("Введите порт сервера [9091]: ");
            string rawPort = Console.ReadLine();
            int port;
            if (String.IsNullOrWhiteSpace(rawPort))
                port = 9091;
            else if (!Int32.TryParse(rawPort, out port) || port < 1 || port > 65535)
            {
                ConsoleGraphic.WriteContentLine("Порт должен быть числом от 1 до 65535.");
                return;
            }

            doCreateServer(port);
        }

        public static bool doCreateServer(int port)
        {
            string error;
            if (!TryStartServer(port, out error))
            {
                ConsoleGraphic.WriteContentLine("Не удалось создать хаб: " + error);
                return false;
            }

            ConsoleGraphic.WriteContentLine($"Хаб запущен на TCP-порту {port}.");
            ConsoleGraphic.WriteContentLine("Локальный клиент подключается к 127.0.0.1; UPnP настраивается в фоне.");
            StartPortMapping(port, serverCancellation.Token);

            return UserInterface.DoConnect("127.0.0.1", port, 1);
        }

        public static bool TryStartServer(int port, out string error)
        {
            if (port < 1 || port > 65535)
            {
                error = "порт должен быть в диапазоне от 1 до 65535";
                return false;
            }

            lock (serverLock)
            {
                if (isRunning)
                {
                    error = $"хаб уже работает на порту {ListeningPort}";
                    return false;
                }

                try
                {
                    TcpListener listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();

                    server = listener;
                    serverCancellation = new CancellationTokenSource();
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

                    PortMappingStatus = "UPnP: поиск роутера...";
                    PortMappingStatus = await TryOpenPortAsync(port, cancellationToken).ConfigureAwait(false);
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
                    PortMappingStatus = await TryClosePortAsync().ConfigureAwait(false);
                });
                return portMappingLifecycle;
            }
        }
    }
}
