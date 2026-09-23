using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    internal sealed record WhoisResult(WhoisInfo Info);

    internal sealed class WhoisSession
    {
        private readonly object gate = new object();
        private readonly TaskCompletionSource capabilities = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<WhoisResult> pending;
        private string pendingId;
        internal bool Supported => capabilities.Task.IsCompletedSuccessfully;

        internal async Task<bool> ReceiveAsync(string message, Func<string, CancellationToken, Task> send,
            Action<string> notify, CancellationToken token)
        {
            if (!WhoisProtocol.IsControl(message)) return false;
            if (message == WhoisProtocol.Capabilities) capabilities.TrySetResult();
            if (WhoisProtocol.TryReply(message, out string id, out WhoisInfo info))
            {
                lock (gate) if (id == pendingId) pending?.TrySetResult(new WhoisResult(info));
            }
            if (Supported && WhoisProtocol.TryProbe(message, false, out string probe))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try { await send(WhoisProtocol.Pong(probe), timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException error) { throw new IOException("RTT reply could not be sent completely.", error); }
            }
            if (Supported && WhoisProtocol.TryNotice(message, out string requester)) notify(requester);
            return true;
        }

        internal async Task<WhoisResult> QueryAsync(string nickname, Func<string, CancellationToken, Task> send, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            TaskCompletionSource<WhoisResult> request = null;
            try
            {
                await capabilities.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                string id = Guid.NewGuid().ToString("N");
                lock (gate)
                {
                    if (pending != null) return null;
                    request = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingId = id; pending = request;
                }
                try { await send(WhoisProtocol.Request(id, nickname), timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException error) { throw new IOException("Whois request could not be sent completely.", error); }
                return await request.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            finally
            {
                lock (gate) if (request != null && ReferenceEquals(pending, request)) { pending = null; pendingId = null; }
            }
        }
    }
}
