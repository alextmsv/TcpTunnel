using System;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using Open.Nat;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
namespace TCPTunnel
{
    public class NetWorker
    {
        private static Broadcaster broadcaster = new Broadcaster();
        public static bool connected = false;
        public static string nickname = "";
        public static void TryConnect()
        {
            Program.matrix("Введите IP адрес сервера [localhost]: ");
            string ip = Console.ReadLine();
            if (String.IsNullOrEmpty(ip))
                ip = "localhost";

            Program.matrix("Введите порт сервера [9095]: ");
            int serverPort;
            if (!Int32.TryParse(Console.ReadLine(), out serverPort))
                serverPort = 9095;

            //Program.matrix(">>> Попытка подключения к серверу: " + ip + ":" + serverPort);
            Program.waiting(">>> Попытка подключения к серверу: " + ip + ":" + serverPort, ip, serverPort);
            Console.Clear();
            DoConnect(ip, serverPort);
        }
        public static void DoConnect(string address, int port, string nickName = "")
        {
            nickName = nickname;
            TcpClient client = new TcpClient();
            try
            {
                client.Connect(address, port);
                connected = true;
                Console.Clear();
                Program.bufferClear();
            }
            catch
            {
                Program.matrix("Не удалось подключиться к " + address);
                return;
            }
            ConnectionContext publicName = new ConnectionContext();
            string ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            Thread clientThread = new Thread(ClientThread);
            clientThread.Start(client);
            if (nickname.Length == 0)
            {
                Program.matrix("Введите свой псевдоним: ");
                nickname = Console.ReadLine();
            }
            Program.bufferClear();
            while ((nickname.IndexOfAny(@"!@#$%^&*()[]{}+, /\| ".ToCharArray()) != -1) || nickname.Length > 20 || nickname.Length < 3)
            {
                Program.matrix("Некорректный псевдоним");
                Console.Clear();
                Program.matrix("Введите свой псевдоним: ");
                nickname = Console.ReadLine();
            } 
            using (BinaryWriter writer = new BinaryWriter(client.GetStream()))
            {
                writer.Write($"{nickname}   присоединился к хабу.");
                while (client.Connected)
                {
                    string message = Console.ReadLine();
                    //string message = Program.ReadLineWithPrompt($"[{nickname}]:");
                    Program.bufferClear();
                    writer.Write($"[{nickname}]: {message}    ({Dns.GetHostByName(Dns.GetHostName()).AddressList[1]})");
                }
                Program.matrix("Пользователь отключился от сервера!");
                return;
            }
        }
        async static void openPort(int port)
        {
            var discoverer = new NatDiscoverer();
            var device = await discoverer.DiscoverDeviceAsync();
            var ip = await device.GetExternalIPAsync();
            try
            {
                await device.DeletePortMapAsync(new Mapping(Protocol.Tcp, port, port));
            }
            catch (MappingException ex)
            {
                Program.matrix(ex.ErrorText+", сервер закрыт для посторонних. (Отключите Брандмауэр(Firewall) в анти-вирусе. ИЛИ Вы пытаетесь запустить 2 сервера одновременно");
                Console.ReadKey();
            }
            await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port));
        }
        public static void tryCreateServer()
        {
            Program.matrix("Введите порт сервера: ");
            int port;
            if (!Int32.TryParse(Console.ReadLine(), out port))
                port = 9095;
            Program.matrix($"Порт = {port}");
            Thread.Sleep(20);
            Console.SetCursorPosition(Menu.left + 1, Menu.top);
            Console.Write(port);
            Console.Clear();
            doCreateServer(port);
        }
        public static void doCreateServer(int port)
        {
            TcpListener server = new TcpListener(IPAddress.Any, port);
            openPort(port);
            Thread.Sleep(300);
            server.Start();
            Program.matrix("Сервер запущен!");
            Console.Write("\r\n");
            Process.Start(Application.ExecutablePath);
            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                ConnectionContext context = new ConnectionContext();
                context.client = client;
                context.broadcaster = broadcaster;
                broadcaster.AddClient(client);
                Thread clientThread = new Thread(ServerThread);
                clientThread.Start(context);
            }
        }
        public static bool ping(string ip, int port, int timeout = 2000)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var result = client.BeginConnect(ip, port, null, null);
                    var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeout));
                    if (success)
                    {
                        client.EndConnect(result);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (SocketException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An unexpected error occurred: {ex.Message}");
                return false;
            }

        }
        public static void ServerThread(object contextParam)
        {
            ConnectionContext context = (ConnectionContext)contextParam;
            TcpClient client = context.client;
            BinaryReader reader = new BinaryReader(client.GetStream());
            try
            {
                while (client.Connected)
                {
                    if (client.Available > 0)
                    {
                        string message = reader.ReadString();
                        Console.WriteLine($"<<< {message}");
                        broadcaster.Broadcast(message);
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
                
            }
            catch (Exception ex)
            {
                Program.matrix("Проблема с отправкой broadcast сообщения! " + ex.Message);
            }
        }

        public static void ClientThread(object clientParam)
        {
            TcpClient client = (TcpClient)clientParam;
            try
            {
                BinaryReader reader = new BinaryReader(client.GetStream());
                while (client.Connected)
                {
                    if (client.Available > 0)
                    {
                        string message = reader.ReadString();
                        Console.WriteLine("<<< " + message);
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.matrix("Не удалось обработать сообщение: " + ex.Message);
            }
        }
    }
}
