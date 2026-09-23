using System;
using System.Threading.Tasks;

namespace TCPTunnel
{
    internal enum SessionEndKind { Active, Voluntary, ConnectionLost, ReadTimedOut, InvalidFrame, ServerReason }

    internal sealed class SessionEndState
    {
        private readonly object gate = new object();
        private SessionEndKind kind;
        private string message;

        internal SessionEndKind Kind { get { lock (gate) return kind; } }
        internal string Message { get { lock (gate) return message; } }
        internal bool RequiresAcknowledgement => Kind != SessionEndKind.Active && Kind != SessionEndKind.Voluntary;

        internal void Leave()
        {
            lock (gate)
                if (kind == SessionEndKind.Active) kind = SessionEndKind.Voluntary;
        }

        internal void RecordFailure(SessionEndKind failure)
        {
            if (failure != SessionEndKind.ConnectionLost && failure != SessionEndKind.ReadTimedOut && failure != SessionEndKind.InvalidFrame)
                throw new ArgumentOutOfRangeException(nameof(failure));
            lock (gate)
                if (kind == SessionEndKind.Active) kind = failure;
        }

        internal void RecordServerReason(SystemMessageKind systemKind, string text)
        {
            if (systemKind != SystemMessageKind.Kicked && systemKind != SystemMessageKind.MessageTooLong &&
                systemKind != SystemMessageKind.TooManyMessages)
                return;
            lock (gate)
            {
                if (kind == SessionEndKind.Voluntary || kind == SessionEndKind.ServerReason) return;
                message = text;
                kind = SessionEndKind.ServerReason;
            }
        }

        internal async Task DrainReceiverAsync(Task receiver)
        {
            if (Kind != SessionEndKind.ConnectionLost) return;
            try { await receiver.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch (Exception) {}
        }

        internal string GetDisplayMessage() => Kind switch
        {
            SessionEndKind.ServerReason => Message,
            SessionEndKind.ReadTimedOut => Lang.Get(TextId.FrameReadTimedOut),
            SessionEndKind.InvalidFrame => Lang.Get(TextId.DisconnectInvalidFrame),
            _ => Lang.Get(TextId.HubConnectionLost)
        };
    }
}
