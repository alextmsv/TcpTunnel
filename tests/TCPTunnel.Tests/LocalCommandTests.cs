using TCPTunnel;

internal static class LocalCommandTests
{
    public static bool LocalLanguageDispatchSelfTest()
    {
        int changes = 0, errors = 0; AppLanguage language = AppLanguage.English;
        void Change(AppLanguage value) { changes++; language = value; }
        void Error(string _) => errors++;
        if (!LocalCommands.Handle("\\lang ru", Change, Error) || changes != 1 || language != AppLanguage.Russian) return false;
        if (!LocalCommands.Handle("\\LANG EN", Change, Error) || changes != 2 || language != AppLanguage.English) return false;
        foreach (string invalid in new[] { "\\lang", "\\lang xx", "\\lang ru extra", "\\unknown", "\\" })
            if (!LocalCommands.Handle(invalid, Change, Error)) return false;
        foreach (string nonCommand in new[] { "text", "/lang ru", "\\\\server\\share\\image.png" })
            if (LocalCommands.Handle(nonCommand, Change, Error)) return false;
        string directory = Path.Combine(Environment.CurrentDirectory, "obj", "local-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "image.png");
        try
        {
            File.WriteAllText(path, "fixture");
            string rooted = path.Substring(Path.GetPathRoot(path)!.Length - 1);
            if (LocalCommands.Handle(rooted, Change, Error)) return false;
        }
        finally { File.Delete(path); Directory.Delete(directory); }
        return changes == 2 && errors == 5;
    }

    public static bool DialogueProfileCompatibilitySelfTest()
    {
        var defaults = new AppProfile { InputPinnedBottom = true, MatrixText = true };
        var legacy = ApplicationSettings.ParseProfile(new(), defaults);
        if (legacy.InputPinnedBottom || legacy.MatrixText) return false;
        var profile = new AppProfile { Nickname = "tester", IdentityId = Guid.NewGuid(), IdentityFingerprint = new('A', 64), InputPinnedBottom = true, MatrixText = true };
        var restored = ApplicationSettings.ParseProfile(ApplicationSettings.ParseLines(ApplicationSettings.Serialize(profile)), new());
        return restored.InputPinnedBottom && restored.MatrixText && restored.IdentityId == profile.IdentityId && restored.IdentityFingerprint == profile.IdentityFingerprint;
    }
}
