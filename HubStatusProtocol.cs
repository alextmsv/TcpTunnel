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
        internal static string CreateRequest(string id, bool modern = false)
        {
            if (!ValidId(id)) throw new ArgumentException("Invalid request id.", nameof(id));
            return Prefix + (modern ? "GET2|" : "GET|") + id;
        }
        internal static bool TryParseRequest(string message, out string id)
            => TryParseRequest(message, out id, out _);
        internal static bool TryParseRequest(string message, out string id, out bool modern)
        {
            id = null;
            modern = message?.StartsWith(Prefix + "GET2|", StringComparison.Ordinal) == true;
            string start = Prefix + (modern ? "GET2|" : "GET|");
            if (message == null || !message.StartsWith(start, StringComparison.Ordinal)) return false;
            string value = message.Substring(start.Length);
            if (!ValidId(value)) return false;
            id = value;
            return true;
        }
        internal static string CreateReply(string id, string owner, int count, string original = null)
        {
            if (!ValidId(id) || count < 0 || count > 64 || (!String.IsNullOrEmpty(owner) && !NetWorker.IsNicknameValid(owner)))
                throw new ArgumentException("Invalid hub status.");
            if (original != null && !NetWorker.IsNicknameValid(original)) throw new ArgumentException("Invalid original owner name.");
            return Prefix + (original != null ? "REPLY2|" : "REPLY|") + id + "|" + Convert.ToBase64String(Utf8.GetBytes(owner ?? "")) + "|" + count.ToString(CultureInfo.InvariantCulture) +
                (original != null ? "|" + Convert.ToBase64String(Utf8.GetBytes(original)) : "");
        }
        internal static bool TryParseReply(string message, out string id, out string owner, out int count)
            => TryParseReply(message, out id, out owner, out count, out _);
        internal static bool TryParseReply(string message, out string id, out string owner, out int count, out string original)
        {
            id = owner = null;
            original = null;
            count = 0;
            bool modern = message?.StartsWith(Prefix + "REPLY2|", StringComparison.Ordinal) == true;
            string start = Prefix + (modern ? "REPLY2|" : "REPLY|");
            if (message == null || message.Length > 512 || !message.StartsWith(start, StringComparison.Ordinal)) return false;
            string[] parts = message.Substring(start.Length).Split('|');
            if (parts.Length != (modern ? 4 : 3) || !ValidId(parts[0]) ||
                !Int32.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count > 64) return false;
            try { owner = Utf8.GetString(Convert.FromBase64String(parts[1])); if (modern) original = Utf8.GetString(Convert.FromBase64String(parts[3])); }
            catch (Exception ex) when (ex is FormatException || ex is DecoderFallbackException) { return false; }
            if (owner.Length > 0 && !NetWorker.IsNicknameValid(owner)) return false;
            if (modern && !NetWorker.IsNicknameValid(original)) return false;
            id = parts[0];
            return true;
        }
        private static bool ValidId(string id) => id != null && id.Length == 32 && Guid.TryParseExact(id, "N", out _);
    }
}
