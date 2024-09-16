using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;

namespace TCPTunnel
{
    public class Menu
    {
        public static int top = Console.CursorTop;
        public static int left = Console.CursorLeft;
        const int centerX = (71 - 1) / 2;
        const int centerY = (16 - 1) / 2;
        bool skipped = false;
        public void mainMatrix(string text, int x = centerX, int y = centerY, int time = 1)
        {
            Console.SetCursorPosition(Math.Abs(x - (text.Length / 2)), y);
            Program.matrix(text);
            Thread.Sleep(time * 1000); // Перевод в секунды
            for (int j = text.Length - 1; j >= 0; j--)
            {
                Console.SetCursorPosition(Math.Abs(x - (text.Length / 2)), y);
                Console.Write(new string(' ', text.Length));
                Console.SetCursorPosition(Math.Abs(x - (text.Length / 2)), y);
                Console.Write(text.Substring(0, j));
                Thread.Sleep(20);
            }
            Console.SetCursorPosition(x, y);
            Console.Write(new string(' ', text.Length));
            Console.SetCursorPosition(x, y);

        }
        public static object[] splitIPByArg(List<string> list, string argument)
        {
            int index = list.IndexOf(argument);
            if (index == -1 || index + 1 >= list.Count)
            {
                return null;
            }
            string fullIP = list[index + 1];

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
                    return new object[] {
            ip,
            port
          };
                }
            }
            return null;
        }

        public void main(List<string> args)
        {
            args.Add("-skip");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WindowWidth = 71;
            Console.WindowHeight = 16;
            if (args.Count > 0)
            {
                if (args.Contains("-hi"))
                {
                    Console.WriteLine("sup)");
                    Thread.Sleep(500);
                }
                if (args.Contains("-create"))
                {
                    if (args[args.IndexOf("-create")] + 1 is string)
                    {
                        int.TryParse(args[args.IndexOf("-create")] + 1, out int port);
                        ServerInterface.doCreateServer(port);
                    }
                    else ServerInterface.tryCreateServer();
                }
                if (args.Contains("-nickname"))
                {
                    string testnick = args[args.IndexOf("-nickname") + 1];
                    NetWorker.nickname = NetWorker.filterNick(testnick);
                }
                if (args.Contains("-ping"))
                {
                    string ip = splitIPByArg(args, "-ping")[0].ToString();
                    int port = Convert.ToInt32(splitIPByArg(args, "-ping")[1]);
                    if (NetWorker.ping(ip, port) == true)
                    {
                        Program.matrix($"Сервер {ip}:{port} работает!.");
                    }
                    else Program.matrix($"Сервер {ip}:{port} мёртв.");
                    Console.ReadKey();
                    ConsoleGraphic.Clear(); ;
                }
                if (args.Contains("-connect"))
                {
                    if (splitIPByArg(args, "-connect").Length > 0)
                    {
                        string ip = splitIPByArg(args, "-connect")[0].ToString();
                        int port = Convert.ToInt32(splitIPByArg(args, "-connect")[1]);
                        UserInterface.DoConnect(ip, port);
                    }
                    Console.WriteLine("Вы допустили ошибку в параметре -connect");
                }
                if (args.Contains("-skip"))
                {
                    skipped = true;
                    goto main;
                }
            }
            mainMatrix("Добро пожаловать в чат", centerX, centerY);
        main:
            Program.bufferClear();
            Console.Title = "Меню";
            if (skipped) ConsoleGraphic.Clear(0, 0);
            else ConsoleGraphic.Clear();
            string[] choice = {
                "Создать сервер",
                "Войти на сервер",
                (NetWorker.nickname.Length <= 0) ? "Ввести псевдоним?" : ("Ваш текущий псевдоним: " + NetWorker.nickname),
                "Выход"
             };
            left += 10;
            top += 1;
            
            for (int i = 0; i < choice.Length; i++)
            {
                Console.SetCursorPosition(left, top+i);
                Program.matrix(choice[i]);
                Console.Write("\r\n");
            }

            int arrow = 0;
            bool isMenu = true;
            while (isMenu)
            {
                for (int i = 0; i < choice.Length; i++)
                {
                    if (i == arrow)
                    {
                        Console.BackgroundColor = Console.ForegroundColor;
                        Console.ForegroundColor = ConsoleColor.Black;
                    }
                    Console.SetCursorPosition(left, top+i);
                    Console.WriteLine(choice[i]);
                    Console.ResetColor();
                }
                switch (Console.ReadKey(true).Key)
                {
                    case ConsoleKey.RightArrow:
                    case ConsoleKey.DownArrow:
                        if (arrow < choice.Length - 1) arrow++;
                        continue;
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.UpArrow:
                        if (arrow > 0) arrow--;
                        continue;

                    case ConsoleKey.Escape:
                    default:

                        Console.Title = choice[Math.Abs(arrow)];
                        break;
                }

                if (arrow == 0)
                {
                    ServerInterface.tryCreateServer();
                }
                else if (arrow == 1)
                {
                    UserInterface.TryConnect();
                    isMenu = false;
                }
                else if (arrow == 2)
                {
                    Stopwatch stopwatch = new Stopwatch();
                    stopwatch.Start();
                    bool shitname = false;
                    ConsoleGraphic.Clear(); ;
                    mainMatrix("Добро пожаловать в процедуру смены ника в TCPTunnel");
                    Console.SetCursorPosition(0, Console.CursorTop);
                    Program.matrix("В свободном поле вы сможете задать себе ник:");
                    string testname = Console.ReadLine();
                    Console.Write($"{testname}");
                    Program.matrix("...\n", 500);
                    if (testname.IndexOfAny(@"!@#$%^&*()[]{}+, /\| ".ToCharArray()) != -1)
                    {
                        shitname = true;
                        Program.matrix("Ваш ник содержит недопустимые символы\n");
                    }
                    if (testname.Length > 20 || testname.Length < 3)
                    {
                        shitname = true;
                        Program.matrix("Ваш ник не в диапазоне от 3 до 20 символов\n");
                    }
                    if (shitname)
                    {
                        Program.matrix("Попробуй еще раз");
                        Console.ReadKey();
                        goto main;
                    }
                    stopwatch.Stop();
                    NetWorker.nickname = testname;
                    Program.matrix("Хорошее имя");
                    if (stopwatch.Elapsed.TotalSeconds > 25)
                    {
                        Program.matrix(", долго придумывал))))", 200);
                    }
                    Console.ReadKey();
                    goto main;
                }
                Program.bye();
            }

        }

    }
}