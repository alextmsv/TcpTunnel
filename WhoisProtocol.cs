using System;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TCPTunnel
{
    internal sealed record WhoisInfo(string Nickname, string Address, bool PublicAddressUnavailable,
        int? PingMilliseconds, int WindowWidth, int WindowHeight, bool? SnakeEnabled, bool Paused,
        int SnakeDelay, int SnakeColor, int SnakeGlyph, long Messages,
        string Transport = null, int? SignalDbm = null);

    internal static class WhoisProtocol
    {
        internal const string Prefix = "\u001eTCPTUNNEL|EVENT|WHO1|";
        internal const string Hello = Prefix + "HELLO";
        internal const string Capabilities = Prefix + "CAPS";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        internal static bool IsControl(string message) => message?.StartsWith(Prefix, StringComparison.Ordinal) == true;
        internal static string Request(string id, string nick) => Prefix + "GET|" + id + "|" + Encode(nick);
        internal static string Reply(string id, WhoisInfo info) => Prefix + "REPLY|" + id + "|" + JsonSerializer.Serialize(info);
        internal static string Notice(string nick) => Prefix + "NOTICE|" + Encode(nick);
        internal static string Size(int width, int height) => Prefix + "SIZE|" + width + "|" + height;
        internal static string Ping(string id) => Prefix + "PING|" + id;
        internal static string Pong(string id) => Prefix + "PONG|" + id;
        internal static string Signal(int dbm) => Prefix + "SIGNAL|" + dbm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        internal const string TransportTcp = "tcp";
        internal const string TransportBluetooth = "bluetooth";
        internal const string TransportLocal = "local";

        internal static bool TrySignal(string message, out int dbm)
        {
            dbm = 0;
            return Fields(message, "SIGNAL", out var parts) && parts.Length == 2 &&
                int.TryParse(parts[1], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out dbm) &&
                ValidSignal(dbm);
        }

        private static bool ValidSignal(int dbm) => dbm is >= -127 and <= 20;

        internal static bool TryRequest(string message, out string id, out string nick)
        {
            id = nick = null;
            if (!Fields(message, "GET", out string[] parts) || parts.Length != 3 || !ValidId(parts[1])) return false;
            id = parts[1]; return DecodeNick(parts[2], out nick);
        }
        internal static bool TryReply(string message, out string id, out WhoisInfo info)
        {
            id = null; info = null;
            if (!Fields(message, "REPLY", out string[] parts) || parts.Length != 3 || !ValidId(parts[1])) return false;
            try
            {
                info = JsonSerializer.Deserialize<WhoisInfo>(parts[2]);
                if (info != null && !ValidInfo(info)) { info = null; return false; }
                id = parts[1]; return true; // JSON null explicitly means not found.
            }
            catch (JsonException) { return false; }
        }
        internal static bool TryNotice(string message, out string nick)
        {
            nick = null;
            return Fields(message, "NOTICE", out var parts) && parts.Length == 2 && DecodeNick(parts[1], out nick);
        }
        internal static bool TryProbe(string message, bool pong, out string id)
        {
            id = null;
            if (!Fields(message, pong ? "PONG" : "PING", out var parts) || parts.Length != 2 || !ValidId(parts[1])) return false;
            id = parts[1]; return true;
        }
        internal static bool TrySize(string message, out int width, out int height)
        {
            width = height = 0;
            return Fields(message, "SIZE", out var parts) && parts.Length == 3 &&
                int.TryParse(parts[1], out width) && int.TryParse(parts[2], out height) && ValidSize(width, height);
        }
        private static bool ValidInfo(WhoisInfo info) => NetWorker.IsNicknameValid(info.Nickname) &&
            (info.Transport is null or TransportTcp or TransportBluetooth or TransportLocal) &&
            (info.SignalDbm == null || ValidSignal(info.SignalDbm.Value)) &&
            (info.Address == "" || IPAddress.TryParse(info.Address, out _)) && info.Messages >= 0 &&
            (info.PingMilliseconds == null || info.PingMilliseconds is >= 0 and <= 60000) && ValidSize(info.WindowWidth, info.WindowHeight) &&
            (info.SnakeEnabled == null || (info.SnakeDelay is >= 20 and <= 1000 &&
                ConsoleGraphic.IsVisibleSnakeColor((ConsoleColor)info.SnakeColor) && info.SnakeGlyph is >= Char.MinValue and <= Char.MaxValue &&
                ConsoleGraphic.IsValidSnakeGlyph(((char)info.SnakeGlyph).ToString())));
        private static bool ValidSize(int width, int height) => width is >= 0 and <= 32768 && height is >= 0 and <= 32768;
        private static bool ValidId(string id) => id.Length == 32 && Guid.TryParseExact(id, "N", out _);
        private static string Encode(string nick) => Convert.ToBase64String(Utf8.GetBytes(nick));
        private static bool DecodeNick(string encoded, out string nick)
        {
            nick = null;
            try { nick = Utf8.GetString(Convert.FromBase64String(encoded)); return NetWorker.IsNicknameValid(nick); }
            catch (Exception error) when (error is FormatException or DecoderFallbackException) { return false; }
        }
        private static bool Fields(string message, string kind, out string[] fields)
        {
            fields = null;
            if (message == null || message.Length > 2048 || !IsControl(message)) return false;
            fields = message.Substring(Prefix.Length).Split('|', 3);
            return fields.Length > 0 && fields[0] == kind;
        }
    }
}
