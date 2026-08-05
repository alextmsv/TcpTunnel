using System;
using System.Text;

namespace TCPTunnel
{
    internal enum SystemMessageKind
    {
        UserJoined,
        UserLeft,
        MessageTooLong,
        TooManyMessages
    }

    internal static class SystemMessageProtocol
    {
        private const string Prefix = "TCPTUNNEL_SYSTEM_V1|";

        public static string Create(SystemMessageKind kind, string argument = null)
        {
            string encodedArgument = Convert.ToBase64String(Encoding.UTF8.GetBytes(argument ?? String.Empty));
            return Prefix + kind + "|" + encodedArgument;
        }

        public static bool TryLocalize(string message, out string localized, out SystemMessageKind kind)
        {
            localized = null;
            kind = default(SystemMessageKind);
            if (String.IsNullOrEmpty(message) || !message.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            string[] parts = message.Substring(Prefix.Length).Split(new[] { '|' }, 2);
            if (parts.Length != 2 || !Enum.TryParse(parts[0], false, out kind))
                return false;

            string argument;
            try
            {
                argument = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            }
            catch (FormatException)
            {
                return false;
            }

            switch (kind)
            {
                case SystemMessageKind.UserJoined:
                    localized = Lang.Get(TextId.UserJoined, argument);
                    return true;
                case SystemMessageKind.UserLeft:
                    localized = Lang.Get(TextId.UserLeft, argument);
                    return true;
                case SystemMessageKind.MessageTooLong:
                    localized = Lang.Get(TextId.MessageTooLong);
                    return true;
                case SystemMessageKind.TooManyMessages:
                    localized = Lang.Get(TextId.TooManyMessages);
                    return true;
                default:
                    return false;
            }
        }

        public static bool RunSelfTest()
        {
            string original = "Тест User | Test";
            string localized;
            SystemMessageKind kind;
            return TryLocalize(Create(SystemMessageKind.UserJoined, original), out localized, out kind) &&
                   kind == SystemMessageKind.UserJoined &&
                   localized.IndexOf(original, StringComparison.Ordinal) >= 0 &&
                   !TryLocalize("ordinary chat message", out localized, out kind);
        }
    }
}
