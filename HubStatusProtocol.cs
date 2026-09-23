using System;
using System.Globalization;
using System.Text;

namespace TCPTunnel
{
    internal static class HubStatusProtocol
    {
        private const string Prefix = "\u001eTCPTUNNEL|EVENT|HUB1|";
        internal const string Hello = Prefix + "HELLO";
        internal const string Capabilities = Prefix + "CAPS";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static bool IsControl(string message) => message != null && message.StartsWith(Prefix, StringComparison.Ordinal);
        internal static string CreateRequest(string id)
        {
            if (!ValidId(id)) throw new ArgumentException("Invalid request id.", nameof(id));
            return Prefix + "GET|" + id;
        }
        internal static bool TryParseRequest(string message, out string id)
        {
            id = null;
            if (message == null || !message.StartsWith(Prefix + "GET|", StringComparison.Ordinal)) return false;
            string value = message.Substring((Prefix + "GET|").Length);
            if (!ValidId(value)) return false;
            id = value;
            return true;
        }
        internal static string CreateReply(string id, string owner, int count)
        {
            if (!ValidId(id) || count < 0 || count > 64 || (!String.IsNullOrEmpty(owner) && !NetWorker.IsNicknameValid(owner)))
                throw new ArgumentException("Invalid hub status.");
            return Prefix + "REPLY|" + id + "|" + Convert.ToBase64String(Utf8.GetBytes(owner ?? "")) + "|" + count.ToString(CultureInfo.InvariantCulture);
        }
        internal static bool TryParseReply(string message, out string id, out string owner, out int count)
        {
            id = owner = null;
            count = 0;
            if (message == null || message.Length > 256 || !message.StartsWith(Prefix + "REPLY|", StringComparison.Ordinal)) return false;
            string[] parts = message.Substring((Prefix + "REPLY|").Length).Split('|');
            if (parts.Length != 3 || !ValidId(parts[0]) ||
                !Int32.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count > 64) return false;
            try { owner = Utf8.GetString(Convert.FromBase64String(parts[1])); }
            catch (Exception ex) when (ex is FormatException || ex is DecoderFallbackException) { return false; }
            if (owner.Length > 0 && !NetWorker.IsNicknameValid(owner)) return false;
            id = parts[0];
            return true;
        }
        private static bool ValidId(string id) => id != null && id.Length == 32 && Guid.TryParseExact(id, "N", out _);
    }
}
