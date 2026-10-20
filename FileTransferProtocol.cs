#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCPTunnel;

internal sealed record FileTransferPacket(string Kind, string Id = "", string Target = "", string Sender = "", string Name = "", long Size = 0, string Key = "", long Sequence = 0, string Data = "");

internal static class FileTransferProtocol
{
    internal const string Prefix = "FILE1:";
    internal const string Probe = "\u001eTCPTUNNEL|EVENT|HUB1|FILES|HELLO";
    internal const string Capabilities = "\u001eTCPTUNNEL|EVENT|HUB1|FILES|CAPS";
    internal const string Unavailable = "\u001eTCPTUNNEL|EVENT|HUB1|FILES|OFF";
    internal const int ChunkBytes = 6144;
    internal const long MaximumFileBytes = 256L * 1024 * 1024;
    internal const int MaximumPending = 16;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 3, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static bool IsControl(string frame) => frame.StartsWith(Prefix, StringComparison.Ordinal);
    internal static string Encode(FileTransferPacket packet) => Prefix + JsonSerializer.Serialize(packet, Options);
    internal static bool TryParse(string frame, out FileTransferPacket packet)
    {
        packet = null!;
        if (!IsControl(frame) || frame.Length > 12000) return false;
        try
        {
            var p = JsonSerializer.Deserialize<FileTransferPacket>(frame.AsSpan(Prefix.Length), Options);
            if (p == null || p.Kind == null || p.Id == null || p.Target == null || p.Sender == null || p.Name == null || p.Key == null || p.Data == null ||
                p.Sender.Length > 64 || p.Target.Length > 64 || p.Sender.Length != 0 && !NetWorker.IsNicknameValid(p.Sender) ||
                p.Target.Length != 0 && !NetWorker.IsNicknameValid(p.Target)) return false;
            if (p.Kind == "hello")
            {
                if (p.Id != "" || p.Name != "" || p.Size != 0 || p.Key != "" || p.Sequence != 0 || p.Data != "") return false;
            }
            else
            {
                if (!Guid.TryParseExact(p.Id, "N", out _) || p.Target.Length == 0 || p.Size < 0 || p.Size > MaximumFileBytes || p.Sequence < 0 || p.Sequence > MaximumFileBytes / ChunkBytes + 1) return false;
                switch (p.Kind)
                {
                    case "offer":
                        if (!SafeName(p.Name) || !ValidBase64(p.Key, 91, 91) || p.Sequence != 0 || p.Data != "") return false;
                        break;
                    case "accept":
                        if (!ValidBase64(p.Key, 91, 91) || p.Name != "" || p.Size != 0 || p.Sequence != 0 || p.Data != "") return false;
                        break;
                    case "chunk":
                        if (p.Name != "" || p.Size != 0 || p.Key != "" || !ValidBase64(p.Data, 17, ChunkBytes + 16)) return false;
                        break;
                    case "end":
                        if (p.Name != "" || p.Size != 0 || p.Key != "" || !ValidBase64(p.Data, 48, 48)) return false;
                        break;
                    case "reject":
                        if (p.Name != "" || p.Size != 0 || p.Key != "" || p.Sequence != 0 || p.Data != "") return false;
                        break;
                    default: return false;
                }
            }
            packet = p; return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return false; }
    }
    internal static bool TryBind(string frame, string authenticatedNickname, out string bound)
    {
        bound = "";
        if (!NetWorker.IsNicknameValid(authenticatedNickname) || !TryParse(frame, out var packet) || packet.Sender.Length != 0) return false;
        bound = Encode(packet with { Sender = authenticatedNickname }); return true;
    }
    internal static bool SafeName(string name)
    {
        if (String.IsNullOrWhiteSpace(name) || name.Length > 120 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(c => Char.IsControl(c) || Char.IsSurrogate(c) || Char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or
                System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator || "<>:\"/\\|?*".Contains(c))) return false;
        string stem = name.Split('.')[0].TrimEnd(' ');
        return !new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && (stem[3] is >= '0' and <= '9' || "¹²³".Contains(stem[3])));
    }
    private static bool ValidBase64(string text, int minimum, int maximum)
    {
        if (text.Length > ((maximum + 2) / 3) * 4) return false;
        Span<byte> bytes = stackalloc byte[ChunkBytes + 16];
        return Convert.TryFromBase64String(text, bytes, out int length) && length >= minimum && length <= maximum;
    }
}
