using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCPTunnel;

internal sealed record DuelPacket(string Kind, Guid Id = default, string Peer = "", string Level = "", int Minutes = 0,
    bool Blitz = false, int Slot = 0, int X = 0, int Y = 0, int You = 0, int Them = 0, string Outcome = "", Guid Nonce = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Version = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string Mode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string Size = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Count = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Radius = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Motion = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Seed = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Streak = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Delta = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Clear = 0);

internal static class DuelProtocol
{
    internal const int Width = 900, Height = 540;
    private const string Prefix = "\u001eTCPTUNNEL|EVENT|HUB1|DUEL|";
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 2, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static bool IsControl(string text) => text?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    internal static string Encode(DuelPacket packet) => Prefix + JsonSerializer.Serialize(packet, Options);
    internal static (int Radius, int Interval, int Lifetime) Rules(string level) => level switch
    {
        "easy" => (30, 1450, 1250),
        "normal" => (21, 1150, 940),
        "hard" => (12, 850, 640),
        _ => default
    };

    internal static bool TryDecode(string text, out DuelPacket packet)
    {
        packet = null;
        if (!IsControl(text) || text.Length > 1024) return false;
        try
        {
            var value = JsonSerializer.Deserialize<DuelPacket>(text.AsSpan(Prefix.Length), Options);
            if (value?.Kind == null || value.Kind.Length > 12 || value.Peer == null || value.Peer.Length > 64 ||
                value.Level == null || value.Level.Length > 6 || value.Outcome == null || value.Outcome.Length > 16 ||
                value.Mode?.Length > 9 || value.Size?.Length > 6 || value.Version is not (0 or 2))
                return false;
            packet = value;
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static bool TryCommand(string input, out DuelPacket packet)
    {
        packet = null;
        string[] parts = input.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[1].Equals("accept", StringComparison.OrdinalIgnoreCase))
        { packet = new("accept"); return true; }
        if (parts.Length == 2 && parts[1].Equals("practice", StringComparison.OrdinalIgnoreCase))
        { packet = new("practice", Level: "normal", Minutes: 1); return true; }
        if (parts.Length == 2 && parts[1].StartsWith('@') && NetWorker.IsNicknameValid(parts[1][1..]))
        { packet = new("setup", Peer: parts[1][1..]); return true; }
        if (parts.Length is not (4 or 5) || parts[2].Length > 6 || Rules(parts[2].ToLowerInvariant()).Radius == 0 ||
            parts[3].Length < 2 || parts[3][^1] != 'm' ||
            !Int32.TryParse(parts[3].AsSpan(0, parts[3].Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
            minutes is < 1 or > 15 || (parts.Length == 5 && !parts[4].Equals("blitz", StringComparison.OrdinalIgnoreCase))) return false;
        if (parts[1].Equals("practice", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length != 4) return false;
            packet = new("practice", Level: parts[2].ToLowerInvariant(), Minutes: minutes);
            return true;
        }
        string name = parts[1].StartsWith('@') ? parts[1][1..] : parts[1];
        if (!NetWorker.IsNicknameValid(name)) return false;
        packet = new("offer", Peer: name, Level: parts[2].ToLowerInvariant(), Minutes: minutes, Blitz: parts.Length == 5);
        return true;
    }

    internal static bool Hits(int x, int y, int targetX, int targetY, int radius)
    {
        if ((uint)x >= Width || (uint)y >= Height) return false;
        int dx = x - targetX, dy = y - targetY;
        return dx * dx + dy * dy <= radius * radius;
    }

    internal static int Radius(string size) => size switch { "large" or "any" => 30, "medium" => 21, "small" => 12, _ => 0 };
    internal static int MineLimit => DuelArena.Grid.Length - 1;

    internal static bool ValidOptions(DuelPacket packet)
    {
        if (packet.Minutes is < 1 or > 15 || Rules(packet.Level).Radius == 0) return false;
        if (packet.Version == 0) return packet.Mode == null && packet.Size == null && packet.Count == 0;
        return packet.Version == 2 && Radius(packet.Size) != 0 && (packet.Mode switch
        {
            "static" or "moving" => packet.Count == 0,
            "minefield" => packet.Size == "any" && packet.Level == "normal" && packet.Count >= 1 && packet.Count <= MineLimit,
            _ => false
        });
    }
}
