using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Open.Nat;

namespace TCPTunnel
{
    public class NetWorker
    {
        private const int AuthenticationTimeoutMilliseconds = 7000;
        private const int NatDiscoveryTimeoutSeconds = 5;
        private const int RenewableMappingLifetimeSeconds = 3600;

        private static readonly ConsoleGraphic graphic = new ConsoleGraphic();
        private static readonly SemaphoreSlim natLock = new SemaphoreSlim(1, 1);
        private static readonly object natStatusLock = new object();
        private static NatDevice mappedDevice;
        private static Mapping activeMapping;
        private static string activeMappingProtocol;
        private static Task mappingRenewalTask = Task.CompletedTask;
        private static TextId natStatusTextId = TextId.NatNotStarted;
        private static object[] natStatusArguments = new object[0];

        public const string DO_AUTH_MESSAGE = "DoAuth()";
        public const string AUTH_OK_MESSAGE = "AuthOk()";
        public const string AUTH_ERROR_MESSAGE = "AuthError()";
        public static readonly Broadcaster broadcaster = new Broadcaster();

        public static volatile bool connected;
        public static string nickname = "";

        public static bool IsNicknameValid(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 20 || name.Length < 3)
                return false;

            const string illegalCharacters = @"!@#$%^&*()[]{}+,/\|";
            foreach (char character in name)
            {
                if (Char.IsWhiteSpace(character) || Char.IsControl(character) || illegalCharacters.IndexOf(character) >= 0)
                    return false;
            }

            return true;
        }

        public static string filterNick(string name, bool force = false)
        {
            while (!IsNicknameValid(name))
            {
                if (force)
                    return null;

                Program.matrix(Lang.Get(TextId.AuthInvalidNickname), 20, ConsoleColor.DarkRed);
                graphic.Clear();
                Program.matrix(Lang.Get(TextId.EnterYourNickname), 20, ConsoleColor.DarkYellow);
                name = Console.ReadLine();
                if (name == null)
                    return null;
            }
            return name;
        }

        internal static string GetPortMappingStatus()
        {
            lock (natStatusLock)
                return Lang.Get(natStatusTextId, natStatusArguments);
        }

        internal static string SetPortMappingStatus(TextId textId, params object[] arguments)
        {
            lock (natStatusLock)
            {
                natStatusTextId = textId;
                natStatusArguments = arguments == null ? new object[0] : (object[])arguments.Clone();
                return Lang.Get(natStatusTextId, natStatusArguments);
            }
        }

        public async static Task<string> TryOpenPortAsync(int port, CancellationToken cancellationToken)
        {
            try
            {
                await natLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string upnpError = await TryCreatePortMappingAsync(
                        PortMapper.Upnp,
                        port,
                        cancellationToken).ConfigureAwait(false);
                    if (upnpError == null)
                        return SetPortMappingStatus(TextId.NatPortMapped, activeMappingProtocol, port);

                    string pmpError = await TryCreatePortMappingAsync(
                        PortMapper.Pmp,
                        port,
                        cancellationToken).ConfigureAwait(false);
                    if (pmpError == null)
                        return SetPortMappingStatus(TextId.NatPortMappedRenewable, activeMappingProtocol, port);

                    return SetPortMappingStatus(TextId.NatFailed, upnpError, pmpError);
                }
                finally
                {
                    natLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return cancellationToken.IsCancellationRequested
                    ? SetPortMappingStatus(TextId.NatCancelled)
                    : SetPortMappingStatus(TextId.NatRouterTimeout);
            }
            catch (Exception ex)
            {
                return SetPortMappingStatus(TextId.NatUnavailable, DescribeNatException(ex));
            }
        }

        private static async Task<string> TryCreatePortMappingAsync(
            PortMapper mapper,
            int port,
            CancellationToken cancellationToken)
        {
            string protocolName = mapper == PortMapper.Upnp ? "UPnP" : "NAT-PMP";
            NatDevice device;

            try
            {
                var discoverer = new NatDiscoverer();
                using (var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    discoveryCancellation.CancelAfter(TimeSpan.FromSeconds(NatDiscoveryTimeoutSeconds));
                    device = await discoverer
                        .DiscoverDeviceAsync(mapper, discoveryCancellation)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Lang.Get(TextId.NatDeviceNotFound, NatDiscoveryTimeoutSeconds);
            }
            catch (Exception ex)
            {
                return DescribeNatException(ex);
            }

            int[] lifetimes = mapper == PortMapper.Upnp
                ? new[] { 0, RenewableMappingLifetimeSeconds }
                : new[] { RenewableMappingLifetimeSeconds };
            string lastError = null;

            foreach (int lifetime in lifetimes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Mapping mapping = new Mapping(Protocol.Tcp, port, port, lifetime, "TCPTunnel");

                try
                {
                    await device.CreatePortMapAsync(mapping).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { await device.DeletePortMapAsync(mapping).ConfigureAwait(false); } catch { }
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    mappedDevice = device;
                    activeMapping = mapping;
                    activeMappingProtocol = protocolName;
                    if (lifetime > 0)
                        mappingRenewalTask = RenewPortMappingAsync(device, mapping, cancellationToken);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lastError = Lang.Get(TextId.NatRuleTimeout);
                }
                catch (Exception ex)
                {
                    lastError = DescribeNatException(ex);
                }
            }

            return lastError ?? Lang.Get(TextId.NatRuleRejected);
        }

        private static async Task RenewPortMappingAsync(
            NatDevice device,
            Mapping mapping,
            CancellationToken cancellationToken)
        {
            int nextDelaySeconds = Math.Max(60, mapping.Lifetime / 2);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(nextDelaySeconds), cancellationToken).ConfigureAwait(false);
                    await natLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (!Object.ReferenceEquals(mappedDevice, device) || !Object.ReferenceEquals(activeMapping, mapping))
                            return;

                        await device.CreatePortMapAsync(mapping).ConfigureAwait(false);
                        nextDelaySeconds = Math.Max(60, mapping.Lifetime / 2);
                    }
                    catch
                    {
                        nextDelaySeconds = 60;
                    }
                    finally
                    {
                        natLock.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static string DescribeNatException(Exception exception)
        {
            Exception current = exception;
            while (current != null)
            {
                MappingException mappingException = current as MappingException;
                if (mappingException != null && mappingException.ErrorCode != 0)
                    return Lang.Get(TextId.NatError, mappingException.ErrorCode, mappingException.ErrorText);
                current = current.InnerException;
            }

            return exception.Message;
        }

        public async static Task<string> TryClosePortAsync()
        {
            await natLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (mappedDevice == null || activeMapping == null)
                    return SetPortMappingStatus(TextId.NatNoActiveRule);

                try
                {
                    await mappedDevice.DeletePortMapAsync(activeMapping).ConfigureAwait(false);
                    return SetPortMappingStatus(TextId.NatPortClosed, activeMappingProtocol ?? "NAT", activeMapping.PublicPort);
                }
                catch (Exception ex)
                {
                    return SetPortMappingStatus(TextId.NatDeleteFailed, DescribeNatException(ex));
                }
                finally
                {
                    mappedDevice = null;
                    activeMapping = null;
                    activeMappingProtocol = null;
                }
            }
            finally
            {
                natLock.Release();
            }
        }

        public static bool ping(string ip, int port, int timeout = 2000)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    IAsyncResult result = client.BeginConnect(ip, port, null, null);
                    using (result.AsyncWaitHandle)
                    {
                        if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeout)))
                            return false;
                    }

                    client.EndConnect(result);
                    return true;
                }
            }
            catch (SocketException)
            {
                return false;
            }
            catch (Exception ex)
            {
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.UnexpectedError, ex.Message));
                return false;
            }
        }

        public static async Task ServerClientLoopAsync(Client client, CancellationToken serverCancellationToken)
        {
            bool authenticated = false;
            string authenticatedNickname = null;

            try
            {
                {
                    await client.SendAsync(DO_AUTH_MESSAGE, serverCancellationToken).ConfigureAwait(false);
                    string authReply = await ReadStringWithTimeoutAsync(client, AuthenticationTimeoutMilliseconds, serverCancellationToken).ConfigureAwait(false);
                    if (!authReply.StartsWith("REPLY:", StringComparison.Ordinal))
                    {
                        await client.SendAsync(AUTH_ERROR_MESSAGE + ":INVALID_REQUEST", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    string requestedNickname = authReply.Substring("REPLY:".Length).Trim();
                    if (!IsNicknameValid(requestedNickname))
                    {
                        await client.SendAsync(AUTH_ERROR_MESSAGE + ":INVALID_NICKNAME", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (!broadcaster.TryAuthenticate(client, requestedNickname))
                    {
                        await client.SendAsync(AUTH_ERROR_MESSAGE + ":NICKNAME_TAKEN", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    authenticated = true;
                    authenticatedNickname = requestedNickname;
                    IPEndPoint remoteEndPoint = client.TcpClient.Client.RemoteEndPoint as IPEndPoint;
                    client.IpAddress = remoteEndPoint == null ? "unknown" : remoteEndPoint.Address.ToString();
                    await client.SendAsync(AUTH_OK_MESSAGE, serverCancellationToken).ConfigureAwait(false);
                }

                await broadcaster.BroadcastAsync(
                    null,
                    SystemMessageProtocol.Create(SystemMessageKind.UserJoined, authenticatedNickname),
                    serverCancellationToken).ConfigureAwait(false);

                while (!serverCancellationToken.IsCancellationRequested)
                {
                    string message = await MessageProtocol.ReadStringAsync(client.Stream, serverCancellationToken).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(message))
                        continue;

                    if (message.Length > MessageProtocol.MaxMessageCharacters)
                    {
                        await client.SendAsync(SystemMessageProtocol.Create(SystemMessageKind.MessageTooLong), serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (!client.TryConsumeMessageToken())
                    {
                        await client.SendAsync(SystemMessageProtocol.Create(SystemMessageKind.TooManyMessages), serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    SnakeProfile snakeProfile;
                    if (SnakeProtocol.TryParseClientProfile(message, out snakeProfile))
                    {
                        await SynchronizeSnakeProfileAsync(client, snakeProfile, serverCancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (SnakeProtocol.IsSnakeControlMessage(message))
                        continue;

                    await broadcaster.BroadcastAsync(client, $"[{authenticatedNickname}]: {message}", serverCancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Hub остановлен или истёк таймаут авторизации.
            }
            catch (EndOfStreamException)
            {
                // Клиент штатно закрыл соединение.
            }
            catch (IOException)
            {
                // Некорректный пакет или оборванное соединение.
            }
            catch (SocketException)
            {
                // Соединение оборвалось.
            }
            catch (ObjectDisposedException)
            {
                // Соединение закрыто при остановке Hub.
            }
            catch (TimeoutException)
            {
                // Клиент не завершил авторизацию вовремя.
            }
            catch (Exception)
            {
                // Ошибка конкретного клиента не должна останавливать Hub.
            }
            finally
            {
                broadcaster.RemoveClient(client);
                if (authenticated && !serverCancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await broadcaster.BroadcastSnakeAsync(
                            null,
                            SnakeProtocol.CreateRemove(authenticatedNickname),
                            CancellationToken.None).ConfigureAwait(false);
                        await broadcaster.BroadcastAsync(
                            null,
                            SystemMessageProtocol.Create(SystemMessageKind.UserLeft, authenticatedNickname),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }

        private static async Task SynchronizeSnakeProfileAsync(
            Client client,
            SnakeProfile profile,
            CancellationToken cancellationToken)
        {
            SnakeProfile previousProfile;
            bool isFirstProfile = !client.TryGetSnakeProfile(out previousProfile);
            client.UpdateSnakeProfile(profile);

            if (isFirstProfile)
            {
                Client[] participants = broadcaster.GetAuthenticatedClients(client);
                foreach (Client participant in participants)
                {
                    SnakeProfile participantProfile;
                    if (!participant.TryGetSnakeProfile(out participantProfile) || !participantProfile.Enabled)
                        continue;

                    await client.SendAsync(
                        SnakeProtocol.CreateSet(participant.Nickname, participantProfile),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            string update = profile.Enabled
                ? SnakeProtocol.CreateSet(client.Nickname, profile)
                : SnakeProtocol.CreateRemove(client.Nickname);
            await broadcaster.BroadcastSnakeAsync(client, update, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string> ReadStringWithTimeoutAsync(Client client, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            Task<string> readTask = MessageProtocol.ReadStringAsync(client.Stream, cancellationToken);
            using (var timeoutCancellation = new CancellationTokenSource())
            {
                Task timeoutTask = Task.Delay(timeoutMilliseconds, timeoutCancellation.Token);
                Task completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
                if (completed == readTask)
                {
                    timeoutCancellation.Cancel();
                    return await readTask.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                client.Close();
                try { await readTask.ConfigureAwait(false); } catch { }
                throw new TimeoutException(Lang.Get(TextId.AuthTimedOut));
            }
        }
    }
}
