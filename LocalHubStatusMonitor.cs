using System;
using System.Collections.Generic;

namespace TCPTunnel
{
    internal sealed record LocalHubSnapshot(bool Running, int Port, string Nat, int Participants);

    internal sealed class LocalHubStatusMonitor
    {
        private LocalHubSnapshot previous;

        internal IReadOnlyList<string> Observe(LocalHubSnapshot current, bool inOwnHub)
        {
            if (current == previous) return Array.Empty<string>();
            var messages = new List<string>();
            if (current.Running)
            {
                if (previous?.Running != true || previous.Port != current.Port)
                    messages.Add(current.Port > 0
                        ? Lang.Get(TextId.OwnHubStarted, current.Port)
                        : Lang.Get(TextId.OwnHubStartedBluetooth));
                if (previous?.Running != true || previous.Nat != current.Nat)
                    messages.Add(Lang.Get(TextId.OwnHubNat, current.Nat));
                if (!inOwnHub && (previous?.Running != true || previous.Participants != current.Participants))
                    messages.Add(Lang.Get(TextId.OwnHubParticipants, current.Participants));
            }
            else if (previous?.Running == true)
                messages.Add(Lang.Get(TextId.OwnHubStopped));
            previous = current;
            return messages;
        }
    }
}
