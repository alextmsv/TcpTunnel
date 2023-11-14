using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;

namespace TCPTunnel
{
    public class NetWorker
    {
        private static Broadcaster broadcaster = new Broadcaster();

        public static void DoConnect(string address, int port)
        {
            TcpClient client = new TcpClient();
            try
            {
                client.Connect(address, port);
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
            Program.matrix("Введите свой псевдоним:  ");
            string nickname = Console.ReadLine();
            Program.bufferClear();
            while ((nickname.IndexOfAny(@"!@#$%^&*()[]{}+, /\| ".ToCharArray()) != -1) || nickname.Length > 20 || nickname.Length < 3)
            {
                Program.matrix("Некорректный псевдоним");
                Console.Clear();
                Program.matrix("Введите свой псевдоним:  ");
                nickname = Console.ReadLine();
            } 
            using (BinaryWriter writer = new BinaryWriter(client.GetStream()))
            {
                writer.Write($"{nickname} присоединился к хабу.");
                while (client.Connected)
                {
                    string message = Console.ReadLine();
                    Program.bufferClear();
                    writer.Write($"[{nickname}]: {message}    ({Dns.GetHostByName(Dns.GetHostName()).AddressList[1]})");
                }
                Program.matrix("Пользователь отключился от сервера!");
                return;
            }
        }
        
        public static void doCreateServer(int port)
        {
            TcpListener server = new TcpListener(IPAddress.Any, port);
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
