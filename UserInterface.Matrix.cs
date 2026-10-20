using System;
using System.Collections.Generic;
using System.Diagnostics;
namespace TCPTunnel;
public partial class UserInterface
{
    private readonly record struct TextCellFragment(int Offset, int Count, int Left, int Top);
    private static int matrixLayoutVersion;
    private static HashSet<ChatHistoryEntry> FinishMatrixEffectsLocked()
    {
        HashSet<ChatHistoryEntry> mentions = null;
        foreach (ChatHistoryEntry entry in chatHistory)
        {
            if (entry.Matrix != null && entry.MentionsLocalUser) { mentions ??= new(); mentions.Add(entry); }
            entry.Matrix?.Finish(); entry.Matrix = null;
        }
        return mentions;
    }
    private static void InvalidateMatrixTargetsLocked()
    {
        matrixLayoutVersion++;
        foreach (ChatHistoryEntry entry in chatHistory) { entry.MatrixFragments.Clear(); entry.MatrixLayoutVersion = -1; }
    }
    private static void PublishMatrixTargetsLocked()
    {
        foreach (ChatHistoryEntry entry in chatHistory) if (entry.MatrixFragments.Count > 0) entry.MatrixLayoutVersion = matrixLayoutVersion;
    }
    private static void RegisterMatrixFragment(ChatHistoryEntry entry, int offset, int count, int left, int top)
    {
        if (entry.Matrix != null && count > 0) entry.MatrixFragments.Add(new(offset, count, left, top));
    }
    private static void RegisterPlainMatrixFragments(ChatHistoryEntry entry, int left, int top, int width)
    {
        if (entry.Matrix == null) return;
        int offset = 0;
        while (offset < entry.Text.Length)
        {
            int count = Math.Min(width - left, entry.Text.Length - offset);
            RegisterMatrixFragment(entry, offset, count, left, top++); offset += count; left = 0;
        }
    }
    private static bool TickMatrixEffectsLocked(out bool active)
    {
        active = false; bool changed = false;
        var mentions = new List<MentionFragment>();
        foreach (ChatHistoryEntry entry in chatHistory)
        {
            if (entry.Matrix == null) continue;
            int before = entry.Matrix.VisibleLength;
            if (!ApplicationSettings.Current.MatrixText) entry.Matrix.Finish();
            int elapsed = (int)Math.Min(Int32.MaxValue, Stopwatch.GetElapsedTime(entry.MatrixStarted).TotalMilliseconds);
            bool tick = entry.Matrix.Tick(elapsed);
            int after = entry.Matrix.VisibleLength;
            changed |= tick || after != before;
            if (entry.MatrixLayoutVersion == matrixLayoutVersion && after > before)
            {
                foreach (var fragment in entry.MatrixFragments)
                {
                    int start = Math.Max(before, fragment.Offset), end = Math.Min(after, fragment.Offset + fragment.Count);
                    while (start < end)
                    {
                        ChatTextStyle style = GetChatTextStyle(entry, start); int runEnd = start + 1;
                        while (runEnd < end && GetChatTextStyle(entry, runEnd).IsSameAs(style)) runEnd++;
                        bool mention = entry.Mentions.Exists(span => start >= span.Start && start < span.Start + span.Length);
                        if (!ConsoleGraphic.WriteTextRun(fragment.Left + start - fragment.Offset, fragment.Top, entry.Text, start, runEnd - start, style.Foreground, style.Background, !mention))
                        { MarkConsoleResizePendingLocked(); return changed; }
                        start = runEnd;
                    }
                }
            }
            if (!entry.Matrix.Active)
            {
                entry.Matrix = null;
                if (entry.MentionsLocalUser && entry.MatrixLayoutVersion == matrixLayoutVersion)
                    foreach (var fragment in entry.MatrixFragments) CollectMentionFragments(entry, fragment.Offset, fragment.Count, fragment.Left, fragment.Top, mentions);
            }
            else active = true;
        }
        if (mentions.Count > 0) AnimateMentionFragments(mentions);
        return changed;
    }
}