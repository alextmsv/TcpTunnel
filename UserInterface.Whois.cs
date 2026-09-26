using System;
using System.Linq;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace TCPTunnel
{
    public partial class UserInterface
    {
        private static WhoisSession whoisSession;
        private static Stream diagnosticsStream;
        private static CancellationToken diagnosticsToken;
        private static long nextMetadataCheck, nextWhoisBlink;
        private static (int Width, int Height)? sentSize;
        private static int chatInputGeneration;
        private static int lastWhoisUiInput;
        private static readonly WhoisUnreadSummary trimmedWhoisNotices = new();

        private static void ShowWhois(string target, Stream stream, WhoisSession session, CancellationToken token)
        {
            WhoisResult result = session.QueryAsync(target, (frame, ct) => MessageProtocol.WriteStringAsync(stream, frame, ct), token)
                .GetAwaiter().GetResult();
            if (!connected || token.IsCancellationRequested) return;
            if (result == null) { WriteSystemChatLine(Lang.Get(TextId.WhoisUnavailable)); return; }
            WhoisInfo info = result.Info;
            if (info == null) { WriteSystemChatLine(Lang.Get(TextId.WhoisNotFound)); return; }
            string unavailable = Lang.Get(TextId.StatusUnavailable);
            WriteSystemChatLine("@" + info.Nickname);
            if (info.Transport == WhoisProtocol.TransportBluetooth)
            {
                WriteSystemChatLine(Lang.Get(TextId.WhoisViaBluetooth));
                WriteSystemChatLine(info.SignalDbm.HasValue ? Lang.Get(TextId.WhoisSignal, info.SignalDbm) : Lang.Get(TextId.WhoisSignalUnavailable));
            }
            else
            {
                if (info.Transport == WhoisProtocol.TransportLocal)
                    WriteSystemChatLine(Lang.Get(TextId.WhoisLocalOwner));
                else
                    WriteSystemChatLine(info.PublicAddressUnavailable ? Lang.Get(TextId.WhoisPublicIpUnavailable) :
                        "IP: " + (String.IsNullOrEmpty(info.Address) ? unavailable : info.Address));
                WriteSystemChatLine(info.PingMilliseconds.HasValue ? Lang.Get(TextId.WhoisPing, info.PingMilliseconds) : Lang.Get(TextId.WhoisPingUnavailable));
            }
            WriteSystemChatLine(Lang.Get(TextId.WhoisWindow, info.WindowWidth > 0 && info.WindowHeight > 0 ? info.WindowWidth + "×" + info.WindowHeight : unavailable));
            if (info.SnakeEnabled.HasValue)
            {
                WriteChatLine(Lang.Get(TextId.WhoisSnake, new string((char)info.SnakeGlyph, 11), info.SnakeDelay), (ConsoleColor)info.SnakeColor);
                WriteSystemChatLine(Lang.Get(info.SnakeEnabled == false ? TextId.WhoisSnakeOff : info.Paused ? TextId.WhoisSnakePaused : TextId.WhoisSnakeMoving));
            }
            else WriteSystemChatLine(Lang.Get(TextId.WhoisSnakeUnavailable));
            WriteSystemChatLine(Lang.Get(TextId.WhoisMessages, info.Messages));
        }

        private static void ReceiveWhoisNotice(string requester)
        {
            WriteChatLine(Lang.Get(TextId.WhoisNotice, requester), null, false, true, new[] { requester });
            WindowAttention.FlashTaskbarUntilForeground();
        }

        private static bool CollectHiddenWhoisNotices(int first)
        {
            bool visibleUnread = false;
            for (int index = 0; index < chatHistory.Count; index++)
            {
                ChatHistoryEntry entry = chatHistory[index];
                if (!entry.WhoisUnread) continue;
                if (index < first)
                {
                    trimmedWhoisNotices.Add(entry.WhoisRequesters, chatInputGeneration);
                    entry.WhoisAttention = null;
                }
                else visibleUnread = true;
            }
            return visibleUnread;
        }

        private static int WhoisVisibleStart(int width, int rows)
        {
            if (!ConsoleGraphic.Enabled) return GetFirstVisibleHistoryEntry(width, rows);
            FindVisibleHistoryStart(width, rows, out int first, out int skipped);
            return skipped > 0 ? first + 1 : first;
        }

        private static void UpdateWhoisUi()
        {
            long now = Environment.TickCount64;
            if (whoisSession?.Supported == true && now >= nextMetadataCheck)
            {
                nextMetadataCheck = now + 1000;
                var size = ConsoleWindowState.CurrentPixelSize();
                if (size.Width <= 0 || size.Height <= 0)
                    size = (Console.WindowWidth, Console.WindowHeight);
                if (sentSize != size)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(diagnosticsToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    try { MessageProtocol.WriteStringAsync(diagnosticsStream, WhoisProtocol.Size(size.Width, size.Height), timeout.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException error) { throw new System.IO.IOException("Window metadata send timed out.", error); }
                    sentSize = size;
                }
                ReportBluetoothSignal(now);
            }
            if (now < nextWhoisBlink) return;
            nextWhoisBlink = now + 400;
            lock (consoleLock)
            {
                bool focused = WindowAttention.IsForeground;
                int width = GetContentWidth();
                int rows = ConsoleGraphic.Enabled
                    ? Math.Max(0, ConsoleGraphic.ContentBottom - ConsoleGraphic.ContentTop + 1 - (inputActive ? GetRequiredInputRows(width, ConsoleGraphic.ContentBottom - ConsoleGraphic.ContentTop + 1) : 0))
                    : Math.Max(1, Console.WindowHeight - 1 - (inputActive ? Math.Max(1, renderedInputRows) : 0));
                int first = WhoisVisibleStart(width, rows);
                bool visibleUnread = CollectHiddenWhoisNotices(first);
                bool freshInput = chatInputGeneration != lastWhoisUiInput;
                lastWhoisUiInput = chatInputGeneration;
                if (!visibleUnread && trimmedWhoisNotices.Count > 0 && (focused || freshInput))
                {
                    bool more = trimmedWhoisNotices.HasMore;
                    int cells = width * Math.Min(rows, Math.Max(1, rows / 2));
                    int budget = cells - Lang.Get(TextId.WhoisUnreadSummary, "").Length - (more ? 3 : 0);
                    string[] names = trimmedWhoisNotices.Take(budget);
                    if (names.Length > 0)
                    {
                        string text = Lang.Get(TextId.WhoisUnreadSummary, String.Join(", ", names.Select(name => "@" + name)) + (more ? ", …" : ""));
                        WriteChatLine(text, null, false, true, names);
                        first = WhoisVisibleStart(width, rows);
                    }
                }
                bool redraw = false, unread = trimmedWhoisNotices.Count > 0;
                for (int index = 0; index < chatHistory.Count; index++)
                {
                    ChatHistoryEntry entry = chatHistory[index];
                    if (!entry.WhoisUnread) continue;
                    entry.WhoisAttention.Observe(index >= first && hasKnownConsoleGeometry && !consoleResizePending,
                        focused, chatInputGeneration, now);
                    unread |= entry.WhoisUnread;
                    entry.WhoisBlink = !entry.WhoisBlink;
                    redraw |= index >= first;
                }
                if (!unread && pendingMentionAnimations.Count == 0) WindowAttention.StopFlashing();
                if (redraw) RedrawChatLayoutLocked();
            }
        }
    }
}
