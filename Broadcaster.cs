using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public sealed class Broadcaster
    {
        private readonly object clientsLock = new object();
        private readonly List<Client> clients = new List<Client>();
        private readonly SemaphoreSlim broadcastLock = new SemaphoreSlim(1, 1);

        public int AuthenticatedClientCount
        {
            get
            {
                lock (clientsLock)
                    return clients.Count(client => client.IsReady);
            }
        }

        internal int ConnectionCount
        {
            get { lock (clientsLock) return clients.Count; }
        }

        public void AddConnection(Client client)
        {
            lock (clientsLock)
            {
                clients.Add(client);
            }
        }

        public bool TryAuthenticate(Client client, string nickname)
        {
            if (!TryReserveNickname(client, nickname))
                return false;
            return CompleteAuthentication(client);
        }

        internal bool TryReserveNickname(Client client, string nickname)
        {
            lock (clientsLock)
            {
                if (!clients.Contains(client) || !NetWorker.IsNicknameValid(nickname) || client.IsAuthenticated)
                    return false;
                bool nicknameTaken = clients.Any(existing =>
                    existing.IsAuthenticated &&
                    !Object.ReferenceEquals(existing, client) &&
                    (NicknamePolicy.Same(existing.Nickname, nickname) || NicknamePolicy.Same(existing.OriginalNickname, nickname)));

                if (nicknameTaken)
                    return false;

                client.Nickname = nickname;
                client.OriginalNickname = nickname;
                client.IsAuthenticated = true;
                client.IsReady = false;
                return true;
            }
        }

        internal bool CompleteAuthentication(Client client)
        {
            lock (clientsLock)
            {
                if (!clients.Contains(client) || !client.IsAuthenticated || client.IsReady)
                    return false;
                if (!client.PromoteAdmission()) return false;
                client.IsReady = true;
                return true;
            }
        }

        internal bool CanRename(Client client, string name)
        {
            lock (clientsLock)
                return clients.Contains(client) && client.IsReady && NicknamePolicy.IsValid(name) &&
                    !clients.Any(other => other != client && other.IsAuthenticated && (NicknamePolicy.Same(other.Nickname, name) || NicknamePolicy.Same(other.OriginalNickname, name))) &&
                    !(client.Transport != ChatTransport.Local && (NicknamePolicy.Same(ServerInterface.AdministratorNickname, name) || NicknamePolicy.Same(ServerInterface.AdministratorOriginalNickname, name)));
        }

        internal bool TryRename(Client client, string name, Action beforeCommit = null)
        {
            lock (clientsLock)
            {
                if (!CanRename(client, name)) return false;
                beforeCommit?.Invoke();
                client.Nickname = name; return true;
            }
        }

        internal async Task RenameAsync(Client client, string id, string name, CancellationToken token)
        {
            await broadcastLock.WaitAsync(token).ConfigureAwait(false);
            Task[] deliveries;
            try
            {
                string previous = client.Nickname;
                bool changed = TryRename(client, name);
                var change = new NicknameChange(previous, changed ? name : previous, client.OriginalNickname);
                var queued = new List<Task> { client.SendAsync(NicknameProtocol.Reply(id, changed ? "OK" : "TAKEN", change), token) };
                if (changed && previous != name)
                {
                    if (client.Transport == ChatTransport.Local) ServerInterface.RenameAdministrator(client, name);
                    foreach (Client recipient in GetAuthenticatedClients())
                    {
                        var messages = new List<string>();
                        if (recipient.SupportsNicknameSnake) messages.Add(NicknameProtocol.Changed(change));
                        else { messages.Add(SystemMessageProtocol.Create(SystemMessageKind.UserLeft, previous)); messages.Add(SystemMessageProtocol.Create(SystemMessageKind.UserJoined, name)); }
                        messages.Add(SnakeProtocol.CreateRemove(previous));
                        if (client.TryGetSnakeProfile(out var profile) && profile.Enabled) messages.Add(SnakeProtocol.CreateSet(name, profile, recipient.SupportsNicknameSnake));
                        queued.Add(recipient.SendBatchAsync(messages, token));
                    }
                }
                deliveries = queued.ToArray();
            }
            finally { broadcastLock.Release(); }
            await Task.WhenAll(deliveries).ConfigureAwait(false);
        }

        internal bool CompleteAuthenticationWithRoster(Client client, CancellationToken token, out Task delivery)
        {
            lock (clientsLock)
            {
                delivery = Task.CompletedTask;
                if (!clients.Contains(client) || !client.IsAuthenticated || client.IsReady) return false;
                if (!client.PromoteAdmission()) return false;
                string[] roster = clients.Where(other => other.IsReady && !Object.ReferenceEquals(other, client))
                    .Select(other => SystemMessageProtocol.Create(SystemMessageKind.ParticipantPresent, other.Nickname)).ToArray();
                delivery = client.SendBatchAsync(roster, token);
                client.IsReady = true;
                return true;
            }
        }

        public bool RemoveClient(Client client)
        {
            bool removed;
            lock (clientsLock)
            {
                removed = clients.Remove(client);
                client.IsAuthenticated = false;
                client.IsReady = false;
            }

            client.Close();
            return removed;
        }

        public Client[] GetAuthenticatedClients(Client excludedClient = null)
        {
            lock (clientsLock)
            {
                return clients
                    .Where(client => client.IsReady && !Object.ReferenceEquals(client, excludedClient))
                    .ToArray();
            }
        }

        public Task BroadcastAsync(Client sender, string message, CancellationToken cancellationToken)
        {
            return BroadcastCoreAsync(sender, message, false, cancellationToken);
        }

        public async Task BroadcastBatchAsync(
            Client sender,
            IReadOnlyList<string> messages,
            CancellationToken cancellationToken)
        {
            if (messages == null || messages.Count == 0)
                return;

            await broadcastLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task[] deliveries;
            try
            {
                Client[] recipients = GetAuthenticatedClients(sender);
                EncodedFrameBatch frames = recipients.Length == 0 ? null : EncodedFrameBatch.Encode(messages);
                deliveries = new Task[recipients.Length];
                for (int index = 0; index < recipients.Length; index++)
                    deliveries[index] = SendSafelyAsync(recipients[index], frames, cancellationToken);
            }
            finally
            {
                broadcastLock.Release();
            }

            await Task.WhenAll(deliveries).ConfigureAwait(false);
        }

        public Task BroadcastSnakeAsync(Client sender, string message, CancellationToken cancellationToken)
        {
            return BroadcastCoreAsync(sender, message, true, cancellationToken);
        }

        internal async Task BroadcastSnakeProfileAsync(Client sender, SnakeProfile profile, CancellationToken token)
        {
            await broadcastLock.WaitAsync(token).ConfigureAwait(false);
            Task[] deliveries;
            try
            {
                Client[] recipients = GetAuthenticatedClients(sender).Where(HasSnakeProfile).ToArray();
                string modernText = profile.Enabled ? SnakeProtocol.CreateSet(sender.Nickname, profile) : SnakeProtocol.CreateRemove(sender.Nickname);
                var modern = EncodedFrameBatch.Encode(new[] { modernText });
                var legacy = profile.Nickname && profile.Enabled ? EncodedFrameBatch.Encode(new[] { SnakeProtocol.CreateSet(sender.Nickname, profile, false) }) : modern;
                deliveries = recipients.Select(recipient => SendSafelyAsync(recipient, recipient.SupportsNicknameSnake ? modern : legacy, token)).ToArray();
            }
            finally { broadcastLock.Release(); }
            await Task.WhenAll(deliveries).ConfigureAwait(false);
        }

        internal Task SendSystemMessageToAsync(
            Client recipient,
            SystemMessageKind kind,
            string argument,
            CancellationToken cancellationToken)
        {
            if (recipient == null)
                throw new ArgumentNullException(nameof(recipient));
            return recipient.SendAsync(SystemMessageProtocol.Create(kind, argument), cancellationToken);
        }

        public async Task<bool> KickAsync(
            string nickname,
            string reason,
            CancellationToken cancellationToken)
        {
            Client target;
            lock (clientsLock)
            {
                target = clients.FirstOrDefault(client =>
                    client.IsAuthenticated &&
                    String.Equals(client.Nickname, nickname, StringComparison.OrdinalIgnoreCase));
            }

            if (target == null)
                return false;

            try
            {
                await SendSystemMessageToAsync(
                    target,
                    SystemMessageKind.Kicked,
                    reason,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                RemoveClient(target);
            }

            return true;
        }

        private async Task BroadcastCoreAsync(
            Client sender,
            string message,
            bool snakeProfilesOnly,
            CancellationToken cancellationToken)
        {
            await broadcastLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task[] deliveries;
            try
            {
                Client[] recipients;
                lock (clientsLock)
                {
                    recipients = clients
                        .Where(client =>
                            client.IsReady &&
                            !Object.ReferenceEquals(client, sender) &&
                            (!snakeProfilesOnly || HasSnakeProfile(client)))
                        .ToArray();
                }

                EncodedFrameBatch frames = recipients.Length == 0 ? null : EncodedFrameBatch.Encode(new[] { message });
                deliveries = recipients
                    .Select(recipient => SendSafelyAsync(recipient, frames, cancellationToken))
                    .ToArray();
            }
            finally
            {
                broadcastLock.Release();
            }

            await Task.WhenAll(deliveries).ConfigureAwait(false);
        }

        private static bool HasSnakeProfile(Client client)
        {
            SnakeProfile profile;
            return client.TryGetSnakeProfile(out profile);
        }

        public void DisconnectAll()
        {
            Client[] snapshot;
            lock (clientsLock)
            {
                snapshot = clients.ToArray();
                clients.Clear();
            }

            foreach (Client client in snapshot)
                client.Close();
        }

        private async Task SendSafelyAsync(Client recipient, EncodedFrameBatch frames, CancellationToken cancellationToken)
        {
            try
            {
                await recipient.SendEncodedAsync(frames, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                RemoveClient(recipient);
            }
        }
    }
}
