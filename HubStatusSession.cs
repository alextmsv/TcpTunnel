using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    internal sealed record RemoteHubStatus(string Owner, int Participants);

    internal sealed class HubStatusSession
    {
        internal WhoisSession Whois { get; } = new WhoisSession();
        private readonly object gate = new object();
        private readonly TaskCompletionSource capabilities = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<RemoteHubStatus> pending;
        private string pendingId;

        internal bool Receive(string message)
        {
            if (!HubStatusProtocol.IsControl(message)) return false;
            if (message == HubStatusProtocol.Capabilities) capabilities.TrySetResult();
            if (HubStatusProtocol.TryParseReply(message, out string id, out string owner, out int count))
            {
                lock (gate)
                    if (id == pendingId) pending?.TrySetResult(new RemoteHubStatus(owner, count));
            }
            return true;
        }

        internal async Task<RemoteHubStatus> QueryAsync(Func<string, CancellationToken, Task> send, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            TaskCompletionSource<RemoteHubStatus> request = null;
            try
            {
                await capabilities.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                string id = Guid.NewGuid().ToString("N");
                lock (gate)
                {
                    if (pending != null) return null;
                    request = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending = request;
                    pendingId = id;
                }
                try
                {
                    await send(HubStatusProtocol.CreateRequest(id), deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException error)
                {
                    throw new IOException("Status request could not be sent completely.", error);
                }
                return await request.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            finally
            {
                lock (gate)
                    if (request != null && ReferenceEquals(pending, request)) { pending = null; pendingId = null; }
            }
        }
    }
}
