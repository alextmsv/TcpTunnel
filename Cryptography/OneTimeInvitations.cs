#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace TCPTunnel.Cryptography;

internal sealed class OneTimeInvitations(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, long> tokens = new(StringComparer.Ordinal);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    internal string Create()
    {
        lock (gate)
        {
            Prune();
            if (tokens.Count >= 64) throw new InvalidOperationException("Too many active invitations. Wait for their ten-minute expiry.");
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            tokens.Add(Hash(token), time.GetTimestamp());
            return token;
        }
    }
    internal bool Available(string token)
    {
        if (!Valid(token)) return false;
        lock (gate) { Prune(); return tokens.ContainsKey(Hash(token)); }
    }
    internal bool Redeem(string token, Action approve)
    {
        if (!Valid(token)) return false;
        lock (gate)
        {
            Prune();
            string hash = Hash(token);
            if (!tokens.ContainsKey(hash)) return false;
            approve(); tokens.Remove(hash); return true;
        }
    }
    internal void RevokeAll() { lock (gate) tokens.Clear(); }
    internal static bool Valid(string? token) => token != null && token.Length == 64 && token.All(Uri.IsHexDigit);
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
    private void Prune()
    {
        foreach (string key in tokens.Where(item => time.GetElapsedTime(item.Value) >= Lifetime).Select(item => item.Key).ToArray()) tokens.Remove(key);
    }
}
