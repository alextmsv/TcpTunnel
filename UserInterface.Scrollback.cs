using System;

namespace TCPTunnel;

public partial class UserInterface
{
    private static ChatHistoryEntry scrollAnchorEntry;
    private static int scrollAnchorRow, scrollAnchorWidth, scrollUnreadCount;
    private static bool scrollHistoryTrimmed, scrollHintShown;
    private static int scrollViewportTop, scrollViewportBottomExclusive;
    private static bool ScrollbackActive => scrollAnchorEntry != null;

    private static void ResetScrollbackLocked(bool resetHint = false)
    {
        scrollAnchorEntry = null; scrollAnchorRow = 0; scrollAnchorWidth = 0;
        scrollUnreadCount = 0; scrollHistoryTrimmed = false;
        scrollViewportTop = scrollViewportBottomExclusive = 0;
        if (resetHint) scrollHintShown = false;
    }

    private static void CountScrollbackAppendLocked()
    {
        if (ScrollbackActive) scrollUnreadCount = Math.Min(Int32.MaxValue - 1, scrollUnreadCount) + 1;
    }

    private static void ReconcileScrollbackTrimLocked()
    {
        if (!ScrollbackActive || chatHistory.Contains(scrollAnchorEntry)) return;
        if (chatHistory.Count == 0) { ResetScrollbackLocked(); return; }
        scrollAnchorEntry = chatHistory[0]; scrollAnchorRow = 0; scrollHistoryTrimmed = true;
    }

    private static int ScrollbackBottom => ConsoleGraphic.Enabled ? ConsoleGraphic.ContentBottom : Math.Max(0, Math.Min(Console.WindowHeight, Console.BufferHeight) - 1);

    private static int ScrollbackLiveRows()
    {
        int rows = Math.Max(1, ScrollbackBottom - ConsoleGraphic.ContentTop + 1);
        int inputRows = inputActive ? GetRequiredInputRows(GetContentWidth(), rows) : 0;
        int popupRows = inputActive ? GetRequiredMentionPopupRows(Math.Max(0, rows - inputRows)) : 0;
        return Math.Max(0, rows - inputRows - popupRows);
    }

    private static int ScrollbackAnchorRow(int width)
    {
        ReconcileScrollbackTrimLocked();
        if (!ScrollbackActive) return 0;
        if (scrollAnchorWidth > 0 && scrollAnchorWidth != width)
        {
            int previousCaption = scrollAnchorEntry.IsVisual ? GetWrappedRowCount(scrollAnchorEntry.Text, scrollAnchorWidth) : Int32.MaxValue;
            scrollAnchorRow = scrollAnchorRow < previousCaption
                ? (int)Math.Min(Int32.MaxValue, (long)scrollAnchorRow * scrollAnchorWidth / Math.Max(1, width))
                : GetWrappedRowCount(scrollAnchorEntry.Text, width) + scrollAnchorRow - previousCaption;
        }
        scrollAnchorWidth = width;
        return scrollAnchorRow = Math.Clamp(scrollAnchorRow, 0, Math.Max(0, GetHistoryRowCount(scrollAnchorEntry, width) - 1));
    }

    private static void FindViewportHistoryStart(int width, int availableRows, out int firstEntry, out int rowsToSkip)
    {
        if (!ScrollbackActive) { FindVisibleHistoryStart(width, availableRows, out firstEntry, out rowsToSkip); return; }
        rowsToSkip = ScrollbackAnchorRow(width);
        firstEntry = ScrollbackActive ? Math.Max(0, chatHistory.IndexOf(scrollAnchorEntry)) : chatHistory.Count;
    }

    private static (int Total, int Start) GetScrollbackPosition(int width, int firstEntry, int rowsToSkip)
    {
        int total = 0, start = 0;
        for (int i = 0; i < chatHistory.Count; i++)
        {
            int height = GetHistoryRowCount(chatHistory[i], width);
            if (i < firstEntry) start += height;
            total += height;
        }
        return (total, start + rowsToSkip);
    }

    private static void ScrollChatRowsLocked(int delta)
    {
        if (chatHistory.Count == 0 || (!ScrollbackActive && delta > 0) || !EnsureConsoleGeometryLocked()) return;
        int width = GetContentWidth(), rows = ScrollbackLiveRows();
        if (rows == 0) return;
        int anchorRow = ScrollbackAnchorRow(width);
        if (!ScrollbackActive && delta > 0) return;
        if (ScrollbackActive && delta < 0 && anchorRow == 0 && scrollAnchorEntry == chatHistory[0]) return;
        var (total, current) = GetScrollbackPosition(width, ScrollbackActive ? chatHistory.IndexOf(scrollAnchorEntry) : 0, anchorRow);
        int tail = Math.Max(0, total - rows);
        if (!ScrollbackActive) current = tail;
        int target = (int)Math.Clamp((long)current + delta, 0L, tail);
        if (target == current && (!ScrollbackActive || target < tail)) return;
        if (target >= tail) ResetScrollbackLocked();
        else
        {
            foreach (ChatHistoryEntry entry in chatHistory)
            {
                int height = GetHistoryRowCount(entry, width);
                if (target < height) { scrollAnchorEntry = entry; scrollAnchorRow = target; scrollAnchorWidth = width; break; }
                target -= height;
            }
        }
        if (!RedrawChatLayoutLocked()) MarkConsoleResizePendingLocked();
    }

    private static bool HandleScrollbackInputLocked(ConsoleInputEvent inputEvent)
    {
        if (inputEvent.Kind == ConsoleInputKind.Key)
        {
            ConsoleKeyInfo key = inputEvent.Key;
            if (key.Key == ConsoleKey.End && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                if (ScrollbackActive) { ResetScrollbackLocked(); if (!RedrawChatLayoutLocked()) MarkConsoleResizePendingLocked(); }
                return true;
            }
            if (key.Key == ConsoleKey.Home && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                ScrollChatRowsLocked(Int32.MinValue);
                return true;
            }
            if (key.Key is not (ConsoleKey.PageUp or ConsoleKey.PageDown)) return false;
            ScrollChatRowsLocked((key.Key == ConsoleKey.PageUp ? -1 : 1) * Math.Max(1, ScrollbackLiveRows() - 2));
            return true;
        }
        if (inputEvent.Kind != ConsoleInputKind.Wheel || !ConsoleInput.MouseMode) return false;
        int top = ScrollbackActive || ConsoleGraphic.Enabled ? scrollViewportTop : ConsoleGraphic.ContentTop;
        int bottom = ScrollbackActive || ConsoleGraphic.Enabled ? scrollViewportBottomExclusive : Math.Min(inputStartRow, ScrollbackBottom + 1);
        if (inputEvent.X < ConsoleGraphic.ContentLeft || inputEvent.X >= ConsoleGraphic.ContentLeft + ConsoleGraphic.ContentWidth ||
            inputEvent.Y < top || inputEvent.Y >= bottom) return false;
        ScrollChatRowsLocked(Math.Sign(inputEvent.Direction) * 3);
        return true;
    }

    private static void DrawScrollbackBar(int width, int top, int totalRows, int chatRows, int liveRows, int firstEntry, int rowsToSkip, ConsoleGraphic.ContentFrame frame)
    {
        if (!ConsoleGraphic.Enabled || width >= ConsoleGraphic.ContentWidth) return;
        var (total, start) = GetScrollbackPosition(width, firstEntry, rowsToSkip);
        int tail = Math.Max(0, total - liveRows);
        char[] column = new string(' ', totalRows).ToCharArray();
        if (tail > 0 && chatRows > 0)
        {
            int thumb = Math.Clamp((int)((long)chatRows * chatRows / total), 1, Math.Max(1, chatRows - 1));
            int travel = chatRows - thumb;
            int position = ScrollbackActive ? (int)((long)Math.Clamp(start, 0, tail) * travel / tail) : travel;
            Array.Fill(column, '|', 0, chatRows);
            Array.Fill(column, '#', position, thumb);
        }
        int left = ConsoleGraphic.ContentLeft + width;
        if (frame != null)
        {
            for (int i = 0; i < totalRows; i++)
                frame.Put(left, top + i, column.AsSpan(i, 1), ConsoleTheme.SystemText);
            return;
        }
        ConsoleGraphic.WriteTextRun(left, top, new string(column), 0, totalRows,
            ConsoleTheme.SystemText, ConsoleColor.Black, vertical: true);
    }

    private static void DrawScrollbackStatus(int row, int width, ConsoleGraphic.ContentFrame frame)
    {
        string text = Lang.Current == AppLanguage.Russian
            ? "История | новых: " + scrollUnreadCount + " | PgUp/PgDn | Ctrl+End — к новым"
            : "History | new: " + scrollUnreadCount + " | PgUp/PgDn | Ctrl+End: latest";
        if (scrollHistoryTrimmed) text = (Lang.Current == AppLanguage.Russian ? "Начало истории удалено | " : "Earlier history discarded | ") + text;
        text = ConsoleText.Safe(text);
        if (frame != null)
        {
            frame.Put(ConsoleGraphic.ContentLeft, row, text.AsSpan(0, Math.Min(width, text.Length)), ConsoleTheme.SystemText);
            return;
        }
        ConsoleGraphic.ClearContentRow(row);
        ConsoleGraphic.WriteTextRun(ConsoleGraphic.ContentLeft, row, text, 0, Math.Min(width, text.Length), ConsoleTheme.SystemText, ConsoleColor.Black);
    }

    private static void ShowScrollbackHintLocked()
    {
        if (scrollHintShown) return;
        scrollHintShown = true;
        WriteSystemChatLine(Lang.Current == AppLanguage.Russian
            ? "История чата: PgUp/PgDn, Ctrl+Home — в начало, Ctrl+End — к новым; колесо — в режиме мыши."
            : "Chat history: PgUp/PgDn, Ctrl+Home for oldest, Ctrl+End for latest; mouse wheel in mouse mode.");
    }
}
