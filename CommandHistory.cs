using System;
using System.Collections.Generic;

namespace TCPTunnel;

internal sealed class CommandHistory
{
    private const int Limit = 64;
    private readonly List<string> entries = new();
    private int index = -1, draftCursor;
    private string draft = String.Empty;

    internal void Submit(string input)
    {
        ResetNavigation();
        if (String.IsNullOrEmpty(input) || input.Length > MessageProtocol.MaxMessageCharacters) return;
        var names = input[0] switch
        {
            '/' => CommandCatalog.Names,
            '\\' => CommandCatalog.LocalNames,
            _ => null
        };
        if (names == null) return;
        int end = 1;
        while (end < input.Length && !Char.IsWhiteSpace(input[end])) end++;
        foreach (string name in names)
        {
            if (!input.AsSpan(1, end - 1).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (entries.Count > 0 && entries[^1] == input) return;
            if (entries.Count == Limit) entries.RemoveAt(0);
            entries.Add(input);
            return;
        }
    }

    internal bool Move(bool older, string current, int cursor, out string text, out int position)
    {
        text = current; position = cursor;
        if (entries.Count == 0 || (!older && index < 0) || (older && index == 0)) return false;
        if (index < 0)
        {
            draft = current; draftCursor = cursor;
            index = entries.Count;
        }
        index += older ? -1 : 1;
        if (index == entries.Count)
        {
            text = draft; position = draftCursor;
            ResetNavigation();
        }
        else
        {
            text = entries[index]; position = text.Length;
        }
        return true;
    }

    internal void ResetNavigation()
    {
        index = -1;
        draft = String.Empty;
        draftCursor = 0;
    }

    internal void Clear()
    {
        entries.Clear();
        ResetNavigation();
    }
}
