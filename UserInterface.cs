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
        private static readonly List<string> chatHistory = new List<string>();
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

                    WriteChatLine(">>> " + message);
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
                    WriteChatLine("Соединение с хабом потеряно.");
                connected = false;
                client.Close();
            }
        }

        public static bool TryConnect()
        {
            if (!EnsureNickname())
                return false;

            Program.matrix("Введите IP адрес или имя сервера [localhost]: ");
            string ip = Console.ReadLine();
            if (String.IsNullOrWhiteSpace(ip))
                ip = "localhost";

            Program.matrix("Введите порт сервера [9091]: ");
            string rawPort = Console.ReadLine();
            int serverPort;
            if (String.IsNullOrWhiteSpace(rawPort))
                serverPort = 9091;
            else if (!Int32.TryParse(rawPort, out serverPort) || serverPort < 1 || serverPort > 65535)
            {
                ConsoleGraphic.WriteContentLine("Порт должен быть числом от 1 до 65535.");
                return false;
            }

            return DoConnect(ip, serverPort, DefaultConnectionAttempts);
        }

        public static bool DoConnect(string address, int port, int attempts = DefaultConnectionAttempts)
        {
            if (String.IsNullOrWhiteSpace(address))
            {
                ConsoleGraphic.WriteContentLine("Не указано имя или IP-адрес сервера.");
                return false;
            }

            if (port < 1 || port > 65535)
            {
                ConsoleGraphic.WriteContentLine("Порт должен быть в диапазоне от 1 до 65535.");
                return false;
            }

            if (!EnsureNickname())
                return false;

            if (Interlocked.CompareExchange(ref isBusy, 1, 0) != 0)
            {
                ConsoleGraphic.WriteContentLine("Подключение уже выполняется.");
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
                            $"Подключение к {address}:{port} [{attempt}/{attempts}]",
                            ConsoleColor.Yellow,
                            ServerInterface.IsRunning ? 3 : 0);
                    }
                    else
                    {
                        ConsoleGraphic.WriteContentLine($">>> Подключение к {address}:{port}, попытка {attempt} из {attempts}...");
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
                                ConsoleGraphic.WriteBottomStatus("Не удалось начать сеанс: " + ex.Message, ConsoleColor.Red);
                            else
                                ConsoleGraphic.WriteContentLine("Не удалось начать сеанс: " + ex.Message);
                            return false;
                        }
                    }

                    client.Close();
                    if (ConsoleGraphic.Enabled)
                        ConsoleGraphic.WriteBottomStatus("Не удалось подключиться: " + error, ConsoleColor.Red);
                    else
                        ConsoleGraphic.WriteContentLine("Не удалось подключиться: " + error);
                    if (attempt < attempts)
                        Thread.Sleep(RetryDelayMilliseconds);
                }

                if (ConsoleGraphic.Enabled)
                {
                    ConsoleGraphic.WriteBottomStatus(
                        $"Хаб {address}:{port} недоступен. Возвращаюсь в меню",
                        ConsoleColor.Red);
                }
                else
                {
                    ConsoleGraphic.WriteContentLine($"Хаб {address}:{port} недоступен после {attempts} попыток. Возвращаюсь в меню.");
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

            Program.matrix("Введите свой псевдоним: ");
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
                        error = "превышено время ожидания";
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
                    throw new IOException("Сервер использует неизвестный протокол авторизации.");

                MessageProtocol.WriteStringAsync(stream, "REPLY:" + nickname, authToken).GetAwaiter().GetResult();
                string authResult = ReadWithTimeoutAsync(client, stream, authToken).GetAwaiter().GetResult();
                if (!AUTH_OK_MESSAGE.Equals(authResult, StringComparison.Ordinal))
                    throw new IOException(authResult.StartsWith(AUTH_ERROR_MESSAGE, StringComparison.Ordinal)
                        ? authResult
                        : "Сервер отклонил псевдоним.");
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
                : (remoteEndPoint == null ? "unknown" : remoteEndPoint.Address.ToString());
            serverCardPort = remoteEndPoint == null ? 0 : remoteEndPoint.Port;
            ConsoleGraphic.SetReservedBottomRows(showServerCard ? 2 : 0);
            graphic.Clear();
            ResetChatSessionLayout();
            if (showServerCard)
                ConsoleGraphic.DrawServerEndpointCard(serverCardAddress, serverCardPort);
            WriteChatLine($"Подключено к {client.Client.RemoteEndPoint}. Команды: /status, /stop, /exit.");

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
                            : "В этом процессе локальный Hub не запущен.");
                        continue;
                    }
                    if (message.Equals("/stop", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isLocalHubSession)
                        {
                            WriteChatLine("Останавливаю локальный хаб...");
                            connected = false;
                            ServerInterface.StopServer();
                            break;
                        }

                        if (!ConsoleGraphic.Enabled)
                        {
                            WriteChatLine("ConsoleGraphics выключена: активной змейки нет.");
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
                            ? "Личная змейка остановлена и синхронизирована."
                            : "Личная змейка продолжила движение и синхронизирована.");
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
                WriteChatLine("Не удалось отправить сообщение: соединение закрыто.");
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
                throw new TimeoutException("Сервер не ответил вовремя.");
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
            catch (ArgumentOutOfRangeException)
            {
                // The terminal was resized while the input line was being redrawn.
            }
            catch (IOException)
            {
                // The console handle can disappear briefly while a terminal closes.
            }

            renderedInputRows = 0;
            renderedInputWidth = 0;
        }

        private static void WriteChatLine(string message)
        {
            lock (consoleLock)
            {
                bool consoleReady = EnsureConsoleGeometryLocked();
                string safeMessage = SanitizeForConsole(message);
                AppendChatHistoryLocked(safeMessage);
                if (!consoleReady)
                    return;

                bool restoreInput = inputActive;
                if (restoreInput)
                    EraseRenderedInput();

                try
                {
                    WriteWrappedChatLine(safeMessage);
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

        private static void WriteWrappedChatLine(string message)
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
                    WriteStyledChatText(message, offset, count);
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

        private static void WriteStyledChatText(string message, int offset, int count)
        {
            int end = offset + count;
            int position = offset;
            while (position < end)
            {
                ConsoleColor color = GetChatColor(message, position);
                int runEnd = position + 1;
                while (runEnd < end && GetChatColor(message, runEnd) == color)
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
            bool serverEvent = incoming && !message.StartsWith(">>> [", StringComparison.Ordinal);
            if (serverEvent && message.IndexOf("подключился к хабу", StringComparison.OrdinalIgnoreCase) >= 0)
                return ConsoleColor.Green;
            if (serverEvent && message.IndexOf("отключился от хаба", StringComparison.OrdinalIgnoreCase) >= 0)
                return ConsoleColor.Red;

            if (outgoing || incoming)
            {
                if (position < 3)
                    return outgoing ? ConsoleColor.Cyan : ConsoleColor.Green;

                int colon = message.IndexOf(':', 4);
                if (colon >= 0 && position <= colon)
                    return outgoing ? ConsoleColor.DarkCyan : ConsoleColor.Yellow;

                return ConsoleColor.White;
            }

            if (ContainsAny(message, "не удалось", "потеряно", "недоступен", "закрыто", "ошибка"))
                return ConsoleColor.Red;
            if (ContainsAny(message, "подключено", "успешно", "запущен", "продолжила"))
                return ConsoleColor.Green;
            if (ContainsAny(message, "ожид", "настрой", "попытк", "остановлена", "останавливаю"))
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

        private static void AppendChatHistoryLocked(string message)
        {
            chatHistory.Add(message ?? String.Empty);
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

                foreach (string historyLine in chatHistory)
                    WriteWrappedChatLine(historyLine);

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
