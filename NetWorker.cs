using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SharpOpenNat;

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
        private static INatDevice mappedDevice;
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
            return NicknamePolicy.IsValid(name);
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
                name = ConsoleInput.ReadLine();
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

        internal static Func<string> CapturePortMappingStatus()
        {
            lock (natStatusLock)
            {
                TextId id = natStatusTextId; object[] args = (object[])natStatusArguments.Clone();
                return () => Lang.Get(id, args);
            }
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
            INatDevice device;

            try
            {
                using (var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    discoveryCancellation.CancelAfter(TimeSpan.FromSeconds(NatDiscoveryTimeoutSeconds));
                    device = await OpenNat.Discoverer
                        .DiscoverDeviceAsync(mapper, discoveryCancellation.Token)
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

            int[] lifetimes = new[] { RenewableMappingLifetimeSeconds };
            string lastError = null;

            foreach (int lifetime in lifetimes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Mapping mapping = new Mapping(Protocol.Tcp, port, port, lifetime, "TCPTunnel");

                try
                {
                    await device.CreatePortMapAsync(mapping, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { await device.DeletePortMapAsync(mapping, CancellationToken.None).ConfigureAwait(false); } catch { }
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
            INatDevice device,
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

                        await device.CreatePortMapAsync(mapping, cancellationToken).ConfigureAwait(false);
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
                    await mappedDevice.DeletePortMapAsync(activeMapping, CancellationToken.None).ConfigureAwait(false);
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

        public static (bool Reachable, string Milliseconds) ping(string ip, int port, int timeout = 2000)
            => PingAsync(ip, port, timeout).GetAwaiter().GetResult();

        public static async Task<(bool Reachable, string Milliseconds)> PingAsync(string ip, int port, int timeout = 2000)
        {
            if (String.IsNullOrWhiteSpace(ip) || port < 1 || port > 65535 || timeout < 1)
                return (false, null);

            try
            {
                using (var client = new TcpClient())
                using (var cancellation = new CancellationTokenSource(timeout))
                {
                    var stopwatch = Stopwatch.StartNew();
                    await client.ConnectAsync(ip, port, cancellation.Token).ConfigureAwait(false);
                    return (true, stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (SocketException)
            {
                return (false, null);
            }
            catch (Exception)
            {
                return (false, null);
            }
        }

        public static async Task ServerClientLoopAsync(Client client, CancellationToken serverCancellationToken)
        {
            var secureHub = ServerInterface.SecureHub;
            if (secureHub != null)
            {
                await secureHub.RunAsync(client, serverCancellationToken).ConfigureAwait(false);
                return;
            }
            bool authenticated = false;
            string authenticatedNickname = null;
            var animationAssembler = new ImageAnimationAssembler();
            using var diagnosticsCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
            Task diagnosticsHeartbeat = null;

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

                    if (!client.PromoteAdmission())
                    {
                        await client.SendAsync(AUTH_ERROR_MESSAGE + ":HUB_FULL", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if ((ServerInterface.IsRunning && client.Transport != ChatTransport.Local && (NicknamePolicy.Same(requestedNickname, ServerInterface.AdministratorNickname) || NicknamePolicy.Same(requestedNickname, ServerInterface.AdministratorOriginalNickname))) ||
                        !broadcaster.TryReserveNickname(client, requestedNickname))
                    {
                        await client.SendAsync(AUTH_ERROR_MESSAGE + ":NICKNAME_TAKEN", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    authenticated = true;
                    authenticatedNickname = requestedNickname;
                    client.IpAddress = client.ObservedIpAddress;
                    await client.SendAsync(AUTH_OK_MESSAGE, serverCancellationToken).ConfigureAwait(false);
                }

                if (!broadcaster.CompleteAuthenticationWithRoster(client, serverCancellationToken, out Task rosterDelivery))
                    return;
                await rosterDelivery.ConfigureAwait(false);
                if (client.Transport == ChatTransport.Local) ServerInterface.RenameAdministrator(client, client.Nickname);

                await broadcaster.BroadcastAsync(
                    null,
                    SystemMessageProtocol.Create(SystemMessageKind.UserJoined, authenticatedNickname),
                    serverCancellationToken).ConfigureAwait(false);

                while (!serverCancellationToken.IsCancellationRequested)
                {
                    string message = await animationAssembler.ReadNextAsync(client.Stream, serverCancellationToken).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(message))
                        continue;

                    if (FileTransferProtocol.IsControl(message))
                    {
                        await FileTransferRouter.RouteAsync(client, message, serverCancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (message == SnakeProtocol.Hello)
                    {
                        if (!client.TryConsumeControlToken() || client.SupportsNicknameSnake) continue;
                        client.InitialNicknameSnakeSync = client.TryGetSnakeProfile(out _);
                        client.SupportsNicknameSnake = true;
                        await client.SendAsync(SnakeProtocol.Capabilities, serverCancellationToken).ConfigureAwait(false);
                        foreach (Client participant in broadcaster.GetAuthenticatedClients(client))
                            if (participant.TryGetSnakeProfile(out var existing) && existing.Enabled)
                                await client.SendAsync(SnakeProtocol.CreateSet(participant.Nickname, existing), serverCancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (WhoisProtocol.IsControl(message))
                    {
                        await WhoisServer.HandleAsync(client, message, serverCancellationToken).ConfigureAwait(false);
                        if (client.Diagnostics.Supported && diagnosticsHeartbeat == null)
                            diagnosticsHeartbeat = WhoisServer.HeartbeatAsync(client, diagnosticsCancellation.Token);
                        continue;
                    }
                    if (NicknameProtocol.IsControl(message))
                    {
                        if (client.SupportsNicknameSnake && client.TryConsumeControlToken() && !animationAssembler.IsActive &&
                            NicknameProtocol.TryRequest(message, out string renameId, out string renameName))
                        {
                            await broadcaster.RenameAsync(client, renameId, renameName, serverCancellationToken).ConfigureAwait(false);
                            authenticatedNickname = client.Nickname;
                        }
                        continue;
                    }
                    if (HubStatusProtocol.IsControl(message))
                    {
                        if (message == FileTransferProtocol.Probe)
                        {
                            if (!client.SupportsFileTransfer)
                            {
                                client.SupportsFileTransfer = true;
                                await client.SendAsync(FileTransferProtocol.Capabilities, serverCancellationToken).ConfigureAwait(false);
                            }
                            continue;
                        }
                        if (message == PrivacyProtocol.Ack) { client.IpDisclosureAllowed = true; continue; }
                        if (message.Length > 256 || !client.TryConsumeControlToken()) continue;
                        if (PrivacyProtocol.TryRequest(message, out string privacyId))
                            await client.SendAsync(PrivacyProtocol.Reply(privacyId, client.HideClientIpAddresses), serverCancellationToken).ConfigureAwait(false);
                        else if (message == HubStatusProtocol.Hello)
                        {
                            client.SupportsHubStatus = true;
                            await client.SendAsync(HubStatusProtocol.Capabilities, serverCancellationToken).ConfigureAwait(false);
                        }
                        else if (client.SupportsHubStatus && HubStatusProtocol.TryParseRequest(message, out string requestId, out bool modernStatus))
                        {
                            await client.SendAsync(HubStatusProtocol.CreateReply(requestId,
                                ServerInterface.AdministratorNickname, broadcaster.AuthenticatedClientCount, modernStatus && client.SupportsNicknameSnake && IsNicknameValid(ServerInterface.AdministratorOriginalNickname) ? ServerInterface.AdministratorOriginalNickname : null), serverCancellationToken).ConfigureAwait(false);
                        }
                        continue;
                    }

                    if (ImageAnimationProtocol.IsAnimationControlMessage(message))
                    {
                        ImageAnimationControlFrame control;
                        if (!ImageAnimationProtocol.TryParseClient(message, out control))
                        {
                            animationAssembler.Reset();
                            await broadcaster.SendSystemMessageToAsync(
                                client,
                                SystemMessageKind.InvalidImage,
                                null,
                                serverCancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (control.Kind == ImageAnimationControlKind.Begin && !animationAssembler.IsActive &&
                            !client.TryConsumeImageToken())
                        {
                            await broadcaster.SendSystemMessageToAsync(
                                client,
                                SystemMessageKind.TooManyImages,
                                null,
                                serverCancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        AnimatedImagePacket animationPacket;
                        ImageAnimationAssemblyResult assemblyResult =
                            animationAssembler.Accept(control, out animationPacket);
                        if (assemblyResult == ImageAnimationAssemblyResult.Invalid)
                        {
                            await broadcaster.SendSystemMessageToAsync(
                                client,
                                SystemMessageKind.InvalidImage,
                                null,
                                serverCancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (assemblyResult == ImageAnimationAssemblyResult.Completed)
                        {
                            client.Diagnostics.CountMessage();
                            string[] transfer = ImageAnimationProtocol.CreateServerTransfer(
                                animationPacket,
                                authenticatedNickname);
                            await broadcaster.BroadcastBatchAsync(
                                client,
                                transfer,
                                serverCancellationToken).ConfigureAwait(false);
                        }
                        continue;
                    }

                    if (ImageProtocol.IsImageControlMessage(message))
                    {
                        if (!client.TryConsumeImageToken())
                        {
                            await broadcaster.SendSystemMessageToAsync(
                                client,
                                SystemMessageKind.TooManyImages,
                                null,
                                serverCancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        ImagePacket imagePacket;
                        if (!ImageProtocol.TryParseClientFrame(message, out imagePacket))
                        {
                            await broadcaster.SendSystemMessageToAsync(
                                client,
                                SystemMessageKind.InvalidImage,
                                null,
                                serverCancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        string imageFrame = ImageProtocol.CreateServerFrame(imagePacket, authenticatedNickname);
                        client.Diagnostics.CountMessage();
                        await broadcaster.BroadcastAsync(client, imageFrame, serverCancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (message.Length > MessageProtocol.MaxMessageCharacters)
                    {
                        await broadcaster.SendSystemMessageToAsync(
                            client,
                            SystemMessageKind.MessageTooLong,
                            null,
                            serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (!client.TryConsumeMessageToken())
                    {
                        await broadcaster.SendSystemMessageToAsync(
                            client,
                            SystemMessageKind.TooManyMessages,
                            null,
                            serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    SnakeProfile snakeProfile;
                    if (SnakeProtocol.TryParseClientProfile(message, out snakeProfile))
                    {
                        if (snakeProfile.Nickname && !client.SupportsNicknameSnake) continue;
                        await SynchronizeSnakeProfileAsync(client, snakeProfile, serverCancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (SnakeProtocol.IsSnakeControlMessage(message))
                        continue;
                    if (LegacyEventProtocol.IsControlMessage(message))
                        continue;

                    client.Diagnostics.CountMessage();
                    await broadcaster.BroadcastAsync(client, $"[{authenticatedNickname}]: {message}", serverCancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (TimeoutException)
            {
            }
            catch (Exception)
            {
            }
            finally
            {
                diagnosticsCancellation.Cancel();
                broadcaster.RemoveClient(client);
                if (diagnosticsHeartbeat != null) await diagnosticsHeartbeat.ConfigureAwait(false);
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
            SnakeProfile oldBase = previousProfile; oldBase.Nickname = profile.Nickname;
            bool initialCapabilitySync = client.InitialNicknameSnakeSync && SnakeProtocol.SameConfiguration(oldBase, profile);
            client.InitialNicknameSnakeSync = false;
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
                        SnakeProtocol.CreateSet(participant.Nickname, participantProfile, client.SupportsNicknameSnake),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await broadcaster.BroadcastSnakeProfileAsync(client, profile, cancellationToken).ConfigureAwait(false);
            if (!isFirstProfile && !initialCapabilitySync && !SnakeProtocol.SameConfiguration(previousProfile, profile))
                await broadcaster.BroadcastAsync(null, SystemMessageProtocol.Create(SystemMessageKind.SnakeChanged, client.Nickname), cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string> ReadStringWithTimeoutAsync(Client client, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            Task<string> readTask = MessageProtocol.ReadStringAsync(client.Stream, cancellationToken);
            try
            {
                return await readTask.WaitAsync(
                    TimeSpan.FromMilliseconds(timeoutMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                client.Close();
                try { await readTask.ConfigureAwait(false); } catch { }
                throw new TimeoutException(Lang.Get(TextId.AuthTimedOut));
            }
        }
    }
}
