using System;
using System.Collections.Generic;

namespace TCPTunnel
{
    internal sealed class WhoisUnreadState
    {
        private int lastInputGeneration;
        private long? visibleSince;
        internal bool Unread { get; private set; } = true;
        internal WhoisUnreadState(int inputGeneration) { lastInputGeneration = inputGeneration; }

        internal void Observe(bool visible, bool focused, int inputGeneration, long now)
        {
            bool freshInput = inputGeneration != lastInputGeneration;
            lastInputGeneration = inputGeneration;
            if (!Unread) return;
            if (visible && freshInput) { Unread = false; return; }
            if (!visible || !focused) { visibleSince = null; return; }
            visibleSince ??= now;
            if (now - visibleSince >= 600) Unread = false;
        }
    }

    internal sealed class WhoisUnreadSummary
    {
        private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        internal int Count => names.Count;
        internal bool HasMore { get; private set; }
        internal int InputGeneration { get; private set; }
        internal void Add(IEnumerable<string> requesters, int inputGeneration)
        {
            if (names.Count == 0) InputGeneration = inputGeneration;
            foreach (string name in requesters)
            {
                if (names.Count < ServerInterface.MaxConnectedClients || names.Contains(name)) names.Add(name);
                else HasMore = true;
            }
        }
        internal string[] Drain()
        {
            var result = new string[names.Count]; names.CopyTo(result);
            names.Clear(); HasMore = false;
            return result;
        }
        internal string[] Take(int textBudget)
        {
            var result = new List<string>();
            int length = 0;
            foreach (string name in names)
            {
                if (length + name.Length + 3 > textBudget) break;
                result.Add(name); length += name.Length + 3;
            }
            foreach (string name in result) names.Remove(name);
            if (names.Count == 0) HasMore = false;
            return result.ToArray();
        }
    }
}
