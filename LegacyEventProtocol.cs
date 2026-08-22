using System;

namespace TCPTunnel
{
    internal static class LegacyEventProtocol
    {
        private const string Prefix = "\u001eTCPTUNNEL|EVENT|";

        public static bool IsControlMessage(string message)
        {
            return message != null && message.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static bool RunSelfTest()
        {
            return IsControlMessage(Prefix + "LEGACY") &&
                   !IsControlMessage("ordinary chat message") &&
                   !IsControlMessage(null);
        }
    }
}
