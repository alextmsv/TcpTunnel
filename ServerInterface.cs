using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using System;

namespace TCPTunnel
{
    public class ServerInterface : NetWorker
    {
        public static void tryCreateServer()
        {
            Program.matrix("Введите порт сервера: ");
            int port;
            if (!Int32.TryParse(Console.ReadLine(), out port))
                port = 9091;
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
            Program.matrix("Сервер запущен!\r\n");
            Process.Start(Application.ExecutablePath);
            while (true)
            {
                Client client = new Client(server.AcceptTcpClient());
                broadcaster.AddClient(client);
                Thread clientThread = new Thread(ServerThread);
                clientThread.Start(client);
                //switch (Console.ReadKey(true).Key)
                //{
                //    case ConsoleKey.Escape:
                //        keyCounter++;
                //        if (keyCounter == 1)
                //        {
                //            Program.matrix("\nВнимание! ");
                //            Program.matrix("Если вы нажмете на Escape еще раз, то сервер выключится!", 5);
                //        }
                //        else if (keyCounter >= 2)
                //        {
                //            server.Stop();
                //        }
                //        break;
                //    default:
                //        break;
                //}
            }
            
        }
    }
}
