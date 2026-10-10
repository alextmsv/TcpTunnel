using TCPTunnel;

internal static class CommandRegressionTests
{
    public static bool WhoisCommandSelfTest()
    {
        var targets = new List<string>();
        var context = new CommandContext { Whois = targets.Add, WriteLine = (_, _) => { } };
        foreach (string input in new[] { "/whois alex", "/WHOIS @alex", "/whois", "/whois @", "/whois a b", "Command /whois alex" })
            Commands.InitCommand(input, context);
        return targets.SequenceEqual(new[] { "alex", "alex" });
    }

    public static bool KickFreeReasonSelfTest()
    {
        foreach (var sample in new[]
        {
            ("/kick alex", ""),
            ("/kick @alex Причина из нескольких слов", "Причина из нескольких слов"),
            ("/KICK alex \"Причина из нескольких слов\"", "Причина из нескольких слов"),
            ("/kick alex Please stop saying \"hello\"", "Please stop saying \"hello\""),
            ("/kick alex unmatched \" quote is text", "unmatched \" quote is text"),
            ("/kick   alex   reason", "reason"),
            ("/kick alex " + new string('a', 200), new string('a', 200))
        })
        {
            string? target = null, reason = null;
            var context = Context((n, r) => { target = n; reason = r; return KickCommandResult.Success; });
            if (Commands.InitCommand(sample.Item1, context) != CommandDisposition.Handled ||
                target != "alex" || reason != sample.Item2)
                throw new Exception($"KICK-01/02: failed to parse {sample.Item1}");
        }
        return true;
    }

    public static bool KickRejectionsSelfTest()
    {
        int calls = 0;
        var context = Context((_, _) => { calls++; return KickCommandResult.Success; });
        foreach (string input in new[] { "/kick", "/kick @", "/kick ab", "/kick \"alex\" reason",
                     "/kick alex " + new string('x', 201), "/kicker alex", "There is a /kick command" })
            Commands.InitCommand(input, context);
        context.IsLocalHubAdministrator = false;
        Commands.InitCommand("/kick alex reason with spaces", context);
        return calls == 0;
    }

    private static CommandContext Context(Func<string, string, KickCommandResult> kick) => new()
    {
        IsLocalHubAdministrator = true,
        Kick = kick,
        WriteLine = (_, _) => { }
    };
}
