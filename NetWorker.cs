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
        private const string DO_AUTH_MESSAGE = "DoAuth()";
        private static bool isBusy = false;
        public static bool connected = false;
        public static string nickname = "";


        public static void TryConnect()
        {
            if (nickname.Length <= 0) nickname = filterNick(Program.ReadLineWithPrompt("Введите свой псевдоним: "));
            Program.matrix("Введите IP адрес сервера [localhost]: ");
            string ip = Console.ReadLine();
            if (String.IsNullOrEmpty(ip))
                ip = "localhost";

            Program.matrix("Введите порт сервера [9095]: ");
            int serverPort;
            if (!Int32.TryParse(Console.ReadLine(), out serverPort))
                serverPort = 9095;

            Program.waiting(">>> Попытка подключения к серверу: " + ip + ":" + serverPort, ip, serverPort);
            //NetWorker.DoConnect(ip, serverPort);
            Console.Clear();
            //DoConnect(ip, serverPort);
        }
        public static void DoConnect(string address, int port)
        {
            if (isBusy) 
                return;

            if (!isBusy) isBusy = true;
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
            Thread clientThread = new Thread(ClientThread);
            clientThread.Start(client);
            using (BinaryWriter writer = new BinaryWriter(client.GetStream()))
            {
                //writer.Write($"{nickname}  присоединился к хабу.");
                while (client.Connected)
                {
                    string message = Console.ReadLine();
                    Program.bufferClear();
                    //writer.Write($"[{nickname}]: {message}    ()"); //Временно
                    writer.Write(message);
                }
                Program.matrix("Пользователь отключился от сервера!");
                return;
            }
        }
        public static string filterNick(string name)
        {
            while ((name.IndexOfAny(@"!@#$%^&*()[]{}+, /\| ".ToCharArray()) != -1) || name.Length > 20 || name.Length < 3)
            {
                Program.matrix("Некорректный псевдоним");
                Console.Clear();
                Program.matrix("Введите свой псевдоним: ");
                name = Console.ReadLine();
            }
            return name;
        }
        async static void openPort(int port)
        {
            var discoverer = new NatDiscoverer();
            var device = await discoverer.DiscoverDeviceAsync();
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
                Client client = new Client(server.AcceptTcpClient());
                broadcaster.AddClient(client);
                Thread clientThread = new Thread(ServerThread);
                clientThread.Start(client);
            }
        }
        public static bool ping(string ip, int port, int timeout = 2000) //добавить время ответа
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
        public static void ServerThread(object incomingClient)
        {
            Client client = (Client)incomingClient;
            TcpClient pipe = client.TcpClient;
            NetworkStream sink = pipe.GetStream();

            if (String.IsNullOrWhiteSpace(client.nickname))
            {
                BinaryWriter writer = new BinaryWriter(sink);
                writer.Write(DO_AUTH_MESSAGE);
            }

            BinaryReader reader = new BinaryReader(sink);
            try
            {
                while (pipe.Connected)
                {
                    if (pipe.Available > 0)
                    {
                        string message = reader.ReadString();
                        if (String.IsNullOrWhiteSpace(client.nickname))
                        {
                            if (message.StartsWith("REPLY:"))
                            {
                                string raw = message.Replace("REPLY:", "");
                                string[] parsed = raw.Split(new char[] { '|' }, 2);
                                string nickname = parsed[0];
                                string ip = parsed[1];
                                client.nickname = nickname;
                                client.ipAddress = ip;
                                broadcaster.Broadcast(null, $"{nickname} подключился к хабу!");
                                continue;
                            }
                        }

                        string sender_nickname = client.nickname;
                        string sender_ip = client.ipAddress;
                        Console.WriteLine($"<<< {sender_nickname} [{sender_ip}]: {message}");
                        broadcaster.Broadcast(client, message);
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
                        if (DO_AUTH_MESSAGE.Equals(message))
                        {
                            string ip = new WebClient().DownloadString("https://api.ipify.org");
                            BinaryWriter writer = new BinaryWriter(client.GetStream());
                            writer.Write($"REPLY: {nickname}|{ip}");
                            continue;
                        }

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
