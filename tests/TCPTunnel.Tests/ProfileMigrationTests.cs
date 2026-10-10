using TCPTunnel;

internal static class ProfileMigrationTests
{
    public static bool ProfileMigrationSelfTest()
    {
        string[] old = { "# keep comment", "graphics=false", "unknown=value", "snakeDelay=125" };
        string[] defaults = { "graphics=true", "snakeDelay=75", "windowWidth=0", "windowHeight=0" };
        string[] migrated = ApplicationSettings.MergeProfileLines(old, defaults, true);
        return migrated.Take(old.Length).SequenceEqual(old) && migrated.Length == 6 &&
            ApplicationSettings.MergeProfileLines(migrated, defaults, true).SequenceEqual(migrated) &&
            ApplicationSettings.MergeProfileLines(migrated, new[] { "graphics=true" }, false)
                .SequenceEqual(new[] { "# keep comment", "graphics=true", "unknown=value", "snakeDelay=125", "windowWidth=0", "windowHeight=0" });
    }

    public static bool ProfileWindowRoundtripSelfTest()
    {
        var profile = new AppProfile { WindowWidth = 1024, WindowHeight = 768, WindowMaximized = true };
        var decoded = ApplicationSettings.ParseProfile(ApplicationSettings.ParseLines(ApplicationSettings.Serialize(profile)), new AppProfile());
        var invalid = ApplicationSettings.ParseProfile(new() { ["windowWidth"] = "-1", ["windowHeight"] = "2147483647" }, new AppProfile());
        return decoded.WindowWidth == 1024 && decoded.WindowHeight == 768 && decoded.WindowMaximized &&
            invalid.WindowWidth == 0 && invalid.WindowHeight == 0;
    }
}
