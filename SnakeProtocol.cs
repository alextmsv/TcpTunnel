using System;
using System.Text;

namespace TCPTunnel
{
    internal struct SnakeProfile
    {
        public bool Enabled;
        public bool Paused;
        public int DelayMilliseconds;
        public ConsoleColor Color;
        public int Step;
    }

    internal enum SnakeUpdateKind
    {
        None,
        Set,
        Remove
    }

    internal static class SnakeProtocol
    {
        public const int ReferencePerimeterLength = 170;
        private const string Prefix = "\u001eTCPTUNNEL|SNAKE|";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static bool IsSnakeControlMessage(string message)
        {
            return message != null && message.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static string CreateClientProfile(SnakeProfile profile)
        {
            return Prefix + "PROFILE|" +
                   (profile.Enabled ? "1" : "0") + "|" +
                   profile.DelayMilliseconds + "|" +
                   (int)profile.Color + "|" +
                   profile.Step + "|" +
                   (profile.Paused ? "1" : "0");
        }

        public static bool TryParseClientProfile(string message, out SnakeProfile profile)
        {
            profile = default(SnakeProfile);
            string[] fields;
            if (message == null || !message.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            fields = message.Substring(Prefix.Length).Split('|');
            if ((fields.Length != 5 && fields.Length != 6) || fields[0] != "PROFILE")
                return false;

            int enabled;
            int delay;
            int color;
            int step;
            int paused = 0;
            if (!Int32.TryParse(fields[1], out enabled) || (enabled != 0 && enabled != 1) ||
                !Int32.TryParse(fields[2], out delay) || delay < 20 || delay > 1000 ||
                !Int32.TryParse(fields[3], out color) ||
                !Int32.TryParse(fields[4], out step) ||
                (fields.Length == 6 && (!Int32.TryParse(fields[5], out paused) || (paused != 0 && paused != 1))))
                return false;

            ConsoleColor consoleColor = (ConsoleColor)color;
            if (!ConsoleGraphic.IsVisibleSnakeColor(consoleColor))
                return false;

            profile.Enabled = enabled == 1;
            profile.Paused = paused == 1;
            profile.DelayMilliseconds = delay;
            profile.Color = consoleColor;
            profile.Step = NormalizeStep(step);
            return true;
        }

        public static string CreateSet(string nickname, SnakeProfile profile)
        {
            return Prefix + "SET|" + EncodeNickname(nickname) + "|" +
                   profile.DelayMilliseconds + "|" +
                   (int)profile.Color + "|" +
                   NormalizeStep(profile.Step) + "|" +
                   (profile.Paused ? "1" : "0");
        }

        public static string CreateRemove(string nickname)
        {
            return Prefix + "REMOVE|" + EncodeNickname(nickname);
        }

        public static bool TryParseServerUpdate(
            string message,
            out SnakeUpdateKind kind,
            out string nickname,
            out SnakeProfile profile)
        {
            kind = SnakeUpdateKind.None;
            nickname = null;
            profile = default(SnakeProfile);

            if (message == null || !message.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            string[] fields = message.Substring(Prefix.Length).Split('|');
            if (fields.Length == 2 && fields[0] == "REMOVE")
            {
                if (!TryDecodeNickname(fields[1], out nickname))
                    return false;

                kind = SnakeUpdateKind.Remove;
                return true;
            }

            if ((fields.Length != 5 && fields.Length != 6) || fields[0] != "SET" ||
                !TryDecodeNickname(fields[1], out nickname))
                return false;

            int delay;
            int color;
            int step;
            int paused = 0;
            if (!Int32.TryParse(fields[2], out delay) || delay < 20 || delay > 1000 ||
                !Int32.TryParse(fields[3], out color) ||
                !Int32.TryParse(fields[4], out step) ||
                (fields.Length == 6 && (!Int32.TryParse(fields[5], out paused) || (paused != 0 && paused != 1))))
                return false;

            ConsoleColor consoleColor = (ConsoleColor)color;
            if (!ConsoleGraphic.IsVisibleSnakeColor(consoleColor))
                return false;

            profile.Enabled = true;
            profile.Paused = paused == 1;
            profile.DelayMilliseconds = delay;
            profile.Color = consoleColor;
            profile.Step = NormalizeStep(step);
            kind = SnakeUpdateKind.Set;
            return true;
        }

        public static bool RunSelfTest()
        {
            try
            {
                SnakeProfile source = new SnakeProfile
                {
                    Enabled = true,
                    Paused = true,
                    DelayMilliseconds = 125,
                    Color = ConsoleColor.Cyan,
                    Step = 169
                };

                SnakeProfile parsedClient;
                if (!TryParseClientProfile(CreateClientProfile(source), out parsedClient) ||
                    !parsedClient.Enabled || !parsedClient.Paused || parsedClient.DelayMilliseconds != source.DelayMilliseconds ||
                    parsedClient.Color != source.Color || parsedClient.Step != source.Step)
                    return false;

                SnakeUpdateKind kind;
                string nickname;
                SnakeProfile parsedServer;
                if (!TryParseServerUpdate(
                        CreateSet("тестер", source),
                        out kind,
                        out nickname,
                        out parsedServer) ||
                    kind != SnakeUpdateKind.Set || nickname != "тестер" ||
                    !parsedServer.Paused || parsedServer.DelayMilliseconds != source.DelayMilliseconds ||
                    parsedServer.Color != source.Color || parsedServer.Step != source.Step)
                    return false;

                if (!TryParseServerUpdate(
                        CreateRemove("тестер"),
                        out kind,
                        out nickname,
                        out parsedServer) ||
                    kind != SnakeUpdateKind.Remove || nickname != "тестер")
                    return false;

                SnakeProfile legacyClient;
                if (!TryParseClientProfile(Prefix + "PROFILE|1|75|10|5", out legacyClient) ||
                    legacyClient.Paused)
                    return false;

                SnakeProfile legacyServer;
                if (!TryParseServerUpdate(
                        Prefix + "SET|" + EncodeNickname("тестер") + "|75|10|5",
                        out kind,
                        out nickname,
                        out legacyServer) ||
                    legacyServer.Paused)
                    return false;

                return !TryParseClientProfile(Prefix + "PROFILE|1|1|10|0", out parsedClient);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string EncodeNickname(string nickname)
        {
            return Convert.ToBase64String(Utf8.GetBytes(nickname ?? String.Empty));
        }

        private static bool TryDecodeNickname(string encoded, out string nickname)
        {
            nickname = null;
            try
            {
                nickname = Utf8.GetString(Convert.FromBase64String(encoded));
                return NetWorker.IsNicknameValid(nickname);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static int NormalizeStep(int step)
        {
            return ((step % ReferencePerimeterLength) + ReferencePerimeterLength) % ReferencePerimeterLength;
        }
    }
}
