using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;

namespace TCPTunnel
{
    public class Menu
    {
        ConsoleGraphic graphic = new ConsoleGraphic();
        public static int top = Console.CursorTop;
        public static int left = Console.CursorLeft;
        const int centerX = (71 - 1) / 2;
        const int centerY = (16 - 1) / 2;
        const int selectionAnimationDelay = 4;
        static readonly int[] snakeSpeeds = { 35, 75, 125, 200 };
        static readonly ConsoleColor[] snakeColors = {
            ConsoleColor.Green,
            ConsoleColor.Cyan,
            ConsoleColor.Yellow,
            ConsoleColor.Red,
            ConsoleColor.White,
            ConsoleColor.Blue
        };
        bool skipped = false;
        public void mainMatrix(
            string text,
            int x = centerX,
            int y = centerY,
            int time = 1,
            int symbolDelay = 20,
            int eraseDelay = 20)
        {
            int startX = Math.Max(ConsoleGraphic.ContentLeft, x - text.Length / 2);
            Console.SetCursorPosition(startX, y);
            Program.matrix(text, symbolDelay, ConsoleColor.White, false);
            Thread.Sleep(time * 1000); // Перевод в секунды
            for (int j = text.Length - 1; j >= 0; j--)
            {
                Console.SetCursorPosition(startX, y);
                Console.Write(new string(' ', text.Length));
                Console.SetCursorPosition(startX, y);
                Console.Write(text.Substring(0, j));
                Thread.Sleep(eraseDelay);
            }
            Console.SetCursorPosition(startX, y);
            Console.Write(new string(' ', text.Length));
            Console.SetCursorPosition(startX, y);

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
            ConsoleGraphic.ConfigureConsole(71, 16);
            ApplyGraphicsArguments(args);
            args.Add("-skip");
            Console.ForegroundColor = ConsoleColor.White;
            if (args.Count > 0)
            {
                if (args.Contains("-hi"))
                {
                    ConsoleGraphic.WriteContentLine("sup)");
                    Thread.Sleep(500);
                }
                if (args.Contains("-nickname"))
                {
                    int nicknameIndex = args.IndexOf("-nickname");
                    if (nicknameIndex + 1 < args.Count)
                        NetWorker.nickname = NetWorker.filterNick(args[nicknameIndex + 1]);
                }
                if (args.Contains("-create"))
                {
                    int createIndex = args.IndexOf("-create");
                    int port;
                    if (createIndex + 1 < args.Count && Int32.TryParse(args[createIndex + 1], out port))
                        ServerInterface.doCreateServer(port);
                    else
                        ServerInterface.tryCreateServer();
                }
                if (args.Contains("-ping"))
                {
                    object[] endpoint = splitIPByArg(args, "-ping");
                    if (endpoint == null)
                    {
                        ConsoleGraphic.WriteContentLine("Параметр -ping ожидает адрес в формате host:port.");
                    }
                    else
                    {
                        string ip = endpoint[0].ToString();
                        int port = Convert.ToInt32(endpoint[1]);
                        Program.matrix(NetWorker.ping(ip, port)
                            ? $"Сервер {ip}:{port} работает!."
                            : $"Сервер {ip}:{port} мёртв.");
                        Console.ReadKey();
                        graphic.Clear();
                    }
                }
                if (args.Contains("-connect"))
                {
                    object[] endpoint = splitIPByArg(args, "-connect");
                    if (endpoint != null)
                    {
                        string ip = endpoint[0].ToString();
                        int port = Convert.ToInt32(endpoint[1]);
                        UserInterface.DoConnect(ip, port);
                    }
                    else
                    {
                        ConsoleGraphic.WriteContentLine("Параметр -connect ожидает адрес в формате host:port.");
                    }
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
            Console.Title = "--------------------------------------Меню----------------------------------------------";
            if (skipped) graphic.Clear(0, 0);
            else graphic.Clear();
            string[] choice = {
                ServerInterface.IsRunning ? "Войти в свой хаб" : "Создать сервер",
                "Войти на сервер",
                (NetWorker.nickname.Length <= 0) ? "Ввести псевдоним?" : ("Ваш текущий псевдоним: " + NetWorker.nickname),
                "ConsoleGraphics Options",
                "Выход"
             };
            left = 10;
            top = 1;

            int arrow = 0;
            bool isMenu = true;

            for (int i = 0; i < choice.Length; i++)
                DrawChoice(choice[i], i, i == arrow, false);

            while (isMenu)
            {
                ConsoleKey key = Console.ReadKey(true).Key;
                int previousArrow = arrow;

                switch (key)
                {
                    case ConsoleKey.RightArrow:
                    case ConsoleKey.DownArrow:
                        arrow = (arrow + 1) % choice.Length;
                        AnimateSelection(choice, previousArrow, arrow);
                        continue;
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.UpArrow:
                        arrow = (arrow - 1 + choice.Length) % choice.Length;
                        AnimateSelection(choice, previousArrow, arrow);
                        continue;

                    case ConsoleKey.Escape:
                    default:

                        Console.Title = $"-----------------------------------{choice[Math.Abs(arrow)]}----------------------------------------------";
                        break;
                }
                if (arrow == 0)
                {
                    PrepareActionScreen(choice[arrow]);
                    if (ServerInterface.IsRunning)
                        UserInterface.DoConnect("127.0.0.1", ServerInterface.ListeningPort, 1);
                    else
                        ServerInterface.tryCreateServer();
                    goto main;
                }
                else if (arrow == 1)
                {
                    PrepareActionScreen(choice[arrow]);
                    UserInterface.TryConnect();
                    goto main;
                }
                else if (arrow == 2)
                {
                    Stopwatch stopwatch = new Stopwatch();
                    stopwatch.Start();
                    PrepareActionScreen(choice[arrow]);
                    int top = Console.CursorTop;
                    mainMatrix(
                        "Добро пожаловать в процедуру смены ника в TCPTunnel",
                        centerX,
                        centerY,
                        0,
                        3,
                        3);
                    Console.SetCursorPosition(2, top++);
                    Program.matrix("В свободном поле вы сможете задать себе ник: ");
                    string testname = Console.ReadLine();
                    Console.SetCursorPosition(2, top++);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(testname);
                    Program.matrix("...\n", 500, ConsoleColor.DarkGray);
                    if (!NetWorker.IsNicknameValid(testname))
                    {
                        Program.matrix("Ник должен содержать от 3 до 20 символов без пробелов и спецсимволов\n");
                        Program.matrix("Попробуй еще раз");
                        Console.ReadKey();
                        goto main;
                    }
                    stopwatch.Stop();
                    NetWorker.nickname = testname;
                    Console.SetCursorPosition(2, top+=2);
                    Program.matrix("Хорошее имя\n", 8, ConsoleColor.Green);
                    if (stopwatch.Elapsed.TotalSeconds > 25)
                    {
                        Program.matrix(", долго придумывал))))", 10);
                    }
                    Thread.Sleep(1500);
                    goto main;
                }
                else if (arrow == 3)
                {
                    ShowConsoleGraphicsOptions();
                    skipped = true;
                    goto main;
                }
                else
                {
                    PrepareActionScreen(choice[arrow]);
                    Program.bye();
                }
            }

        }

        private static void AnimateSelection(string[] choices, int previousIndex, int currentIndex)
        {
            DrawChoice(choices[previousIndex], previousIndex, false, true);
            DrawChoice(choices[currentIndex], currentIndex, true, true);
        }

        private static void DrawChoice(string text, int index, bool selected, bool animate)
        {
            ConsoleGraphic.DrawMenuOption(
                text,
                index,
                left,
                top,
                selected,
                animate && ConsoleGraphic.Enabled,
                selectionAnimationDelay);
        }

        private void ShowConsoleGraphicsOptions()
        {
            int selectedOption = 0;
            while (true)
            {
                string[] choices = {
                    "ConsoleGraphics: " + (ConsoleGraphic.Enabled ? "включена" : "выключена"),
                    "Кастомизация",
                    "Назад"
                };
                int selection = ReadOptionsSelection("ConsoleGraphics Options", choices, selectedOption);
                if (selection < 0 || selection == 2)
                    return;

                selectedOption = selection;

                if (selection == 0)
                    ConsoleGraphic.Enabled = !ConsoleGraphic.Enabled;
                else
                    ShowCustomizationOptions();
            }
        }

        private void ShowCustomizationOptions()
        {
            int selectedOption = 0;
            while (true)
            {
                string[] choices = { "Змейка", "Назад" };
                int selection = ReadOptionsSelection("Кастомизация", choices, selectedOption);
                if (selection < 0 || selection == 1)
                    return;

                selectedOption = selection;

                ShowSnakeOptions();
            }
        }

        private void ShowSnakeOptions()
        {
            int selectedOption = 0;
            while (true)
            {
                string[] choices = {
                    "Скорость: " + GetSnakeSpeedName(ConsoleGraphic.BorderAnimationDelayMilliseconds),
                    "Цвет: " + GetSnakeColorName(ConsoleGraphic.BorderSnakeColor),
                    "Назад"
                };
                int selection = ReadOptionsSelection("Кастомизация змейки", choices, selectedOption);
                if (selection < 0 || selection == 2)
                    return;

                selectedOption = selection;

                if (selection == 0)
                    ConsoleGraphic.BorderAnimationDelayMilliseconds = GetNextSnakeSpeed();
                else
                    ConsoleGraphic.BorderSnakeColor = GetNextSnakeColor();
            }
        }

        private int ReadOptionsSelection(string title, string[] choices, int selectedOption)
        {
            Console.Title = "-----------------------------------" + title + "----------------------------------------------";
            graphic.Clear(0, 0);
            left = 10;
            top = 1;
            int arrow = Math.Max(0, Math.Min(selectedOption, choices.Length - 1));

            for (int index = 0; index < choices.Length; index++)
                DrawChoice(choices[index], index, index == arrow, false);

            while (true)
            {
                ConsoleKey key = Console.ReadKey(true).Key;
                int previousArrow = arrow;
                if (key == ConsoleKey.DownArrow || key == ConsoleKey.RightArrow)
                {
                    arrow = (arrow + 1) % choices.Length;
                    AnimateSelection(choices, previousArrow, arrow);
                }
                else if (key == ConsoleKey.UpArrow || key == ConsoleKey.LeftArrow)
                {
                    arrow = (arrow - 1 + choices.Length) % choices.Length;
                    AnimateSelection(choices, previousArrow, arrow);
                }
                else if (key == ConsoleKey.Enter || key == ConsoleKey.Spacebar)
                {
                    return arrow;
                }
                else if (key == ConsoleKey.Escape)
                {
                    return -1;
                }
            }
        }

        private static int GetNextSnakeSpeed()
        {
            int current = ConsoleGraphic.BorderAnimationDelayMilliseconds;
            for (int index = 0; index < snakeSpeeds.Length; index++)
            {
                if (snakeSpeeds[index] == current)
                    return snakeSpeeds[(index + 1) % snakeSpeeds.Length];
            }
            return snakeSpeeds[0];
        }

        private static ConsoleColor GetNextSnakeColor()
        {
            ConsoleColor current = ConsoleGraphic.BorderSnakeColor;
            for (int index = 0; index < snakeColors.Length; index++)
            {
                if (snakeColors[index] == current)
                    return snakeColors[(index + 1) % snakeColors.Length];
            }
            return snakeColors[0];
        }

        private static string GetSnakeSpeedName(int delayMilliseconds)
        {
            switch (delayMilliseconds)
            {
                case 35: return "быстрая (35 мс)";
                case 75: return "обычная (75 мс)";
                case 125: return "спокойная (125 мс)";
                case 200: return "медленная (200 мс)";
                default: return delayMilliseconds + " мс";
            }
        }

        private static string GetSnakeColorName(ConsoleColor color)
        {
            switch (color)
            {
                case ConsoleColor.Green: return "зелёный";
                case ConsoleColor.Cyan: return "голубой";
                case ConsoleColor.Yellow: return "жёлтый";
                case ConsoleColor.Red: return "красный";
                case ConsoleColor.White: return "белый";
                case ConsoleColor.Blue: return "синий";
                default: return color.ToString();
            }
        }

        private void PrepareActionScreen(string title)
        {
            Console.Title = $"-----------------------------------{title}----------------------------------------------";
            graphic.Clear(0, 0);
        }

        private static void ApplyGraphicsArguments(List<string> args)
        {
            if (args.Contains("-no-graphics"))
                ConsoleGraphic.Enabled = false;

            int graphicsIndex = args.IndexOf("-graphics");
            if (graphicsIndex < 0 || graphicsIndex + 1 >= args.Count)
                return;

            string value = args[graphicsIndex + 1];
            if (value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleGraphic.Enabled = true;
            }
            else if (value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                     value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                     value.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleGraphic.Enabled = false;
            }
        }

    }
}
