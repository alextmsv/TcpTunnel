using TCPTunnel;

internal static class NicknameSnakeTests
{
    public static bool NicknameSnakeWireBoundsSelfTest()
    {
        var profile = new SnakeProfile { Enabled = true, DelayMilliseconds = 87, Color = ConsoleColor.Cyan, Glyph = '~', Nickname = true };
        string request = SnakeProtocol.CreateClientProfile(profile);
        if (!SnakeProtocol.TryParseClientProfile(request, out var read) || !read.Nickname || read.Glyph != '~' || read.DelayMilliseconds != 87) return false;
        foreach (string invalid in new[] { request[..^1] + "2", request[..^1] + "-1", request + "|1", request[..^1] + "2147483648", request[..^1] + "\u001b" })
            if (SnakeProtocol.TryParseClientProfile(invalid, out _)) return false;
        if (!SnakeProtocol.TryParseServerUpdate(SnakeProtocol.CreateSet("wdg", profile), out _, out _, out var modern) || !modern.Nickname) return false;
        if (!SnakeProtocol.TryParseServerUpdate(SnakeProtocol.CreateSet("wdg", profile, false), out _, out _, out var legacy) || legacy.Nickname) return false;
        return new string(SnakeProtocol.NicknameBody("wdg")) == "wdg" && new string(SnakeProtocol.NicknameBody("alextmsv")) == "alextmsv" &&
            new string(SnakeProtocol.NicknameBody("aaa")) == "aaa" && new string(SnakeProtocol.NicknameBody("Русский")) == "Русский" &&
            SnakeProtocol.NicknameBody("ab\u202Ec").Length == 0 && SnakeProtocol.NicknameBody("ab\u115F").Length == 0 &&
            !NicknamePolicy.IsValid("ab\u1160");
    }
}
