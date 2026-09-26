using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Windows.Devices.Bluetooth;

namespace TCPTunnel
{
    public partial class UserInterface
    {
        internal enum BluetoothConnectOutcome { UseIp, Handled, Cancelled }
        private enum DiscoveryOutcome { Selected, NoneFound, Failed, Cancelled }

        private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(12);
        private const int SignalGood = -65;
        private const int SignalWeak = -80;
        private const int HubListFirstLine = 4;

        private static ChatTransport currentTransport = ChatTransport.Tcp;
        private static BluetoothHubScanner sessionScanner;
        private static ulong sessionHubAddress;
        private static int? sentSignal;
        private static long nextSignalSend;
        private static int panelLeft, panelTop, panelWidth, panelRows;
        private static int panelCursorLeft, panelCursorTop;

        internal static int? CurrentBluetoothSignal => sessionScanner?.GetSignal(sessionHubAddress);

        internal static string DescribeHubAccess(HubOptions options)
        {
            string ip = options.IpMode switch
            {
                HubIpMode.Public => "WWW",
                HubIpMode.LanOnly => "LAN",
                _ => null
            };
            if (ip == null)
                return "Bluetooth";
            return options.Bluetooth ? ip + " + Bluetooth" : ip;
        }

        private static string DescribeBeaconMode(BluetoothHubMode mode) => mode switch
        {
            BluetoothHubMode.PublicAndBluetooth => "WWW + Bluetooth",
            BluetoothHubMode.LanAndBluetooth => "LAN + Bluetooth",
            _ => "Bluetooth"
        };

        private static string FormatParticipants(int count)
        {
            TextId id;
            if (Lang.Current == AppLanguage.English)
                id = count == 1 ? TextId.ParticipantsOne : TextId.ParticipantsMany;
            else if (count % 100 is >= 11 and <= 14)
                id = TextId.ParticipantsMany;
            else if (count % 10 == 1)
                id = TextId.ParticipantsOne;
            else if (count % 10 is >= 2 and <= 4)
                id = TextId.ParticipantsFew;
            else
                id = TextId.ParticipantsMany;
            return Lang.Get(id, count);
        }

        private static void ReportBluetoothSignal(long now)
        {
            if (currentTransport != ChatTransport.Bluetooth || now < nextSignalSend || diagnosticsStream == null)
                return;
            nextSignalSend = now + 3000;
            int? signal = CurrentBluetoothSignal;
            if (signal == null || signal == sentSignal)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(diagnosticsToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try { MessageProtocol.WriteStringAsync(diagnosticsStream, WhoisProtocol.Signal(signal.Value), timeout.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException error) { throw new System.IO.IOException("Signal metadata send timed out.", error); }
            sentSignal = signal;
        }

        internal static bool EnterOwnHub() => ServerInterface.Options.UsesTcp
            ? DoConnect("127.0.0.1", ServerInterface.ListeningPort, 1)
            : DoConnectLocal(ServerInterface.CreateLocalConnection());

        internal static bool DoConnectLocal(IChatConnection connection)
        {
            if (!EnsureNickname())
            {
                connection.Dispose();
                return false;
            }
            return RunPreparedConnection(connection, Lang.Get(TextId.BluetoothEndpoint), true);
        }

        private static bool RunPreparedConnection(IChatConnection connection, string host, bool localHub)
        {
            if (Interlocked.CompareExchange(ref isBusy, 1, 0) != 0)
            {
                connection.Dispose();
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.ConnectionInProgress));
                return false;
            }
            try
            {
                RunClientSession(connection, null, host, localHub);
                return true;
            }
            catch (Exception ex)
            {
                if (ConsoleGraphic.Enabled)
                    ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.SessionStartFailed, ex.Message), ConsoleTheme.SystemText);
                else
                    ConsoleGraphic.WriteContentLine(Lang.Get(TextId.SessionStartFailed, ex.Message));
                return false;
            }
            finally
            {
                connection.Dispose();
                connected = false;
                Interlocked.Exchange(ref isBusy, 0);
            }
        }

        private static BluetoothConnectOutcome TryConnectBluetooth()
        {
            if (BluetoothSupport.Check(false) != BluetoothAvailability.Available)
                return BluetoothConnectOutcome.UseIp;

            BeginPanel();
            int answer = ReadInlineChoice(0, Lang.Get(TextId.BluetoothAskConnect), new[] { Lang.Get(TextId.Yes), Lang.Get(TextId.No) });
            if (answer < 0)
                return BluetoothConnectOutcome.Cancelled;
            if (answer == 1)
            {
                ClearPanel();
                WritePanelLine(0, Lang.Get(TextId.BluetoothNoThanks), ConsoleTheme.MenuText);
                Thread.Sleep(700);
                ClearPanel();
                RestorePanelCursor();
                return BluetoothConnectOutcome.UseIp;
            }

            while (true)
            {
                ClearPanel();
                DiscoveryOutcome outcome = RunDiscovery(out DiscoveredHub hub, out string error);
                if (outcome == DiscoveryOutcome.Cancelled)
                    return BluetoothConnectOutcome.Cancelled;
                if (outcome == DiscoveryOutcome.Selected)
                {
                    ConnectToDiscoveredHub(hub);
                    return BluetoothConnectOutcome.Handled;
                }

                ClearPanel();
                if (outcome == DiscoveryOutcome.Failed)
                    WritePanelLine(0, Lang.Get(TextId.BluetoothApiError, error), ConsoleColor.Red);
                else
                    WritePanelLine(0, Lang.Get(TextId.BluetoothNoBeacons), ConsoleTheme.MenuText);
                int retry = ReadInlineChoice(2, Lang.Get(TextId.BluetoothTryAgain),
                    new[] { Lang.Get(TextId.BluetoothRetry), Lang.Get(TextId.BluetoothUseIp) });
                if (retry < 0)
                    return BluetoothConnectOutcome.Cancelled;
                if (retry == 1)
                {
                    ClearPanel();
                    RestorePanelCursor();
                    return BluetoothConnectOutcome.UseIp;
                }
            }
        }

        private static DiscoveryOutcome RunDiscovery(out DiscoveredHub selectedHub, out string error)
        {
            selectedHub = null;
            error = null;
            BluetoothHubScanner scanner = null;
            try
            {
                scanner = new BluetoothHubScanner();
                scanner.Start();
            }
            catch (Exception ex)
            {
                scanner?.Dispose();
                error = ex.Message;
                return DiscoveryOutcome.Failed;
            }

            using (scanner)
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                TimeSpan lastSeen = TimeSpan.Zero;
                int dots = 0;
                long nextDot = 0;
                long nextRefresh = 0;
                int renderedVersion = -1;
                int renderedLines = 0;
                (ulong Address, uint Instance)? selectedKey = null;
                IReadOnlyList<DiscoveredHub> hubs = Array.Empty<DiscoveredHub>();
                WritePanelLine(panelRows - 1, Lang.Get(TextId.BluetoothDiscoveryHint), ConsoleTheme.SystemText);

                while (true)
                {
                    if (scanner.Failure is BluetoothError failure)
                    {
                        error = failure.ToString();
                        return DiscoveryOutcome.Failed;
                    }

                    long now = Environment.TickCount64;
                    if (now >= nextDot)
                    {
                        dots = dots % 3 + 1;
                        nextDot = now + 400;
                        WritePanelLine(0, Lang.Get(TextId.BluetoothSearching) + String.Concat(Enumerable.Repeat(" .", dots)), ConsoleTheme.MenuText);
                    }

                    bool redraw = false;
                    if (scanner.Version != renderedVersion || now >= nextRefresh)
                    {
                        renderedVersion = scanner.Version;
                        nextRefresh = now + 1000;
                        hubs = scanner.Snapshot();
                        redraw = true;
                    }

                    if (hubs.Count > 0)
                        lastSeen = elapsed.Elapsed;
                    else if (elapsed.Elapsed - lastSeen > SearchTimeout)
                        return DiscoveryOutcome.NoneFound;

                    int selectedIndex = FindSelection(hubs, ref selectedKey);
                    if (redraw)
                        renderedLines = RenderHubList(hubs, selectedIndex, renderedLines);

                    if (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Escape)
                            return DiscoveryOutcome.Cancelled;
                        if (hubs.Count > 0 && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                        {
                            int step = key.Key == ConsoleKey.DownArrow ? 1 : -1;
                            int next = selectedIndex < 0
                                ? (step > 0 ? 0 : hubs.Count - 1)
                                : (selectedIndex + step + hubs.Count) % hubs.Count;
                            selectedKey = (hubs[next].Address, hubs[next].Instance);
                            renderedLines = RenderHubList(hubs, next, renderedLines);
                        }
                        else if (key.Key == ConsoleKey.Enter)
                        {
                            if (selectedKey.HasValue)
                            {
                                var wanted = selectedKey.Value;
                                selectedHub = scanner.Snapshot().FirstOrDefault(hub => hub.Address == wanted.Address && hub.Instance == wanted.Instance);
                                if (selectedHub != null)
                                    return DiscoveryOutcome.Selected;
                            }
                            WritePanelLine(2, Lang.Get(TextId.BluetoothBeaconVanished), ConsoleColor.Red);
                        }
                    }

                    ConsoleGraphic.EnsureBorderAnimationRunning();
                    Thread.Sleep(30);
                }
            }
        }

        private static int FindSelection(IReadOnlyList<DiscoveredHub> hubs, ref (ulong Address, uint Instance)? selectedKey)
        {
            if (hubs.Count == 0)
                return -1;
            if (!selectedKey.HasValue)
            {
                selectedKey = (hubs[0].Address, hubs[0].Instance);
                return 0;
            }
            var wanted = selectedKey.Value;
            for (int index = 0; index < hubs.Count; index++)
                if (hubs[index].Address == wanted.Address && hubs[index].Instance == wanted.Instance)
                    return index;
            return -1;
        }

        private static int RenderHubList(IReadOnlyList<DiscoveredHub> hubs, int selectedIndex, int previousLines)
        {
            string heading = hubs.Count == 0
                ? String.Empty
                : Lang.Get(hubs.Count == 1 ? TextId.BluetoothFoundOne : TextId.BluetoothFoundMany);
            WritePanelLine(2, heading, ConsoleTheme.MenuText);

            int capacity = Math.Max(0, panelRows - HubListFirstLine - 1);
            int shown = Math.Min(hubs.Count, capacity);
            for (int index = 0; index < shown; index++)
            {
                DiscoveredHub hub = hubs[index];
                bool selected = index == selectedIndex;
                int signal = hub.SignalDbm;
                ConsoleColor signalColor = signal >= SignalGood ? ConsoleColor.Green : signal >= SignalWeak ? ConsoleColor.Yellow : ConsoleColor.Red;
                TextId quality = signal >= SignalGood ? TextId.BluetoothSignalGood : signal >= SignalWeak ? TextId.BluetoothSignalMedium : TextId.BluetoothSignalWeak;
                string name = "@" + hub.Beacon.Nickname + (hub.Beacon.NicknameTruncated ? "…" : String.Empty);
                ConsoleColor textColor = selected ? ConsoleTheme.SelectionBackground : ConsoleTheme.MenuText;
                WritePanelSegments(HubListFirstLine + index, new (string, ConsoleColor)[]
                {
                    ((selected ? "> " : "  ") + name + " (", textColor),
                    (signal + " dBm, " + Lang.Get(quality), signalColor),
                    ("; " + Lang.Get(TextId.BluetoothHosts, DescribeBeaconMode(hub.Beacon.Mode)) + "; " +
                        FormatParticipants(hub.Beacon.Participants) + ")", textColor)
                });
            }
            for (int line = shown; line < previousLines; line++)
                WritePanelLine(HubListFirstLine + line, String.Empty, ConsoleTheme.MenuText);
            return shown;
        }

        private static void ConnectToDiscoveredHub(DiscoveredHub hub)
        {
            string name = "@" + hub.Beacon.Nickname + (hub.Beacon.NicknameTruncated ? "…" : String.Empty);
            ClearPanel();
            WritePanelLine(0, Lang.Get(TextId.BluetoothConnecting, name), ConsoleTheme.MenuText);
            BluetoothChatConnection connection = BluetoothConnector.Connect(hub.Address, out string error);
            if (connection == null)
            {
                WritePanelLine(2, Lang.Get(TextId.BluetoothConnectFailed, error), ConsoleColor.Red);
                WritePanelLine(4, Lang.Get(TextId.BluetoothPressAnyKey), ConsoleTheme.SystemText);
                ReadPanelKey();
                return;
            }

            BluetoothHubScanner scanner = new BluetoothHubScanner();
            try { scanner.Start(); }
            catch (Exception)
            {
                scanner.Dispose();
                scanner = null;
            }
            sessionScanner = scanner;
            sessionHubAddress = hub.Address;
            sentSignal = null;
            nextSignalSend = 0;
            try
            {
                RunPreparedConnection(connection, name + " (Bluetooth)", false);
            }
            finally
            {
                sessionScanner = null;
                scanner?.Dispose();
            }
        }

        private static int ReadInlineChoice(int line, string question, string[] options, int selected = 0)
        {
            WritePanelLine(line, question, ConsoleTheme.MenuText);
            while (true)
            {
                var segments = new List<(string, ConsoleColor)>();
                for (int index = 0; index < options.Length; index++)
                {
                    if (index > 0)
                        segments.Add(("  ", ConsoleTheme.MenuText));
                    segments.Add(("[ " + options[index] + " ]", index == selected ? ConsoleTheme.SelectionBackground : ConsoleTheme.MenuText));
                }
                WritePanelSegments(line + 2, segments);

                ConsoleKeyInfo key = ReadPanelKey();
                switch (key.Key)
                {
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.UpArrow:
                        selected = (selected - 1 + options.Length) % options.Length;
                        break;
                    case ConsoleKey.RightArrow:
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.Tab:
                        selected = (selected + 1) % options.Length;
                        break;
                    case ConsoleKey.Enter:
                    case ConsoleKey.Spacebar:
                        return selected;
                    case ConsoleKey.Escape:
                        return -1;
                }
            }
        }

        private static ConsoleKeyInfo ReadPanelKey()
        {
            while (!Console.KeyAvailable)
            {
                ConsoleGraphic.EnsureBorderAnimationRunning();
                Thread.Sleep(20);
            }
            return Console.ReadKey(true);
        }

        private static void BeginPanel()
        {
            lock (consoleLock)
            {
                panelCursorLeft = Console.CursorLeft;
                panelCursorTop = Console.CursorTop;
                if (ConsoleGraphic.Enabled)
                {
                    panelLeft = ConsoleGraphic.ContentLeft + 1;
                    panelTop = ConsoleGraphic.ContentTop + 1;
                    panelWidth = Math.Max(1, ConsoleGraphic.ContentWidth - 2);
                    panelRows = Math.Max(1, ConsoleGraphic.ContentBottom - panelTop + 1);
                }
                else
                {
                    panelLeft = 0;
                    panelTop = Console.CursorTop;
                    panelWidth = Math.Max(1, Console.BufferWidth - 1);
                    panelRows = Math.Max(1, Math.Min(20, Console.BufferHeight - panelTop));
                }
            }
        }

        private static void RestorePanelCursor()
        {
            lock (consoleLock)
            {
                try { Console.SetCursorPosition(panelCursorLeft, panelCursorTop); }
                catch (ArgumentOutOfRangeException) { }
                catch (System.IO.IOException) { }
            }
        }

        private static void ClearPanel()
        {
            for (int line = 0; line < panelRows; line++)
                WritePanelLine(line, String.Empty, ConsoleTheme.MenuText);
        }

        private static void WritePanelLine(int line, string text, ConsoleColor color) =>
            WritePanelSegments(line, new[] { (text, color) });

        private static void WritePanelSegments(int line, IReadOnlyList<(string Text, ConsoleColor Color)> segments)
        {
            if (line < 0 || line >= panelRows)
                return;
            lock (consoleLock)
            {
                try
                {
                    int row = panelTop + line;
                    if (row >= Console.BufferHeight)
                        return;
                    if (ConsoleGraphic.Enabled)
                    {
                        ConsoleGraphic.ClearContentRow(row);
                    }
                    else
                    {
                        Console.ResetColor();
                        Console.SetCursorPosition(panelLeft, row);
                        Console.Write(new string(' ', panelWidth));
                    }
                    Console.SetCursorPosition(panelLeft, row);
                    int remaining = panelWidth;
                    foreach (var (text, color) in segments)
                    {
                        if (remaining <= 0 || String.IsNullOrEmpty(text))
                            continue;
                        string safe = SanitizeForConsole(text);
                        string part = safe.Length > remaining ? safe.Substring(0, remaining) : safe;
                        ConsoleGraphic.ApplyContentColors(color);
                        Console.Write(part);
                        remaining -= part.Length;
                    }
                }
                catch (ArgumentOutOfRangeException) { }
                catch (System.IO.IOException) { }
                finally
                {
                    Console.ResetColor();
                }
            }
        }
    }
}
