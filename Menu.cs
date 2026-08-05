using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;

namespace TCPTunnel
{
    public class Menu
    {
        ConsoleGraphic graphic = new ConsoleGraphic();
        public static int top;
        public static int left;
        const int centerX = (71 - 1) / 2;
        const int centerY = (16 - 1) / 2;
        const int selectionAnimationDelay = 4;
        const int selectionMarkerBlinkMilliseconds = 450;
        const int resizePollMilliseconds = 30;
        const int resizeSettleMilliseconds = 180;
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
        bool graphicsOptionsAvailable = true;
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
            Thread.Sleep(time * 1000);
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
                        ConsoleGraphic.WriteContentLine(Lang.Get(TextId.PingArgument));
                    }
                    else
                    {
                        string ip = endpoint[0].ToString();
                        int port = Convert.ToInt32(endpoint[1]);
                        Program.matrix(NetWorker.ping(ip, port)
                            ? Lang.Get(TextId.ServerAlive, ip, port)
                            : Lang.Get(TextId.ServerDead, ip, port));
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
                        ConsoleGraphic.WriteContentLine(Lang.Get(TextId.ConnectArgument));
                    }
                }
                if (args.Contains("-skip"))
                {
                    skipped = true;
                    goto main;
                }
            }
            mainMatrix(Lang.Get(TextId.Welcome), centerX, centerY);
        main:
            ConsoleGraphic.SetReservedBottomRows(0);
            Program.bufferClear();
            ConsoleTitleAnimator.SetCaption(Lang.Get(TextId.MenuTitle), ConsoleGraphic.Enabled);
            if (skipped) graphic.Clear(0, 0);
            else graphic.Clear();
            var choiceList = new List<string> {
                Lang.Get(ServerInterface.IsRunning ? TextId.EnterOwnHub : TextId.HostServer),
                Lang.Get(TextId.ConnectToHub),
                (NetWorker.nickname.Length <= 0) ? Lang.Get(TextId.EnterNickname) : Lang.Get(TextId.CurrentNickname, NetWorker.nickname)
            };
            int graphicsOptionsIndex = -1;
            if (graphicsOptionsAvailable)
            {
                graphicsOptionsIndex = choiceList.Count;
                choiceList.Add(Lang.Get(TextId.GraphicsOptions));
            }
            int languageIndex = choiceList.Count;
            choiceList.Add(Lang.Get(TextId.LanguageMenu));
            int exitIndex = choiceList.Count;
            choiceList.Add(Lang.Get(TextId.Exit));
            string[] choice = choiceList.ToArray();
            left = 10;
            top = 1;

            int arrow = 0;
            bool isMenu = true;

            for (int i = 0; i < choice.Length; i++)
                DrawChoice(choice[i], i, i == arrow, false);

            while (isMenu)
            {
                ConsoleKey key = ReadMenuKey(choice, arrow);
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

                        ConsoleTitleAnimator.SetCaption(choice[Math.Abs(arrow)], ConsoleGraphic.Enabled);
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
                    PrepareActionScreen(choice[arrow]);
                    if (ConsoleGraphic.Enabled)
                        ChangeNicknameGraphical();
                    else
                        ChangeNicknamePlain();
                    goto main;
                }
                else if (arrow == graphicsOptionsIndex)
                {
                    ShowConsoleGraphicsOptions();
                    skipped = true;
                    goto main;
                }
                else if (arrow == languageIndex)
                {
                    Lang.Toggle();
                    skipped = true;
                    goto main;
                }
                else if (arrow == exitIndex)
                {
                    PrepareActionScreen(choice[arrow]);
                    Program.bye();
                }
            }

        }

        private void ChangeNicknamePlain()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            int inputTop = Console.CursorTop;
            mainMatrix(
                Lang.Get(TextId.ChangeNicknameWelcome),
                centerX,
                centerY,
                0,
                3,
                3);
            Console.SetCursorPosition(2, inputTop++);
            Program.matrix(Lang.Get(TextId.EnterNewNickname) + ": ");
            string testname = Console.ReadLine();
            Console.SetCursorPosition(2, inputTop++);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(testname);
            Program.matrix("...\n", 500, ConsoleColor.DarkGray);
            if (!NetWorker.IsNicknameValid(testname))
            {
                Program.matrix(Lang.Get(TextId.NicknameRules) + "\n");
                Program.matrix(Lang.Get(TextId.TryAgain));
                Console.ReadKey();
                return;
            }

            stopwatch.Stop();
            NetWorker.nickname = testname;
            Console.SetCursorPosition(2, inputTop += 2);
            Program.matrix(Lang.Get(TextId.GoodName) + "\n", 8, ConsoleColor.Green);
            if (stopwatch.Elapsed.TotalSeconds > 25)
                Program.matrix(Lang.Get(TextId.TookYourTime), 10);

            Thread.Sleep(1500);
        }

        private void ChangeNicknameGraphical()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            int titleRow = ConsoleGraphic.ContentTop + 1;
            int hintRow = ConsoleGraphic.ContentTop + 3;
            int inputRow = ConsoleGraphic.ContentTop + 5;

            ConsoleGraphic.WriteCenteredLine(Lang.Get(TextId.ChangeIdentity), titleRow, ConsoleColor.Cyan, true, 3);
            ConsoleGraphic.WriteCenteredLine(
                Lang.Get(TextId.EnterNewNickname),
                hintRow,
                ConsoleColor.DarkGray);
            ConsoleGraphic.WriteCenteredLine("> ", inputRow, ConsoleColor.Cyan);

            string testname = Console.ReadLine();
            for (int dots = 1; dots <= 3; dots++)
            {
                ConsoleGraphic.WriteBottomStatus(
                    Lang.Get(TextId.CheckingName, new string('.', dots)),
                    ConsoleColor.Yellow);
                Thread.Sleep(70);
            }

            if (!NetWorker.IsNicknameValid(testname))
            {
                ConsoleGraphic.WriteCenteredLine(
                    Lang.Get(TextId.NicknameRules),
                    hintRow + 2,
                    ConsoleColor.Red);
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.AuthInvalidNickname), ConsoleColor.Red);
                Console.ReadKey(true);
                return;
            }

            stopwatch.Stop();
            NetWorker.nickname = testname;
            graphic.Clear(0, 0);
            ConsoleGraphic.WriteCenteredLine(Lang.Get(TextId.IdentityUpdated), titleRow, ConsoleColor.Cyan, true, 3);
            ConsoleGraphic.WriteCenteredLine(testname, inputRow, ConsoleColor.White, true, 4);
            ConsoleGraphic.WriteCenteredLine(
                new string('-', Math.Max(8, Math.Min(24, testname.Length + 4))),
                inputRow + 1,
                ConsoleColor.DarkGray);

            ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.GoodName), ConsoleColor.Green);
            Thread.Sleep(120);
            ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.GoodName), ConsoleColor.DarkGreen);
            Thread.Sleep(120);
            ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.GoodName), ConsoleColor.Green);
            if (stopwatch.Elapsed.TotalSeconds > 25)
                ConsoleGraphic.WriteCenteredLine(Lang.Get(TextId.TookYourTime), inputRow + 3, ConsoleColor.DarkGray);

            Thread.Sleep(1260);
        }

        private ConsoleKey ReadMenuKey(string[] choices, int selectedIndex)
        {
            ConsoleGraphic.ConsoleGeometry knownGeometry;
            bool hasKnownGeometry = ConsoleGraphic.TryCaptureConsoleGeometry(out knownGeometry);
            ConsoleGraphic.ConsoleGeometry pendingGeometry = new ConsoleGraphic.ConsoleGeometry();
            bool resizePending = false;
            long stableSince = 0;
            long settleTicks = Math.Max(1L, Stopwatch.Frequency * resizeSettleMilliseconds / 1000L);
            long blinkTicks = Math.Max(1L, Stopwatch.Frequency * selectionMarkerBlinkMilliseconds / 1000L);
            long nextMarkerBlink = Stopwatch.GetTimestamp() + blinkTicks;
            bool markerVisible = true;

            while (true)
            {
                try
                {
                    if (Console.KeyAvailable)
                        return Console.ReadKey(true).Key;
                }
                catch (ArgumentOutOfRangeException)
                {
                }
                catch (System.IO.IOException)
                {
                }

                Thread.Sleep(resizePollMilliseconds);
                ConsoleGraphic.ConsoleGeometry currentGeometry;
                if (!ConsoleGraphic.TryCaptureConsoleGeometry(out currentGeometry))
                    continue;

                long now = Stopwatch.GetTimestamp();
                if (hasKnownGeometry &&
                    currentGeometry.IsSameAs(knownGeometry) &&
                    !resizePending)
                {
                    if (ConsoleGraphic.Enabled && now >= nextMarkerBlink)
                    {
                        markerVisible = !markerVisible;
                        ConsoleGraphic.DrawMenuSelectionMarker(
                            selectedIndex,
                            left,
                            top,
                            markerVisible);
                        nextMarkerBlink = now + blinkTicks;
                    }

                    continue;
                }

                if (!resizePending || !currentGeometry.IsSameAs(pendingGeometry))
                {
                    pendingGeometry = currentGeometry;
                    resizePending = true;
                    stableSince = now;
                    continue;
                }

                if (now - stableSince < settleTicks)
                    continue;

                bool frameCompleted = graphic.TryClear(0, 0);
                left = 10;
                top = 1;
                for (int index = 0; index < choices.Length; index++)
                    frameCompleted &= DrawChoice(choices[index], index, index == selectedIndex, false);

                ConsoleGraphic.ConsoleGeometry renderedGeometry;
                if (frameCompleted &&
                    ConsoleGraphic.TryCaptureConsoleGeometry(out renderedGeometry) &&
                    renderedGeometry.IsSameAs(pendingGeometry))
                {
                    knownGeometry = renderedGeometry;
                    hasKnownGeometry = true;
                    resizePending = false;
                    markerVisible = true;
                    nextMarkerBlink = Stopwatch.GetTimestamp() + blinkTicks;
                }
                else
                {
                    stableSince = Stopwatch.GetTimestamp();
                }
            }
        }

        private static void AnimateSelection(string[] choices, int previousIndex, int currentIndex)
        {
            if (ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.DrawMenuSelectionMarker(previousIndex, left, top, false);
                ConsoleGraphic.DrawMenuSelectionMarker(currentIndex, left, top, true);
            }

            DrawChoice(choices[previousIndex], previousIndex, false, true);
            DrawChoice(choices[currentIndex], currentIndex, true, true);
        }

        private static bool DrawChoice(string text, int index, bool selected, bool animate)
        {
            return ConsoleGraphic.DrawMenuOption(
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
                    Lang.Get(ConsoleGraphic.Enabled ? TextId.GraphicsEnabled : TextId.GraphicsDisabled),
                    Lang.Get(TextId.Customization),
                    Lang.Get(TextId.Back)
                };
                int selection = ReadOptionsSelection(Lang.Get(TextId.GraphicsOptions), choices, selectedOption);
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
                string[] choices = { Lang.Get(TextId.Snake), Lang.Get(TextId.Back) };
                int selection = ReadOptionsSelection(Lang.Get(TextId.Customization), choices, selectedOption);
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
                    Lang.Get(TextId.Speed, GetSnakeSpeedName(ConsoleGraphic.BorderAnimationDelayMilliseconds)),
                    Lang.Get(TextId.Color, GetSnakeColorName(ConsoleGraphic.BorderSnakeColor)),
                    Lang.Get(TextId.Back)
                };
                int selection = ReadOptionsSelection(Lang.Get(TextId.SnakeCustomization), choices, selectedOption);
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
            ConsoleTitleAnimator.SetCaption(title, ConsoleGraphic.Enabled);
            graphic.Clear(0, 0);
            left = 10;
            top = 1;
            int arrow = Math.Max(0, Math.Min(selectedOption, choices.Length - 1));

            for (int index = 0; index < choices.Length; index++)
                DrawChoice(choices[index], index, index == arrow, false);

            while (true)
            {
                ConsoleKey key = ReadMenuKey(choices, arrow);
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
                case 35: return Lang.Get(TextId.SpeedFast);
                case 75: return Lang.Get(TextId.SpeedNormal);
                case 125: return Lang.Get(TextId.SpeedCalm);
                case 200: return Lang.Get(TextId.SpeedSlow);
                default: return delayMilliseconds + (Lang.Current == AppLanguage.Russian ? " мс" : " ms");
            }
        }

        private static string GetSnakeColorName(ConsoleColor color)
        {
            switch (color)
            {
                case ConsoleColor.Green: return Lang.Get(TextId.ColorGreen);
                case ConsoleColor.Cyan: return Lang.Get(TextId.ColorCyan);
                case ConsoleColor.Yellow: return Lang.Get(TextId.ColorYellow);
                case ConsoleColor.Red: return Lang.Get(TextId.ColorRed);
                case ConsoleColor.White: return Lang.Get(TextId.ColorWhite);
                case ConsoleColor.Blue: return Lang.Get(TextId.ColorBlue);
                default: return color.ToString();
            }
        }

        private void PrepareActionScreen(string title)
        {
            ConsoleTitleAnimator.SetCaption(title, ConsoleGraphic.Enabled);
            graphic.Clear(0, 0);
        }

        private void ApplyGraphicsArguments(List<string> args)
        {
            int noGraphicsIndex = args.FindIndex(argument =>
                argument.Equals("-no-graphics", StringComparison.OrdinalIgnoreCase));
            if (noGraphicsIndex >= 0)
            {
                graphicsOptionsAvailable = false;
                ConsoleGraphic.Enabled = false;
                return;
            }

            graphicsOptionsAvailable = true;

            int graphicsIndex = args.FindIndex(argument =>
                argument.Equals("-graphics", StringComparison.OrdinalIgnoreCase));
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
