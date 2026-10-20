#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel;

internal sealed record NicknameChange(string Previous, string Current, string Original);
internal sealed record NicknameReply(string Id, string Error, NicknameChange Change);

internal static class NicknameProtocol
{
    private const string Prefix = "\u001eTCPTUNNEL|EVENT|NICK2|";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    internal static bool IsControl(string text) => text?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    internal static string Request(string id, string name) => Prefix + "GET|" + id + "|" + Encode(name);
    internal static string Reply(string id, string error, NicknameChange change) => Prefix + "REPLY|" + id + "|" + error + "|" + Encode(change.Previous) + "|" + Encode(change.Current) + "|" + Encode(change.Original);
    internal static string Changed(NicknameChange change) => Prefix + "CHANGED|" + Encode(change.Previous) + "|" + Encode(change.Current) + "|" + Encode(change.Original);
    internal static string DisplayName(string current, string? original) => "@" + current + (original != null && original != current ? " (a.k.a. " + original + ")" : "");
    internal static bool TryRequest(string text, out string id, out string name)
    {
        id = name = String.Empty;
        if (!Fields(text, "GET", 3, out var parts) || !ValidId(parts[1]) || !Decode(parts[2], out name)) return false;
        id = parts[1]; return true;
    }
    internal static bool TryReply(string text, out NicknameReply? reply)
    {
        reply = null;
        if (!Fields(text, "REPLY", 6, out var parts) || !ValidId(parts[1]) || parts[2] is not ("OK" or "TAKEN" or "LIMIT" or "STORE") ||
            !Decode(parts[3], out string previous) || !Decode(parts[4], out string current) || !Decode(parts[5], out string original)) return false;
        reply = new(parts[1], parts[2], new(previous, current, original)); return true;
    }
    internal static bool TryChanged(string text, out NicknameChange? change)
    {
        change = null;
        if (!Fields(text, "CHANGED", 4, out var parts) || !Decode(parts[1], out string previous) || !Decode(parts[2], out string current) || !Decode(parts[3], out string original)) return false;
        change = new(previous, current, original); return true;
    }
    private static bool Fields(string text, string verb, int count, out string[] parts)
    {
        parts = Array.Empty<string>();
        if (!IsControl(text) || text.Length > 512) return false;
        parts = text.Substring(Prefix.Length).Split('|'); return parts.Length == count && parts[0] == verb;
    }
    private static bool ValidId(string id) => id.Length == 32 && Guid.TryParseExact(id, "N", out _);
    private static string Encode(string text) => Convert.ToBase64String(Utf8.GetBytes(text));
    private static bool Decode(string text, out string name)
    {
        name = String.Empty;
        try { name = Utf8.GetString(Convert.FromBase64String(text)); return NicknamePolicy.IsValid(name); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { return false; }
    }
}

internal sealed class NicknameSession(Action<string> apply)
{
    private readonly object gate = new();
    private TaskCompletionSource<NicknameReply>? pending;
    private string? id;
    internal bool Receive(string text)
    {
        if (!NicknameProtocol.IsControl(text)) return false;
        if (NicknameProtocol.TryReply(text, out var reply))
        {
            lock (gate)
                if (reply!.Id == id && pending != null)
                {
                    if (reply.Error == "OK") apply(reply.Change.Current);
                    pending.TrySetResult(reply);
                }
        }
        return true;
    }
    internal async Task<NicknameReply> RenameAsync(string name, Func<string, CancellationToken, Task> send, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        TaskCompletionSource<NicknameReply> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string requestId = Guid.NewGuid().ToString("N");
        lock (gate) { if (pending != null) throw new InvalidOperationException("Nickname request already pending."); id = requestId; pending = completion; }
        try
        {
            await send(NicknameProtocol.Request(requestId, name), deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) { throw new IOException("Nickname change could not be confirmed; reconnect to synchronize identity.", error); }
        finally { lock (gate) { pending = null; id = null; } }
    }
}
