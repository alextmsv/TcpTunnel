using System.Text;
using System.Text.Json;
using TCPTunnel;

internal static class ThemeAndProfileTests
{
    public static bool TerminalSessionThemeSelfTest()
    {
        foreach (ConsoleColor color in Enum.GetValues<ConsoleColor>())
        {
            if (ConsoleTheme.ContentColor(color, 0) != color) return false;
            if (
                ConsoleTheme.ContentColor(color, 0xFFFFFF) != color) return false;
        }
        if (ConsoleTheme.ForegroundSgr(ConsoleColor.DarkGreen) != 32 ||
            ConsoleTheme.ForegroundSgr(ConsoleColor.Blue) != 94) return false;
        string white = WindowsTerminalTheme.BackgroundSequence(0xFFFFFF);
        string dark = WindowsTerminalTheme.BackgroundSequence(0x101828);
        if (!white.Contains("\u001b]11;#FFFFFF\u0007") || !white.Contains("\u001b]10;#000000\u0007") ||
            !dark.Contains("\u001b]11;#101828\u0007") || !dark.Contains("\u001b]10;#FFFFFF\u0007")) return false;
        foreach (string legacy in new[] { "ContentArea", "1", "WindowsTerminal", "2" })
        {
            var parsed = ApplicationSettings.ParseProfile(new() { ["backgroundMode"] = legacy }, new AppProfile());
            if (parsed.BackgroundMode != BackgroundColorMode.WindowsTerminal) return false;
        }
        return !Enum.GetNames<BackgroundColorMode>().Contains("ContentArea");
    }
    public static bool TerminalPropertyEditingSelfTest()
    {
        foreach (string source in new[] { "{}", "{/* hi */}", """{"background":"red"}""",
            """{"background":"red", "name":"x"}""", """{"name":"x", "background":"red"}""" })
        {
            string added = WindowsTerminalTheme.EditProperty(source, "background", JsonSerializer.Serialize("#FFFFFF"));
            using var parsed = JsonDocument.Parse(added, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (parsed.RootElement.GetProperty("background").GetString() != "#FFFFFF") return false;
            string removed = WindowsTerminalTheme.EditProperty(added, "background", null!);
            using var restored = JsonDocument.Parse(removed, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (restored.RootElement.TryGetProperty("background", out _)) return false;
        }
        return true;
    }

    public static bool ProfileEnumerationAndCleanupSelfTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tcptunnel-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllLines(Path.Combine(directory, "old.cfg"), ApplicationSettings.Serialize(new AppProfile { Nickname = "Older", SnakeColor = ConsoleColor.Red }));
            File.WriteAllLines(Path.Combine(directory, "recent.cfg"), ApplicationSettings.Serialize(new AppProfile { Nickname = "Newest", SnakeColor = ConsoleColor.Blue }));
            File.SetLastWriteTimeUtc(Path.Combine(directory, "old.cfg"), DateTime.UtcNow.AddDays(-1));
            File.WriteAllText(Path.Combine(directory, "preferences.cfg"), "alwaysImport=true");
            File.WriteAllText(Path.Combine(directory, "broken.cfg"), "nickname=invalid!");
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "keep");
            Directory.CreateDirectory(Path.Combine(directory, "nested"));
            File.WriteAllText(Path.Combine(directory, "nested", "keep.cfg"), "keep");
            var profiles = ApplicationSettings.GetSavedProfiles(directory);
            if (profiles.Count != 2 || profiles[0].Nickname != "Newest" || profiles[0].SnakeColor != ConsoleColor.Blue) return false;
            string keep = Path.Combine(directory, "recent.cfg");
            ApplicationSettings.ClearProfileFiles(directory, keep);
            if (!File.Exists(keep) || Directory.GetFiles(directory, "*.cfg").Length != 1) return false;
            ApplicationSettings.ClearProfileFiles(directory);
            return Directory.GetFiles(directory, "*.cfg").Length == 0 &&
                File.Exists(Path.Combine(directory, "keep.txt")) && File.Exists(Path.Combine(directory, "nested", "keep.cfg"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
