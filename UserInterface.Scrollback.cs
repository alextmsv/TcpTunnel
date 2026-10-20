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

    private static int ScrollbackPageRows()
    {
        int rows = Math.Max(1, ScrollbackBottom - ConsoleGraphic.ContentTop + 1);
        int inputRows = inputActive ? GetRequiredInputRows(GetContentWidth(), rows) : 0;
        int popupRows = inputActive ? GetRequiredMentionPopupRows(Math.Max(0, rows - inputRows)) : 0;
        return Math.Max(1, rows - inputRows - popupRows - (rows - inputRows - popupRows >= 2 ? 1 : 0));
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

    private static void ScrollChatRowsLocked(int delta)
    {
        if (chatHistory.Count == 0 || !EnsureConsoleGeometryLocked()) return;
        int width = GetContentWidth(), rows = ScrollbackPageRows(), total = 0, current = 0;
        int anchorRow = ScrollbackAnchorRow(width);
        foreach (ChatHistoryEntry entry in chatHistory)
        {
            if (entry == scrollAnchorEntry) current = total + anchorRow;
            total += GetHistoryRowCount(entry, width);
        }
        int tail = Math.Max(0, total - rows);
        if (!ScrollbackActive) current = tail;
        int target = (int)Math.Clamp((long)current + delta, 0L, tail);
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
            if (key.Key is not (ConsoleKey.PageUp or ConsoleKey.PageDown)) return false;
            ScrollChatRowsLocked((key.Key == ConsoleKey.PageUp ? -1 : 1) * Math.Max(1, ScrollbackPageRows() - 1));
            return true;
        }
        if (inputEvent.Kind != ConsoleInputKind.Wheel || !ConsoleInput.MouseMode) return false;
        int top = ScrollbackActive || ConsoleGraphic.Enabled ? scrollViewportTop : ConsoleGraphic.ContentTop;
        int bottom = ScrollbackActive || ConsoleGraphic.Enabled ? scrollViewportBottomExclusive : Math.Min(inputStartRow, ScrollbackBottom + 1);
        if (inputEvent.X < ConsoleGraphic.ContentLeft || inputEvent.X >= ConsoleGraphic.ContentLeft + GetContentWidth() ||
            inputEvent.Y < top || inputEvent.Y >= bottom) return false;
        ScrollChatRowsLocked(Math.Sign(inputEvent.Direction) * 3);
        return true;
    }

    private static void DrawScrollbackStatus(int row, int width)
    {
        string text = Lang.Current == AppLanguage.Russian
            ? "История | новых: " + scrollUnreadCount + " | PgUp/PgDn | Ctrl+End — к новым"
            : "History | new: " + scrollUnreadCount + " | PgUp/PgDn | Ctrl+End: latest";
        if (scrollHistoryTrimmed) text = (Lang.Current == AppLanguage.Russian ? "Начало истории удалено | " : "Earlier history discarded | ") + text;
        ConsoleGraphic.ClearContentRow(row);
        text = ConsoleText.Safe(text);
        ConsoleGraphic.WriteTextRun(ConsoleGraphic.ContentLeft, row, text, 0, Math.Min(width, text.Length), ConsoleTheme.SystemText, ConsoleColor.Black);
    }

    private static void ShowScrollbackHintLocked()
    {
        if (scrollHintShown) return;
        scrollHintShown = true;
        WriteSystemChatLine(Lang.Current == AppLanguage.Russian
            ? "История чата: PgUp/PgDn, Ctrl+End — к новым сообщениям; колесо — в режиме мыши."
            : "Chat history: PgUp/PgDn, Ctrl+End for latest messages; mouse wheel in mouse mode.");
    }
}
