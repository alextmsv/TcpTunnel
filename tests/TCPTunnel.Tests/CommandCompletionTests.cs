using System.Reflection;
using TCPTunnel;

internal static class CommandCompletionTests
{
    public static bool CommandTokenBoundariesSelfTest()
    {
        if (!InlineSuggestions.TryGetCommandToken("/staXYZ target", 4, out int end, out string prefix) || end != 7 || prefix != "sta") return false;
        foreach (string text in new[] { "hi /sta", "https://host/status", "C:/status", "/whois @alex" })
            if (InlineSuggestions.TryGetCommandToken(text, text.Length, out _, out _)) return false;
        return InlineSuggestions.TryGetCommandToken("/", 1, out _, out _) &&
            InlineSuggestions.TryGetCommandToken("\\la", 3, out _, out var localPrefix) && localPrefix == "la" &&
            !InlineSuggestions.TryGetCommandToken("\\\\server\\share", 5, out _, out _) &&
            InlineSuggestions.MatchPrefix(CommandCatalog.Names, "STA", 4).SequenceEqual(new[] { "status" }) &&
            CommandCatalog.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == CommandCatalog.Names.Count;
    }

    public static bool CommandCompletionPreservesArgumentsSelfTest()
    {
        var type = typeof(UserInterface); const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var buffer = (System.Text.StringBuilder)type.GetField("inputBuffer", flags)!.GetValue(null)!;
        string oldBuffer = buffer.ToString();
        string[] fields = { "inputCursorIndex", "mentionActive", "mentionSuggestions", "mentionSuggestionIndex", "mentionTokenStart", "suggestionMarker", "commandTokenEnd" };
        var saved = fields.ToDictionary(name => name, name => type.GetField(name, flags)!.GetValue(null));
        void Set(string text, int cursor) { buffer.Clear().Append(text); type.GetField("inputCursorIndex", flags)!.SetValue(null, cursor); }
        try
        {
            Set("/whoBAD @alex", 4);
            type.GetMethod("UpdateMentionSuggestionsLocked", flags)!.Invoke(null, null);
            if (!(bool)type.GetField("mentionActive", flags)!.GetValue(null)!) return false;
            type.GetMethod("AcceptMentionSuggestionLocked", flags)!.Invoke(null, null);
            if (buffer.ToString() != "/whois @alex" || (int)type.GetField("inputCursorIndex", flags)!.GetValue(null)! != 7) return false;
            Set("/help", 5); type.GetMethod("UpdateMentionSuggestionsLocked", flags)!.Invoke(null, null);
            type.GetMethod("AcceptMentionSuggestionLocked", flags)!.Invoke(null, null);
            if (buffer.ToString() != "/help " || (bool)type.GetField("mentionActive", flags)!.GetValue(null)!) return false;
            Set("\\laBAD ru", 3); type.GetMethod("UpdateMentionSuggestionsLocked", flags)!.Invoke(null, null);
            type.GetMethod("AcceptMentionSuggestionLocked", flags)!.Invoke(null, null);
            return buffer.ToString() == "\\lang ru" && !(bool)type.GetField("mentionActive", flags)!.GetValue(null)!;
        }
        finally { buffer.Clear().Append(oldBuffer); foreach (var field in saved) type.GetField(field.Key, flags)!.SetValue(null, field.Value); }
    }
}
