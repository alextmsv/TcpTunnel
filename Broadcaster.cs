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
                    return clients.Count(client => client.IsAuthenticated);
            }
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
            lock (clientsLock)
            {
                bool nicknameTaken = clients.Any(existing =>
                    existing.IsAuthenticated &&
                    !Object.ReferenceEquals(existing, client) &&
                    String.Equals(existing.Nickname, nickname, StringComparison.OrdinalIgnoreCase));

                if (nicknameTaken)
                    return false;

                client.Nickname = nickname;
                client.IsAuthenticated = true;
                return true;
            }
        }

        public bool RemoveClient(Client client)
        {
            bool removed;
            lock (clientsLock)
            {
                removed = clients.Remove(client);
            }

            client.Close();
            return removed;
        }

        public Client[] GetAuthenticatedClients(Client excludedClient = null)
        {
            lock (clientsLock)
            {
                return clients
                    .Where(client => client.IsAuthenticated && !Object.ReferenceEquals(client, excludedClient))
                    .ToArray();
            }
        }

        public Task BroadcastAsync(Client sender, string message, CancellationToken cancellationToken)
        {
            return BroadcastCoreAsync(sender, message, false, cancellationToken);
        }

        public Task BroadcastSnakeAsync(Client sender, string message, CancellationToken cancellationToken)
        {
            return BroadcastCoreAsync(sender, message, true, cancellationToken);
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
            try
            {
                Client[] recipients;
                lock (clientsLock)
                {
                    recipients = clients
                        .Where(client =>
                            client.IsAuthenticated &&
                            !Object.ReferenceEquals(client, sender) &&
                            (!snakeProfilesOnly || HasSnakeProfile(client)))
                        .ToArray();
                }

                Task[] deliveries = recipients
                    .Select(recipient => SendSafelyAsync(recipient, message, cancellationToken))
                    .ToArray();
                await Task.WhenAll(deliveries).ConfigureAwait(false);
            }
            finally
            {
                broadcastLock.Release();
            }
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

        private async Task SendSafelyAsync(Client recipient, string message, CancellationToken cancellationToken)
        {
            try
            {
                await recipient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                RemoveClient(recipient);
            }
        }
    }
}
