using TCPTunnel;

internal static class CommandHelpTests
{
    public static bool LocalizedCommandHelpSelfTest()
    {
        AppLanguage original = Lang.Current;
        string[] commands = { "help", "invite", "pending", "approve", "identity", "kick", "status", "whois", "ping", "clear", "look", "stop", "exit" };
        var lines = new List<string>();
        var context = new CommandContext
        {
            IsLocalHubAdministrator = false,
            WriteLine = (text, _) => lines.Add(text),
            Kick = (_, _) => throw new Exception("Help executed kick."),
            ClearChat = () => throw new Exception("Help cleared chat."),
            LookImage = () => throw new Exception("Help opened a viewer."),
            StopLocalHub = () => throw new Exception("Help stopped a Hub."),
            Whois = _ => throw new Exception("Help queried a participant.")
        };
        try
        {
            foreach (AppLanguage language in new[] { AppLanguage.Russian, AppLanguage.English })
            {
                Lang.Set(language);
                string usage = language == AppLanguage.Russian ? "Использование:" : "Usage:";
                foreach (string command in commands)
                {
                    lines.Clear();
                    if (Commands.InitCommand("/help " + command, context) != CommandDisposition.Handled || lines.Count < 4 ||
                        !lines[0].StartsWith("/" + command + " — ") || !lines.Any(line => line.StartsWith(usage))) return false;
                    string[] expected = lines.ToArray(); lines.Clear();
                    if (Commands.InitCommand("/HELP /" + command.ToUpperInvariant(), context) != CommandDisposition.Handled || !lines.SequenceEqual(expected)) return false;
                }
                lines.Clear();
                Commands.InitCommand("/help", context);
                if (lines.Count != commands.Length + 1 || commands.Any(command => !lines.Any(line => line.StartsWith("/" + command + " — ")))) return false;
                lines.Clear(); Commands.InitCommand("/help unknown", context);
                if (lines.Count != 1 || lines[0] != Lang.Get(TextId.HelpUnknown, "unknown")) return false;
                lines.Clear(); Commands.InitCommand("/help invite extra", context);
                if (lines.Count != 1 || lines[0] != Lang.Get(TextId.HelpUsage)) return false;
            }
            return true;
        }
        finally { Lang.Set(original); }
    }
}
