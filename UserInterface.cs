using System;
using System.IO;
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

        private static readonly ConsoleGraphic graphic = new ConsoleGraphic();
        private static readonly object consoleLock = new object();
        private static readonly StringBuilder inputBuffer = new StringBuilder();
        private static int isBusy;
        private static bool inputActive;
        private static int inputCursorIndex;
        private static int inputStartRow;
        private static int renderedInputRows;
        private static int renderedInputLeft;
        private static int renderedInputWidth;
        private static string inputPrompt = "";

        private static async Task ReceiveMessagesAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string message = await MessageProtocol.ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
                    WriteChatLine("<<< " + message);
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
                    ConsoleGraphic.WriteContentLine($">>> Подключение к {address}:{port}, попытка {attempt} из {attempts}...");

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
                            ConsoleGraphic.WriteContentLine("Не удалось начать сеанс: " + ex.Message);
                            return false;
                        }
                    }

                    client.Close();
                    ConsoleGraphic.WriteContentLine("Не удалось подключиться: " + error);
                    if (attempt < attempts)
                        Thread.Sleep(RetryDelayMilliseconds);
                }

                ConsoleGraphic.WriteContentLine($"Хаб {address}:{port} недоступен после {attempts} попыток. Возвращаюсь в меню.");
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
            }

            connected = true;
            graphic.Clear();
            WriteChatLine($"Подключено к {client.Client.RemoteEndPoint}. Команды: /status, /exit.");

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
            }
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

                    if (HandleInputKey(key))
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
                RecoverInputLayout();
            }
            catch (IOException)
            {
                RecoverInputLayout();
            }
        }

        private static void RenderInputLineCore()
        {
            ConsoleGraphic.AlignViewport();

            int left = GetContentLeft();
            int width = GetContentWidth();
            int availableRows = ConsoleGraphic.Enabled
                ? Math.Max(1, ConsoleGraphic.ContentBottom - ConsoleGraphic.ContentTop + 1)
                : Math.Max(1, Console.BufferHeight - Console.CursorTop);
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
                    Console.Write(visibleText.Substring(sourceIndex, count));
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
                bool restoreInput = inputActive;
                if (restoreInput)
                    EraseRenderedInput();

                try
                {
                    WriteWrappedChatLine(SanitizeForConsole(message));
                }
                catch (ArgumentOutOfRangeException)
                {
                    RecoverChatLayout();
                    WriteWrappedChatLine(SanitizeForConsole(message));
                }
                catch (IOException)
                {
                    connected = false;
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
                    Console.Write(message.Substring(offset, count));
                    offset += count;
                }

                Console.SetCursorPosition(
                    ConsoleGraphic.ContentLeft,
                    Math.Min(ConsoleGraphic.ContentBottom, row + 1));
            }
            while (offset < message.Length);
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

        private static void RecoverChatLayout()
        {
            renderedInputRows = 0;
            renderedInputWidth = 0;
            graphic.Clear(0, 0);
            inputStartRow = Console.CursorTop;
        }

        private static void RecoverInputLayout()
        {
            try
            {
                RecoverChatLayout();
                if (inputActive)
                    RenderInputLineCore();
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
