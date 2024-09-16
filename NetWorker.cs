using System;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using Open.Nat;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public class NetWorker
    {
        public const string DO_AUTH_MESSAGE = "DoAuth()";
        public static Broadcaster broadcaster = new Broadcaster();
        
        public static bool connected = false;
        public static string nickname = "";
        public static string filterNick(string name, bool force = false)
        {
            char[] illegalChars = @"!@#$%^&*()[]{}+, /\| ".ToCharArray();
            bool check = (name.IndexOfAny(illegalChars) != -1) || name.Length > 20 || name.Length < 3;
            while (check && force == false)
            {
                Program.matrix("Некорректный псевдоним", 20, ConsoleColor.DarkRed);
                Console.Clear();
                Program.matrix("Введите свой псевдоним: ", 20, ConsoleColor.DarkYellow);
                name = Console.ReadLine();
            }
            if(check && force == true )
            {
                name = new WebClient().DownloadString("https://api.ipify.org");
                Program.matrix($"\nНик не соответствовал требованиям, \nпоэтому мы изменили его за вас на ваш IP адрес :trollface: ({name})\n", 10);
            }
            return name;
        }
        public async static void openPort(int port)
        {
            var discoverer = new NatDiscoverer();
            var device = await discoverer.DiscoverDeviceAsync();

            try
            {
                await device.DeletePortMapAsync(new Mapping(Protocol.Tcp, port, port));
                await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port));
            }
            catch (MappingException ex)
            {
                Program.matrix(ex.ErrorText);
                Program.matrix("\nКто то занял порт, либо ваш антивирус не разрешает использовать UPnP\n");
                //if (port < 65535)
                //{
                //    Program.matrix("\nИзменяю текущий порт на 1 единицу выше.......\n", 50);
                //    Console.Clear();
                //    openPort(port + 1);
                //}
                //else
                //{
                //    Program.matrix("\nНе удалось открыть порт. Все порты заняты. Попробуйте отключить ваш антивирус\n");
                //}
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
                Console.WriteLine($"Упс... {ex.Message}");
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
                                client.nickname = filterNick(nickname, true);
                                client.ipAddress = ip;
                                broadcaster.Broadcast(null, $"{nickname} подключился к хабу!");
                                continue;
                            }
                        }

                        string sender_nickname = client.nickname;
                        string sender_ip = client.ipAddress;
                        Console.WriteLine($">>> [{client.nickname}]:{message} ({client.ipAddress})");
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

        
    }
}
