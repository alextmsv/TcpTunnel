using TCPTunnel;

internal static class MenuAppearanceTests
{
    public static bool MenuAlignmentAndBorderSelfTest()
    {
        var position = typeof(ConsoleGraphic).GetMethod("TryGetGraphicalMenuPosition",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var geometry = new ConsoleGraphic.ConsoleGeometry
        {
            WindowWidth = 71, BufferWidth = 71, WindowHeight = 16, BufferHeight = 16
        };
        for (int index = 0; index < 6; index++)
        {
            object[] args = { geometry, index, 5, 8, 0, 0, 0 };
            if (position.Invoke(null, args) is not true || (int)args[4] != 5 ||
                (int)args[5] != 8 + index || (int)args[6] != 70) return false;
        }
        object[] border = { geometry, 0, 5, 15, 0, 0, 0 };
        return position.Invoke(null, border) is false;
    }

    public static bool MenuSelectionProfileSelfTest()
    {
        foreach (string style in new[] { "Fill", "Arrow", "Brackets" })
        {
            var profile = ApplicationSettings.ParseProfile(new() { ["selectionStyle"] = style }, new AppProfile());
            var saved = ApplicationSettings.ParseLines(ApplicationSettings.Serialize(profile));
            if (!saved.TryGetValue("selectionStyle", out string? actual) || actual != style) return false;
            var restored = ApplicationSettings.ParseProfile(saved, new AppProfile());
            if (!ApplicationSettings.Serialize(restored).Contains("selectionStyle=" + style)) return false;
        }
        var fallback = ApplicationSettings.ParseProfile(new() { ["selectionStyle"] = "Arrow" }, new AppProfile());
        foreach (string? invalid in new string?[] { null, "999", "unknown", "-1" })
        {
            var values = new Dictionary<string, string>();
            if (invalid != null) values["selectionStyle"] = invalid;
            var profile = ApplicationSettings.ParseProfile(values, fallback);
            if (!ApplicationSettings.Serialize(profile).Contains("selectionStyle=Arrow") ||
                profile.SnakeGlyph != fallback.SnakeGlyph || profile.MenuTextColor != fallback.MenuTextColor) return false;
        }
        return true;
    }
}
