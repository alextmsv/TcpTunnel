using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TCPTunnel
{
    public class UserInterface : NetWorker
    {
        static ConsoleGraphic graphic = new ConsoleGraphic();
        private static bool isBusy = false;
        public static void ClientThread(object clientParam)
        {
            Console.CursorTop = Menu.top + 10;
            Console.CursorLeft = Menu.left + 1;
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
        public static void TryConnect()
        {
            Program.matrix("Начнем... Введите свой псевдоним: ");
            string username = Console.ReadLine();
            if (nickname.Length <= 0) nickname = filterNick(username, true);
            Program.matrix("Введите IP адрес сервера [localhost]: ");
            string ip = Console.ReadLine();
            if (String.IsNullOrEmpty(ip))
                ip = "localhost";
            
            Program.matrix("Введите порт сервера [9091]: ");
            int serverPort;
            if (!Int32.TryParse(Console.ReadLine(), out serverPort))
                serverPort = 9091;

            Program.waiting(">>> Попытка подключения к серверу: " + ip + ":" + serverPort, ip, serverPort);

        }
        public static void DoConnect(string address, int port)
        {
            Console.CursorTop = Menu.top + 10;
            Console.CursorLeft = Menu.left + 1;
            if (isBusy)
                return;

            if (!isBusy) isBusy = true;
            TcpClient client = new TcpClient();
            try
            {
                client.Connect(address, port);
                connected = true;
                graphic.Clear();
                Program.bufferClear();
            }
            catch
            {
                Program.matrix("Не удалось подключиться к " + address, 10);
                return;
            }
            ConnectionContext publicName = new ConnectionContext();
            Thread clientThread = new Thread(ClientThread);
            clientThread.Start(client);
            using (BinaryWriter writer = new BinaryWriter(client.GetStream()))
            {
                while (client.Connected)
                {
                    string message = Console.ReadLine();
                    Program.bufferClear();
                    writer.Write(message);
                }
                Program.matrix($"Пользователь {nickname} отключился от сервера!", 10);
                return;
            }
        }
    }
}
