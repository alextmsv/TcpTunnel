using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Diagnostics;
using System.Text;

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
            ConsoleColor.DarkBlue, ConsoleColor.DarkGreen, ConsoleColor.DarkCyan,
            ConsoleColor.DarkRed, ConsoleColor.DarkMagenta, ConsoleColor.DarkYellow,
            ConsoleColor.Gray, ConsoleColor.Blue, ConsoleColor.Green, ConsoleColor.Cyan,
            ConsoleColor.Red, ConsoleColor.Magenta, ConsoleColor.Yellow, ConsoleColor.White
        };
        static readonly ConsoleColor[] interfaceColors = {
            ConsoleColor.DarkBlue, ConsoleColor.DarkGreen, ConsoleColor.DarkCyan,
            ConsoleColor.DarkRed, ConsoleColor.DarkMagenta, ConsoleColor.DarkYellow,
            ConsoleColor.Gray, ConsoleColor.Blue, ConsoleColor.Green, ConsoleColor.Cyan,
            ConsoleColor.Red, ConsoleColor.Magenta, ConsoleColor.Yellow, ConsoleColor.White
        };
        static readonly ConsoleColor[] borderColors = {
            ConsoleColor.Black,
            ConsoleColor.DarkBlue, ConsoleColor.DarkGreen, ConsoleColor.DarkCyan,
            ConsoleColor.DarkRed, ConsoleColor.DarkMagenta, ConsoleColor.DarkYellow,
            ConsoleColor.Gray, ConsoleColor.Blue, ConsoleColor.Green, ConsoleColor.Cyan,
            ConsoleColor.Red, ConsoleColor.Magenta, ConsoleColor.Yellow, ConsoleColor.White
        };
        static readonly char[] snakeGlyphs = { '-', '~', '_', '=', '.', ':', '*', '+', '#' };
        static int[] activePreviewStarts;
        static ConsoleColor?[] activePreviewColors;
        static int[] activePreviewLengths;
        static bool[] activeHighlightOnly;
        static int menuRowSpacing = 1;
        bool mainMenuFrame;
        ConsoleGraphic.ConsoleGeometry renderedMenuGeometry;
        bool hasRenderedMenuGeometry;
        bool skipped = false;
        bool graphicsOptionsAvailable = true;
        string nicknameFeedback;
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
            try { RunMainMenu(args); }
            finally { ConsoleGraphic.SetMenuScreen(false); }
        }

        private void RunMainMenu(List<string> args)
        {
            ConsoleGraphic.ConfigureConsole(71, 16);
            ApplyGraphicsArguments(args);
            OfferSavedProfile(args);
            if (ConsoleTheme.BackgroundMode == BackgroundColorMode.WindowsTerminal)
                ApplyBackgroundModeChange(BackgroundColorMode.WindowsTerminal);
            ConsoleGraphic.SetMenuScreen(false);
            ConsoleWindowState.Restore(ApplicationSettings.Current);
            ConsoleWindowState.StartTracking();
            args.Add("-skip");
            ConsoleGraphic.ApplyContentColors(ConsoleColor.White);
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
                    {
                        NetWorker.nickname = NetWorker.filterNick(args[nicknameIndex + 1]);
                        ApplicationSettings.SaveCurrentProfile(NetWorker.nickname);
                    }
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
                        var pingResult = NetWorker.ping(ip, port);
                        Program.matrix(pingResult.Reachable
                            ? Lang.Get(TextId.ServerPing, ip, port, pingResult.Milliseconds)
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
            ConsoleGraphic.SetMenuScreen(true);
            ConsoleGraphic.SetReservedBottomRows(0);
            Program.bufferClear();
            ConsoleTitleAnimator.SetCaption(Lang.Get(TextId.MenuTitle), ConsoleGraphic.Enabled);
            if (skipped) graphic.Clear(0, 0);
            else graphic.Clear();
            var choiceList = new List<string> {
                Lang.Get(ServerInterface.IsRunning ? TextId.EnterOwnHub : TextId.HostServer),
                Lang.Get(TextId.ConnectToHub),
                (NetWorker.nickname.Length <= 0) ? Lang.Get(TextId.EnterNickname) : Lang.Get(TextId.ChangeNickname)
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
            int arrow = 0;
            bool isMenu = true;
            mainMenuFrame = true;
            DrawMenuFrame(choice, arrow);

            if (!String.IsNullOrEmpty(nicknameFeedback))
            {
                if (ConsoleGraphic.Enabled)
                    ConsoleGraphic.WriteBottomStatus(nicknameFeedback, ConsoleTheme.SystemText);
                else
                {
                    Console.WriteLine();
                    ConsoleGraphic.WriteContentLine(nicknameFeedback);
                }
                nicknameFeedback = null;
            }

            while (isMenu)
            {
                ConsoleKey key = ReadMenuKey(choice, arrow);
                int previousArrow = arrow;

                switch (key)
                {
                    case ConsoleKey.RightArrow:
                    case ConsoleKey.DownArrow:
                        arrow = (arrow + 1) % choice.Length;
                        hasRenderedMenuGeometry &= AnimateSelection(choice, previousArrow, arrow);
                        continue;
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.UpArrow:
                        arrow = (arrow - 1 + choice.Length) % choice.Length;
                        hasRenderedMenuGeometry &= AnimateSelection(choice, previousArrow, arrow);
                        continue;

                    case ConsoleKey.Escape:
                    default:

                        ConsoleTitleAnimator.SetCaption(choice[Math.Abs(arrow)], ConsoleGraphic.Enabled);
                        break;
                }
                if (arrow == 0)
                {
                    if (ServerInterface.IsRunning)
                    {
                        PrepareActionScreen(choice[arrow]);
                        UserInterface.EnterOwnHub();
                    }
                    else
                    {
                        HubOptions hubOptions = ShowHubOptions();
                        if (hubOptions != null)
                        {
                            PrepareActionScreen(choice[arrow]);
                            ServerInterface.tryCreateServer(hubOptions);
                        }
                        else
                        {
                            skipped = true;
                        }
                    }
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
            bool retry = false;
            while (true)
            {
                graphic.Clear(0, 0);
                ConsoleGraphic.ApplyContentColors(ConsoleTheme.MenuText);
                if (retry) Console.WriteLine(Lang.Get(TextId.AuthInvalidNickname));
                Console.WriteLine(Lang.Get(TextId.NicknamePrompt));
                Console.WriteLine();
                Console.Write("> ");
                Stopwatch stopwatch = Stopwatch.StartNew();
                string testname = Console.ReadLine();
                stopwatch.Stop();
                if (testname == null) return;
                if (!NetWorker.IsNicknameValid(testname))
                {
                    retry = true;
                    continue;
                }
                NetWorker.nickname = testname;
                ApplicationSettings.SaveCurrentProfile(testname);
                nicknameFeedback = Lang.Get(TextId.GoodName);
                if (stopwatch.Elapsed.TotalSeconds > 25)
                    nicknameFeedback += " — " + Lang.Get(TextId.TookYourTime);
                return;
            }
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
            string testname = ReadCenteredNickname(inputRow);
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
            ApplicationSettings.SaveCurrentProfile(NetWorker.nickname);
            graphic.Clear(0, 0);
            ConsoleGraphic.WriteCenteredLine(Lang.Get(TextId.IdentityUpdated), titleRow, ConsoleColor.Cyan, true, 3);
            ConsoleGraphic.WriteCenteredLine(testname, inputRow, ConsoleColor.White, true, 4);
            ConsoleGraphic.WriteCenteredLine(
                new string('-', Math.Max(8, Math.Min(24, testname.Length + 4))),
                inputRow + 1,
                ConsoleColor.DarkGray);

            ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.GoodName), ConsoleColor.Green, 0, true, 100);
            Thread.Sleep(120);
            ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.GoodName), ConsoleColor.DarkGreen);
            Thread.Sleep(120);
            ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.GoodName), ConsoleColor.Green);
            if (stopwatch.Elapsed.TotalSeconds > 25)
                ConsoleGraphic.WriteCenteredLine(Lang.Get(TextId.TookYourTime), inputRow + 3, ConsoleColor.DarkGray);

            Thread.Sleep(300);
        }

        private static string ReadCenteredNickname(int row)
        {
            var value = new StringBuilder();
            int cursor = 0;
            int previousLeft = 0;
            int previousLength = 0;
            while (true)
            {
                try
                {
                    int width = Math.Max(1, Math.Min(Console.WindowWidth, Console.BufferWidth));
                    int safeRow = Math.Max(0, Math.Min(row, Console.BufferHeight - 1));
                    if (previousLength > 0 && previousLeft < Console.BufferWidth)
                    {
                        Console.SetCursorPosition(previousLeft, safeRow);
                        Console.Write(new string(' ', Math.Min(previousLength, Console.BufferWidth - previousLeft)));
                    }

                    string text = "> " + value;
                    int minimumLeft = ConsoleGraphic.Enabled ? ConsoleGraphic.ContentLeft : 0;
                    int rightExclusive = ConsoleGraphic.Enabled ? width - 1 : width;
                    int visibleLength = Math.Min(text.Length, Math.Max(1, rightExclusive - minimumLeft));
                    int start = Math.Max(minimumLeft, (width - visibleLength) / 2);
                    Console.SetCursorPosition(start, safeRow);
                    ConsoleGraphic.ApplyContentColors(ConsoleTheme.InputPrompt);
                    Console.Write("> ");
                    ConsoleGraphic.ApplyContentColors(ConsoleTheme.InputText);
                    if (visibleLength > 2)
                        Console.Write(value.ToString(0, Math.Min(value.Length, visibleLength - 2)));
                    Console.ResetColor();
                    int cursorLeft = Math.Min(rightExclusive - 1, start + 2 + cursor);
                    Console.SetCursorPosition(Math.Max(minimumLeft, cursorLeft), safeRow);
                    previousLeft = start;
                    previousLength = visibleLength;

                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter)
                        return value.ToString();
                    if (key.Key == ConsoleKey.LeftArrow && cursor > 0)
                        cursor--;
                    else if (key.Key == ConsoleKey.RightArrow && cursor < value.Length)
                        cursor++;
                    else if (key.Key == ConsoleKey.Home)
                        cursor = 0;
                    else if (key.Key == ConsoleKey.End)
                        cursor = value.Length;
                    else if (key.Key == ConsoleKey.Backspace && cursor > 0)
                    {
                        value.Remove(cursor - 1, 1);
                        cursor--;
                    }
                    else if (key.Key == ConsoleKey.Delete && cursor < value.Length)
                        value.Remove(cursor, 1);
                    else if (!Char.IsControl(key.KeyChar) && value.Length < 20)
                    {
                        value.Insert(cursor, key.KeyChar);
                        cursor++;
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    Thread.Sleep(30);
                }
                catch (System.IO.IOException)
                {
                    Thread.Sleep(30);
                }
            }
        }

        private ConsoleKey ReadMenuKey(string[] choices, int selectedIndex)
        {
            ConsoleGraphic.ConsoleGeometry knownGeometry = renderedMenuGeometry;
            bool hasKnownGeometry = hasRenderedMenuGeometry;
            ConsoleGraphic.ConsoleGeometry pendingGeometry = new ConsoleGraphic.ConsoleGeometry();
            bool resizePending = false;
            long stableSince = 0;
            long settleTicks = Math.Max(1L, Stopwatch.Frequency * resizeSettleMilliseconds / 1000L);
            long blinkTicks = Math.Max(1L, Stopwatch.Frequency * selectionMarkerBlinkMilliseconds / 1000L);
            long nextMarkerBlink = Stopwatch.GetTimestamp() + blinkTicks;
            bool markerVisible = true;

            while (true)
            {
                if (WindowsTerminalTheme.RefreshAfterActivation())
                {
                    ConsoleGraphic.InvalidateVisualTheme();
                    hasKnownGeometry = graphic.TryClear(0, 0) && DrawMenuFrame(choices, selectedIndex);
                    if (hasKnownGeometry) knownGeometry = renderedMenuGeometry;
                    resizePending = false;
                }
                ConsoleGraphic.EnsureBorderAnimationRunning();
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
                    if (ConsoleGraphic.Enabled && ConsoleTheme.SelectionStyle == MenuSelectionStyle.Arrow && now >= nextMarkerBlink)
                    {
                        markerVisible = !markerVisible;
                        ConsoleGraphic.DrawMenuSelectionMarker(
                            selectedIndex * menuRowSpacing,
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
                frameCompleted &= DrawMenuFrame(choices, selectedIndex);

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

        private bool DrawMenuFrame(string[] choices, int selectedIndex)
        {
            hasRenderedMenuGeometry = false;
            if (!ConsoleGraphic.TryCaptureConsoleGeometry(out var frameGeometry)) return false;
            left = 5;
            top = 1;
            menuRowSpacing = 1;
            bool completed = true;
            if (!String.IsNullOrEmpty(optionsHeader))
            {
                top = 3;
                if (ConsoleGraphic.Enabled)
                    completed &= ConsoleGraphic.WriteCenteredLine(optionsHeader, 1, ConsoleTheme.MenuText);
                else
                {
                    Console.SetCursorPosition(0, 0);
                    Console.Write(optionsHeader);
                }
            }
            if (mainMenuFrame && ConsoleGraphic.Enabled)
            {
                if (!ConsoleGraphic.TryCaptureConsoleGeometry(out var geometry)) return false;
                var layout = MenuPresentation.Layout(geometry.DrawableWidth, geometry.DrawableHeight, choices.Length);
                top = layout.Top;
                menuRowSpacing = layout.Spacing;
                lock (ConsoleGraphic.borderAnimationLock)
                    completed = MenuPresentation.DrawHeader(geometry, layout.Header);
            }
            for (int index = 0; index < choices.Length; index++)
                completed &= DrawChoice(choices[index], index, index == selectedIndex, false);
            if (!String.IsNullOrEmpty(optionsFooter))
                completed &= DrawOptionsFooter(top + choices.Length * menuRowSpacing + 1);
            if (completed && ConsoleGraphic.IsConsoleGeometryCurrent(frameGeometry))
            {
                renderedMenuGeometry = frameGeometry;
                hasRenderedMenuGeometry = true;
            }
            return hasRenderedMenuGeometry;
        }

        private bool DrawOptionsFooter(int firstRow)
        {
            lock (ConsoleGraphic.borderAnimationLock)
            {
                try
                {
                    if (!ConsoleGraphic.TryCaptureConsoleGeometry(out var geometry))
                        return false;
                    int footerLeft = ConsoleGraphic.Enabled ? left + 2 : 2;
                    int lastRow = ConsoleGraphic.Enabled ? ConsoleGraphic.ContentBottom : geometry.DrawableHeight - 1;
                    int width = Math.Max(8, (ConsoleGraphic.Enabled ? geometry.DrawableWidth - 2 : geometry.BufferWidth) - footerLeft - 1);
                    int row = firstRow;
                    foreach (string line in WrapFooter(optionsFooter, width))
                    {
                        if (row > lastRow)
                            break;
                        Console.SetCursorPosition(footerLeft, row++);
                        ConsoleGraphic.ApplyContentColors(ConsoleTheme.SystemText);
                        Console.Write(line);
                    }
                    Console.ResetColor();
                    return ConsoleGraphic.IsConsoleGeometryCurrent(geometry);
                }
                catch (ArgumentOutOfRangeException) { return false; }
                catch (System.IO.IOException) { return false; }
            }
        }

        private static IEnumerable<string> WrapFooter(string text, int width)
        {
            foreach (string paragraph in text.Split('\n'))
            {
                string remaining = paragraph.Trim();
                if (remaining.Length == 0)
                {
                    yield return String.Empty;
                    continue;
                }
                while (remaining.Length > width)
                {
                    int split = remaining.LastIndexOf(' ', width);
                    if (split <= 0)
                        split = width;
                    yield return remaining.Substring(0, split).TrimEnd();
                    remaining = remaining.Substring(split).TrimStart();
                }
                yield return remaining;
            }
        }

        private HubOptions ShowHubOptions()
        {
            var context = new HubOptionContext { Bluetooth = BluetoothSupport.Check(true) };
            IReadOnlyList<HubOptionDescriptor> descriptors = HubOptionRegistry.All;
            int selectedOption = 0;
            try
            {
                while (true)
                {
                    int createIndex = descriptors.Count;
                    int backIndex = createIndex + 1;
                    var choices = new string[backIndex + 1];
                    var starts = new int[choices.Length];
                    var lengths = new int[choices.Length];
                    var colors = new ConsoleColor?[choices.Length];
                    var highlightOnly = new bool[choices.Length];
                    for (int index = 0; index < descriptors.Count; index++)
                    {
                        HubOptionView view = descriptors[index].Render(context);
                        choices[index] = view.Text;
                        starts[index] = view.HighlightStart;
                        lengths[index] = view.HighlightLength;
                        colors[index] = view.HighlightStart >= 0 ? ConsoleColor.Green : null;
                        highlightOnly[index] = view.SelectHighlightOnly && view.HighlightStart >= 0;
                    }
                    activeHighlightOnly = highlightOnly;
                    choices[createIndex] = Lang.Get(TextId.HubCreate);
                    choices[backIndex] = Lang.Get(TextId.Back);
                    starts[createIndex] = starts[backIndex] = -1;

                    optionsHeader = context.BluetoothAvailable
                        ? Lang.Get(TextId.BluetoothModuleFound)
                        : Lang.Get(TextId.BluetoothModuleMissing, Lang.Get(BluetoothSupport.Describe(context.Bluetooth)));
                    optionsFooter = HubOptionRegistry.Explanation(context.Options);

                    var choice = ReadOptionsChoice(Lang.Get(TextId.HubOptionsTitle), choices, selectedOption, starts, colors,
                        index => index < descriptors.Count, lengths);
                    if (choice.Index < 0 || choice.Index == backIndex)
                        return null;
                    selectedOption = choice.Index;
                    if (choice.Index == createIndex)
                        return context.Options.IsValid ? context.Options : null;
                    context.Options = descriptors[choice.Index].Change(context, choice.Direction);
                }
            }
            finally
            {
                optionsHeader = null;
                optionsFooter = null;
                activeHighlightOnly = null;
            }
        }

        private static bool AnimateSelection(string[] choices, int previousIndex, int currentIndex)
        {
            if (ConsoleGraphic.Enabled && ConsoleTheme.SelectionStyle == MenuSelectionStyle.Arrow)
            {
                ConsoleGraphic.DrawMenuSelectionMarker(previousIndex * menuRowSpacing, left, top, false);
                ConsoleGraphic.DrawMenuSelectionMarker(currentIndex * menuRowSpacing, left, top, true);
            }
            bool completed = DrawChoice(choices[previousIndex], previousIndex, false, true);
            return DrawChoice(choices[currentIndex], currentIndex, true, true) && completed;
        }

        private static bool DrawChoice(string text, int index, bool selected, bool animate)
        {
            int previewStart = activePreviewStarts != null && index < activePreviewStarts.Length
                ? activePreviewStarts[index]
                : -1;
            ConsoleColor? previewColor = activePreviewColors != null && index < activePreviewColors.Length
                ? activePreviewColors[index]
                : (ConsoleColor?)null;
            int previewLength = activePreviewLengths != null && index < activePreviewLengths.Length
                ? activePreviewLengths[index]
                : 0;
            bool highlightOnly = activeHighlightOnly != null && index < activeHighlightOnly.Length && activeHighlightOnly[index];
            return ConsoleGraphic.DrawMenuOption(
                text,
                index * menuRowSpacing,
                left,
                top,
                selected,
                animate && ConsoleGraphic.Enabled,
                selectionAnimationDelay,
                previewStart,
                previewColor,
                previewLength,
                highlightOnly);
        }

        private void ShowConsoleGraphicsOptions()
        {
            int selectedOption = 0;
            while (true)
            {
                string[] choices = {
                    Lang.Get(ConsoleGraphic.Enabled ? TextId.GraphicsEnabled : TextId.GraphicsDisabled),
                    Lang.Get(TextId.Customization),
                    Lang.Get(TextId.BackgroundColorRow) + ": " + GetBackgroundModeName(ConsoleTheme.BackgroundMode),
                    Lang.Get(TextId.CustomBackgroundColor) + ": #" +
                        ConsoleTheme.BackgroundCustomRgb.ToString("X6", CultureInfo.InvariantCulture),
                    Lang.Get(TextId.Back)
                };
                var choice = ReadOptionsChoice(Lang.Get(TextId.GraphicsOptions), choices, selectedOption,
                    isValueOption: index => index == 0 || index == 2);
                int selection = choice.Index;
                if (selection < 0 || selection == 4) return;
                selectedOption = selection;
                if (selection == 0)
                    ConsoleGraphic.Enabled = !ConsoleGraphic.Enabled;
                else if (selection == 1)
                    ShowCustomizationOptions();
                else if (selection == 2)
                    ApplyBackgroundModeChange(OptionNavigation.Next(
                        new[] { BackgroundColorMode.Off, BackgroundColorMode.WindowsTerminal },
                        ConsoleTheme.BackgroundMode, choice.Direction));
                else
                    ShowCustomBackgroundColorPrompt();
            }
        }
        private static string GetBackgroundModeName(BackgroundColorMode mode) => mode switch
        {
            BackgroundColorMode.WindowsTerminal => Lang.Get(TextId.BackgroundModeWindowsTerminal),
            _ => Lang.Get(TextId.CustomColorOff)
        };

        private void ApplyBackgroundModeChange(BackgroundColorMode newMode)
        {
            string error = null;
            bool applied = true;
            if (newMode == BackgroundColorMode.WindowsTerminal)
                applied = WindowsTerminalTheme.TryApplyBackground(ConsoleTheme.BackgroundCustomRgb, out error);
            else if (ConsoleTheme.BackgroundMode == BackgroundColorMode.WindowsTerminal)
                applied = WindowsTerminalTheme.TryClearBackground(out error);

            if (!applied)
            {
                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.BackgroundApplyFailed, error), ConsoleColor.Red);
                Thread.Sleep(1500);
                return;
            }
            ConsoleTheme.BackgroundMode = newMode;
            ConsoleGraphic.InvalidateVisualTheme();
            ApplicationSettings.CaptureAndSave();
        }
        private void ShowCustomBackgroundColorPrompt()
        {
            Console.ResetColor();
            graphic.Clear(0, 0);
            int left = ConsoleGraphic.ContentLeft;
            int row = ConsoleGraphic.ContentTop;
            ConsoleGraphic.ApplyContentColors(ConsoleTheme.MenuText);
            Console.SetCursorPosition(left, row);
            Console.Write(Lang.Get(TextId.CustomColorPrompt));
            Console.SetCursorPosition(left, row + 2);
            Console.ResetColor();
            ConsoleGraphic.ApplyContentColors(ConsoleTheme.InputText);
            Console.Write("> #");
            string input = (Console.ReadLine() ?? String.Empty).Trim().TrimStart('#');

            if (input.Length == 0)
                return;

            if (input.Length != 6 || !Int32.TryParse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                Console.SetCursorPosition(left, row + 4);
                ConsoleGraphic.ApplyContentColors(ConsoleColor.Red);
                Console.Write(Lang.Get(TextId.CustomColorInvalid));
                Console.ResetColor();
                Console.ReadKey(true);
                return;
            }

            int previousBackground = ConsoleTheme.BackgroundCustomRgb;
            ConsoleTheme.BackgroundCustomRgb = rgb & 0xFFFFFF;
            if (ConsoleTheme.BackgroundMode == BackgroundColorMode.WindowsTerminal &&
                !WindowsTerminalTheme.TryApplyBackground(ConsoleTheme.BackgroundCustomRgb, out string applyError))
            {
                Console.SetCursorPosition(left, row + 4);
                ConsoleGraphic.ApplyContentColors(ConsoleColor.Red);
                ConsoleTheme.BackgroundCustomRgb = previousBackground;
                Console.Write(Lang.Get(TextId.BackgroundApplyFailed, applyError));
                Console.ResetColor();
                Console.ReadKey(true);
            }

            ConsoleGraphic.InvalidateVisualTheme();
            ApplicationSettings.CaptureAndSave();
        }

        private void ShowCustomSelectionColorPrompt()
        {
            Console.ResetColor();
            graphic.Clear(0, 0);
            int left = ConsoleGraphic.ContentLeft;
            int row = ConsoleGraphic.ContentTop;
            ConsoleGraphic.ApplyContentColors(ConsoleTheme.MenuText);
            Console.SetCursorPosition(left, row);
            Console.Write(Lang.Get(TextId.CustomColorPrompt));
            Console.SetCursorPosition(left, row + 2);
            Console.ResetColor();
            ConsoleGraphic.ApplyContentColors(ConsoleTheme.InputText);
            Console.Write("> #");
            string input = (Console.ReadLine() ?? String.Empty).Trim().TrimStart('#');

            if (input.Length == 0)
            {
                ConsoleTheme.ClearCustomSelectionColor();
                ConsoleGraphic.InvalidateVisualTheme();
                ApplicationSettings.CaptureAndSave();
                return;
            }

            if (input.Length != 6 || !Int32.TryParse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                Console.SetCursorPosition(left, row + 4);
                ConsoleGraphic.ApplyContentColors(ConsoleColor.Red);
                Console.Write(Lang.Get(TextId.CustomColorInvalid));
                Console.ResetColor();
                Console.ReadKey(true);
                return;
            }

            ConsoleTheme.SetCustomSelectionColor(rgb);
            ConsoleGraphic.InvalidateVisualTheme();
            ApplicationSettings.CaptureAndSave();
        }

        private void ShowCustomizationOptions()
        {
            int selectedOption = 0;
            while (true)
            {
                string[] choices = {
                    Lang.Get(TextId.Snake),
                    Lang.Get(TextId.InterfaceColors),
                    Lang.Get(TextId.ResetSettings),
                    Lang.Get(TextId.Back)
                };
                int firstOption = ConsoleGraphic.Enabled ? 0 : 1;
                int visibleSelection = ReadOptionsSelection(Lang.Get(TextId.Customization), choices[firstOption..], selectedOption);
                if (visibleSelection < 0)
                    return;
                int selection = visibleSelection + firstOption;
                if (selection == 3)
                    return;

                selectedOption = visibleSelection;
                if (selection == 0)
                    ShowSnakeOptions();
                else if (selection == 1)
                    ShowInterfaceColorOptions();
                else
                {
                    ApplicationSettings.ResetCustomizations();
                    ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.SettingsReset), ConsoleColor.Green);
                    Thread.Sleep(500);
                }
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
                    Lang.Get(TextId.GlyphValue, ConsoleGraphic.BorderSnakeGlyph),
                    Lang.Get(TextId.Back)
                };
                var choice = ReadOptionsChoice(Lang.Get(TextId.SnakeCustomization), choices, selectedOption, isValueOption: index => index < 3);
                int selection = choice.Index;
                if (selection < 0 || selection == 3)
                    return;

                selectedOption = selection;

                if (selection == 0)
                    ConsoleGraphic.BorderAnimationDelayMilliseconds = OptionNavigation.Next(snakeSpeeds, ConsoleGraphic.BorderAnimationDelayMilliseconds, choice.Direction);
                else if (selection == 1)
                    ConsoleGraphic.BorderSnakeColor = OptionNavigation.Next(snakeColors, ConsoleGraphic.BorderSnakeColor, choice.Direction);
                else
                    ConsoleGraphic.BorderSnakeGlyph = OptionNavigation.Next(snakeGlyphs, ConsoleGraphic.BorderSnakeGlyph, choice.Direction);
                ApplicationSettings.CaptureAndSave();
            }
        }

        private void ShowInterfaceColorOptions()
        {
            int selectedOption = 0;
            while (true)
            {
                string testMessage = Lang.Get(TextId.PreviewMessage);
                string[] previews = {
                    "+----------------+",
                    ">>> username: " + testMessage,
                    "<<< username: " + testMessage,
                    "<<< [you]: " + testMessage,
                    Lang.Get(TextId.PreviewSystem),
                    Lang.Get(TextId.PreviewMenuItem),
                    String.Empty
                };
                ConsoleColor[] colors = {
                    ConsoleTheme.Border,
                    ConsoleTheme.IncomingMarker,
                    ConsoleTheme.OutgoingMarker,
                    ConsoleTheme.InputPrompt,
                    ConsoleTheme.SystemText,
                    ConsoleTheme.MenuText,
                    ConsoleColor.White
                };
                TextId[] labels = {
                    TextId.Border,
                    TextId.IncomingMessages,
                    TextId.OutgoingMessages,
                    TextId.InputField,
                    TextId.SystemMessages,
                    TextId.MenuTextColor,
                    TextId.SelectionStyle,
                    TextId.SelectionColor,
                    TextId.CustomSelectionColor,
                    TextId.Back
                };
                string[] choices = new string[labels.Length];
                int[] previewStarts = new int[labels.Length];
                ConsoleColor?[] previewColors = new ConsoleColor?[labels.Length];
                for (int index = 0; index < labels.Length; index++)
                {
                    if (index >= 6)
                    {
                        choices[index] = Lang.Get(labels[index]);
                        previewStarts[index] = -1;
                        continue;
                    }
                    string prefix = Lang.Get(labels[index]) + ": ";
                    choices[index] = prefix + GetSnakeColorName(colors[index]) + "  " + previews[index];
                    previewStarts[index] = prefix.Length;
                    previewColors[index] = colors[index];
                }

                choices[6] = Lang.Get(TextId.SelectionStyle) + ": " + Lang.Get(
                    ConsoleTheme.SelectionStyle == MenuSelectionStyle.Arrow ? TextId.SelectionArrow :
                    ConsoleTheme.SelectionStyle == MenuSelectionStyle.Brackets ? TextId.SelectionBrackets : TextId.SelectionFill);
                choices[7] = Lang.Get(TextId.SelectionColor) + ": " + (ConsoleTheme.SelectionUsesCustomColor
                    ? Lang.Get(TextId.CustomColor) : GetSnakeColorName(ConsoleTheme.SelectionBackground));
                choices[8] = Lang.Get(TextId.CustomSelectionColor) + ": " + (ConsoleTheme.SelectionUsesCustomColor
                    ? "#" + ConsoleTheme.SelectionCustomRgb.ToString("X6", CultureInfo.InvariantCulture) : Lang.Get(TextId.CustomColorOff));
                // Only the border is absent in plain mode; message and selection colors still apply.
                int firstOption = ConsoleGraphic.Enabled ? 0 : 1;
                var choice = ReadOptionsChoice(
                    Lang.Get(TextId.InterfaceColors),
                    choices[firstOption..],
                    selectedOption,
                    previewStarts[firstOption..],
                    previewColors[firstOption..],
                    index => index + firstOption < 8);
                if (choice.Index < 0)
                    return;
                int selection = choice.Index + firstOption;
                if (selection == 9)
                    return;
                selectedOption = choice.Index;
                if (selection == 0)
                    ConsoleTheme.Border = OptionNavigation.Next(borderColors, ConsoleTheme.Border, choice.Direction);
                else if (selection == 1)
                {
                    ConsoleTheme.IncomingMarker = OptionNavigation.Next(interfaceColors, ConsoleTheme.IncomingMarker, choice.Direction);
                    ConsoleTheme.IncomingText = ConsoleTheme.IncomingMarker;
                }
                else if (selection == 2)
                {
                    ConsoleTheme.OutgoingMarker = OptionNavigation.Next(interfaceColors, ConsoleTheme.OutgoingMarker, choice.Direction);
                    ConsoleTheme.OutgoingText = ConsoleTheme.OutgoingMarker;
                }
                else if (selection == 3)
                {
                    ConsoleTheme.InputPrompt = OptionNavigation.Next(interfaceColors, ConsoleTheme.InputPrompt, choice.Direction);
                    ConsoleTheme.InputText = ConsoleTheme.InputPrompt;
                }
                else if (selection == 4)
                    ConsoleTheme.SystemText = OptionNavigation.Next(interfaceColors, ConsoleTheme.SystemText, choice.Direction);
                else if (selection == 5)
                    ConsoleTheme.MenuText = OptionNavigation.Next(interfaceColors, ConsoleTheme.MenuText, choice.Direction);
                else if (selection == 6)
                    ConsoleTheme.SelectionStyle = OptionNavigation.Next(
                        new[] { MenuSelectionStyle.Fill, MenuSelectionStyle.Arrow, MenuSelectionStyle.Brackets },
                        ConsoleTheme.SelectionStyle, choice.Direction);
                else if (selection == 7)
                {
                    ConsoleTheme.SelectionBackground = OptionNavigation.Next(interfaceColors, ConsoleTheme.SelectionBackground, choice.Direction);
                    ConsoleTheme.ClearCustomSelectionColor();
                }
                else
                    ShowCustomSelectionColorPrompt();
                ConsoleGraphic.InvalidateVisualTheme();
                ApplicationSettings.CaptureAndSave();
            }
        }

        private string optionsHeader;
        private string optionsFooter;

        private void OfferSavedProfile(List<string> args)
        {
            if (ApplicationSettings.ProfileWasAutoLoaded) return;
            var profiles = ApplicationSettings.GetSavedProfiles();
            if (profiles.Count == 0) return;
            bool remember = false, clear = false;
            int selected = 0;
            try
            {
                while (true)
                {
                    optionsHeader = Lang.Get(TextId.SavedProfilesCount, profiles.Count);
                    string latestPrefix = Lang.Get(TextId.UseLatestProfile);
                    string[] choices = {
                        Lang.Get(TextId.CreateOwnProfile),
                        Lang.Get(TextId.OpenProfileList),
                        latestPrefix + profiles[0].Nickname,
                        (remember ? "[ V ] " : "[   ] ") + Lang.Get(TextId.AlwaysLoadChosenProfile),
                        (clear ? "[ V ] " : "[   ] ") + Lang.Get(TextId.ClearAllProfiles)
                    };
                    var choice = ReadOptionsChoice(optionsHeader, choices, selected,
                        new[] { -1, -1, latestPrefix.Length, -1, -1 },
                        new ConsoleColor?[] { null, null, profiles[0].SnakeColor, null, null },
                        index => index >= 3);
                    selected = choice.Index;
                    if (selected == 3) { remember = !remember; continue; }
                    if (selected == 4) { clear = !clear; continue; }
                    if (selected < 0)
                    {
                        ApplicationSettings.DismissPendingProfile();
                        return;
                    }
                    ApplicationSettings.SavedProfile profile = selected == 2 ? profiles[0] : null;
                    if (selected == 1)
                    {
                        profile = ChooseSavedProfile(profiles);
                        if (profile == null) { selected = 1; continue; }
                    }
                    if (!ApplicationSettings.SelectSavedProfile(profile, remember, clear, out string error))
                    {
                        ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ProfileLoadError, error), ConsoleColor.Red);
                        Thread.Sleep(1800);
                        continue;
                    }
                    optionsHeader = null;
                    Lang.ApplyArguments(args);
                    ApplyGraphicsArguments(args);
                    if (profile == null)
                    {
                        graphic.Clear(0, 0);
                        if (ConsoleGraphic.Enabled) ChangeNicknameGraphical();
                        else ChangeNicknamePlain();
                    }
                    return;
                }
            }
            finally { optionsHeader = null; }
        }

        private ApplicationSettings.SavedProfile ChooseSavedProfile(List<ApplicationSettings.SavedProfile> profiles)
        {
            int offset = 0;
            while (true)
            {
                int pageSize = Math.Max(1, Console.WindowHeight - 8);
                int count = Math.Min(pageSize, profiles.Count - offset);
                var choices = new List<string>();
                var starts = new List<int>();
                var colors = new List<ConsoleColor?>();
                for (int index = 0; index < count; index++)
                {
                    var profile = profiles[offset + index];
                    choices.Add(profile.Nickname);
                    starts.Add(0);
                    colors.Add(profile.SnakeColor);
                }
                int previous = -1, next = -1;
                if (offset > 0) { previous = choices.Count; choices.Add("←"); starts.Add(-1); colors.Add(null); }
                if (offset + count < profiles.Count) { next = choices.Count; choices.Add("→"); starts.Add(-1); colors.Add(null); }
                choices.Add(Lang.Get(TextId.Back)); starts.Add(-1); colors.Add(null);
                int selection = ReadOptionsSelection(Lang.Get(TextId.OpenProfileList), choices.ToArray(), 0, starts.ToArray(), colors.ToArray());
                if (selection < 0 || selection == choices.Count - 1) return null;
                if (selection == previous) { offset = Math.Max(0, offset - pageSize); continue; }
                if (selection == next) { offset += count; continue; }
                return profiles[offset + selection];
            }
        }
        private int ReadOptionsSelection(
            string title,
            string[] choices,
            int selectedOption,
            int[] previewStarts = null,
            ConsoleColor?[] previewColors = null)
            => ReadOptionsChoice(title, choices, selectedOption, previewStarts, previewColors).Index;

        private (int Index, int Direction) ReadOptionsChoice(
            string title,
            string[] choices,
            int selectedOption,
            int[] previewStarts = null,
            ConsoleColor?[] previewColors = null,
            Func<int, bool> isValueOption = null,
            int[] previewLengths = null)
        {
            activePreviewStarts = previewStarts;
            activePreviewColors = previewColors;
            activePreviewLengths = previewLengths;
            ConsoleGraphic.SetMenuScreen(true);
            ConsoleTitleAnimator.SetCaption(title, ConsoleGraphic.Enabled);
            graphic.Clear(0, 0);
            mainMenuFrame = false;
            int arrow = Math.Max(0, Math.Min(selectedOption, choices.Length - 1));
            DrawMenuFrame(choices, arrow);

            while (true)
            {
                ConsoleKey key = ReadMenuKey(choices, arrow);
                int previousArrow = arrow;
                if (key == ConsoleKey.DownArrow)
                {
                    arrow = (arrow + 1) % choices.Length;
                    hasRenderedMenuGeometry &= AnimateSelection(choices, previousArrow, arrow);
                }
                else if (key == ConsoleKey.UpArrow)
                {
                    arrow = (arrow - 1 + choices.Length) % choices.Length;
                    hasRenderedMenuGeometry &= AnimateSelection(choices, previousArrow, arrow);
                }
                else if (key == ConsoleKey.Enter || key == ConsoleKey.Spacebar ||
                    (OptionNavigation.ValueDirection(key) != 0 && isValueOption?.Invoke(arrow) == true))
                {
                    activePreviewStarts = null;
                    activePreviewColors = null;
                    activePreviewLengths = null;
                    return (arrow, OptionNavigation.ValueDirection(key));
                }
                else if (key == ConsoleKey.Escape)
                {
                    activePreviewStarts = null;
                    activePreviewColors = null;
                    activePreviewLengths = null;
                    return (-1, 0);
                }
            }
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
                case ConsoleColor.Black: return Lang.Get(TextId.ColorBlack);
                case ConsoleColor.DarkBlue: return Lang.Get(TextId.ColorDarkBlue);
                case ConsoleColor.DarkGreen: return Lang.Get(TextId.ColorDarkGreen);
                case ConsoleColor.DarkCyan: return Lang.Get(TextId.ColorDarkCyan);
                case ConsoleColor.DarkRed: return Lang.Get(TextId.ColorDarkRed);
                case ConsoleColor.DarkMagenta: return Lang.Get(TextId.ColorDarkMagenta);
                case ConsoleColor.DarkYellow: return Lang.Get(TextId.ColorDarkYellow);
                case ConsoleColor.Gray: return Lang.Get(TextId.ColorGray);
                case ConsoleColor.Green: return Lang.Get(TextId.ColorGreen);
                case ConsoleColor.Cyan: return Lang.Get(TextId.ColorCyan);
                case ConsoleColor.Yellow: return Lang.Get(TextId.ColorYellow);
                case ConsoleColor.Red: return Lang.Get(TextId.ColorRed);
                case ConsoleColor.Magenta: return Lang.Get(TextId.ColorMagenta);
                case ConsoleColor.White: return Lang.Get(TextId.ColorWhite);
                case ConsoleColor.Blue: return Lang.Get(TextId.ColorBlue);
                default: return color.ToString();
            }
        }

        private void PrepareActionScreen(string title)
        {
            ConsoleGraphic.SetMenuScreen(false);
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
