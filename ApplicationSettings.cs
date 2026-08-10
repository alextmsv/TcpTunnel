using System;
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
        public ConsoleColor MenuTextColor = ConsoleColor.White;
        public ConsoleColor IncomingColor = ConsoleColor.Green;
        public ConsoleColor IncomingTextColor = ConsoleColor.White;
        public ConsoleColor OutgoingColor = ConsoleColor.Cyan;
        public ConsoleColor OutgoingTextColor = ConsoleColor.White;
        public ConsoleColor InputColor = ConsoleColor.Cyan;
        public ConsoleColor InputTextColor = ConsoleColor.White;
        public ConsoleColor SystemColor = ConsoleColor.DarkGray;
    }

    internal static class ApplicationSettings
    {
        private const string DefaultResourceName = "TCPTunnel.default.cfg";
        private const string PreferencesFileName = "preferences.cfg";
        private static readonly object sync = new object();
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static string lastProfileNickname = String.Empty;
        private static bool alwaysImport;

        public static AppProfile Current { get; private set; } = new AppProfile();
        public static string PendingProfileNickname { get; private set; }
        public static string LastHost => String.IsNullOrWhiteSpace(Current.LastHost) ? "localhost" : Current.LastHost;
        public static int LastPort => Current.LastPort >= 1 && Current.LastPort <= 65535 ? Current.LastPort : 9091;

        public static void Initialize()
        {
            lock (sync)
            {
                Current = LoadEmbeddedDefaults();
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
                        Current = LoadProfile(lastProfileNickname, Current);
                    else
                        PendingProfileNickname = lastProfileNickname;
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
                Current.MenuTextColor = defaults.MenuTextColor;
                Current.IncomingColor = defaults.IncomingColor;
                Current.IncomingTextColor = defaults.IncomingTextColor;
                Current.OutgoingColor = defaults.OutgoingColor;
                Current.OutgoingTextColor = defaults.OutgoingTextColor;
                Current.InputColor = defaults.InputColor;
                Current.InputTextColor = defaults.InputTextColor;
                Current.SystemColor = defaults.SystemColor;
                ApplyCurrent();
                ConsoleGraphic.InvalidateVisualTheme();
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
            return profile.Nickname == "Тестер" && profile.LastHost == "127.0.0.1" &&
                   profile.LastPort == 9091 && !profile.GraphicsEnabled &&
                   profile.SnakeDelay == 125 && profile.SnakeColor == ConsoleColor.Cyan &&
                   profile.SnakeGlyph == '~' && profile.BorderColor == ConsoleColor.Black &&
                   profile.MenuTextColor == ConsoleColor.Yellow;
        }

        private static void SaveCurrentProfileLocked(string nickname)
        {
            if (!NetWorker.IsNicknameValid(nickname))
                return;

            CaptureCurrent(nickname);
            try
            {
                Directory.CreateDirectory(GetStorageDirectory());
                WriteFileAtomic(GetProfilePath(nickname), Serialize(Current));
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
            return ParseProfile(ReadFile(GetProfilePath(nickname)), fallback);
        }

        private static AppProfile ParseProfile(Dictionary<string, string> values, AppProfile fallback)
        {
            var profile = Clone(fallback);
            string value;
            int number;
            bool flag;
            if (values.TryGetValue("nickname", out value)) profile.Nickname = DecodeText(value) ?? profile.Nickname;
            if (values.TryGetValue("lastHost", out value)) profile.LastHost = DecodeText(value) ?? profile.LastHost;
            if (values.TryGetValue("lastPort", out value) && Int32.TryParse(value, out number) && number >= 1 && number <= 65535) profile.LastPort = number;
            if (values.TryGetValue("graphics", out value) && Boolean.TryParse(value, out flag)) profile.GraphicsEnabled = flag;
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

        private static string[] Serialize(AppProfile profile)
        {
            return new[]
            {
                "version=1", "nickname=" + EncodeText(profile.Nickname), "lastHost=" + EncodeText(profile.LastHost),
                "lastPort=" + profile.LastPort, "graphics=" + profile.GraphicsEnabled,
                "language=" + (profile.Language == AppLanguage.English ? "en" : "ru"),
                "snakeDelay=" + profile.SnakeDelay, "snakeColor=" + (int)profile.SnakeColor,
                "snakeGlyph=" + EncodeText(profile.SnakeGlyph.ToString()), "borderColor=" + (int)profile.BorderColor,
                "cornerColor=" + (int)profile.CornerColor, "selectionColor=" + (int)profile.SelectionColor,
                "menuTextColor=" + (int)profile.MenuTextColor,
                "incomingColor=" + (int)profile.IncomingColor, "incomingTextColor=" + (int)profile.IncomingTextColor,
                "outgoingColor=" + (int)profile.OutgoingColor, "outgoingTextColor=" + (int)profile.OutgoingTextColor,
                "inputColor=" + (int)profile.InputColor, "inputTextColor=" + (int)profile.InputTextColor,
                "systemColor=" + (int)profile.SystemColor
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
                IncomingColor = value.IncomingColor,
                IncomingTextColor = value.IncomingTextColor,
                OutgoingColor = value.OutgoingColor, OutgoingTextColor = value.OutgoingTextColor,
                InputColor = value.InputColor, InputTextColor = value.InputTextColor,
                SystemColor = value.SystemColor
            };
        }

        private static Dictionary<string, string> ReadFile(string path)
        {
            try { return File.Exists(path) ? ParseLines(File.ReadAllLines(path, Utf8)) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
            catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }

        private static Dictionary<string, string> ParseLines(IEnumerable<string> lines)
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

        private static void WriteFileAtomic(string path, IEnumerable<string> lines)
        {
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllLines(temporary, lines, Utf8);
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch (PlatformNotSupportedException) { File.Copy(temporary, path, true); }
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
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
