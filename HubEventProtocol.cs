using System;
using System.Text;

namespace TCPTunnel
{
    internal static class HubEventProtocol
    {
        private const string Prefix = "\u001eTCPTUNNEL|EVENT|";
        private const string GasterEvent = "GASTER";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static bool IsControlMessage(string message)
        {
            return message != null && message.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static string CreateGasterEvent(string nickname, int durationMilliseconds, int seed)
        {
            return Prefix + GasterEvent + "|" +
                   Math.Max(1000, Math.Min(60000, durationMilliseconds)) + "|" +
                   seed + "|" +
                   Convert.ToBase64String(Utf8.GetBytes(nickname ?? String.Empty));
        }

        public static bool TryParseGasterEvent(
            string message,
            out string nickname,
            out int durationMilliseconds,
            out int seed)
        {
            nickname = null;
            durationMilliseconds = 0;
            seed = 0;
            if (!IsControlMessage(message))
                return false;

            string[] fields = message.Substring(Prefix.Length).Split('|');
            if (fields.Length != 4 || fields[0] != GasterEvent ||
                !Int32.TryParse(fields[1], out durationMilliseconds) ||
                durationMilliseconds < 1000 || durationMilliseconds > 60000 ||
                !Int32.TryParse(fields[2], out seed))
                return false;
            try
            {
                nickname = Utf8.GetString(Convert.FromBase64String(fields[3]));
                return NetWorker.IsNicknameValid(nickname);
            }
            catch
            {
                return false;
            }
        }

        public static bool RunSelfTest()
        {
            string nickname;
            int duration;
            int seed;
            return TryParseGasterEvent(
                       CreateGasterEvent("W_D_Gaster", 60000, 666),
                       out nickname,
                       out duration,
                       out seed) &&
                   nickname == "W_D_Gaster" && duration == 60000 && seed == 666 &&
                   !TryParseGasterEvent(Prefix + "GASTER|999999|1|bad", out nickname, out duration, out seed);
        }
    }
}
