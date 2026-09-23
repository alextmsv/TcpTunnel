using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public partial class UserInterface
    {
        private static async Task ReceiveAnimationAsync(AnimatedImagePacket packet)
        {
            int viewportWidth;
            int usableRows;
            int historyVersion;
            SnapshotImageViewport(out viewportWidth, out usableRows, out historyVersion);
            FrozenAnimation frozen = await Task.Run(() => ImageRenderer.Freeze(
                packet,
                viewportWidth,
                usableRows,
                packet.Sender,
                false)).ConfigureAwait(false);
            WriteChatAnimation(frozen, frozen.ShouldOfferLook ? packet : null, historyVersion);
        }

        private static void PrepareAndSendAnimation(
            Stream stream,
            CancellationToken cancellationToken,
            string path)
        {
            WriteSystemChatLine(Lang.Get(TextId.PreparingAnimation));
            try
            {
                AnimatedImagePacket packet = Task.Run(() => ImageCodec.PrepareAnimation(path))
                    .GetAwaiter().GetResult();
                string transferId = ImageAnimationProtocol.CreateTransferId();
                MessageProtocol.WriteStringAsync(
                    stream,
                    ImageAnimationProtocol.CreateClientBegin(transferId, packet),
                    cancellationToken).GetAwaiter().GetResult();
                for (int index = 0; index < packet.FrameCount; index++)
                {
                    MessageProtocol.WriteStringAsync(
                        stream,
                        ImageAnimationProtocol.CreateClientFrame(
                            transferId,
                            index,
                            packet.FrameDelays[index],
                            packet.PackedFrames[index]),
                        cancellationToken).GetAwaiter().GetResult();
                }
                MessageProtocol.WriteStringAsync(
                    stream,
                    ImageAnimationProtocol.CreateClientEnd(transferId),
                    cancellationToken).GetAwaiter().GetResult();

                int viewportWidth;
                int usableRows;
                int ignoredHistoryVersion;
                SnapshotImageViewport(out viewportWidth, out usableRows, out ignoredHistoryVersion);
                FrozenAnimation frozen = Task.Run(() => ImageRenderer.Freeze(
                    packet,
                    viewportWidth,
                    usableRows,
                    nickname,
                    true)).GetAwaiter().GetResult();
                WriteChatAnimation(frozen, frozen.ShouldOfferLook ? packet : null);
            }
            catch (ImagePreparationException ex)
            {
                WriteSystemChatLine(Lang.Get(GetImageErrorText(ex.Error)));
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is FormatException)
            {
                WriteSystemChatLine(Lang.Get(TextId.ImageDecodeFailed));
            }
        }

        private static void WriteChatAnimation(
            FrozenAnimation animation,
            AnimatedImagePacket largePacket = null,
            int expectedHistoryVersion = -1)
        {
            lock (consoleLock)
            {
                if (expectedHistoryVersion >= 0 && expectedHistoryVersion != imageHistoryVersion)
                    return;
                if (largePacket != null)
                {
                    lastLargeAnimationPacket = largePacket;
                    lastLargeImagePacket = null;
                }
                bool consoleReady = EnsureConsoleGeometryLocked();
                AppendAnimationHistoryLocked(animation);
                if (!consoleReady)
                    return;
                if (!RedrawChatLayoutLocked())
                    MarkConsoleResizePendingLocked();
                ScheduleAnimationMonitor();
            }
        }

        private static void ScheduleAnimationMonitor()
        {
            if (Interlocked.CompareExchange(ref animationMonitorActive, 1, 0) != 0)
                return;
            int sessionVersion = Volatile.Read(ref chatSessionVersion);
            Task.Run(async () =>
            {
                int delayMilliseconds = 10;
                try
                {
                    while (connected && sessionVersion == Volatile.Read(ref chatSessionVersion))
                    {
                        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
                        bool hasVisibleAnimation;
                        lock (consoleLock)
                        {
                            if (!connected || sessionVersion != Volatile.Read(ref chatSessionVersion))
                                break;
                            hasVisibleAnimation = RenderVisibleAnimationFramesLocked(out delayMilliseconds);
                        }
                        if (!hasVisibleAnimation)
                            break;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref animationMonitorActive, 0);
                }
            });
        }

        private static bool RenderVisibleAnimationFramesLocked(out int nextDelayMilliseconds)
        {
            nextDelayMilliseconds = 100;
            if (consoleResizePending)
                return HasVisibleAnimationTargetLocked();

            ConsoleGraphic.ConsoleGeometry geometry;
            if (!ConsoleGraphic.TryCaptureConsoleGeometry(out geometry) ||
                !hasKnownConsoleGeometry || !geometry.IsSameAs(knownConsoleGeometry))
            {
                MarkConsoleResizePendingLocked();
                return HasVisibleAnimationTargetLocked();
            }

            int previousLeft;
            int previousTop;
            try
            {
                previousLeft = Console.CursorLeft;
                previousTop = Console.CursorTop;
            }
            catch (IOException)
            {
                MarkConsoleResizePendingLocked();
                return false;
            }

            bool found = false;
            try
            {
                for (int index = 0; index < chatHistory.Count; index++)
                {
                    ChatHistoryEntry entry = chatHistory[index];
                    if (!entry.IsAnimation || entry.AnimationTop < 0 ||
                        entry.AnimationVisibleRows <= 0 || entry.AnimationVisibleWidth <= 0)
                        continue;
                    found = true;
                    long elapsedMilliseconds = GetAnimationElapsedMilliseconds(entry);
                    int frame = ImageRenderer.GetFrameIndex(entry.Animation, elapsedMilliseconds);
                    nextDelayMilliseconds = Math.Min(
                        nextDelayMilliseconds,
                        ImageRenderer.GetMillisecondsUntilNextFrame(entry.Animation, elapsedMilliseconds));
                    if (frame == entry.LastRenderedAnimationFrame)
                        continue;

                    char[] row = entry.AnimationRowBuffer;
                    if (row == null || row.Length != entry.AnimationVisibleWidth)
                        row = entry.AnimationRowBuffer = new char[entry.AnimationVisibleWidth];
                    Console.ForegroundColor = ConsoleColor.Gray;
                    for (int visibleRow = 0; visibleRow < entry.AnimationVisibleRows; visibleRow++)
                    {
                        int top = entry.AnimationTop + visibleRow;
                        int sourceRow = entry.AnimationFirstSourceRow + visibleRow;
                        if (top < 0 || top >= Console.BufferHeight ||
                            sourceRow < 0 || sourceRow >= entry.Animation.Height)
                            continue;
                        int left = ConsoleGraphic.Enabled ? ConsoleGraphic.ContentLeft : 0;
                        if (left < 0 || left + row.Length > Console.BufferWidth)
                            continue;
                        Console.SetCursorPosition(left, top);
                        ImageRenderer.FillAsciiRow(entry.Animation, frame, sourceRow, row);
                        Console.Write(row);
                    }
                    entry.LastRenderedAnimationFrame = frame;
                }
                Console.ResetColor();
                int safeLeft = Math.Max(0, Math.Min(previousLeft, Console.BufferWidth - 1));
                int safeTop = Math.Max(0, Math.Min(previousTop, Console.BufferHeight - 1));
                Console.SetCursorPosition(safeLeft, safeTop);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IOException)
            {
                Console.ResetColor();
                MarkConsoleResizePendingLocked();
            }
            nextDelayMilliseconds = Math.Max(5, Math.Min(100, nextDelayMilliseconds));
            return found;
        }

        private static bool HasVisibleAnimationTargetLocked()
        {
            for (int index = 0; index < chatHistory.Count; index++)
            {
                ChatHistoryEntry entry = chatHistory[index];
                if (entry.IsAnimation && entry.AnimationTop >= 0 && entry.AnimationVisibleRows > 0)
                    return true;
            }
            return false;
        }

        private static bool HasAnimationHistoryLocked()
        {
            for (int index = 0; index < chatHistory.Count; index++)
            {
                if (chatHistory[index].IsAnimation)
                    return true;
            }
            return false;
        }

        private static void InvalidateAnimationTargetsLocked()
        {
            for (int index = 0; index < chatHistory.Count; index++)
            {
                ChatHistoryEntry entry = chatHistory[index];
                if (!entry.IsAnimation)
                    continue;
                entry.AnimationTop = -1;
                entry.AnimationFirstSourceRow = 0;
                entry.AnimationVisibleRows = 0;
                entry.AnimationVisibleWidth = 0;
                entry.LastRenderedAnimationFrame = -1;
            }
        }

        private static int GetCurrentAnimationFrame(ChatHistoryEntry entry)
        {
            return ImageRenderer.GetFrameIndex(entry.Animation, GetAnimationElapsedMilliseconds(entry));
        }

        private static long GetAnimationElapsedMilliseconds(ChatHistoryEntry entry)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - entry.AnimationStartedTimestamp;
            if (elapsedTicks <= 0)
                return 0;
            long wholeSeconds = elapsedTicks / Stopwatch.Frequency;
            long remainder = elapsedTicks % Stopwatch.Frequency;
            return wholeSeconds * 1000L + remainder * 1000L / Stopwatch.Frequency;
        }

        private static int GetVisualWidth(ChatHistoryEntry entry)
        {
            return entry.IsAnimation ? entry.Animation.Width : entry.Image.Width;
        }

        private static int GetVisualHeight(ChatHistoryEntry entry)
        {
            return entry.IsAnimation ? entry.Animation.Height : entry.Image.Height;
        }

        private static bool VisualShouldOfferLook(ChatHistoryEntry entry)
        {
            return entry.IsAnimation ? entry.Animation.ShouldOfferLook : entry.Image.ShouldOfferLook;
        }

        private static void FillVisualAsciiRow(
            ChatHistoryEntry entry,
            int frame,
            int row,
            Span<char> destination)
        {
            if (entry.IsAnimation)
                ImageRenderer.FillAsciiRow(entry.Animation, frame, row, destination);
            else
                ImageRenderer.FillAsciiRow(entry.Image, row, destination);
        }

        private static int GetVisualPromptBoxRowCount(ChatHistoryEntry entry, int availableWidth)
        {
            string prompt = GetVisualLookPrompt(entry);
            int width = Math.Max(1, Math.Min(Math.Max(1, availableWidth), prompt.Length + 4));
            if (width < 5)
                return 1;
            int innerWidth = width - 4;
            return 2 + Math.Max(1, (prompt.Length + innerWidth - 1) / innerWidth);
        }

        private static List<string> BuildVisualPromptBox(ChatHistoryEntry entry, int availableWidth)
        {
            string prompt = GetVisualLookPrompt(entry);
            int width = Math.Max(1, Math.Min(Math.Max(1, availableWidth), prompt.Length + 4));
            var rows = new List<string>();
            if (width < 5)
            {
                rows.Add(prompt.Substring(0, Math.Min(width, prompt.Length)));
                return rows;
            }
            string border = "+" + new string('-', width - 2) + "+";
            rows.Add(border);
            int innerWidth = width - 4;
            int offset = 0;
            do
            {
                int count = Math.Min(innerWidth, Math.Max(0, prompt.Length - offset));
                string part = count > 0 ? prompt.Substring(offset, count) : String.Empty;
                rows.Add("| " + part.PadRight(innerWidth) + " |");
                offset += count;
            }
            while (offset < prompt.Length);
            rows.Add(border);
            return rows;
        }

        private static string GetVisualLookPrompt(ChatHistoryEntry entry)
        {
            bool oversized = entry.IsAnimation
                ? entry.Animation.IsOversized
                : entry.Image.IsOversized;
            return Lang.Get(oversized
                ? TextId.ImageTooLargePrompt
                : TextId.ImageStronglyCompressedPrompt);
        }
    }
}
