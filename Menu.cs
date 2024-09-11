using System;
using System.Collections.Generic;
using System.Threading;

namespace TCPTunnel
{
    public class Menu
    {
        
        public static int top = Console.CursorTop;
        public static int left = Console.CursorLeft;
        int centerX = 24;
        int centerY = 7;
        public void mainMatrix(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                Console.Write(text[i]);
                Thread.Sleep(20);
            }
            Console.Clear();
            for (int j = text.Length - 1; j <= text.Length - 1; j--)
            {
                if (j < 0 || j - 1 < 0) break;
                text = text.TrimEnd(text[j]);
                Console.SetCursorPosition(centerX, centerY);
                Console.WriteLine(text);
                Thread.Sleep(20);
                Program.bufferClear();
            }
        }
        public static object[] splitIPByArg(List<string> list, string argument)
        {
            int index = list.IndexOf(argument);
            if (index == -1 || index + 1 >= list.Count)
            {
                return null;
            }
            string fullIP = list[index+1];

            if (fullIP == null)
            {
                return null;
            }

            string[] splitIP = fullIP.Split(':');
            if (splitIP.Length == 2)
            {
                string ip = splitIP[0];
                if (int.TryParse(splitIP[1], out int port))
                {
                    return new object[] { ip, port };
                }
            }
            return null;
        }
        
        public void main(List<string> args) {
            Console.WindowWidth = 71;
            Console.WindowHeight = 16;
            if (args.Count>0) {
                if (args.Contains("-hi"))
                {
                    Console.WriteLine("sup)");
                    Thread.Sleep(500);
                }
                if (args.Contains("-create"))
                {
                    if (args[args.IndexOf("-create")]+1 is string)
                    {
                        int.TryParse(args[args.IndexOf("-create")] + 1, out int port);
                        NetWorker.doCreateServer(port);
                    }
                    else NetWorker.tryCreateServer();
                }
                if (args.Contains("-nickname"))
                {
                    NetWorker.nickname = args[args.IndexOf("-nickname") + 1];
                }
                if (args.Contains("-ping"))
                {
                    string ip = splitIPByArg(args, "-ping")[0].ToString();
                    int port = Convert.ToInt32(splitIPByArg(args, "-ping")[1]);
                    if (NetWorker.ping(ip, port) == true)
                    {
                        Program.matrix($"Сервер с IP: {ip}:{port} работает, присоединяйтесь.");
                    }
                    else Program.matrix($"Сервер с IP: {ip}:{port} мёртв.");
                    Console.ReadKey();
                    Console.Clear();
                }
                if (args.Contains("-connect"))
                {
                    if (splitIPByArg(args, "-connect").Length > 0) 
                    {
                        string ip = splitIPByArg(args, "-connect")[0].ToString();
                        int port = Convert.ToInt32(splitIPByArg(args, "-connect")[1]);
                        NetWorker.DoConnect(ip, port);
                    }
                    Console.WriteLine("Вы допустили ошибку в параметре -connect");
                }
                if (args.Contains("-skip")) goto main;
            }
            Console.SetCursorPosition(centerX, centerY);
            mainMatrix("Добро пожаловать в чат");
            main:
                Program.bufferClear();
                Console.Title = "Меню";
                Console.Clear();
                string[] choice = { "Создать сервер", "Войти на сервер", "Выход" };
                Console.SetCursorPosition(0, 0);
                for(int i = 0; i<choice.Length; i++)
                {
                    Program.matrix(choice[i]);
                    Console.Write("\r\n");
                }
                int arrow=0;
                bool isMenu = true;
                while (isMenu)
                {
                    Console.SetCursorPosition(0, 0);
                    for (int i = 0; i < choice.Length; i++)
                    {
                        if (i == arrow) {
                            Console.BackgroundColor = Console.ForegroundColor;
                            Console.ForegroundColor = ConsoleColor.Black;
                        }
                        Console.WriteLine(choice[i]);
                        Console.ResetColor();
                    }
                    switch (Console.ReadKey(true).Key.ToString())
                    {
                        case "DownArrow":
                            if (arrow < choice.Length - 1) arrow++;
                            continue;

                        case "UpArrow":
                            if (arrow > 0) arrow--;
                            continue;

                        case "Escape":
                        default:
                            Program.bufferClear();
                            Console.Title = choice[Math.Abs(arrow)];
                            break;
                    }
                    if (arrow == 0)
                    {
                        NetWorker.tryCreateServer();
                    } else if(arrow == 1)
                    {
                        NetWorker.TryConnect();
                        isMenu = false;
                    }
                    Program.bye();
                }

        } 

    }
}
