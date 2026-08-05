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
    public class UserInterface : NetWorker
    {
        private sealed class ChatHistoryEntry
        {
            public ChatHistoryEntry(string text, ConsoleColor? forcedColor)
            {
                Text = text;
                ForcedColor = forcedColor;
            }

            public string Text { get; }
            public ConsoleColor? ForcedColor { get; }
        }

        private const int DefaultConnectionAttempts = 3;
        private const int ConnectionTimeoutMilliseconds = 3000;
        private const int RetryDelayMilliseconds = 1000;
        private const int MaxVisibleInputRows = 3;
        private const int MaxChatHistoryLines = 200;
        private const int ResizePollMilliseconds = 100;
        private const int ResizeSettleMilliseconds = 180;

        private static readonly ConsoleGraphic graphic = new ConsoleGraphic();
        private static readonly object consoleLock = new object();
        private static readonly StringBuilder inputBuffer = new StringBuilder();
        private static readonly List<ChatHistoryEntry> chatHistory = new List<ChatHistoryEntry>();
        private static int isBusy;
        private static bool inputActive;
        private static int inputCursorIndex;
        private static int inputStartRow;
        private static int renderedInputRows;
        private static int renderedInputLeft;
        private static int renderedInputWidth;
        private static string inputPrompt = "";
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

        private static async Task ReceiveMessagesAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string message = await MessageProtocol.ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (TryApplySnakeUpdate(message) || SnakeProtocol.IsSnakeControlMessage(message))
                        continue;

                    string localizedSystemMessage;
                    SystemMessageKind systemKind;
                    if (SystemMessageProtocol.TryLocalize(message, out localizedSystemMessage, out systemKind))
                    {
                        ConsoleColor? eventColor = systemKind == SystemMessageKind.UserJoined
                            ? ConsoleColor.Green
                            : (systemKind == SystemMessageKind.UserLeft ? ConsoleColor.Red : (ConsoleColor?)null);
                        WriteChatLine(">>> " + localizedSystemMessage, eventColor);
                    }
                    else
                    {
                        WriteChatLine(">>> " + message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Сеанс завершён локальным пользователем.
            }
            catch (EndOfStreamException)
            {
                // Сервер штатно закрыл соединение.
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
                if (connected)
                    WriteChatLine(Lang.Get(TextId.HubConnectionLost), ConsoleColor.Red);
                connected = false;
                client.Close();
            }
        }

        public static bool TryConnect()
        {
            if (!EnsureNickname())
                return false;

            Program.matrix(Lang.Get(TextId.EnterServerAddress));
            string ip = Console.ReadLine();
            if (String.IsNullOrWhiteSpace(ip))
                ip = "localhost";

            Program.matrix(Lang.Get(TextId.EnterServerPort));
            string rawPort = Console.ReadLine();
            int serverPort;
            if (String.IsNullOrWhiteSpace(rawPort))
                serverPort = 9091;
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
                            ConsoleColor.Yellow,
                            ServerInterface.IsRunning ? 3 : 0);
                    }
                    else
                    {
                        ConsoleGraphic.WriteContentLine(Lang.Get(TextId.ConnectingAttempt, address, port, attempt, attempts));
                    }

                    if (TryOpenConnection(client, address, port, out error))
                    {
                        try
                        {
                            RunClient(client);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            client.Close();
                            if (ConsoleGraphic.Enabled)
                                ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.SessionStartFailed, ex.Message), ConsoleColor.Red);
                            else
                                ConsoleGraphic.WriteContentLine(Lang.Get(TextId.SessionStartFailed, ex.Message));
                            return false;
                        }
                    }

                    client.Close();
                    if (ConsoleGraphic.Enabled)
                        ConsoleGraphic.WriteBottomStatus(Lang.Get(TextId.ConnectFailed, error), ConsoleColor.Red);
                    else
                        ConsoleGraphic.WriteContentLine(Lang.Get(TextId.ConnectFailed, error));
                    if (attempt < attempts)
                        Thread.Sleep(RetryDelayMilliseconds);
                }

                if (ConsoleGraphic.Enabled)
                {
                    ConsoleGraphic.WriteBottomStatus(
                        Lang.Get(TextId.HubUnavailableCompact, address, port),
                        ConsoleColor.Red);
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

        private static bool EnsureNickname()
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

        private static void RunClient(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            using (var authCancellation = new CancellationTokenSource())
            {
                CancellationToken authToken = authCancellation.Token;
                string authRequest = ReadWithTimeoutAsync(client, stream, authToken).GetAwaiter().GetResult();
                if (!DO_AUTH_MESSAGE.Equals(authRequest, StringComparison.Ordinal))
                    throw new IOException(Lang.Get(TextId.UnknownAuthProtocol));

                MessageProtocol.WriteStringAsync(stream, "REPLY:" + nickname, authToken).GetAwaiter().GetResult();
                string authResult = ReadWithTimeoutAsync(client, stream, authToken).GetAwaiter().GetResult();
                if (!AUTH_OK_MESSAGE.Equals(authResult, StringComparison.Ordinal))
                    throw new IOException(LocalizeAuthenticationError(authResult));
                SnakeProfile localSnakeProfile = new SnakeProfile
                {
                    Enabled = ConsoleGraphic.Enabled,
                    Paused = ConsoleGraphic.BorderSnakePaused,
                    DelayMilliseconds = ConsoleGraphic.BorderAnimationDelayMilliseconds,
                    Color = ConsoleGraphic.BorderSnakeColor,
                    Step = ConsoleGraphic.CurrentBorderSnakeStep
                };
                MessageProtocol.WriteStringAsync(
                    stream,
                    SnakeProtocol.CreateClientProfile(localSnakeProfile),
                    authToken).GetAwaiter().GetResult();
            }

            connected = true;
            ConsoleGraphic.ClearRemoteSnakes();
            IPEndPoint remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            isLocalHubSession = ServerInterface.IsRunning &&
                                remoteEndPoint != null &&
                                remoteEndPoint.Port == ServerInterface.ListeningPort &&
                                IPAddress.IsLoopback(remoteEndPoint.Address);
            showServerCard = ConsoleGraphic.Enabled && remoteEndPoint != null;
            serverCardAddress = isLocalHubSession
                ? ServerInterface.DisplayAddress
                : (remoteEndPoint == null ? "?" : remoteEndPoint.Address.ToString());
            serverCardPort = remoteEndPoint == null ? 0 : remoteEndPoint.Port;
            ConsoleGraphic.SetReservedBottomRows(showServerCard ? 2 : 0);
            graphic.Clear();
            ResetChatSessionLayout();
            if (showServerCard)
                ConsoleGraphic.DrawServerEndpointCard(serverCardAddress, serverCardPort);
            WriteChatLine(Lang.Get(TextId.ConnectedCommands, client.Client.RemoteEndPoint), ConsoleColor.Green);

            var sessionCancellation = new CancellationTokenSource();
            Task receiverTask = ReceiveMessagesAsync(client, stream, sessionCancellation.Token);

            try
            {
                while (connected)
                {
                    string message = ReadChatMessage();
                    if (message == null || message.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                        break;
                    if (message.Equals("/status", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteChatLine(ServerInterface.IsRunning
                            ? ServerInterface.PortMappingStatus
                            : Lang.Get(TextId.LocalHubNotRunning));
                        continue;
                    }
                    if (message.Equals("/stop", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isLocalHubSession)
                        {
                            WriteChatLine(Lang.Get(TextId.StoppingLocalHub), ConsoleColor.Yellow);
                            connected = false;
                            ServerInterface.StopServer();
                            break;
                        }

                        if (!ConsoleGraphic.Enabled)
                        {
                            WriteChatLine(Lang.Get(TextId.NoActiveSnake), ConsoleColor.Yellow);
                            continue;
                        }

                        bool paused = ConsoleGraphic.ToggleBorderSnakePause();
                        SnakeProfile updatedProfile = new SnakeProfile
                        {
                            Enabled = true,
                            Paused = paused,
                            DelayMilliseconds = ConsoleGraphic.BorderAnimationDelayMilliseconds,
                            Color = ConsoleGraphic.BorderSnakeColor,
                            Step = ConsoleGraphic.CurrentBorderSnakeStep
                        };
                        MessageProtocol.WriteStringAsync(
                            stream,
                            SnakeProtocol.CreateClientProfile(updatedProfile),
                            sessionCancellation.Token).GetAwaiter().GetResult();
                        WriteChatLine(paused
                            ? Lang.Get(TextId.SnakePaused)
                            : Lang.Get(TextId.SnakeResumed),
                            paused ? ConsoleColor.Yellow : ConsoleColor.Green);
                        continue;
                    }
                    if (String.IsNullOrWhiteSpace(message))
                        continue;

                    MessageProtocol.WriteStringAsync(stream, message, sessionCancellation.Token).GetAwaiter().GetResult();
                    WriteChatLine($"<<< [{nickname}]: {message}");
                }
            }
            catch (IOException)
            {
                WriteChatLine(Lang.Get(TextId.SendFailedClosed), ConsoleColor.Red);
            }
            finally
            {
                connected = false;
                sessionCancellation.Cancel();
                client.Close();
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
            }
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

            if (kind == SnakeUpdateKind.Set && ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.SetRemoteSnake(
                    participant,
                    profile.DelayMilliseconds,
                    profile.Color,
                    profile.Step,
                    profile.Paused);
            }
            else
            {
                ConsoleGraphic.RemoveRemoteSnake(participant);
            }

            return true;
        }

        private static async Task<string> ReadWithTimeoutAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
            Task<string> readTask = MessageProtocol.ReadStringAsync(stream, cancellationToken);
            using (var timeoutCancellation = new CancellationTokenSource())
            {
                Task timeoutTask = Task.Delay(ConnectionTimeoutMilliseconds, timeoutCancellation.Token);
                Task completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
                if (completed == readTask)
                {
                    timeoutCancellation.Cancel();
                    return await readTask.ConfigureAwait(false);
                }

                client.Close();
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
                RenderInputLine();
            }

            while (connected)
            {
                CheckForConsoleResize();
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(20);
                    continue;
                }

                ConsoleKeyInfo key = Console.ReadKey(true);
                lock (consoleLock)
                {
                    if (key.Key == ConsoleKey.Enter)
                    {
                        string message = inputBuffer.ToString();
                        EraseRenderedInput();
                        inputActive = false;
                        inputBuffer.Clear();
                        inputCursorIndex = 0;
                        return message;
                    }

                    if (HandleInputKey(key) && EnsureConsoleGeometryLocked())
                        RenderInputLine();
                }
            }

            lock (consoleLock)
            {
                EraseRenderedInput();
                inputActive = false;
                inputBuffer.Clear();
                inputCursorIndex = 0;
            }
            return null;
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

            inputStartRow = ConsoleGraphic.EnsureContentSpace(inputStartRow, rows);
            renderedInputLeft = left;
            renderedInputWidth = width;
            renderedInputRows = rows;

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

        private static void WriteChatLine(string message, ConsoleColor? forcedColor = null)
        {
            lock (consoleLock)
            {
                bool consoleReady = EnsureConsoleGeometryLocked();
                string safeMessage = SanitizeForConsole(message);
                AppendChatHistoryLocked(safeMessage, forcedColor);
                if (!consoleReady)
                    return;

                bool restoreInput = inputActive;
                if (restoreInput)
                    EraseRenderedInput();

                try
                {
                    WriteWrappedChatLine(safeMessage, forcedColor);
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

                if (restoreInput)
                {
                    inputStartRow = Console.CursorTop;
                    RenderInputLine();
                }
            }
        }

        private static void WriteWrappedChatLine(string message, ConsoleColor? forcedColor = null)
        {
            if (!ConsoleGraphic.Enabled)
            {
                MoveCursorToContentColumn();
                Console.WriteLine(message);
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
                    WriteStyledChatText(message, offset, count, forcedColor);
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
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(visibleText.Substring(sourceIndex, promptCharacters));
            }

            int messageCharacters = count - promptCharacters;
            if (messageCharacters > 0)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(visibleText.Substring(sourceIndex + promptCharacters, messageCharacters));
            }

            Console.ResetColor();
        }

        private static void WriteStyledChatText(string message, int offset, int count, ConsoleColor? forcedColor)
        {
            int end = offset + count;
            int position = offset;
            while (position < end)
            {
                ConsoleColor color = forcedColor ?? GetChatColor(message, position);
                int runEnd = position + 1;
                while (runEnd < end && (forcedColor ?? GetChatColor(message, runEnd)) == color)
                    runEnd++;

                Console.ForegroundColor = color;
                Console.Write(message.Substring(position, runEnd - position));
                position = runEnd;
            }

            Console.ResetColor();
        }

        private static ConsoleColor GetChatColor(string message, int position)
        {
            bool outgoing = message.StartsWith("<<< ", StringComparison.Ordinal);
            bool incoming = message.StartsWith(">>> ", StringComparison.Ordinal);
            if (outgoing || incoming)
            {
                if (position < 3)
                    return outgoing ? ConsoleColor.Cyan : ConsoleColor.Green;

                int colon = message.IndexOf(':', 4);
                if (colon >= 0 && position <= colon)
                    return outgoing ? ConsoleColor.DarkCyan : ConsoleColor.Yellow;

                return ConsoleColor.White;
            }

            if (ContainsAny(message, "не удалось", "потеряно", "недоступен", "закрыто", "ошибка",
                "failed", "lost", "unavailable", "closed", "error"))
                return ConsoleColor.Red;
            if (ContainsAny(message, "подключено", "успешно", "запущен", "продолжила",
                "connected", "success", "started", "resumed"))
                return ConsoleColor.Green;
            if (ContainsAny(message, "ожид", "настрой", "попытк", "остановлена", "останавливаю",
                "wait", "configur", "attempt", "paused", "stopping"))
                return ConsoleColor.Yellow;

            return ConsoleColor.DarkGray;
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            foreach (string value in values)
            {
                if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
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
            lock (consoleLock)
            {
                chatHistory.Clear();
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

        private static void AppendChatHistoryLocked(string message, ConsoleColor? forcedColor)
        {
            chatHistory.Add(new ChatHistoryEntry(message ?? String.Empty, forcedColor));
            if (chatHistory.Count > MaxChatHistoryLines)
                chatHistory.RemoveRange(0, chatHistory.Count - MaxChatHistoryLines);
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

        private static bool RedrawChatLayoutLocked()
        {
            ConsoleGraphic.ConsoleGeometry targetGeometry;
            if (!ConsoleGraphic.TryCaptureConsoleGeometry(out targetGeometry))
                return false;

            try
            {
                renderedInputRows = 0;
                renderedInputWidth = 0;
                if (!graphic.TryClear(0, 0))
                {
                    MarkConsoleResizePendingLocked();
                    return false;
                }

                if (showServerCard && ConsoleGraphic.Enabled)
                    ConsoleGraphic.DrawServerEndpointCard(serverCardAddress, serverCardPort);

                foreach (ChatHistoryEntry historyLine in chatHistory)
                    WriteWrappedChatLine(historyLine.Text, historyLine.ForcedColor);

                inputStartRow = Console.CursorTop;
                if (inputActive)
                    RenderInputLineCore();

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
