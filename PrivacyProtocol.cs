using System;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel;

internal static class PrivacyProtocol
{
    private const string Prefix = "\u001eTCPTUNNEL|EVENT|HUB1|PRIVACY|";
    internal const string Ack = Prefix + "ACK";
    internal static string Request(string id) => Prefix + "GET|" + id;
    internal static string Reply(string id, bool hidden) => Prefix + "REPLY|" + id + "|" + (hidden ? "1" : "0");
    internal static bool TryRequest(string text, out string id)
    {
        id = null;
        if (text?.StartsWith(Prefix + "GET|", StringComparison.Ordinal) != true) return false;
        string value = text.Substring(Prefix.Length + 4);
        if (!Guid.TryParseExact(value, "N", out _)) return false;
        id = value; return true;
    }
    internal static bool TryReply(string text, string id, out bool hidden)
    {
        hidden = false;
        if (text == Reply(id, true)) { hidden = true; return true; }
        return text == Reply(id, false);
    }
}

internal sealed class PrivacySession
{
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource<bool> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Receive(string text)
    {
        if (PrivacyProtocol.TryReply(text, id, out bool hidden)) result.TrySetResult(hidden);
    }
    internal async Task<bool?> QueryAsync(Func<string, CancellationToken, Task> send, CancellationToken token)
    {
        await send(PrivacyProtocol.Request(id), token).ConfigureAwait(false);
        try { return await result.Task.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }
}
