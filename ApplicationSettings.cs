using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace TCPTunnel
{
    internal sealed class AppProfile
    {
        public string Nickname = String.Empty;
        public string LastHost = "localhost";
        public int LastPort = 9091;
        public bool GraphicsEnabled = true;
        public AppLanguage Language = Lang.Current;
        public int SnakeDelay = 75;
        public ConsoleColor SnakeColor = ConsoleColor.Green;
        public char SnakeGlyph = '-';
        public ConsoleColor BorderColor = ConsoleColor.Magenta;
        public ConsoleColor CornerColor = ConsoleColor.Blue;
        public ConsoleColor SelectionColor = ConsoleColor.Cyan;
        public bool SelectionCustomColorEnabled;
        public int SelectionCustomRgb = 0x2E7DE0;
        public BackgroundColorMode BackgroundMode = BackgroundColorMode.Off;
        public int BackgroundCustomRgb = 0x101828;
        public MenuSelectionStyle SelectionStyle = MenuSelectionStyle.Fill;
        public ConsoleColor MenuTextColor = ConsoleColor.White;
        public ConsoleColor IncomingColor = ConsoleColor.Green;
        public ConsoleColor IncomingTextColor = ConsoleColor.White;
        public ConsoleColor OutgoingColor = ConsoleColor.Cyan;
        public ConsoleColor OutgoingTextColor = ConsoleColor.White;
        public ConsoleColor InputColor = ConsoleColor.Cyan;
        public ConsoleColor InputTextColor = ConsoleColor.White;
        public ConsoleColor SystemColor = ConsoleColor.DarkGray;
        public int WindowWidth;
        public int WindowHeight;
        public bool WindowMaximized;
    }

    internal static class ApplicationSettings
    {
        private const string DefaultResourceName = "TCPTunnel.default.cfg";
        private const string PreferencesFileName = "preferences.cfg";
        private static readonly object sync = new object();
        private static readonly ConcurrentDictionary<string, object> fileWriteLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static string lastProfileNickname = String.Empty;
        private static bool alwaysImport;

        public static AppProfile Current { get; private set; } = new AppProfile();
        public static string PendingProfileNickname { get; private set; }
        internal static bool ProfileWasAutoLoaded { get; private set; }
        internal sealed record SavedProfile(string Path, string Nickname, ConsoleColor SnakeColor, DateTime ModifiedUtc);

        internal static List<SavedProfile> GetSavedProfiles(string directory = null)
        {
            directory ??= GetStorageDirectory();
            var profiles = new List<SavedProfile>();
            if (!Directory.Exists(directory)) return profiles;
            try
            {
                foreach (string path in Directory.GetFiles(directory, "*.cfg"))
                {
                    if (String.Equals(Path.GetFileName(path), PreferencesFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        AppProfile profile = ParseProfile(ReadFile(path), LoadEmbeddedDefaults());
                        if (NetWorker.IsNicknameValid(profile.Nickname))
                            profiles.Add(new SavedProfile(Path.GetFullPath(path), profile.Nickname, profile.SnakeColor, File.GetLastWriteTimeUtc(path)));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            profiles.Sort((a, b) => {
                int order = b.ModifiedUtc.CompareTo(a.ModifiedUtc);
                return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
            });
            return profiles;
        }

        internal static bool SelectSavedProfile(SavedProfile selected, bool remember, bool clear, out string error)
        {
            lock (sync)
            {
                error = null;
                try
                {
                    AppProfile profile = LoadEmbeddedDefaults();
                    string[] selectedLines = Array.Empty<string>();
                    if (selected != null)
                    {
                        string path = Path.GetFullPath(selected.Path);
                        if (!String.Equals(Path.GetDirectoryName(path), Path.GetFullPath(GetStorageDirectory()), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Invalid profile directory.");
                        selectedLines = File.ReadAllLines(path, Utf8);
                        profile = ParseProfile(ParseLines(selectedLines), profile);
                        if (!NetWorker.IsNicknameValid(profile.Nickname)) throw new IOException("Invalid profile nickname.");
                    }
                    string savedPath = selected == null ? null : GetProfilePath(profile.Nickname);
                    Directory.CreateDirectory(GetStorageDirectory());
                    if (savedPath != null)
                        WriteFileAtomic(savedPath, MergeProfileLines(selectedLines, Serialize(profile), false));
                    if (clear) ClearProfileFiles(GetStorageDirectory(), savedPath);
                    Current = profile;
                    alwaysImport = remember;
                    lastProfileNickname = selected == null ? String.Empty : profile.Nickname;
                    PendingProfileNickname = null;
                    NetWorker.nickname = profile.Nickname;
                    ApplyCurrent();
                    WriteFileAtomic(GetPreferencesPath(), new[]
                    {
                        "lastProfile=" + EncodeText(lastProfileNickname),
                        "alwaysImport=" + alwaysImport
                    });
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { error = ex.Message; return false; }
            }
        }

        internal static void ClearProfileFiles(string directory, string keepPath = null)
        {
            string root = Path.GetFullPath(directory);
            if (!Directory.Exists(root)) return;
            foreach (string file in Directory.GetFiles(root, "*.cfg", SearchOption.TopDirectoryOnly))
            {
                string full = Path.GetFullPath(file);
                if (!String.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid profile directory.");
                if (keepPath != null && String.Equals(full, Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(full);
            }
        }
        public static string LastHost => String.IsNullOrWhiteSpace(Current.LastHost) ? "localhost" : Current.LastHost;
        public static int LastPort => Current.LastPort >= 1 && Current.LastPort <= 65535 ? Current.LastPort : 9091;

        public static void Initialize()
        {
            lock (sync)
            {
                Current = LoadEmbeddedDefaults();
                ProfileWasAutoLoaded = false;
                PendingProfileNickname = null;
                Dictionary<string, string> preferences = ReadFile(GetPreferencesPath());
                string encodedNickname;
                if (preferences.TryGetValue("lastProfile", out encodedNickname))
                    lastProfileNickname = DecodeText(encodedNickname) ?? String.Empty;
                bool parsedAlways;
                alwaysImport = preferences.TryGetValue("alwaysImport", out encodedNickname) &&
                               Boolean.TryParse(encodedNickname, out parsedAlways) && parsedAlways;

                if (NetWorker.IsNicknameValid(lastProfileNickname) && ProfileExists(lastProfileNickname))
                {
                    if (alwaysImport)
                    {
                        Current = LoadProfile(lastProfileNickname, Current);
                        ProfileWasAutoLoaded = NetWorker.IsNicknameValid(Current.Nickname);
                    }
                    else
                        PendingProfileNickname = lastProfileNickname;
                }
                if (!ProfileWasAutoLoaded && PendingProfileNickname == null)
                {
                    var profiles = GetSavedProfiles();
                    if (profiles.Count > 0) PendingProfileNickname = profiles[0].Nickname;
                }
                ApplyCurrent();
            }
        }

        public static bool ImportPendingProfile(bool remember)
        {
            lock (sync)
            {
                if (!NetWorker.IsNicknameValid(PendingProfileNickname) || !ProfileExists(PendingProfileNickname))
                    return false;

                Current = LoadProfile(PendingProfileNickname, Current);
                alwaysImport = remember;
                lastProfileNickname = Current.Nickname;
                PendingProfileNickname = null;
                ApplyCurrent();
                SavePreferences();
                return true;
            }
        }

        public static void DismissPendingProfile()
        {
            lock (sync)
                PendingProfileNickname = null;
        }

        public static void RememberEndpoint(string host, int port)
        {
            lock (sync)
            {
                if (!String.IsNullOrWhiteSpace(host))
                    Current.LastHost = host.Trim();
                if (port >= 1 && port <= 65535)
                    Current.LastPort = port;
                SaveCurrentProfileLocked(NetWorker.nickname);
            }
        }

        public static void SaveCurrentProfile(string nickname)
        {
            lock (sync)
                SaveCurrentProfileLocked(nickname);
        }

        public static void ResetCustomizations()
        {
            lock (sync)
            {
                AppProfile defaults = LoadEmbeddedDefaults();
                Current.GraphicsEnabled = defaults.GraphicsEnabled;
                Current.SnakeDelay = defaults.SnakeDelay;
                Current.SnakeColor = defaults.SnakeColor;
                Current.SnakeGlyph = defaults.SnakeGlyph;
                Current.BorderColor = defaults.BorderColor;
                Current.CornerColor = defaults.CornerColor;
                Current.SelectionColor = defaults.SelectionColor;
                Current.SelectionCustomColorEnabled = defaults.SelectionCustomColorEnabled;
                Current.SelectionCustomRgb = defaults.SelectionCustomRgb;
                if (Current.BackgroundMode == BackgroundColorMode.WindowsTerminal)
                    WindowsTerminalTheme.TryClearBackground(out _);
                Current.BackgroundMode = defaults.BackgroundMode;
                Current.BackgroundCustomRgb = defaults.BackgroundCustomRgb;
                Current.SelectionStyle = defaults.SelectionStyle;
                Current.MenuTextColor = defaults.MenuTextColor;
                Current.IncomingColor = defaults.IncomingColor;
                Current.IncomingTextColor = defaults.IncomingTextColor;
                Current.OutgoingColor = defaults.OutgoingColor;
                Current.OutgoingTextColor = defaults.OutgoingTextColor;
                Current.InputColor = defaults.InputColor;
                Current.InputTextColor = defaults.InputTextColor;
                Current.SystemColor = defaults.SystemColor;
                ApplyCurrent();
                SaveCurrentProfileLocked(NetWorker.nickname);
            }
        }

        public static void CaptureAndSave()
        {
            lock (sync)
                SaveCurrentProfileLocked(NetWorker.nickname);
        }

        public static bool RunSelfTest()
        {
            AppProfile profile = ParseProfile(new Dictionary<string, string>
            {
                { "nickname", EncodeText("Тестер") },
                { "lastHost", EncodeText("127.0.0.1") },
                { "lastPort", "9091" },
                { "graphics", "false" },
                { "snakeDelay", "125" },
                { "snakeColor", ((int)ConsoleColor.Cyan).ToString(CultureInfo.InvariantCulture) },
                { "snakeGlyph", EncodeText("~") },
                { "borderColor", ((int)ConsoleColor.Black).ToString(CultureInfo.InvariantCulture) },
                { "menuTextColor", ((int)ConsoleColor.Yellow).ToString(CultureInfo.InvariantCulture) }
            }, new AppProfile());
            profile.Language = AppLanguage.English;
            profile.CornerColor = ConsoleColor.DarkBlue;
            profile.SelectionColor = ConsoleColor.White;
            profile.SelectionCustomColorEnabled = true;
            profile.SelectionCustomRgb = 0x2E7DE0;
            profile.BackgroundMode = BackgroundColorMode.WindowsTerminal;
            profile.BackgroundCustomRgb = 0x101828;
            profile.IncomingColor = ConsoleColor.Green;
            profile.IncomingTextColor = ConsoleColor.DarkGreen;
            profile.OutgoingColor = ConsoleColor.Cyan;
            profile.OutgoingTextColor = ConsoleColor.DarkCyan;
            profile.InputColor = ConsoleColor.Blue;
            profile.InputTextColor = ConsoleColor.Gray;
            profile.SystemColor = ConsoleColor.DarkYellow;

            AppProfile roundTrip = ParseProfile(
                ParseLines(Serialize(profile)),
                new AppProfile());
            bool roundTripIsValid =
                roundTrip.Nickname == profile.Nickname &&
                roundTrip.LastHost == profile.LastHost &&
                roundTrip.LastPort == profile.LastPort &&
                roundTrip.GraphicsEnabled == profile.GraphicsEnabled &&
                roundTrip.Language == profile.Language &&
                roundTrip.SnakeDelay == profile.SnakeDelay &&
                roundTrip.SnakeColor == profile.SnakeColor &&
                roundTrip.SnakeGlyph == profile.SnakeGlyph &&
                roundTrip.BorderColor == profile.BorderColor &&
                roundTrip.CornerColor == profile.CornerColor &&
                roundTrip.SelectionColor == profile.SelectionColor &&
                roundTrip.SelectionCustomColorEnabled == profile.SelectionCustomColorEnabled &&
                roundTrip.SelectionCustomRgb == profile.SelectionCustomRgb &&
                roundTrip.BackgroundMode == profile.BackgroundMode &&
                roundTrip.BackgroundCustomRgb == profile.BackgroundCustomRgb &&
                roundTrip.MenuTextColor == profile.MenuTextColor &&
                roundTrip.IncomingColor == profile.IncomingColor &&
                roundTrip.IncomingTextColor == profile.IncomingTextColor &&
                roundTrip.OutgoingColor == profile.OutgoingColor &&
                roundTrip.OutgoingTextColor == profile.OutgoingTextColor &&
                roundTrip.InputColor == profile.InputColor &&
                roundTrip.InputTextColor == profile.InputTextColor &&
                roundTrip.SystemColor == profile.SystemColor;

            AppProfile partial = ParseProfile(new Dictionary<string, string>
            {
                { "borderColor", ((int)ConsoleColor.Yellow).ToString(CultureInfo.InvariantCulture) }
            }, roundTrip);
            bool partialImportIsValid =
                partial.BorderColor == ConsoleColor.Yellow &&
                partial.CornerColor == roundTrip.CornerColor &&
                partial.MenuTextColor == roundTrip.MenuTextColor &&
                partial.SnakeColor == roundTrip.SnakeColor;

            AppProfile savedCurrent = Current;
            string savedNickname = NetWorker.nickname;
            int revisionBeforeApply = ConsoleGraphic.VisualThemeRevision;
            bool applyIsValid;
            try
            {
                Current = Clone(roundTrip);
                ApplyCurrent();
                applyIsValid =
                    ConsoleGraphic.Enabled == roundTrip.GraphicsEnabled &&
                    Lang.Current == roundTrip.Language &&
                    ConsoleGraphic.BorderAnimationDelayMilliseconds == roundTrip.SnakeDelay &&
                    ConsoleGraphic.BorderSnakeColor == roundTrip.SnakeColor &&
                    ConsoleGraphic.BorderSnakeGlyph == roundTrip.SnakeGlyph &&
                    NetWorker.nickname == roundTrip.Nickname &&
                    ConsoleTheme.Border == roundTrip.BorderColor &&
                    ConsoleTheme.Corners == roundTrip.CornerColor &&
                    ConsoleTheme.SelectionBackground == roundTrip.SelectionColor &&
                    ConsoleTheme.SelectionUsesCustomColor == roundTrip.SelectionCustomColorEnabled &&
                    ConsoleTheme.SelectionCustomRgb == roundTrip.SelectionCustomRgb &&
                    ConsoleTheme.BackgroundMode == roundTrip.BackgroundMode &&
                    ConsoleTheme.BackgroundCustomRgb == roundTrip.BackgroundCustomRgb &&
                    ConsoleTheme.MenuText == roundTrip.MenuTextColor &&
                    ConsoleTheme.IncomingMarker == roundTrip.IncomingColor &&
                    ConsoleTheme.IncomingText == roundTrip.IncomingTextColor &&
                    ConsoleTheme.OutgoingMarker == roundTrip.OutgoingColor &&
                    ConsoleTheme.OutgoingText == roundTrip.OutgoingTextColor &&
                    ConsoleTheme.InputPrompt == roundTrip.InputColor &&
                    ConsoleTheme.InputText == roundTrip.InputTextColor &&
                    ConsoleTheme.SystemText == roundTrip.SystemColor &&
                    ConsoleGraphic.VisualThemeRevision > revisionBeforeApply;
            }
            finally
            {
                Current = savedCurrent;
                ApplyCurrent();
                NetWorker.nickname = savedNickname;
            }

            return roundTripIsValid && partialImportIsValid && applyIsValid;
        }

        private static void SaveCurrentProfileLocked(string nickname)
        {
            if (!NetWorker.IsNicknameValid(nickname))
                return;

            CaptureCurrent(nickname);
            try
            {
                Directory.CreateDirectory(GetStorageDirectory());
                string path = GetProfilePath(nickname);
                string[] existing = File.Exists(path) ? File.ReadAllLines(path, Utf8) : Array.Empty<string>();
                WriteFileAtomic(path, MergeProfileLines(existing, Serialize(Current), false));
                lastProfileNickname = nickname;
                SavePreferences();
            }
            catch
            {
                // Settings persistence must never interrupt a chat session.
            }
        }

        private static void CaptureCurrent(string nickname)
        {
            Current.Nickname = nickname;
            Current.GraphicsEnabled = ConsoleGraphic.Enabled;
            Current.Language = Lang.Current;
            Current.SnakeDelay = ConsoleGraphic.BorderAnimationDelayMilliseconds;
            Current.SnakeColor = ConsoleGraphic.BorderSnakeColor;
            Current.SnakeGlyph = ConsoleGraphic.BorderSnakeGlyph;
            Current.BorderColor = ConsoleTheme.Border;
            Current.CornerColor = ConsoleTheme.Corners;
            Current.SelectionColor = ConsoleTheme.SelectionBackground;
            Current.SelectionCustomColorEnabled = ConsoleTheme.SelectionUsesCustomColor;
            Current.SelectionCustomRgb = ConsoleTheme.SelectionCustomRgb;
            Current.BackgroundMode = ConsoleTheme.BackgroundMode;
            Current.BackgroundCustomRgb = ConsoleTheme.BackgroundCustomRgb;
            Current.SelectionStyle = ConsoleTheme.SelectionStyle;
            Current.MenuTextColor = ConsoleTheme.MenuText;
            Current.IncomingColor = ConsoleTheme.IncomingMarker;
            Current.IncomingTextColor = ConsoleTheme.IncomingText;
            Current.OutgoingColor = ConsoleTheme.OutgoingMarker;
            Current.OutgoingTextColor = ConsoleTheme.OutgoingText;
            Current.InputColor = ConsoleTheme.InputPrompt;
            Current.InputTextColor = ConsoleTheme.InputText;
            Current.SystemColor = ConsoleTheme.SystemText;
        }

        public static void ApplyCurrent()
        {
            ConsoleGraphic.Enabled = Current.GraphicsEnabled;
            Lang.Set(Current.Language);
            ConsoleGraphic.BorderAnimationDelayMilliseconds = Current.SnakeDelay;
            ConsoleGraphic.BorderSnakeColor = Current.SnakeColor;
            ConsoleGraphic.BorderSnakeGlyph = Current.SnakeGlyph;
            ConsoleTheme.Border = Current.BorderColor;
            ConsoleTheme.Corners = Current.CornerColor;
            ConsoleTheme.SelectionBackground = Current.SelectionColor;
            if (Current.SelectionCustomColorEnabled)
                ConsoleTheme.SetCustomSelectionColor(Current.SelectionCustomRgb);
            else
                ConsoleTheme.ClearCustomSelectionColor();
            ConsoleTheme.BackgroundMode = Current.BackgroundMode;
            ConsoleTheme.BackgroundCustomRgb = Current.BackgroundCustomRgb;
            ConsoleTheme.SelectionStyle = Current.SelectionStyle;
            ConsoleTheme.MenuText = Current.MenuTextColor;
            ConsoleTheme.IncomingMarker = Current.IncomingColor;
            ConsoleTheme.IncomingText = Current.IncomingTextColor;
            ConsoleTheme.OutgoingMarker = Current.OutgoingColor;
            ConsoleTheme.OutgoingText = Current.OutgoingTextColor;
            ConsoleTheme.InputPrompt = Current.InputColor;
            ConsoleTheme.InputText = Current.InputTextColor;
            ConsoleTheme.SystemText = Current.SystemColor;
            if (NetWorker.IsNicknameValid(Current.Nickname))
                NetWorker.nickname = Current.Nickname;
            ConsoleGraphic.InvalidateVisualTheme();
        }

        private static AppProfile LoadEmbeddedDefaults()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultResourceName))
                using (var reader = stream == null ? null : new StreamReader(stream, Utf8, true))
                {
                    if (reader != null)
                        return ParseProfile(ParseLines(reader.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)), new AppProfile());
                }
            }
            catch
            {
            }
            return new AppProfile();
        }

        private static AppProfile LoadProfile(string nickname, AppProfile fallback)
        {
            string path = GetProfilePath(nickname);
            try
            {
                string[] lines = File.ReadAllLines(path, Utf8);
                AppProfile profile = ParseProfile(ParseLines(lines), fallback);
                string[] migrated = MergeProfileLines(lines, Serialize(profile), true);
                if (migrated.Length != lines.Length)
                {
                    try { WriteFileAtomic(path, migrated); } catch { }
                }
                return profile;
            }
            catch { return Clone(fallback); }
        }

        internal static void RememberWindow(WindowSize size)
        {
            lock (sync)
            {
                if (PendingProfileNickname != null || !NetWorker.IsNicknameValid(NetWorker.nickname) ||
                    size.Width <= 0 || size.Height <= 0 ||
                    (Current.WindowWidth == size.Width && Current.WindowHeight == size.Height && Current.WindowMaximized == size.Maximized)) return;
                Current.WindowWidth = size.Width;
                Current.WindowHeight = size.Height;
                Current.WindowMaximized = size.Maximized;
                SaveCurrentProfileLocked(NetWorker.nickname);
            }
        }

        internal static AppProfile ParseProfile(Dictionary<string, string> values, AppProfile fallback)
        {
            var profile = Clone(fallback);
            string value;
            int number;
            bool flag;
            if (values.TryGetValue("nickname", out value)) profile.Nickname = DecodeText(value) ?? profile.Nickname;
            if (values.TryGetValue("lastHost", out value)) profile.LastHost = DecodeText(value) ?? profile.LastHost;
            if (values.TryGetValue("lastPort", out value) && Int32.TryParse(value, out number) && number >= 1 && number <= 65535) profile.LastPort = number;
            if (values.TryGetValue("graphics", out value) && Boolean.TryParse(value, out flag)) profile.GraphicsEnabled = flag;
            if (values.TryGetValue("windowWidth", out value) && Int32.TryParse(value, out number) && number >= 0 && number <= 32768) profile.WindowWidth = number;
            if (values.TryGetValue("windowHeight", out value) && Int32.TryParse(value, out number) && number >= 0 && number <= 32768) profile.WindowHeight = number;
            if (values.TryGetValue("windowMaximized", out value) && Boolean.TryParse(value, out flag)) profile.WindowMaximized = flag;
            if (values.TryGetValue("language", out value))
            {
                if (value == "en") profile.Language = AppLanguage.English;
                else if (value == "ru") profile.Language = AppLanguage.Russian;
            }
            if (values.TryGetValue("snakeDelay", out value) && Int32.TryParse(value, out number)) profile.SnakeDelay = Math.Max(20, Math.Min(1000, number));
            if (values.TryGetValue("snakeColor", out value) && Int32.TryParse(value, out number)) profile.SnakeColor = ConsoleTheme.ValidateColor(number, profile.SnakeColor);
            if (values.TryGetValue("snakeGlyph", out value))
            {
                string glyph = DecodeText(value);
                if (ConsoleGraphic.IsValidSnakeGlyph(glyph)) profile.SnakeGlyph = glyph[0];
            }
            profile.BorderColor = ReadColor(values, "borderColor", profile.BorderColor, true);
            profile.CornerColor = ReadColor(values, "cornerColor", profile.CornerColor);
            profile.SelectionColor = ReadColor(values, "selectionColor", profile.SelectionColor);
            if (values.TryGetValue("selectionCustomColorEnabled", out value) && Boolean.TryParse(value, out flag))
                profile.SelectionCustomColorEnabled = flag;
            if (values.TryGetValue("selectionCustomRgb", out value) &&
                Int32.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number) &&
                number >= 0 && number <= 0xFFFFFF)
                profile.SelectionCustomRgb = number;
            if (values.TryGetValue("selectionStyle", out value) &&
                Enum.TryParse(value, true, out MenuSelectionStyle style) && Enum.IsDefined(typeof(MenuSelectionStyle), style))
                profile.SelectionStyle = style;
            if (values.TryGetValue("backgroundMode", out var legacyBackground) && (legacyBackground == "ContentArea" || legacyBackground == "1"))
                profile.BackgroundMode = BackgroundColorMode.WindowsTerminal;
            if (values.TryGetValue("backgroundMode", out value) &&
                Enum.TryParse(value, true, out BackgroundColorMode backgroundMode) && Enum.IsDefined(typeof(BackgroundColorMode), backgroundMode))
                profile.BackgroundMode = backgroundMode;
            if (values.TryGetValue("backgroundCustomRgb", out value) &&
                Int32.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number) &&
                number >= 0 && number <= 0xFFFFFF)
                profile.BackgroundCustomRgb = number;
            profile.MenuTextColor = ReadColor(values, "menuTextColor", profile.MenuTextColor);
            profile.IncomingColor = ReadColor(values, "incomingColor", profile.IncomingColor);
            profile.IncomingTextColor = ReadColor(values, "incomingTextColor", profile.IncomingTextColor);
            profile.OutgoingColor = ReadColor(values, "outgoingColor", profile.OutgoingColor);
            profile.OutgoingTextColor = ReadColor(values, "outgoingTextColor", profile.OutgoingTextColor);
            profile.InputColor = ReadColor(values, "inputColor", profile.InputColor);
            profile.InputTextColor = ReadColor(values, "inputTextColor", profile.InputTextColor);
            profile.SystemColor = ReadColor(values, "systemColor", profile.SystemColor);
            return profile;
        }

        private static ConsoleColor ReadColor(
            Dictionary<string, string> values,
            string key,
            ConsoleColor fallback,
            bool allowBlack = false)
        {
            string value;
            int number;
            return values.TryGetValue(key, out value) && Int32.TryParse(value, out number)
                ? ConsoleTheme.ValidateColor(number, fallback, allowBlack)
                : fallback;
        }

        internal static string[] Serialize(AppProfile profile)
        {
            return new[]
            {
                "version=1", "nickname=" + EncodeText(profile.Nickname), "lastHost=" + EncodeText(profile.LastHost),
                "lastPort=" + profile.LastPort, "graphics=" + profile.GraphicsEnabled,
                "language=" + (profile.Language == AppLanguage.English ? "en" : "ru"),
                "snakeDelay=" + profile.SnakeDelay, "snakeColor=" + (int)profile.SnakeColor,
                "snakeGlyph=" + EncodeText(profile.SnakeGlyph.ToString()), "borderColor=" + (int)profile.BorderColor,
                "cornerColor=" + (int)profile.CornerColor, "selectionColor=" + (int)profile.SelectionColor,
                "selectionCustomColorEnabled=" + profile.SelectionCustomColorEnabled,
                "selectionCustomRgb=" + profile.SelectionCustomRgb.ToString("X6", CultureInfo.InvariantCulture),
                "backgroundMode=" + profile.BackgroundMode,
                "backgroundCustomRgb=" + profile.BackgroundCustomRgb.ToString("X6", CultureInfo.InvariantCulture),
                "selectionStyle=" + profile.SelectionStyle,
                "menuTextColor=" + (int)profile.MenuTextColor,
                "incomingColor=" + (int)profile.IncomingColor, "incomingTextColor=" + (int)profile.IncomingTextColor,
                "outgoingColor=" + (int)profile.OutgoingColor, "outgoingTextColor=" + (int)profile.OutgoingTextColor,
                "inputColor=" + (int)profile.InputColor, "inputTextColor=" + (int)profile.InputTextColor,
                "systemColor=" + (int)profile.SystemColor,
                "windowWidth=" + profile.WindowWidth, "windowHeight=" + profile.WindowHeight,
                "windowMaximized=" + profile.WindowMaximized
            };
        }

        private static AppProfile Clone(AppProfile value)
        {
            return new AppProfile
            {
                Nickname = value.Nickname, LastHost = value.LastHost, LastPort = value.LastPort,
                GraphicsEnabled = value.GraphicsEnabled, Language = value.Language,
                SnakeDelay = value.SnakeDelay, SnakeColor = value.SnakeColor, SnakeGlyph = value.SnakeGlyph,
                BorderColor = value.BorderColor, CornerColor = value.CornerColor,
                SelectionColor = value.SelectionColor, MenuTextColor = value.MenuTextColor,
                SelectionCustomColorEnabled = value.SelectionCustomColorEnabled,
                SelectionCustomRgb = value.SelectionCustomRgb,
                BackgroundMode = value.BackgroundMode,
                BackgroundCustomRgb = value.BackgroundCustomRgb,
                SelectionStyle = value.SelectionStyle,
                IncomingColor = value.IncomingColor,
                IncomingTextColor = value.IncomingTextColor,
                OutgoingColor = value.OutgoingColor, OutgoingTextColor = value.OutgoingTextColor,
                InputColor = value.InputColor, InputTextColor = value.InputTextColor,
                SystemColor = value.SystemColor,
                WindowWidth = value.WindowWidth, WindowHeight = value.WindowHeight,
                WindowMaximized = value.WindowMaximized
            };
        }

        private static Dictionary<string, string> ReadFile(string path)
        {
            try { return File.Exists(path) ? ParseLines(File.ReadAllLines(path, Utf8)) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
            catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }

        internal static Dictionary<string, string> ParseLines(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                string line = (raw ?? String.Empty).Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int separator = line.IndexOf('=');
                if (separator > 0) result[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }
            return result;
        }

        internal static string[] MergeProfileLines(string[] existing, string[] updates, bool onlyMissing)
        {
            var values = ParseLines(updates);
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>(existing.Length + updates.Length);
            foreach (string raw in existing)
            {
                string line = (raw ?? String.Empty).Trim();
                int equals = line.IndexOf('=');
                string key = equals > 0 && line[0] != '#' && line[0] != ';' ? line.Substring(0, equals).Trim() : null;
                if (key != null) present.Add(key);
                result.Add(!onlyMissing && key != null && values.TryGetValue(key, out string value) ? key + "=" + value : raw);
            }
            foreach (var value in values)
                if (!present.Contains(value.Key)) result.Add(value.Key + "=" + value.Value);
            return result.ToArray();
        }

        private static void WriteFileAtomic(string path, IEnumerable<string> lines)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            string fullPath = Path.GetFullPath(path);
            object pathLock = fileWriteLocks.GetOrAdd(fullPath, _ => new object());
            lock (pathLock)
            {
                string directory = Path.GetDirectoryName(fullPath);
                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllLines(temporary, lines, Utf8);
                    if (!File.Exists(fullPath))
                    {
                        File.Move(temporary, fullPath);
                        return;
                    }
                    byte[] current = File.ReadAllBytes(fullPath);
                    byte[] next = File.ReadAllBytes(temporary);
                    if (current.AsSpan().SequenceEqual(next))
                        return;
                    File.Replace(temporary, fullPath, null);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        private static void SavePreferences()
        {
            try
            {
                Directory.CreateDirectory(GetStorageDirectory());
                WriteFileAtomic(GetPreferencesPath(), new[]
                {
                    "lastProfile=" + EncodeText(lastProfileNickname),
                    "alwaysImport=" + alwaysImport
                });
            }
            catch
            {
            }
        }

        private static bool ProfileExists(string nickname) { return File.Exists(GetProfilePath(nickname)); }
        private static string GetPreferencesPath() { return Path.Combine(GetStorageDirectory(), PreferencesFileName); }
        private static string GetProfilePath(string nickname) { return Path.Combine(GetStorageDirectory(), EncodeFileName(nickname) + ".cfg"); }
        private static string GetStorageDirectory() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCPTunnel", "profiles"); }
        private static string EncodeFileName(string value) { return Convert.ToBase64String(Utf8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        private static string EncodeText(string value) { return Convert.ToBase64String(Utf8.GetBytes(value ?? String.Empty)); }
        private static string DecodeText(string value) { try { return Utf8.GetString(Convert.FromBase64String(value ?? String.Empty)); } catch { return null; } }
    }
}
