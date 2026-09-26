using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public partial class UserInterface : NetWorker
    {
        private sealed class ChatHistoryEntry
        {
            public ChatHistoryEntry(
                string text,
                ConsoleColor? forcedColor,
                List<MentionSpan> mentions,
                bool useSystemTheme = false)
            {
                textValue = text;
                ForcedColor = forcedColor;
                Mentions = mentions ?? new List<MentionSpan>();
                UseSystemTheme = useSystemTheme;
            }

            public ChatHistoryEntry(FrozenImage image)
            {
                Image = image ?? throw new ArgumentNullException(nameof(image));
                textValue = (image.IsOutgoing ? "<<<" : ">>>") +
                       " [" + image.Sender + "]: [" + Lang.Get(TextId.ImageLabel) + "]";
                Mentions = new List<MentionSpan>();
            }

            public ChatHistoryEntry(FrozenAnimation animation)
            {
                Animation = animation ?? throw new ArgumentNullException(nameof(animation));
                textValue = (animation.IsOutgoing ? "<<<" : ">>>") +
                       " [" + animation.Sender + "]: [" + Lang.Get(TextId.AnimationLabel) + "]";
                Mentions = new List<MentionSpan>();
                AnimationStartedTimestamp = Stopwatch.GetTimestamp();
            }

            private readonly string textValue;
            public StatusCard Card { get; }
            public ChatHistoryEntry(StatusCard card) : this("", null, null, true) { Card = card; }
            public ChatHistoryEntry(Func<string> dynamicText) : this("", null, null, true) { DynamicText = dynamicText; }
            public Func<string> DynamicText { get; }
            public string Text => Card?.Render(GetContentWidth()) ?? DynamicText?.Invoke() ?? textValue;
            public ConsoleColor? ForcedColor { get; }
            public List<MentionSpan> Mentions { get; }
            public bool UseSystemTheme { get; }
            public WhoisUnreadState WhoisAttention { get; set; }
            public bool WhoisUnread => WhoisAttention?.Unread == true;
            public string[] WhoisRequesters { get; set; }
            public bool WhoisBlink { get; set; }
            public FrozenImage Image { get; }
            public bool IsImage { get { return Image != null; } }
            public FrozenAnimation Animation { get; }
            public bool IsAnimation { get { return Animation != null; } }
            public bool IsVisual { get { return Image != null || Animation != null; } }
            public int AnimationTop { get; set; } = -1;
            public int AnimationFirstSourceRow { get; set; }
            public int AnimationVisibleRows { get; set; }
            public int AnimationVisibleWidth { get; set; }
            public int LastRenderedAnimationFrame { get; set; } = -1;
            public long AnimationStartedTimestamp { get; }
            public char[] AnimationRowBuffer { get; set; }
            public bool MentionsLocalUser
            {
                get { return Mentions.Exists(mention => mention.IsLocalUser); }
            }
        }

        private sealed class MentionSpan
        {
            public int Start;
            public int Length;
            public bool IsLocalUser;
        }

        private sealed class MentionFragment
        {
            public int Left;
            public int Top;
            public string Text;
        }

        private struct ChatTextStyle
        {
            public ConsoleColor Foreground;
            public ConsoleColor Background;

            public bool IsSameAs(ChatTextStyle other)
            {
                return Foreground == other.Foreground && Background == other.Background;
            }
        }

        private const int DefaultConnectionAttempts = 3;
        private const int ConnectionTimeoutMilliseconds = 3000;
        private const int RetryDelayMilliseconds = 1000;
        private const int MaxVisibleInputRows = 3;
        private const int ImageInputRowReservation = 1;
        private const int MaxChatHistoryLines = 200;
        private const int ResizePollMilliseconds = 100;
        private const int ResizeSettleMilliseconds = 180;
        private const int MentionBlinkDelayMilliseconds = 130;
        private const int MentionBlinkCycles = 3;
        private const int MaxMentionSuggestionRows = 4;

        private static readonly ConsoleGraphic graphic = new ConsoleGraphic();
        private static readonly object consoleLock = ConsoleGraphic.borderAnimationLock;
        private static readonly object participantsLock = new object();
        private static readonly StringBuilder inputBuffer = new StringBuilder();
        private static readonly List<ChatHistoryEntry> chatHistory = new List<ChatHistoryEntry>();
        private static readonly HashSet<string> activeParticipants =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SnakeProfile> pendingParticipantSnakes =
            new Dictionary<string, SnakeProfile>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<ChatHistoryEntry> pendingMentionAnimations =
            new HashSet<ChatHistoryEntry>();
        private static int isBusy;
        private static bool inputActive;
        private static int inputCursorIndex;
        private static int inputStartRow;
        private static int renderedInputRows;
        private static int renderedInputLeft;
        private static int renderedInputWidth;
        private static string inputPrompt = "";
        private static bool mentionActive;
        private static string[] mentionSuggestions = Array.Empty<string>();
        private static int mentionSuggestionIndex;
        private static int mentionTokenStart = -1;
        private static List<MentionFragment> activeMentionBlinkFragments;
        private static long mentionBlinkStartTimestamp;
        private static int mentionBlinkLastPaintedPhase = -1;
        private static ConsoleGraphic.ConsoleGeometry knownConsoleGeometry;
        private static ConsoleGraphic.ConsoleGeometry pendingConsoleGeometry;
        private static bool hasKnownConsoleGeometry;
        private static bool consoleResizePending;
        private static bool isLocalHubSession;
        private static bool showServerCard;
        private static string serverCardAddress = "127.0.0.1";
        private static int serverCardPort;
        private static long resizeStableSinceTimestamp;
        private static long nextResizePollTimestamp;
        private static int mentionMonitorActive;
        private static int chatSessionVersion;
        private static int imageHistoryBytes;
        private static int imageHistoryVersion;
        private static ImagePacket lastLargeImagePacket;
        private static AnimatedImagePacket lastLargeAnimationPacket;
        private static int animationMonitorActive;

        private static Task ReceiveMessagesAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken, SessionEndState end, HubStatusSession status) =>
            ReceiveConnectionMessagesAsync(new TcpChatConnection(client), stream, cancellationToken, end, status);

        internal static async Task ReceiveConnectionMessagesAsync(IChatConnection connection, Stream stream, CancellationToken cancellationToken, SessionEndState end, HubStatusSession status)
        {
            var animationAssembler = new ImageAnimationAssembler();
            TransferProgress download = null;
            int downloadFrames = 0;
            Func<string, CancellationToken, Task> sendControl = (frame, token) => MessageProtocol.WriteStringAsync(stream, frame, token);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string message = await MessageProtocol.ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (await status.Whois.ReceiveAsync(message, sendControl, ReceiveWhoisNotice, cancellationToken).ConfigureAwait(false)) continue;
                    if (status.Receive(message)) continue;
                    if (ImageAnimationProtocol.IsAnimationControlMessage(message))
                    {
                        ImageAnimationControlFrame animationControl;
                        AnimatedImagePacket animationPacket;
                        if (!ImageAnimationProtocol.TryParseServer(message, out animationControl) ||
                            animationAssembler.Accept(animationControl, out animationPacket) == ImageAnimationAssemblyResult.Invalid)
                        {
                            animationAssembler.Reset();
                            download?.Remove();
                            download = null;
                            WriteSystemChatLine(Lang.Get(TextId.InvalidImagePacket));
                            continue;
                        }
                        if (animationControl.Kind == ImageAnimationControlKind.Begin)
                        {
                            download?.Remove();
                            string sender = animationControl.Sender;
                            downloadFrames = animationControl.FrameCount;
                            download = new TransferProgress(
                                percent => Lang.Get(TextId.AnimationDownloading, sender, percent),
                                Lang.Get(TextId.AnimationDownloadingPlain, sender));
                        }
                        else if (animationControl.Kind == ImageAnimationControlKind.Frame)
                        {
                            download?.Report(animationControl.FrameIndex + 1, downloadFrames);
                        }
                        if (animationPacket != null)
                        {
                            await ReceiveAnimationAsync(animationPacket).ConfigureAwait(false);
                            download?.Remove();
                            download = null;
                        }
                        continue;
                    }
                    ImagePacket imagePacket;
                    if (ImageProtocol.TryParseServerFrame(message, out imagePacket))
                    {
                        await ReceiveImageAsync(imagePacket).ConfigureAwait(false);
                        continue;
                    }
                    if (ImageProtocol.IsImageControlMessage(message))
                    {
                        WriteSystemChatLine(Lang.Get(TextId.InvalidImagePacket));
                        continue;
                    }
                    if (TryApplySnakeUpdate(message) || SnakeProtocol.IsSnakeControlMessage(message))
                        continue;
                    if (LegacyEventProtocol.IsControlMessage(message))
                        continue;

                    string localizedSystemMessage;
                    SystemMessageKind systemKind;
                    string systemArgument;
                    if (SystemMessageProtocol.TryLocalize(
                        message,
                        out localizedSystemMessage,
                        out systemKind,
                        out systemArgument))
                    {
                        UpdateParticipantState(systemKind, systemArgument);
                        if (systemKind == SystemMessageKind.ParticipantPresent)
                            continue;

                        end.RecordServerReason(systemKind, localizedSystemMessage);
                        if (end.Kind == SessionEndKind.ServerReason)
                        {
                            connected = false;
                            return;
                        }
                        WriteSystemChatLine(">>> " + localizedSystemMessage);
                    }
                    else
                    {
                        WriteChatLine(">>> " + message, null, true);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Сеанс завершён локальным пользователем.
            }
            catch (EndOfStreamException)
            {
                end.RecordFailure(SessionEndKind.ConnectionLost);
            }
            catch (TimeoutException)
            {
                end.RecordFailure(SessionEndKind.ReadTimedOut);
            }
            catch (InvalidDataException)
            {
                end.RecordFailure(SessionEndKind.InvalidFrame);
            }
            catch (System.Text.DecoderFallbackException)
            {
                end.RecordFailure(SessionEndKind.InvalidFrame);
            }
            catch (IOException)
            {
                // Соединение оборвалось.
            }
            catch (SocketException)
            {
                // Соединение оборвалось.
            }
            finally
            {
                download?.Remove();
                end.RecordFailure(SessionEndKind.ConnectionLost);
                connected = false;
                connection.Dispose();
                WindowAttention.StopFlashing();
            }
        }

        public static bool TryConnect()
        {
            if (!EnsureNickname())
                return false;

            BluetoothConnectOutcome bluetooth = TryConnectBluetooth();
            if (bluetooth == BluetoothConnectOutcome.Handled)
                return true;
            if (bluetooth == BluetoothConnectOutcome.Cancelled)
                return false;

            string defaultHost = ApplicationSettings.LastHost;
            int defaultPort = ApplicationSettings.LastPort;
            Program.matrix(Lang.Get(TextId.EnterServerAddressSaved, defaultHost));
            string ip = Console.ReadLine();
            if (String.IsNullOrWhiteSpace(ip))
                ip = defaultHost;

            Program.matrix(Lang.Get(TextId.EnterServerPortSaved, defaultPort));
            string rawPort = Console.ReadLine();
            int serverPort;
            if (String.IsNullOrWhiteSpace(rawPort))
                serverPort = defaultPort;
            else if (!Int32.TryParse(rawPort, out serverPort) || serverPort < 1 || serverPort > 65535)
            {
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.InvalidPortNumber));
                return false;
            }

            return DoConnect(ip, serverPort, DefaultConnectionAttempts);
        }

        public static bool DoConnect(string address, int port, int attempts = DefaultConnectionAttempts)
        {
            if (String.IsNullOrWhiteSpace(address))
            {
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.MissingServerAddress));
                return false;
            }

            if (port < 1 || port > 65535)
            {
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.InvalidPortNumber));
                return false;
            }

            if (!EnsureNickname())
                return false;

            if (Interlocked.CompareExchange(ref isBusy, 1, 0) != 0)
            {
                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.ConnectionInProgress));
                return false;
            }

            attempts = Math.Max(1, attempts);
            try
            {
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    TcpClient client = new TcpClient();
                    string error;
                    if (ConsoleGraphic.Enabled)
                    {
                        ConsoleGraphic.WriteBottomStatus(
                            Lang.Get(TextId.ConnectingCompact, address, port, attempt, attempts),
                            ConsoleTheme.SystemText,
                            ServerInterface.IsRunning ? 3 : 0);
                    }
                    else
                    {
                        ConsoleGraphic.WriteContentLine(Lang.Get(TextId.ConnectingAttempt, address, port, attempt, attempts));
                    }

                    if (TryOpenConnection(client, address, port, out error))
                    {
                        ApplicationSettings.RememberEndpoint(address, port);
                        try
                        {
                            RunClient(client, address);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            client.Close();
                            if (ConsoleGraphic.Enabled)
                                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.SessionStartFailed, ex.Message), ConsoleTheme.SystemText);
                            else
                                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.SessionStartFailed, ex.Message));
                            return false;
                        }
                    }

                    client.Close();
                    if (ConsoleGraphic.Enabled)
                        ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ConnectFailed, error), ConsoleTheme.SystemText);
                    else
                        ConsoleGraphic.WriteContentLine(Lang.Get(TextId.ConnectFailed, error));
                    if (attempt < attempts)
                        Thread.Sleep(RetryDelayMilliseconds);
                }

                if (ConsoleGraphic.Enabled)
                {
                    ConsoleGraphic.WriteBottomStatus(
                        Lang.Get(TextId.HubUnavailableCompact, address, port),
                        ConsoleTheme.SystemText);
                }
                else
                {
                    ConsoleGraphic.WriteContentLine(Lang.Get(TextId.HubUnavailableAttempts, address, port, attempts));
                }
                return false;
            }
            finally
            {
                connected = false;
                Interlocked.Exchange(ref isBusy, 0);
            }
        }

        internal static bool EnsureNickname()
        {
            if (IsNicknameValid(nickname))
                return true;

            Program.matrix(Lang.Get(TextId.EnterYourNickname));
            nickname = filterNick(Console.ReadLine());
            return IsNicknameValid(nickname);
        }

        private static bool TryOpenConnection(TcpClient client, string address, int port, out string error)
        {
            try
            {
                IAsyncResult result = client.BeginConnect(address, port, null, null);
                using (result.AsyncWaitHandle)
                {
                    if (!result.AsyncWaitHandle.WaitOne(ConnectionTimeoutMilliseconds))
                    {
                        error = Lang.Get(TextId.ConnectionTimedOut);
                        return false;
                    }
                }

                client.EndConnect(result);
                client.NoDelay = true;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void RunClient(TcpClient client, string host)
        {
            using var connection = new TcpChatConnection(client);
            RunClientSession(connection, client.Client.RemoteEndPoint as IPEndPoint, host);
        }

        private static void RunClientSession(IChatConnection connection, IPEndPoint remoteEndPoint, string host, bool localHub = false)
        {
            Stream stream = connection.Stream;
            using (var authCancellation = new CancellationTokenSource())
            {
                CancellationToken authToken = authCancellation.Token;
                string authRequest = ReadWithTimeoutAsync(connection, stream, authToken).GetAwaiter().GetResult();
                if (!DO_AUTH_MESSAGE.Equals(authRequest, StringComparison.Ordinal))
                    throw new IOException(Lang.Get(TextId.UnknownAuthProtocol));

                MessageProtocol.WriteStringAsync(stream, "REPLY:" + nickname, authToken).GetAwaiter().GetResult();
                string authResult = ReadWithTimeoutAsync(connection, stream, authToken).GetAwaiter().GetResult();
                if (!AUTH_OK_MESSAGE.Equals(authResult, StringComparison.Ordinal))
                    throw new IOException(LocalizeAuthenticationError(authResult));
                SnakeProfile localSnakeProfile = new SnakeProfile
                {
                    Enabled = ConsoleGraphic.Enabled,
                    Paused = ConsoleGraphic.BorderSnakePaused,
                    DelayMilliseconds = ConsoleGraphic.CurrentBorderSnakeReferenceDelayMilliseconds,
                    Color = ConsoleGraphic.BorderSnakeColor,
                    Step = ConsoleGraphic.CurrentBorderSnakeReferenceStep,
                    Glyph = ConsoleGraphic.BorderSnakeGlyph
                };
                MessageProtocol.WriteStringAsync(
                    stream,
                    SnakeProtocol.CreateClientProfile(localSnakeProfile),
                    authToken).GetAwaiter().GetResult();
            }

            connected = true;
            currentTransport = connection.Transport;
            ConsoleGraphic.ClearRemoteSnakes();
            isLocalHubSession = ServerInterface.IsRunning &&
                                (localHub ||
                                 (remoteEndPoint != null &&
                                  remoteEndPoint.Port == ServerInterface.ListeningPort &&
                                  NetworkAddressResolver.IsLoopback(remoteEndPoint.Address)));
            bool bluetoothOnlyHub = isLocalHubSession && !ServerInterface.Options.UsesTcp;
            showServerCard = ConsoleGraphic.Enabled && (remoteEndPoint != null || bluetoothOnlyHub);
            serverCardAddress = bluetoothOnlyHub
                ? Lang.Get(TextId.BluetoothEndpoint)
                : isLocalHubSession
                    ? ServerInterface.DisplayAddress
                    : (remoteEndPoint == null
                        ? "?"
                        : NetworkAddressResolver.NormalizeAddressText(remoteEndPoint.Address));
            serverCardPort = bluetoothOnlyHub || remoteEndPoint == null ? 0 : remoteEndPoint.Port;
            ConsoleGraphic.SetReservedBottomRows(showServerCard ? 3 : 0);
            graphic.Clear();
            ResetChatSessionLayout();
            if (showServerCard)
                ConsoleGraphic.DrawServerEndpointCard(serverCardAddress, serverCardPort);
            string displayedEndpoint = bluetoothOnlyHub
                ? Lang.Get(TextId.BluetoothEndpoint)
                : isLocalHubSession
                    ? ServerInterface.DisplayAddress + ":" + ServerInterface.ListeningPort
                    : remoteEndPoint == null ? host : NetworkAddressResolver.FormatEndpoint(remoteEndPoint);
            WriteSystemChatLine(Lang.Get(TextId.ConnectedCommands, displayedEndpoint));
            if (isLocalHubSession && ServerInterface.Options.UsesPortMapping && !ServerInterface.DisplayAddressIsPublic)
            {
                WriteSystemChatLine(
                    Lang.Get(TextId.PublicIPv4Unavailable, ServerInterface.DisplayAddress));
            }

            var sessionCancellation = new CancellationTokenSource();
            var end = new SessionEndState();
            var status = new HubStatusSession();
            Task receiverTask = ReceiveConnectionMessagesAsync(connection, stream, sessionCancellation.Token, end, status);
            CommandContext commandContext = CreateCommandContext(stream, sessionCancellation.Token, end, status, remoteEndPoint, host);
            localStatusMonitor = new LocalHubStatusMonitor();
            nextLocalStatusCheck = 0;
            whoisSession = status.Whois;
            diagnosticsStream = stream;
            diagnosticsToken = sessionCancellation.Token;
            sentSize = null;
            nextMetadataCheck = nextWhoisBlink = 0;

            try
            {
                MessageProtocol.WriteStringAsync(stream, HubStatusProtocol.Hello, sessionCancellation.Token).GetAwaiter().GetResult();
                MessageProtocol.WriteStringAsync(stream, WhoisProtocol.Hello, sessionCancellation.Token).GetAwaiter().GetResult();
                while (connected)
                {
                    string message = ReadChatMessage();
                    if (message == null)
                        break;
                    CommandDisposition commandResult = Commands.InitCommand(message, commandContext);
                    if (commandResult == CommandDisposition.EndSession)
                    {
                        end.Leave();
                        break;
                    }
                    if (commandResult == CommandDisposition.Handled)
                        continue;

                    if (String.IsNullOrWhiteSpace(message))
                        continue;

                    string imagePath;
                    ImageInputKind imageKind = ImageInput.Classify(message, out imagePath);
                    if (imageKind != ImageInputKind.NotImage)
                    {
                        if (imageKind == ImageInputKind.AnimatedGif)
                            PrepareAndSendAnimation(stream, sessionCancellation.Token, imagePath);
                        else
                            PrepareAndSendImage(
                                stream,
                                sessionCancellation.Token,
                                imagePath,
                                imageKind == ImageInputKind.WebPImage);
                        continue;
                    }

                    MessageProtocol.WriteStringAsync(stream, message, sessionCancellation.Token).GetAwaiter().GetResult();
                    WriteChatLine($"<<< [{nickname}]: {message}", null, true);
                }
            }
            catch (IOException)
            {
                end.RecordFailure(SessionEndKind.ConnectionLost);
            }
            catch (SocketException)
            {
                end.RecordFailure(SessionEndKind.ConnectionLost);
            }
            catch (ObjectDisposedException)
            {
                end.RecordFailure(SessionEndKind.ConnectionLost);
            }
            finally
            {
                end.DrainReceiverAsync(receiverTask).GetAwaiter().GetResult();
                localStatusMonitor = null;
                whoisSession = null;
                diagnosticsStream = null;
                connected = false;
                sessionCancellation.Cancel();
                connection.Dispose();
                try { receiverTask.GetAwaiter().GetResult(); } catch { }
                sessionCancellation.Dispose();
                ConsoleGraphic.ClearRemoteSnakes();
                if (showServerCard && ConsoleGraphic.Enabled)
                {
                    ConsoleGraphic.DrawServerEndpointCard(serverCardAddress, serverCardPort, false);
                    Thread.Sleep(250);
                }
                isLocalHubSession = false;
                showServerCard = false;
                serverCardPort = 0;
                currentTransport = ChatTransport.Tcp;
                sentSignal = null;
                EndMentionSession();
            }
            if (end.RequiresAcknowledgement)
                AcknowledgeDisconnect(end.GetDisplayMessage());
        }

        private static CommandContext CreateCommandContext(
            Stream stream,
            CancellationToken cancellationToken,
            SessionEndState end,
            HubStatusSession status,
            IPEndPoint endpoint, string host)
        {
            return new CommandContext
            {
                IsLocalHubAdministrator = isLocalHubSession,
                ClearChat = ClearChatLocally,
                LookImage = LookAtLastLargeImage,
                WriteLine = (text, color) => WriteSystemChatLine(text),
                ShowStatus = () => ShowHubStatus(stream, status, endpoint, host, cancellationToken),
                Whois = target => ShowWhois(target, stream, status.Whois, cancellationToken),
                GetStatus = () => ServerInterface.IsRunning
                    ? Lang.Get(
                        TextId.HubStatusWithClients,
                        ServerInterface.PortMappingStatus,
                        ServerInterface.ConnectedClientCount)
                    : Lang.Get(TextId.LocalHubNotRunning),
                StopLocalHub = () =>
                {
                    end.Leave();
                    connected = false;
                    ServerInterface.StopServer();
                },
                Kick = (target, reason) => ServerInterface.KickClientAsync(
                    target,
                    nickname,
                    reason).GetAwaiter().GetResult(),
                ToggleSnake = () => ToggleAndSynchronizeSnake(stream, cancellationToken)
            };
        }

        private static async Task ReceiveImageAsync(ImagePacket packet)
        {
            int viewportWidth;
            int usableRows;
            int historyVersion;
            SnapshotImageViewport(out viewportWidth, out usableRows, out historyVersion);
            FrozenImage frozen = await Task.Run(() => ImageRenderer.Freeze(
                packet,
                viewportWidth,
                usableRows,
                packet.Sender,
                false)).ConfigureAwait(false);

            WriteChatImage(frozen, frozen.ShouldOfferLook ? packet : null, historyVersion);
        }

        private static void PrepareAndSendImage(
            Stream stream,
            CancellationToken cancellationToken,
            string path,
            bool isWebP)
        {
            WriteSystemChatLine(Lang.Get(TextId.PreparingImage));
            try
            {
                ImagePacket packet = Task.Run(() => ImageCodec.Prepare(path, isWebP))
                    .GetAwaiter().GetResult();
                string frame = ImageProtocol.CreateClientFrame(packet);
                MessageProtocol.WriteStringAsync(stream, frame, cancellationToken).GetAwaiter().GetResult();

                int viewportWidth;
                int usableRows;
                int ignoredHistoryVersion;
                SnapshotImageViewport(out viewportWidth, out usableRows, out ignoredHistoryVersion);
                FrozenImage frozen = Task.Run(() => ImageRenderer.Freeze(
                    packet,
                    viewportWidth,
                    usableRows,
                    nickname,
                    true)).GetAwaiter().GetResult();
                WriteChatImage(frozen, frozen.ShouldOfferLook ? packet : null);
            }
            catch (ImagePreparationException ex)
            {
                WriteSystemChatLine(Lang.Get(GetImageErrorText(ex.Error)));
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is FormatException)
            {
                WriteSystemChatLine(Lang.Get(TextId.ImageDecodeFailed));
            }
        }

        private static TextId GetImageErrorText(ImagePreparationError error)
        {
            switch (error)
            {
                case ImagePreparationError.FileTooLarge:
                    return TextId.ImageFileTooLarge;
                case ImagePreparationError.DimensionsTooLarge:
                    return TextId.ImageDimensionsTooLarge;
                case ImagePreparationError.AnimationTooLarge:
                    return TextId.ImageAnimationTooLarge;
                case ImagePreparationError.CodecUnavailable:
                    return TextId.ImageCodecUnavailable;
                case ImagePreparationError.DecodeFailed:
                    return TextId.ImageDecodeFailed;
                default:
                    return TextId.ImageInvalidFile;
            }
        }

        private static void SnapshotImageViewport(
            out int viewportWidth,
            out int usableRows,
            out int historyVersion)
        {
            lock (consoleLock)
            {
                EnsureConsoleGeometryLocked();
                viewportWidth = Math.Max(1, GetContentWidth());
                int totalRows = ConsoleGraphic.Enabled
                    ? Math.Max(1, ConsoleGraphic.ContentBottom - ConsoleGraphic.ContentTop + 1)
                    : Math.Max(1, Math.Min(Console.WindowHeight, Console.BufferHeight) - 1);
                // Image size must not depend on whether the input editor happens
                // to be active at the instant the packet is frozen. The sender is
                // between input loops after Enter while receivers normally still
                // have an active prompt, which previously produced different sizes
                // for identical console geometry.
                usableRows = GetStableImageUsableRows(totalRows);
                historyVersion = imageHistoryVersion;
            }
        }

        private static int GetStableImageUsableRows(int totalRows)
        {
            return Math.Max(2, totalRows - ImageInputRowReservation);
        }

        private static void LookAtLastLargeImage()
        {
            ImagePacket packet;
            AnimatedImagePacket animation;
            lock (consoleLock)
            {
                packet = lastLargeImagePacket;
                animation = lastLargeAnimationPacket;
            }
            if (packet == null && animation == null)
            {
                WriteSystemChatLine(Lang.Get(TextId.NoLargeImage));
                return;
            }

            bool launched = animation != null
                ? ImageViewer.Launch(animation)
                : ImageViewer.Launch(packet);
            if (!launched)
                WriteSystemChatLine(Lang.Get(TextId.ImageViewerFailed));
        }

        private static SnakeCommandResult ToggleAndSynchronizeSnake(
            Stream stream,
            CancellationToken cancellationToken)
        {
            if (!ConsoleGraphic.Enabled)
                return SnakeCommandResult.Unavailable;

            bool paused = ConsoleGraphic.ToggleBorderSnakePause();
            SnakeProfile updatedProfile = new SnakeProfile
            {
                Enabled = true,
                Paused = paused,
                DelayMilliseconds = ConsoleGraphic.CurrentBorderSnakeReferenceDelayMilliseconds,
                Color = ConsoleGraphic.BorderSnakeColor,
                Step = ConsoleGraphic.CurrentBorderSnakeReferenceStep,
                Glyph = ConsoleGraphic.BorderSnakeGlyph
            };
            MessageProtocol.WriteStringAsync(
                stream,
                SnakeProtocol.CreateClientProfile(updatedProfile),
                cancellationToken).GetAwaiter().GetResult();
            return paused ? SnakeCommandResult.Paused : SnakeCommandResult.Resumed;
        }

        private static string LocalizeAuthenticationError(string response)
        {
            if (String.Equals(response, AUTH_ERROR_MESSAGE + ":INVALID_REQUEST", StringComparison.Ordinal))
                return Lang.Get(TextId.AuthInvalidRequest);
            if (String.Equals(response, AUTH_ERROR_MESSAGE + ":INVALID_NICKNAME", StringComparison.Ordinal))
                return Lang.Get(TextId.AuthInvalidNickname);
            if (String.Equals(response, AUTH_ERROR_MESSAGE + ":NICKNAME_TAKEN", StringComparison.Ordinal))
                return Lang.Get(TextId.AuthNicknameTaken);

            if (response != null && response.StartsWith(AUTH_ERROR_MESSAGE + ":", StringComparison.Ordinal))
                return response.Substring((AUTH_ERROR_MESSAGE + ":").Length).Trim();

            return Lang.Get(TextId.NicknameRejected);
        }

        private static bool TryApplySnakeUpdate(string message)
        {
            SnakeUpdateKind kind;
            string participant;
            SnakeProfile profile;
            if (!SnakeProtocol.TryParseServerUpdate(message, out kind, out participant, out profile))
                return false;

            if (String.Equals(participant, nickname, StringComparison.OrdinalIgnoreCase))
                return true;

            if (kind == SnakeUpdateKind.Set &&
                (ConsoleGraphic.Enabled || ConsoleGraphic.IsTemporarilySuspended))
            {
                lock (participantsLock)
                {
                    if (!activeParticipants.Contains(participant))
                    {
                        if (pendingParticipantSnakes.Count < ServerInterface.MaxConnectedClients || pendingParticipantSnakes.ContainsKey(participant))
                            pendingParticipantSnakes[participant] = profile;
                        return true;
                    }
                }
                ConsoleGraphic.SetRemoteSnake(
                    participant,
                    profile.DelayMilliseconds,
                    profile.Color,
                    profile.Step,
                    profile.Paused,
                    profile.Glyph);
            }
            else
            {
                lock (participantsLock) pendingParticipantSnakes.Remove(participant);
                ConsoleGraphic.RemoveRemoteSnake(participant);
            }

            return true;
        }

        private static void UpdateParticipantState(SystemMessageKind kind, string participant)
        {
            if (!IsNicknameValid(participant))
                return;

            SnakeProfile? pending = null;
            lock (participantsLock)
            {
                if (kind == SystemMessageKind.UserLeft)
                {
                    activeParticipants.Remove(participant);
                    pendingParticipantSnakes.Remove(participant);
                }
                else if (kind == SystemMessageKind.UserJoined || kind == SystemMessageKind.ParticipantPresent)
                {
                    if (activeParticipants.Count < ServerInterface.MaxConnectedClients || activeParticipants.Contains(participant))
                    {
                        activeParticipants.Add(participant);
                        if (pendingParticipantSnakes.Remove(participant, out SnakeProfile profile)) pending = profile;
                    }
                }
            }
            if (kind == SystemMessageKind.UserLeft) ConsoleGraphic.RemoveRemoteSnake(participant);
            if (pending is SnakeProfile saved)
                ConsoleGraphic.SetRemoteSnake(participant, saved.DelayMilliseconds, saved.Color, saved.Step, saved.Paused, saved.Glyph);
        }

        private static List<MentionSpan> FindMentionSpans(string message)
        {
            var mentions = new List<MentionSpan>();
            if (String.IsNullOrEmpty(message) || message.IndexOf('@') < 0)
                return mentions;

            string[] participants;
            lock (participantsLock)
            {
                participants = new string[activeParticipants.Count];
                activeParticipants.CopyTo(participants);
            }
            Array.Sort(participants, (first, second) => second.Length.CompareTo(first.Length));

            for (int index = 0; index < message.Length; index++)
            {
                if (message[index] != '@')
                    continue;

                foreach (string participant in participants)
                {
                    int mentionLength = participant.Length + 1;
                    if (index + mentionLength > message.Length ||
                        String.Compare(message, index + 1, participant, 0, participant.Length, StringComparison.OrdinalIgnoreCase) != 0 ||
                        !IsMentionBoundary(message, index + mentionLength))
                        continue;

                    mentions.Add(new MentionSpan
                    {
                        Start = index,
                        Length = mentionLength,
                        IsLocalUser = String.Equals(participant, nickname, StringComparison.OrdinalIgnoreCase)
                    });
                    index += mentionLength - 1;
                    break;
                }
            }

            return mentions;
        }

        private static bool IsMentionBoundary(string text, int index)
        {
            if (index >= text.Length)
                return true;

            char character = text[index];
            return !Char.IsLetterOrDigit(character) && character != '_';
        }

        private static void ScheduleDeferredMentionAnimation(ChatHistoryEntry entry)
        {
            lock (consoleLock)
                pendingMentionAnimations.Add(entry);

            WindowAttention.FlashTaskbarUntilForeground();
            int version = Volatile.Read(ref chatSessionVersion);
            if (Interlocked.CompareExchange(ref mentionMonitorActive, 1, 0) != 0)
                return;

            Task.Run(async () =>
            {
                try
                {
                    while (connected && version == Volatile.Read(ref chatSessionVersion) && WindowAttention.IsMinimized)
                        await Task.Delay(100).ConfigureAwait(false);

                    WindowAttention.StopFlashing();
                    if (!connected || version != Volatile.Read(ref chatSessionVersion))
                        return;

                    lock (consoleLock)
                    {
                        var pending = new HashSet<ChatHistoryEntry>(pendingMentionAnimations);
                        pendingMentionAnimations.Clear();
                        RedrawChatLayoutLocked(pending);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref mentionMonitorActive, 0);
                    lock (consoleLock)
                    {
                        if (connected && pendingMentionAnimations.Count > 0)
                        {
                            ChatHistoryEntry pending = null;
                            foreach (ChatHistoryEntry candidate in pendingMentionAnimations)
                            {
                                pending = candidate;
                                break;
                            }
                            if (pending != null)
                                ScheduleDeferredMentionAnimation(pending);
                        }
                    }
                }
            });
        }

        private static void EndMentionSession()
        {
            Interlocked.Increment(ref chatSessionVersion);
            WindowAttention.StopFlashing();
            lock (consoleLock)
            {
                pendingMentionAnimations.Clear();
                trimmedWhoisNotices.Drain();
                FinishMentionBlinkLocked();
            }
            lock (participantsLock)
            {
                activeParticipants.Clear();
                pendingParticipantSnakes.Clear();
            }
        }

        internal static bool RunCommandSelfTest()
        {
            bool commandsAreValid = Commands.RunSelfTest();

            string previousNickname = nickname;
            List<MentionSpan> validMentions;
            List<MentionSpan> invalidMentions;
            lock (participantsLock)
            {
                activeParticipants.Clear();
                activeParticipants.Add("alex");
                activeParticipants.Add("alextmsv");
            }
            nickname = "alextmsv";
            validMentions = FindMentionSpans("hello @alextmsv, @alex!");
            invalidMentions = FindMentionSpans("@missing @alextmsvSuffix");
            var styleEntry = new ChatHistoryEntry(
                "hello @alextmsv",
                null,
                FindMentionSpans("hello @alextmsv"));
            ChatTextStyle mentionStyle = GetChatTextStyle(styleEntry, "hello ".Length);
            ChatTextStyle plainStyle = GetChatTextStyle(styleEntry, 0);
            ConsoleColor previousSystemColor = ConsoleTheme.SystemText;
            ConsoleTheme.SystemText = ConsoleColor.DarkYellow;
            var systemStyleEntry = new ChatHistoryEntry(
                ">>> user joined",
                ConsoleColor.Green,
                null,
                true);
            ChatTextStyle systemStyle = GetChatTextStyle(systemStyleEntry, 0);
            var inferredSystemStyleEntry = new ChatHistoryEntry(
                ">>> user joined",
                null,
                null);
            ChatTextStyle inferredSystemStyle = GetChatTextStyle(inferredSystemStyleEntry, 0);
            var incomingStyleEntry = new ChatHistoryEntry(
                ">>> [user]: connected",
                null,
                null);
            ChatTextStyle incomingStyle = GetChatTextStyle(incomingStyleEntry, 0);
            ConsoleTheme.SystemText = previousSystemColor;
            nickname = previousNickname;
            lock (participantsLock)
                activeParticipants.Clear();

            bool chatLayoutIsValid = RunChatLayoutModelSelfTest();

            return commandsAreValid &&
                   chatLayoutIsValid &&
                   validMentions.Count == 2 &&
                   validMentions[0].Length == "@alextmsv".Length &&
                   validMentions[0].IsLocalUser &&
                   !validMentions[1].IsLocalUser &&
                   invalidMentions.Count == 0 &&
                   mentionStyle.Foreground == ConsoleColor.Black &&
                   mentionStyle.Background == ConsoleColor.White &&
                   plainStyle.Background == ConsoleColor.Black &&
                   systemStyle.Foreground == ConsoleColor.DarkYellow &&
                   inferredSystemStyle.Foreground == ConsoleColor.DarkYellow &&
                   incomingStyle.Foreground == ConsoleTheme.IncomingMarker;
        }

        private static bool RunChatLayoutModelSelfTest()
        {
            var savedHistory = new List<ChatHistoryEntry>(chatHistory);
            int savedImageHistoryBytes = imageHistoryBytes;
            try
            {
                chatHistory.Clear();
                for (int index = 0; index < 100; index++)
                    chatHistory.Add(new ChatHistoryEntry("message-" + index, null, null));

                int firstEntry;
                int rowsToSkip;
                FindVisibleHistoryStart(40, 10, out firstEntry, out rowsToSkip);
                bool tailSelectionIsValid = firstEntry == 90 && rowsToSkip == 0;

                chatHistory.Clear();
                chatHistory.Add(new ChatHistoryEntry(new string('x', 25), null, null));
                FindVisibleHistoryStart(10, 2, out firstEntry, out rowsToSkip);
                bool longLineSelectionIsValid = firstEntry == 0 && rowsToSkip == 1;

                chatHistory.Clear();
                chatHistory.Add(new ChatHistoryEntry("before", null, null));
                chatHistory.Add(new ChatHistoryEntry(new FrozenImage
                {
                    Width = 8,
                    Height = 2,
                    PackedPixels = new byte[8],
                    Sender = "alex"
                }));
                chatHistory.Add(new ChatHistoryEntry("after", null, null));
                FindVisibleHistoryStart(40, 4, out firstEntry, out rowsToSkip);
                bool mixedHistoryIsValid = firstEntry == 1 && rowsToSkip == 0 &&
                                           GetHistoryRowCount(chatHistory[1], 40) == 3;

                var animationEntry = new ChatHistoryEntry(new FrozenAnimation
                {
                    Width = 8,
                    Height = 2,
                    PackedFrames = new[] { new byte[8], new byte[8] },
                    ToneMaps = new[] { new byte[16], new byte[16] },
                    FrameDelays = new ushort[] { 90, 110 },
                    FrameEndMilliseconds = new[] { 90, 200 },
                    DurationMilliseconds = 200,
                    Sender = "alex"
                });
                bool animationLayoutIsValid = animationEntry.IsVisual &&
                                              GetHistoryRowCount(animationEntry, 40) == 3 &&
                                              GetCurrentAnimationFrame(animationEntry) >= 0;

                bool imageViewportIsStable =
                    GetStableImageUsableRows(20) == 19 &&
                    GetStableImageUsableRows(1) == 2;

                return tailSelectionIsValid && longLineSelectionIsValid &&
                       mixedHistoryIsValid && animationLayoutIsValid && imageViewportIsStable;
            }
            finally
            {
                chatHistory.Clear();
                chatHistory.AddRange(savedHistory);
                imageHistoryBytes = savedImageHistoryBytes;
            }
        }

        private static void ClearChatLocally()
        {
            lock (consoleLock)
            {
                chatHistory.Clear();
                imageHistoryBytes = 0;
                lastLargeImagePacket = null;
                lastLargeAnimationPacket = null;
                imageHistoryVersion++;
                pendingMentionAnimations.Clear();
                trimmedWhoisNotices.Drain();
                WindowAttention.StopFlashing();
                if (!RedrawChatLayoutLocked())
                    MarkConsoleResizePendingLocked();
            }
        }

        private static async Task<string> ReadWithTimeoutAsync(IChatConnection connection, Stream stream, CancellationToken cancellationToken)
        {
            Task<string> readTask = MessageProtocol.ReadStringAsync(stream, cancellationToken);
            try
            {
                return await readTask.WaitAsync(
                    TimeSpan.FromMilliseconds(ConnectionTimeoutMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                connection.Dispose();
                try { await readTask.ConfigureAwait(false); } catch { }
                throw new TimeoutException(Lang.Get(TextId.ServerDidNotRespond));
            }
        }

        private static string ReadChatMessage()
        {
            lock (consoleLock)
            {
                MoveCursorToContentColumn();
                inputActive = true;
                inputBuffer.Clear();
                inputCursorIndex = 0;
                inputStartRow = Console.CursorTop;
                renderedInputRows = 0;
                inputPrompt = $"<<< [{nickname}]: ";
                ClearMentionState();
                if (ConsoleGraphic.Enabled)
                {
                    if (!RedrawChatLayoutLocked())
                        MarkConsoleResizePendingLocked();
                }
                else
                {
                    RenderInputLine();
                }
            }

            while (connected)
            {
                if (WindowsTerminalTheme.RefreshAfterActivation())
                {
                    lock (consoleLock)
                    {
                        ConsoleGraphic.InvalidateVisualTheme();
                        hasKnownConsoleGeometry = false;
                        if (!RedrawChatLayoutLocked()) MarkConsoleResizePendingLocked();
                    }
                }
                CheckForConsoleResize();
                CheckLocalHubStatus();
                UpdateWhoisUi();
                ConsoleGraphic.EnsureBorderAnimationRunning();
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(20);
                    continue;
                }

                ConsoleKeyInfo key = Console.ReadKey(true);
                Interlocked.Increment(ref chatInputGeneration);
                lock (consoleLock)
                {
                    if (mentionActive && key.Key == ConsoleKey.Escape)
                    {
                        ClearMentionState();
                        RedrawInputAreaLocked();
                        continue;
                    }

                    if (mentionActive && (key.Key == ConsoleKey.Tab || key.Key == ConsoleKey.DownArrow))
                    {
                        mentionSuggestionIndex = (mentionSuggestionIndex + 1) % mentionSuggestions.Length;
                        RedrawInputAreaLocked();
                        continue;
                    }

                    if (mentionActive && key.Key == ConsoleKey.UpArrow)
                    {
                        mentionSuggestionIndex = (mentionSuggestionIndex - 1 + mentionSuggestions.Length) % mentionSuggestions.Length;
                        RedrawInputAreaLocked();
                        continue;
                    }

                    if (mentionActive && key.Key == ConsoleKey.Enter)
                    {
                        AcceptMentionSuggestionLocked();
                        RedrawInputAreaLocked();
                        continue;
                    }

                    if (key.Key == ConsoleKey.Enter)
                    {
                        string message = inputBuffer.ToString();
                        EraseRenderedInput();
                        inputActive = false;
                        inputBuffer.Clear();
                        inputCursorIndex = 0;
                        ClearMentionState();
                        return message;
                    }

                    if (HandleInputKey(key))
                    {
                        UpdateMentionSuggestionsLocked();
                        RedrawInputAreaLocked();
                    }
                }
            }

            lock (consoleLock)
            {
                EraseRenderedInput();
                inputActive = false;
                inputBuffer.Clear();
                inputCursorIndex = 0;
                ClearMentionState();
            }
            return null;
        }

        private static void RedrawInputAreaLocked()
        {
            if (!EnsureConsoleGeometryLocked())
                return;

            if (ConsoleGraphic.Enabled)
            {
                int availableRows = Math.Max(1, ConsoleGraphic.ContentBottom - ConsoleGraphic.ContentTop + 1);
                int requiredRows = GetRequiredInputRows(GetContentWidth(), availableRows);
                int requiredPopupRows = GetRequiredMentionPopupRows(Math.Max(0, availableRows - requiredRows));
                if (requiredRows + requiredPopupRows == renderedInputRows)
                {
                    RenderInputLine();
                }
                else if (!RedrawChatLayoutLocked())
                {
                    MarkConsoleResizePendingLocked();
                }
            }
            else
            {
                RenderInputLine();
            }
        }

        private static void UpdateMentionSuggestionsLocked()
        {
            if (InlineSuggestions.TryGetToken(inputBuffer.ToString(), inputCursorIndex, '@', out int tokenStart, out string prefix))
            {
                List<string> candidates;
                lock (participantsLock)
                {
                    candidates = new List<string>(activeParticipants.Count);
                    foreach (string participant in activeParticipants)
                        if (!String.Equals(participant, nickname, StringComparison.OrdinalIgnoreCase))
                            candidates.Add(participant);
                }

                string[] matches = InlineSuggestions.MatchPrefix(candidates, prefix, MaxMentionSuggestionRows);
                if (matches.Length > 0)
                {
                    string previouslySelected = mentionActive && mentionSuggestionIndex < mentionSuggestions.Length
                        ? mentionSuggestions[mentionSuggestionIndex]
                        : null;
                    mentionSuggestions = matches;
                    int preserved = previouslySelected == null
                        ? -1
                        : Array.FindIndex(matches, match => String.Equals(match, previouslySelected, StringComparison.OrdinalIgnoreCase));
                    mentionSuggestionIndex = preserved >= 0 ? preserved : 0;
                    mentionTokenStart = tokenStart;
                    mentionActive = true;
                    return;
                }
            }

            ClearMentionState();
        }

        private static void AcceptMentionSuggestionLocked()
        {
            if (!mentionActive || mentionSuggestions.Length == 0 || mentionTokenStart < 0)
                return;

            string replacement = "@" + mentionSuggestions[mentionSuggestionIndex] + " ";
            int removeLength = Math.Max(0, Math.Min(inputBuffer.Length, inputCursorIndex) - mentionTokenStart);
            inputBuffer.Remove(mentionTokenStart, removeLength);
            inputBuffer.Insert(mentionTokenStart, replacement);
            inputCursorIndex = mentionTokenStart + replacement.Length;
            ClearMentionState();
        }

        private static void ClearMentionState()
        {
            mentionActive = false;
            mentionSuggestions = Array.Empty<string>();
            mentionSuggestionIndex = 0;
            mentionTokenStart = -1;
        }

        private static int GetRequiredMentionPopupRows(int maxAvailable)
        {
            if (!mentionActive || mentionSuggestions.Length == 0 || maxAvailable <= 0)
                return 0;
            return Math.Max(0, Math.Min(mentionSuggestions.Length, Math.Min(MaxMentionSuggestionRows, maxAvailable)));
        }

        private static void RenderMentionSuggestionRows(int left, int width, int startRow, int popupRows)
        {
            for (int row = 0; row < popupRows; row++)
            {
                int targetRow = startRow + row;
                if (ConsoleGraphic.Enabled)
                    ConsoleGraphic.ClearContentRow(targetRow);
                else
                {
                    Console.SetCursorPosition(left, targetRow);
                    Console.Write(new string(' ', width));
                }

                Console.SetCursorPosition(left, targetRow);
                bool selected = row == mentionSuggestionIndex;
                string marker = selected ? "> " : "  ";
                int labelWidth = Math.Max(0, width - marker.Length);
                string label = "@" + mentionSuggestions[row];
                if (label.Length > labelWidth)
                    label = label.Substring(0, labelWidth);

                Console.ForegroundColor = selected ? ConsoleTheme.SelectionBackground : ConsoleTheme.MenuText;
                Console.Write(marker + label);
                Console.ResetColor();
            }
        }

        internal static bool RunMentionSuggestionSelfTest()
        {
            string savedNickname = nickname;
            var savedParticipants = new List<string>(activeParticipants);
            string savedBuffer = inputBuffer.ToString();
            int savedCursor = inputCursorIndex;
            bool savedActive = mentionActive;
            string[] savedSuggestions = mentionSuggestions;
            int savedIndex = mentionSuggestionIndex;
            int savedTokenStart = mentionTokenStart;
            try
            {
                nickname = "me";
                lock (participantsLock)
                {
                    activeParticipants.Clear();
                    activeParticipants.Add("alextmsv");
                    activeParticipants.Add("tmsvalex");
                    activeParticipants.Add("me");
                }

                inputBuffer.Clear();
                inputBuffer.Append("hi @");
                inputCursorIndex = inputBuffer.Length;
                UpdateMentionSuggestionsLocked();
                bool bareTriggerShowsBoth = mentionActive && mentionSuggestions.Length == 2;
                bool selfExcludedFromRoster = Array.IndexOf(mentionSuggestions, "me") < 0;

                bool capLimitsRows = GetRequiredMentionPopupRows(1) == 1 && GetRequiredMentionPopupRows(100) == 2;

                inputBuffer.Append('a');
                inputCursorIndex = inputBuffer.Length;
                UpdateMentionSuggestionsLocked();
                bool prefixNarrows = mentionActive && mentionSuggestions.Length == 1 && mentionSuggestions[0] == "alextmsv";

                inputBuffer.Append('d');
                inputCursorIndex = inputBuffer.Length;
                UpdateMentionSuggestionsLocked();
                bool noMatchClosesPopup = !mentionActive && mentionSuggestions.Length == 0 &&
                                           GetRequiredMentionPopupRows(100) == 0;

                inputBuffer.Clear();
                inputBuffer.Append("hi @al world");
                inputCursorIndex = 6;
                UpdateMentionSuggestionsLocked();
                bool cursorInsideEarlierTokenStillMatches = mentionActive && mentionSuggestions.Length == 1 &&
                                                             mentionSuggestions[0] == "alextmsv";

                AcceptMentionSuggestionLocked();
                bool acceptSubstitutesInPlace = !mentionActive &&
                                                 inputBuffer.ToString() == "hi @alextmsv  world" &&
                                                 inputCursorIndex == "hi @alextmsv ".Length;

                inputBuffer.Clear();
                inputBuffer.Append("no trigger here");
                inputCursorIndex = inputBuffer.Length;
                UpdateMentionSuggestionsLocked();
                bool plainTextStaysInactive = !mentionActive;

                return bareTriggerShowsBoth && selfExcludedFromRoster && capLimitsRows && prefixNarrows &&
                       noMatchClosesPopup && cursorInsideEarlierTokenStillMatches && acceptSubstitutesInPlace &&
                       plainTextStaysInactive;
            }
            finally
            {
                nickname = savedNickname;
                lock (participantsLock)
                {
                    activeParticipants.Clear();
                    foreach (string participant in savedParticipants)
                        activeParticipants.Add(participant);
                }
                inputBuffer.Clear();
                inputBuffer.Append(savedBuffer);
                inputCursorIndex = savedCursor;
                mentionActive = savedActive;
                mentionSuggestions = savedSuggestions;
                mentionSuggestionIndex = savedIndex;
                mentionTokenStart = savedTokenStart;
            }
        }

        private static bool HandleInputKey(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.LeftArrow:
                    if (inputCursorIndex > 0)
                        inputCursorIndex--;
                    return true;

                case ConsoleKey.RightArrow:
                    if (inputCursorIndex < inputBuffer.Length)
                        inputCursorIndex++;
                    return true;

                case ConsoleKey.Home:
                    inputCursorIndex = 0;
                    return true;

                case ConsoleKey.End:
                    inputCursorIndex = inputBuffer.Length;
                    return true;

                case ConsoleKey.Backspace:
                    if (inputCursorIndex > 0)
                    {
                        inputBuffer.Remove(inputCursorIndex - 1, 1);
                        inputCursorIndex--;
                    }
                    return true;

                case ConsoleKey.Delete:
                    if (inputCursorIndex < inputBuffer.Length)
                        inputBuffer.Remove(inputCursorIndex, 1);
                    return true;
            }

            if (!Char.IsControl(key.KeyChar))
            {
                if (inputBuffer.Length >= MessageProtocol.MaxMessageCharacters)
                    return false;

                inputBuffer.Insert(inputCursorIndex, key.KeyChar);
                inputCursorIndex++;
                return true;
            }

            return false;
        }

        private static void RenderInputLine()
        {
            try
            {
                EraseRenderedInput();
                RenderInputLineCore();
            }
            catch (ArgumentOutOfRangeException)
            {
                MarkConsoleResizePendingLocked();
            }
            catch (IOException)
            {
                MarkConsoleResizePendingLocked();
            }
        }

        private static void RenderInputLineCore()
        {
            RenderInputLineCoreAt(null);
        }

        private static void RenderInputLineCoreAt(int? fixedStartRow)
        {
            ConsoleGraphic.AlignViewport();

            int left = GetContentLeft();
            int width = GetContentWidth();
            int availableRows = ConsoleGraphic.Enabled
                ? Math.Max(1, ConsoleGraphic.ContentBottom - ConsoleGraphic.ContentTop + 1)
                : Math.Max(1, Math.Min(Console.WindowHeight, Console.BufferHeight));
            int maximumRows = Math.Min(MaxVisibleInputRows, availableRows);
            int capacity = Math.Max(1, width * maximumRows);
            string text = inputPrompt + inputBuffer;
            int cursorOffset = inputPrompt.Length + inputCursorIndex;
            int visibleStart = Math.Max(0, cursorOffset - capacity + 1);
            int visibleLength = Math.Min(capacity, Math.Max(0, text.Length - visibleStart));
            string visibleText = visibleLength == 0
                ? String.Empty
                : text.Substring(visibleStart, visibleLength);
            int visibleCursorOffset = Math.Max(0, cursorOffset - visibleStart);
            int occupiedCells = Math.Max(visibleText.Length, visibleCursorOffset + 1);
            int rows = Math.Max(1, Math.Min(maximumRows, (occupiedCells + width - 1) / width));
            int popupRows = GetRequiredMentionPopupRows(Math.Max(0, availableRows - rows));
            int totalRows = rows + popupRows;

            if (fixedStartRow.HasValue)
            {
                int lastPossibleRow = ConsoleGraphic.Enabled
                    ? Math.Max(ConsoleGraphic.ContentTop, ConsoleGraphic.ContentBottom - totalRows + 1)
                    : Math.Max(0, Console.BufferHeight - totalRows);
                inputStartRow = Math.Max(
                    ConsoleGraphic.Enabled ? ConsoleGraphic.ContentTop : 0,
                    Math.Min(fixedStartRow.Value, lastPossibleRow));
            }
            else
            {
                inputStartRow = ConsoleGraphic.EnsureContentSpace(inputStartRow, totalRows);
            }
            renderedInputLeft = left;
            renderedInputWidth = width;
            renderedInputRows = totalRows;

            for (int row = 0; row < rows; row++)
            {
                int sourceIndex = row * width;
                int count = Math.Min(width, Math.Max(0, visibleText.Length - sourceIndex));
                int targetRow = inputStartRow + row;
                if (ConsoleGraphic.Enabled)
                    ConsoleGraphic.ClearContentRow(targetRow);
                else
                {
                    Console.SetCursorPosition(left, targetRow);
                    Console.Write(new string(' ', width));
                }

                Console.SetCursorPosition(left, targetRow);
                if (count > 0)
                {
                    if (ConsoleGraphic.Enabled)
                        WriteStyledInputText(visibleText, sourceIndex, count, visibleStart + sourceIndex);
                    else
                        Console.Write(visibleText.Substring(sourceIndex, count));
                }
            }

            RenderMentionSuggestionRows(left, width, inputStartRow + rows, popupRows);

            Console.SetCursorPosition(
                left + visibleCursorOffset % width,
                inputStartRow + Math.Min(rows - 1, visibleCursorOffset / width));
        }

        private static void EraseRenderedInput()
        {
            if (renderedInputRows <= 0)
                return;

            try
            {
                int left = Math.Max(0, Math.Min(renderedInputLeft, Console.BufferWidth - 1));
                int currentWidth = GetContentWidth();
                int width = Math.Max(1, Math.Min(renderedInputWidth, currentWidth));
                width = Math.Min(width, Console.BufferWidth - left);

                for (int row = 0; row < renderedInputRows; row++)
                {
                    int targetRow = inputStartRow + row;
                    if (targetRow < 0 || targetRow >= Console.BufferHeight)
                        continue;

                    Console.SetCursorPosition(left, targetRow);
                    Console.Write(new string(' ', width));
                }

                int safeRow = Math.Max(0, Math.Min(inputStartRow, Console.BufferHeight - 1));
                Console.SetCursorPosition(left, safeRow);
            }
            catch (ArgumentOutOfRangeException) { }
            catch (IOException) { }
            renderedInputRows = 0;
            renderedInputWidth = 0;
        }

        private static void WriteChatLine(
            string message,
            ConsoleColor? forcedColor = null,
            bool detectMentions = false,
            bool useSystemTheme = false,
            string[] whoisRequesters = null)
        {
            lock (consoleLock)
            {
                bool consoleReady = EnsureConsoleGeometryLocked();
                string safeMessage = SanitizeForConsole(message);
                List<MentionSpan> mentions = detectMentions
                    ? FindMentionSpans(safeMessage)
                    : new List<MentionSpan>();
                if (whoisRequesters != null) mentions.Add(new MentionSpan { Start = 0, Length = safeMessage.Length });
                ChatHistoryEntry entry = AppendChatHistoryLocked(
                    safeMessage,
                    forcedColor,
                    mentions,
                    useSystemTheme);
                if (whoisRequesters != null)
                {
                    entry.WhoisAttention = new WhoisUnreadState(Volatile.Read(ref chatInputGeneration));
                    entry.WhoisRequesters = whoisRequesters;
                }
                bool localMention = entry.MentionsLocalUser;
                bool deferAnimation = localMention && WindowAttention.IsMinimized;
                if (deferAnimation)
                    ScheduleDeferredMentionAnimation(entry);
                if (!consoleReady)
                    return;

                // MoveBufferArea is not reliable when messages and the editable input
                // line update the same graphical rectangle in quick succession. The CG
                // view is therefore rendered as one fixed snapshot from chatHistory.
                if (ConsoleGraphic.Enabled)
                {
                    ISet<ChatHistoryEntry> animatedEntries = null;
                    if (localMention && !deferAnimation)
                        animatedEntries = new HashSet<ChatHistoryEntry> { entry };

                    if (!RedrawChatLayoutLocked(animatedEntries))
                        MarkConsoleResizePendingLocked();
                    return;
                }

                if (HasAnimationHistoryLocked())
                {
                    if (!RedrawChatLayoutLocked())
                        MarkConsoleResizePendingLocked();
                    ScheduleAnimationMonitor();
                    return;
                }

                bool restoreInput = inputActive;
                if (restoreInput)
                    EraseRenderedInput();

                try
                {
                    var mentionFragments = new List<MentionFragment>();
                    WriteWrappedChatLine(entry, mentionFragments);

                    if (restoreInput)
                    {
                        inputStartRow = Console.CursorTop;
                        RenderInputLine();
                    }

                    if (localMention && !deferAnimation)
                        AnimateMentionFragments(mentionFragments);
                }
                catch (ArgumentOutOfRangeException)
                {
                    MarkConsoleResizePendingLocked();
                    return;
                }
                catch (IOException)
                {
                    MarkConsoleResizePendingLocked();
                    return;
                }

            }
        }

        private static void WriteSystemChatLine(string message)
        {
            WriteChatLine(message, null, false, true);
        }

        private static void WriteChatImage(
            FrozenImage image,
            ImagePacket largePacket = null,
            int expectedHistoryVersion = -1)
        {
            lock (consoleLock)
            {
                if (expectedHistoryVersion >= 0 && expectedHistoryVersion != imageHistoryVersion)
                    return;
                if (largePacket != null)
                {
                    lastLargeImagePacket = largePacket;
                    lastLargeAnimationPacket = null;
                }
                bool consoleReady = EnsureConsoleGeometryLocked();
                ChatHistoryEntry entry = AppendImageHistoryLocked(image);
                if (!consoleReady)
                    return;

                if (ConsoleGraphic.Enabled)
                {
                    if (!RedrawChatLayoutLocked())
                        MarkConsoleResizePendingLocked();
                    return;
                }

                if (HasAnimationHistoryLocked())
                {
                    if (!RedrawChatLayoutLocked())
                        MarkConsoleResizePendingLocked();
                    ScheduleAnimationMonitor();
                    return;
                }

                bool restoreInput = inputActive;
                if (restoreInput)
                    EraseRenderedInput();
                try
                {
                    WritePlainHistoryEntry(entry);
                    if (restoreInput)
                    {
                        inputStartRow = Console.CursorTop;
                        RenderInputLine();
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    MarkConsoleResizePendingLocked();
                }
                catch (IOException)
                {
                    MarkConsoleResizePendingLocked();
                }
            }
        }

        private static void WritePlainHistoryEntry(ChatHistoryEntry entry)
        {
            if (entry.Card != null)
            {
                MoveCursorToContentColumn();
                Console.ForegroundColor = ConsoleTheme.SystemText;
                entry.Card.WritePlain(Console.Out, GetContentWidth());
                Console.ResetColor();
                return;
            }
            if (!entry.IsVisual)
            {
                WriteWrappedChatLine(entry);
                return;
            }

            MoveCursorToContentColumn();
            WriteStyledChatText(entry.Text, 0, entry.Text.Length, entry);
            Console.WriteLine();
            int visualWidth = GetVisualWidth(entry);
            int visualHeight = GetVisualHeight(entry);
            int width = Math.Max(1, Math.Min(GetContentWidth(), visualWidth));
            char[] row = new char[width];
            int animationFrame = entry.IsAnimation ? GetCurrentAnimationFrame(entry) : 0;
            if (entry.IsAnimation)
            {
                entry.AnimationTop = Console.CursorTop;
                entry.AnimationFirstSourceRow = 0;
                entry.AnimationVisibleRows = visualHeight;
                entry.AnimationVisibleWidth = width;
                entry.AnimationRowBuffer = row;
                entry.LastRenderedAnimationFrame = animationFrame;
            }
            Console.ForegroundColor = ConsoleColor.Gray;
            for (int y = 0; y < visualHeight; y++)
            {
                MoveCursorToContentColumn();
                FillVisualAsciiRow(entry, animationFrame, y, row);
                Console.Write(row);
                Console.WriteLine();
            }
            Console.ResetColor();
            if (VisualShouldOfferLook(entry))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                List<string> promptBox = BuildVisualPromptBox(entry, Math.Max(1, GetContentWidth()));
                foreach (string line in promptBox)
                    Console.WriteLine(line);
                Console.ResetColor();
            }
        }

        private static void WriteWrappedChatLine(
            ChatHistoryEntry entry,
            List<MentionFragment> localMentionFragments = null)
        {
            string message = entry.Text;
            if (!ConsoleGraphic.Enabled)
            {
                MoveCursorToContentColumn();
                int startLeft = Console.CursorLeft;
                int startTop = Console.CursorTop;
                int plainWidth = Math.Max(1, Console.BufferWidth);
                CollectPlainConsoleMentionFragments(
                    entry,
                    startLeft,
                    startTop,
                    plainWidth,
                    localMentionFragments);
                WriteStyledChatText(message, 0, message.Length, entry);
                Console.WriteLine();
                return;
            }

            ConsoleGraphic.AlignViewport();
            int width = GetContentWidth();
            int offset = 0;
            do
            {
                int row = ConsoleGraphic.EnsureContentSpace(Console.CursorTop, 2);
                ConsoleGraphic.ClearContentRow(row);
                Console.SetCursorPosition(ConsoleGraphic.ContentLeft, row);

                int count = Math.Min(width, message.Length - offset);
                if (count > 0)
                {
                    CollectMentionFragments(
                        entry,
                        offset,
                        count,
                        ConsoleGraphic.ContentLeft,
                        row,
                        localMentionFragments);
                    WriteStyledChatText(message, offset, count, entry);
                    offset += count;
                }

                Console.SetCursorPosition(
                    ConsoleGraphic.ContentLeft,
                    Math.Min(ConsoleGraphic.ContentBottom, row + 1));
            }
            while (offset < message.Length);
        }

        private static void WriteStyledInputText(
            string visibleText,
            int sourceIndex,
            int count,
            int originalTextIndex)
        {
            int promptCharacters = Math.Max(0, Math.Min(count, inputPrompt.Length - originalTextIndex));
            if (promptCharacters > 0)
            {
                ConsoleGraphic.ApplyContentColors(ConsoleTheme.InputPrompt);
                Console.Write(visibleText.Substring(sourceIndex, promptCharacters));
            }

            int messageCharacters = count - promptCharacters;
            if (messageCharacters > 0)
            {
                ConsoleGraphic.ApplyContentColors(ConsoleTheme.InputText);
                Console.Write(visibleText.Substring(sourceIndex + promptCharacters, messageCharacters));
            }

            Console.ResetColor();
        }

        private static void WriteStyledChatText(string message, int offset, int count, ChatHistoryEntry entry)
        {
            int end = offset + count;
            int position = offset;
            while (position < end)
            {
                ChatTextStyle style = GetChatTextStyle(entry, position);
                int runEnd = position + 1;
                while (runEnd < end && GetChatTextStyle(entry, runEnd).IsSameAs(style))
                    runEnd++;

                bool mention = entry.Mentions.Exists(span => position >= span.Start && position < span.Start + span.Length);
                if (ConsoleTheme.HasContentBackground && !mention)
                    ConsoleGraphic.ApplyContentColors(style.Foreground);
                else
                {
                    Console.ForegroundColor = style.Foreground;
                    Console.BackgroundColor = style.Background;
                }
                Console.Write(message.Substring(position, runEnd - position));
                position = runEnd;
            }

            Console.ResetColor();
        }

        private static ChatTextStyle GetChatTextStyle(ChatHistoryEntry entry, int position)
        {
            foreach (MentionSpan mention in entry.Mentions)
            {
                if (position >= mention.Start && position < mention.Start + mention.Length)
                {
                    return new ChatTextStyle
                    {
                        Foreground = entry.WhoisUnread && entry.WhoisBlink ? ConsoleColor.White : ConsoleColor.Black,
                        Background = entry.WhoisUnread && entry.WhoisBlink ? ConsoleColor.Black : ConsoleColor.White
                    };
                }
            }

            return new ChatTextStyle
            {
                Foreground = entry.UseSystemTheme || IsSystemDisplayLine(entry.Text)
                    ? ConsoleTheme.SystemText
                    : entry.ForcedColor ?? GetChatColor(entry.Text, position),
                Background = ConsoleColor.Black
            };
        }

        private static bool IsSystemDisplayLine(string message)
        {
            if (String.IsNullOrEmpty(message))
                return true;

            bool hasDirectionMarker = message.StartsWith(">>> ", StringComparison.Ordinal) ||
                                      message.StartsWith("<<< ", StringComparison.Ordinal);
            if (!hasDirectionMarker)
                return false;

            // A real chat frame always has the form ">>> [nickname]: text".
            // System protocol lines also use the direction marker for readability,
            // but do not have a nickname envelope and must not inherit chat colors.
            return message.Length <= 4 || message[4] != '[' ||
                   message.IndexOf("]: ", 4, StringComparison.Ordinal) < 0;
        }

        private static void CollectMentionFragments(
            ChatHistoryEntry entry,
            int offset,
            int count,
            int left,
            int top,
            List<MentionFragment> fragments)
        {
            if (fragments == null)
                return;

            int end = offset + count;
            foreach (MentionSpan mention in entry.Mentions)
            {
                if (!mention.IsLocalUser)
                    continue;

                int fragmentStart = Math.Max(offset, mention.Start);
                int fragmentEnd = Math.Min(end, mention.Start + mention.Length);
                if (fragmentStart >= fragmentEnd)
                    continue;

                fragments.Add(new MentionFragment
                {
                    Left = left + fragmentStart - offset,
                    Top = top,
                    Text = entry.Text.Substring(fragmentStart, fragmentEnd - fragmentStart)
                });
            }
        }

        private static void CollectPlainConsoleMentionFragments(
            ChatHistoryEntry entry,
            int startLeft,
            int startTop,
            int width,
            List<MentionFragment> fragments)
        {
            if (fragments == null)
                return;

            foreach (MentionSpan mention in entry.Mentions)
            {
                if (!mention.IsLocalUser)
                    continue;

                int consumed = 0;
                while (consumed < mention.Length)
                {
                    int absoluteCell = startLeft + mention.Start + consumed;
                    int left = absoluteCell % width;
                    int top = startTop + absoluteCell / width;
                    int length = Math.Min(mention.Length - consumed, width - left);
                    fragments.Add(new MentionFragment
                    {
                        Left = left,
                        Top = top,
                        Text = entry.Text.Substring(mention.Start + consumed, length)
                    });
                    consumed += length;
                }
            }
        }

        private static void AnimateMentionFragments(List<MentionFragment> fragments)
        {
            if (fragments == null || fragments.Count == 0)
                return;

            activeMentionBlinkFragments = fragments;
            mentionBlinkStartTimestamp = Stopwatch.GetTimestamp();
            mentionBlinkLastPaintedPhase = -1;
            ScheduleAnimationMonitor();
        }

        private static void FinishMentionBlinkLocked()
        {
            activeMentionBlinkFragments = null;
            mentionBlinkLastPaintedPhase = -1;
        }

        private static bool TickMentionBlinkLocked(ref int nextDelayMilliseconds)
        {
            if (activeMentionBlinkFragments == null)
                return false;

            long elapsedTicks = Stopwatch.GetTimestamp() - mentionBlinkStartTimestamp;
            long elapsedMilliseconds = elapsedTicks <= 0
                ? 0
                : elapsedTicks * 1000L / Stopwatch.Frequency;
            int totalPhases = MentionBlinkCycles * 2;
            int phase = (int)(elapsedMilliseconds / MentionBlinkDelayMilliseconds);

            if (phase >= totalPhases)
            {
                Console.ResetColor();
                FinishMentionBlinkLocked();
                return false;
            }

            if (phase != mentionBlinkLastPaintedPhase)
            {
                try
                {
                    int previousLeft = Console.CursorLeft;
                    int previousTop = Console.CursorTop;
                    if (phase % 2 == 0)
                        PaintMentionFragments(activeMentionBlinkFragments, ConsoleColor.Magenta, ConsoleColor.Yellow);
                    else
                        PaintMentionFragments(activeMentionBlinkFragments, ConsoleColor.Black, ConsoleColor.White);
                    Console.ResetColor();
                    int safeLeft = Math.Max(0, Math.Min(previousLeft, Console.BufferWidth - 1));
                    int safeTop = Math.Max(0, Math.Min(previousTop, Console.BufferHeight - 1));
                    Console.SetCursorPosition(safeLeft, safeTop);
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IOException)
                {
                    FinishMentionBlinkLocked();
                    return false;
                }
                mentionBlinkLastPaintedPhase = phase;
            }

            long msUntilNextPhase = (phase + 1) * (long)MentionBlinkDelayMilliseconds - elapsedMilliseconds;
            nextDelayMilliseconds = Math.Max(1, (int)Math.Min(nextDelayMilliseconds, msUntilNextPhase));
            return true;
        }

        private static void PaintMentionFragments(
            List<MentionFragment> fragments,
            ConsoleColor foreground,
            ConsoleColor background)
        {
            Console.ForegroundColor = foreground;
            Console.BackgroundColor = background;
            foreach (MentionFragment fragment in fragments)
            {
                if (fragment.Top < 0 || fragment.Top >= Console.BufferHeight ||
                    fragment.Left < 0 || fragment.Left + fragment.Text.Length > Console.BufferWidth)
                    continue;

                Console.SetCursorPosition(fragment.Left, fragment.Top);
                Console.Write(fragment.Text);
            }
        }

        private static ConsoleColor GetChatColor(string message, int position)
        {
            bool outgoing = message.StartsWith("<<< ", StringComparison.Ordinal);
            bool incoming = message.StartsWith(">>> ", StringComparison.Ordinal);
            if (outgoing || incoming)
            {
                if (position < 3)
                    return outgoing ? ConsoleTheme.OutgoingMarker : ConsoleTheme.IncomingMarker;

                int colon = message.IndexOf(':', 4);
                if (colon >= 0 && position <= colon)
                    return outgoing ? ConsoleTheme.OutgoingNickname : ConsoleTheme.IncomingNickname;

                return outgoing ? ConsoleTheme.OutgoingText : ConsoleTheme.IncomingText;
            }

            return ConsoleTheme.SystemText;
        }

        private static string SanitizeForConsole(string message)
        {
            if (String.IsNullOrEmpty(message))
                return String.Empty;

            StringBuilder safe = new StringBuilder(message.Length);
            foreach (char character in message)
                safe.Append(Char.IsControl(character) ? ' ' : character);
            return safe.ToString();
        }

        private static void ResetChatSessionLayout()
        {
            Interlocked.Increment(ref chatSessionVersion);
            WindowAttention.StopFlashing();
            lock (participantsLock)
            {
                activeParticipants.Clear();
                if (IsNicknameValid(nickname))
                    activeParticipants.Add(nickname);
            }

            lock (consoleLock)
            {
                chatHistory.Clear();
                imageHistoryBytes = 0;
                lastLargeImagePacket = null;
                lastLargeAnimationPacket = null;
                imageHistoryVersion++;
                pendingMentionAnimations.Clear();
                trimmedWhoisNotices.Drain();
                inputActive = false;
                inputBuffer.Clear();
                inputCursorIndex = 0;
                renderedInputRows = 0;
                renderedInputWidth = 0;
                CaptureConsoleGeometryLocked();
                consoleResizePending = false;
                resizeStableSinceTimestamp = 0;
                nextResizePollTimestamp = Stopwatch.GetTimestamp();
            }
        }

        private static ChatHistoryEntry AppendChatHistoryLocked(
            string message,
            ConsoleColor? forcedColor,
            List<MentionSpan> mentions,
            bool useSystemTheme = false)
        {
            var entry = new ChatHistoryEntry(
                message ?? String.Empty,
                forcedColor,
                mentions,
                useSystemTheme);
            chatHistory.Add(entry);
            TrimChatHistoryLocked();
            return entry;
        }

        private static ChatHistoryEntry AppendImageHistoryLocked(FrozenImage image)
        {
            var entry = new ChatHistoryEntry(image);
            chatHistory.Add(entry);
            imageHistoryBytes += image.PackedPixels.Length;
            TrimChatHistoryLocked();
            return entry;
        }

        private static ChatHistoryEntry AppendAnimationHistoryLocked(FrozenAnimation animation)
        {
            var entry = new ChatHistoryEntry(animation);
            chatHistory.Add(entry);
            imageHistoryBytes += animation.PackedByteCount;
            TrimChatHistoryLocked();
            return entry;
        }

        private static void TrimChatHistoryLocked()
        {
            int removeCount = 0;
            while (removeCount < chatHistory.Count &&
                   (chatHistory.Count - removeCount > MaxChatHistoryLines ||
                    imageHistoryBytes > ImageRenderer.MaxHistoryImageBytes))
            {
                ChatHistoryEntry removed = chatHistory[removeCount++];
                if (removed.WhoisUnread) trimmedWhoisNotices.Add(removed.WhoisRequesters, Volatile.Read(ref chatInputGeneration));
                pendingMentionAnimations.Remove(removed);
                if (removed.IsImage)
                    imageHistoryBytes -= removed.Image.PackedPixels.Length;
                else if (removed.IsAnimation)
                    imageHistoryBytes -= removed.Animation.PackedByteCount;
            }
            if (removeCount > 0)
                chatHistory.RemoveRange(0, removeCount);
        }

        private static void CheckForConsoleResize()
        {
            long now = Stopwatch.GetTimestamp();
            if (now < Volatile.Read(ref nextResizePollTimestamp))
                return;

            long intervalTicks = Math.Max(1L, Stopwatch.Frequency * ResizePollMilliseconds / 1000L);
            Volatile.Write(ref nextResizePollTimestamp, now + intervalTicks);
            lock (consoleLock)
                EnsureConsoleGeometryLocked();
        }

        private static bool EnsureConsoleGeometryLocked()
        {
            ConsoleGraphic.ConsoleGeometry currentGeometry;
            if (!ConsoleGraphic.TryCaptureConsoleGeometry(out currentGeometry))
                return false;

            if (!hasKnownConsoleGeometry)
            {
                knownConsoleGeometry = currentGeometry;
                hasKnownConsoleGeometry = true;
                consoleResizePending = false;
                return true;
            }

            if (currentGeometry.IsSameAs(knownConsoleGeometry) && !consoleResizePending)
                return true;

            long now = Stopwatch.GetTimestamp();
            if (!consoleResizePending || !currentGeometry.IsSameAs(pendingConsoleGeometry))
            {
                pendingConsoleGeometry = currentGeometry;
                consoleResizePending = true;
                resizeStableSinceTimestamp = now;
                return false;
            }

            long settleTicks = Math.Max(1L, Stopwatch.Frequency * ResizeSettleMilliseconds / 1000L);
            if (now - resizeStableSinceTimestamp < settleTicks)
                return false;

            return RedrawChatLayoutLocked();
        }

        private static void CaptureConsoleGeometryLocked()
        {
            ConsoleGraphic.ConsoleGeometry geometry;
            if (!ConsoleGraphic.TryCaptureConsoleGeometry(out geometry))
                return;

            knownConsoleGeometry = geometry;
            hasKnownConsoleGeometry = true;
        }

        private static void MarkConsoleResizePendingLocked()
        {
            ConsoleGraphic.ConsoleGeometry geometry;
            if (ConsoleGraphic.TryCaptureConsoleGeometry(out geometry))
                pendingConsoleGeometry = geometry;

            consoleResizePending = true;
            resizeStableSinceTimestamp = Stopwatch.GetTimestamp();
        }

        private static bool RedrawChatLayoutLocked(ISet<ChatHistoryEntry> animateEntries = null)
        {
            InvalidateAnimationTargetsLocked();
            ConsoleGraphic.ConsoleGeometry targetGeometry;
            if (!ConsoleGraphic.TryCaptureConsoleGeometry(out targetGeometry))
                return false;

            try
            {
                if (ConsoleGraphic.Enabled)
                    return RedrawGraphicalChatLayoutLocked(targetGeometry, animateEntries);

                int inputRowsToReserve = inputActive ? Math.Max(1, renderedInputRows) : 0;
                renderedInputRows = 0;
                renderedInputWidth = 0;
                if (!graphic.TryClear(0, 0))
                {
                    MarkConsoleResizePendingLocked();
                    return false;
                }

                if (showServerCard && ConsoleGraphic.Enabled)
                    ConsoleGraphic.DrawServerEndpointCard(serverCardAddress, serverCardPort);

                int contentWidth = GetContentWidth();
                int availableRows = ConsoleGraphic.Enabled
                    ? Math.Max(1, ConsoleGraphic.ContentBottom - ConsoleGraphic.ContentTop - inputRowsToReserve)
                    : Math.Max(1, Math.Min(Console.WindowHeight, Console.BufferHeight) - 1 - inputRowsToReserve);
                int firstVisibleEntry = GetFirstVisibleHistoryEntry(contentWidth, availableRows);
                var mentionFragments = new List<MentionFragment>();
                for (int index = firstVisibleEntry; index < chatHistory.Count; index++)
                {
                    ChatHistoryEntry historyLine = chatHistory[index];
                    if (historyLine.IsVisual || historyLine.Card != null)
                        WritePlainHistoryEntry(historyLine);
                    else
                        WriteWrappedChatLine(
                            historyLine,
                            animateEntries != null && animateEntries.Contains(historyLine)
                                ? mentionFragments
                                : null);
                }

                inputStartRow = Console.CursorTop;
                if (inputActive)
                    RenderInputLineCore();

                if (mentionFragments.Count > 0)
                    AnimateMentionFragments(mentionFragments);

                ConsoleGraphic.ConsoleGeometry renderedGeometry;
                if (!ConsoleGraphic.TryCaptureConsoleGeometry(out renderedGeometry) ||
                    !renderedGeometry.IsSameAs(targetGeometry))
                {
                    MarkConsoleResizePendingLocked();
                    return false;
                }

                knownConsoleGeometry = renderedGeometry;
                hasKnownConsoleGeometry = true;
                consoleResizePending = false;
                if (HasAnimationHistoryLocked())
                    ScheduleAnimationMonitor();
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                renderedInputRows = 0;
                renderedInputWidth = 0;
                MarkConsoleResizePendingLocked();
                return false;
            }
            catch (IOException)
            {
                renderedInputRows = 0;
                renderedInputWidth = 0;
                MarkConsoleResizePendingLocked();
                return false;
            }
        }

        private static bool RedrawGraphicalChatLayoutLocked(
            ConsoleGraphic.ConsoleGeometry targetGeometry,
            ISet<ChatHistoryEntry> animateEntries)
        {
            bool geometryChanged = !hasKnownConsoleGeometry ||
                                   !targetGeometry.IsSameAs(knownConsoleGeometry) ||
                                   consoleResizePending;
            if (geometryChanged)
            {
                if (!graphic.TryClear(0, 0))
                {
                    MarkConsoleResizePendingLocked();
                    return false;
                }

                if (showServerCard)
                    ConsoleGraphic.DrawServerEndpointCard(serverCardAddress, serverCardPort);
            }

            int width = GetContentWidth();
            int top = ConsoleGraphic.ContentTop;
            int bottom = ConsoleGraphic.ContentBottom;
            int totalRows = Math.Max(1, bottom - top + 1);
            int inputRows = inputActive ? GetRequiredInputRows(width, totalRows) : 0;
            int popupRows = inputActive ? GetRequiredMentionPopupRows(Math.Max(0, totalRows - inputRows)) : 0;
            int chatRows = Math.Max(0, totalRows - inputRows - popupRows);

            int firstEntry;
            int rowsToSkip;
            FindVisibleHistoryStart(width, chatRows, out firstEntry, out rowsToSkip);
            int targetRow = top;
            int chatBottomExclusive = top + chatRows;
            var mentionFragments = new List<MentionFragment>();
            for (int index = firstEntry;
                 index < chatHistory.Count && targetRow < chatBottomExclusive;
                 index++)
            {
                ChatHistoryEntry historyLine = chatHistory[index];
                int skip = index == firstEntry ? rowsToSkip : 0;
                WriteGraphicalHistoryEntryAt(
                    historyLine,
                    skip,
                    ref targetRow,
                    chatBottomExclusive,
                    animateEntries != null && animateEntries.Contains(historyLine)
                        ? mentionFragments
                        : null);
            }

            Console.ResetColor();
            for (int row = targetRow; row < chatBottomExclusive; row++)
                ConsoleGraphic.ClearContentRow(row);

            renderedInputRows = 0;
            renderedInputWidth = 0;
            if (inputActive)
            {
                inputStartRow = top + chatRows;
                RenderInputLineCoreAt(inputStartRow);
            }
            else
            {
                int cursorRow = Math.Max(top, Math.Min(bottom, targetRow));
                Console.SetCursorPosition(ConsoleGraphic.ContentLeft, cursorRow);
            }

            if (mentionFragments.Count > 0)
                AnimateMentionFragments(mentionFragments);

            ConsoleGraphic.ConsoleGeometry renderedGeometry;
            if (!ConsoleGraphic.TryCaptureConsoleGeometry(out renderedGeometry) ||
                !renderedGeometry.IsSameAs(targetGeometry))
            {
                MarkConsoleResizePendingLocked();
                return false;
            }

            knownConsoleGeometry = renderedGeometry;
            hasKnownConsoleGeometry = true;
            consoleResizePending = false;
            if (HasAnimationHistoryLocked())
                ScheduleAnimationMonitor();
            return true;
        }

        private static int GetRequiredInputRows(int width, int availableRows)
        {
            int maximumRows = Math.Max(1, Math.Min(MaxVisibleInputRows, availableRows));
            int capacity = Math.Max(1, width * maximumRows);
            int textLength = inputPrompt.Length + inputBuffer.Length;
            int cursorOffset = inputPrompt.Length + inputCursorIndex;
            int visibleStart = Math.Max(0, cursorOffset - capacity + 1);
            int visibleLength = Math.Min(capacity, Math.Max(0, textLength - visibleStart));
            int visibleCursorOffset = Math.Max(0, cursorOffset - visibleStart);
            int occupiedCells = Math.Max(visibleLength, visibleCursorOffset + 1);
            return Math.Max(1, Math.Min(maximumRows, (occupiedCells + width - 1) / width));
        }

        private static void FindVisibleHistoryStart(
            int width,
            int availableRows,
            out int firstEntry,
            out int rowsToSkip)
        {
            firstEntry = chatHistory.Count;
            rowsToSkip = 0;
            int remainingRows = Math.Max(0, availableRows);
            while (firstEntry > 0 && remainingRows > 0)
            {
                int candidate = firstEntry - 1;
                int candidateRows = GetHistoryRowCount(chatHistory[candidate], width);
                firstEntry = candidate;
                if (candidateRows <= remainingRows)
                {
                    remainingRows -= candidateRows;
                    continue;
                }

                rowsToSkip = candidateRows - remainingRows;
                remainingRows = 0;
            }
        }

        private static void WriteGraphicalHistoryEntryAt(
            ChatHistoryEntry entry,
            int wrappedRowsToSkip,
            ref int targetRow,
            int bottomExclusive,
            List<MentionFragment> localMentionFragments)
        {
            int width = GetContentWidth();
            if (entry.IsVisual)
            {
                WriteGraphicalImageEntryAt(entry, wrappedRowsToSkip, ref targetRow, bottomExclusive, width);
                return;
            }
            string renderedText = entry.Text;
            int wrappedRows = GetWrappedRowCount(renderedText, width);
            for (int wrappedRow = Math.Max(0, wrappedRowsToSkip);
                 wrappedRow < wrappedRows && targetRow < bottomExclusive;
                 wrappedRow++, targetRow++)
            {
                int offset = wrappedRow * width;
                int count = Math.Min(width, Math.Max(0, renderedText.Length - offset));
                ConsoleGraphic.ClearContentRow(targetRow);
                Console.SetCursorPosition(ConsoleGraphic.ContentLeft, targetRow);
                if (count <= 0)
                    continue;

                CollectMentionFragments(
                    entry,
                    offset,
                    count,
                    ConsoleGraphic.ContentLeft,
                    targetRow,
                    localMentionFragments);
                WriteStyledChatText(renderedText, offset, count, entry);
            }
        }

        private static void WriteGraphicalImageEntryAt(
            ChatHistoryEntry entry,
            int rowsToSkip,
            ref int targetRow,
            int bottomExclusive,
            int width)
        {
            int captionRows = GetWrappedRowCount(entry.Text, width);
            List<string> promptBox = VisualShouldOfferLook(entry)
                ? BuildVisualPromptBox(entry, width)
                : null;
            int promptRows = promptBox == null ? 0 : promptBox.Count;
            int visualHeight = GetVisualHeight(entry);
            int totalRows = captionRows + visualHeight + promptRows;
            int visibleWidth = Math.Min(width, GetVisualWidth(entry));
            char[] imageRow = visibleWidth > 0 ? new char[visibleWidth] : Array.Empty<char>();
            int animationFrame = entry.IsAnimation ? GetCurrentAnimationFrame(entry) : 0;

            for (int logicalRow = Math.Max(0, rowsToSkip);
                 logicalRow < totalRows && targetRow < bottomExclusive;
                 logicalRow++, targetRow++)
            {
                ConsoleGraphic.ClearContentRow(targetRow);
                Console.SetCursorPosition(ConsoleGraphic.ContentLeft, targetRow);
                if (logicalRow < captionRows)
                {
                    int offset = logicalRow * width;
                    int count = Math.Min(width, Math.Max(0, entry.Text.Length - offset));
                    if (count > 0)
                        WriteStyledChatText(entry.Text, offset, count, entry);
                    continue;
                }

                int imageRowIndex = logicalRow - captionRows;
                if (imageRowIndex < visualHeight)
                {
                    if (visibleWidth > 0)
                    {
                        if (entry.IsAnimation)
                        {
                            if (entry.AnimationTop < 0)
                            {
                                entry.AnimationTop = targetRow;
                                entry.AnimationFirstSourceRow = imageRowIndex;
                                entry.AnimationVisibleRows = 0;
                                entry.AnimationVisibleWidth = visibleWidth;
                                entry.AnimationRowBuffer = imageRow;
                                entry.LastRenderedAnimationFrame = animationFrame;
                            }
                            entry.AnimationVisibleRows++;
                        }
                        FillVisualAsciiRow(entry, animationFrame, imageRowIndex, imageRow);
                        Console.ForegroundColor = ConsoleColor.Gray;
                        Console.Write(imageRow);
                        Console.ResetColor();
                    }
                    continue;
                }

                int promptRow = imageRowIndex - visualHeight;
                if (promptRow >= 0 && promptRow < promptRows)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write(promptBox[promptRow]);
                    Console.ResetColor();
                }
            }
        }

        private static int GetFirstVisibleHistoryEntry(int width, int availableRows)
        {
            int rows = 0;
            int first = chatHistory.Count;
            while (first > 0)
            {
                int candidateRows = GetHistoryRowCount(chatHistory[first - 1], width);
                if (rows > 0 && rows + candidateRows > availableRows)
                    break;

                rows += candidateRows;
                first--;
                if (rows >= availableRows)
                    break;
            }

            return first;
        }

        private static int GetWrappedRowCount(string text, int width)
        {
            return Math.Max(1, (Math.Max(0, text == null ? 0 : text.Length) + Math.Max(1, width) - 1) /
                Math.Max(1, width));
        }

        private static int GetHistoryRowCount(ChatHistoryEntry entry, int width)
        {
            if (!entry.IsVisual)
                return GetWrappedRowCount(entry.Text, width);

            int rows = GetWrappedRowCount(entry.Text, width) + GetVisualHeight(entry);
            if (VisualShouldOfferLook(entry))
                rows += GetVisualPromptBoxRowCount(entry, width);
            return rows;
        }

        private static int GetImagePromptBoxRowCount(
            FrozenImage image,
            int availableWidth)
        {
            string prompt = GetImageLookPrompt(image);
            int width = Math.Max(1, Math.Min(Math.Max(1, availableWidth), prompt.Length + 4));
            if (width < 5)
                return 1;
            int innerWidth = width - 4;
            return 2 + Math.Max(1, (prompt.Length + innerWidth - 1) / innerWidth);
        }

        private static List<string> BuildImagePromptBox(
            FrozenImage image,
            int availableWidth)
        {
            string prompt = GetImageLookPrompt(image);
            int width = Math.Max(1, Math.Min(Math.Max(1, availableWidth), prompt.Length + 4));
            var rows = new List<string>();
            if (width < 5)
            {
                rows.Add(prompt.Substring(0, Math.Min(width, prompt.Length)));
                return rows;
            }

            string border = "+" + new string('-', width - 2) + "+";
            rows.Add(border);
            int innerWidth = width - 4;
            int offset = 0;
            do
            {
                int count = Math.Min(innerWidth, Math.Max(0, prompt.Length - offset));
                string part = count > 0 ? prompt.Substring(offset, count) : String.Empty;
                rows.Add("| " + part.PadRight(innerWidth) + " |");
                offset += count;
            }
            while (offset < prompt.Length);
            rows.Add(border);
            return rows;
        }

        private static string GetImageLookPrompt(FrozenImage image)
        {
            return Lang.Get(image.IsOversized
                ? TextId.ImageTooLargePrompt
                : TextId.ImageStronglyCompressedPrompt);
        }

        private static void RecoverChatLayout()
        {
            RedrawChatLayoutLocked();
        }

        private static void RecoverInputLayout()
        {
            try
            {
                RecoverChatLayout();
            }
            catch (Exception)
            {
                renderedInputRows = 0;
                renderedInputWidth = 0;
            }
        }

        private static int GetContentLeft()
        {
            return ConsoleGraphic.ContentLeft;
        }

        private static int GetContentWidth()
        {
            return ConsoleGraphic.ContentWidth;
        }

        private static void MoveCursorToContentColumn()
        {
            if (ConsoleGraphic.Enabled && Console.CursorLeft == 0)
                Console.SetCursorPosition(1, Console.CursorTop);
        }
    }
}
