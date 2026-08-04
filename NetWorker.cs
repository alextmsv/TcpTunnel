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

        private static readonly ConsoleGraphic graphic = new ConsoleGraphic();
        private static readonly SemaphoreSlim natLock = new SemaphoreSlim(1, 1);
        private static NatDevice mappedDevice;
        private static Mapping activeMapping;

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

                Program.matrix("Некорректный псевдоним", 20, ConsoleColor.DarkRed);
                graphic.Clear();
                Program.matrix("Введите свой псевдоним: ", 20, ConsoleColor.DarkYellow);
                name = Console.ReadLine();
                if (name == null)
                    return null;
            }
            return name;
        }

        public async static Task<string> TryOpenPortAsync(int port, CancellationToken cancellationToken)
        {
            try
            {
                await natLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var discoverer = new NatDiscoverer();
                    NatDevice device;
                    using (var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        discoveryCancellation.CancelAfter(TimeSpan.FromSeconds(8));
                        device = await discoverer
                            .DiscoverDeviceAsync(PortMapper.Upnp | PortMapper.Pmp, discoveryCancellation)
                            .ConfigureAwait(false);
                    }
                    cancellationToken.ThrowIfCancellationRequested();

                    Mapping mapping = new Mapping(Protocol.Tcp, port, port);
                    try { await device.DeletePortMapAsync(mapping).ConfigureAwait(false); }
                    catch (MappingException) { }

                    await device.CreatePortMapAsync(mapping).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { await device.DeletePortMapAsync(mapping).ConfigureAwait(false); } catch { }
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    mappedDevice = device;
                    activeMapping = mapping;
                    return $"UPnP: TCP-порт {port} успешно проброшен.";
                }
                finally
                {
                    natLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return cancellationToken.IsCancellationRequested
                    ? "UPnP: настройка отменена."
                    : "UPnP: роутер не найден за 8 секунд.";
            }
            catch (Exception ex)
            {
                return "UPnP недоступен: " + ex.Message;
            }
        }

        public async static Task<string> TryClosePortAsync()
        {
            await natLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (mappedDevice == null || activeMapping == null)
                    return "UPnP: активного проброса нет.";

                try
                {
                    await mappedDevice.DeletePortMapAsync(activeMapping).ConfigureAwait(false);
                    return $"UPnP: TCP-порт {activeMapping.PublicPort} закрыт.";
                }
                catch (Exception ex)
                {
                    return "UPnP: не удалось удалить проброс: " + ex.Message;
                }
                finally
                {
                    mappedDevice = null;
                    activeMapping = null;
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
                ConsoleGraphic.WriteContentLine($"Упс... {ex.Message}");
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
                        await client.SendAsync(AUTH_ERROR_MESSAGE + ": неверный запрос", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    string requestedNickname = authReply.Substring("REPLY:".Length).Trim();
                    if (!IsNicknameValid(requestedNickname))
                    {
                        await client.SendAsync(AUTH_ERROR_MESSAGE + ": некорректный псевдоним", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (!broadcaster.TryAuthenticate(client, requestedNickname))
                    {
                        await client.SendAsync(AUTH_ERROR_MESSAGE + ": псевдоним уже занят", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    authenticated = true;
                    authenticatedNickname = requestedNickname;
                    IPEndPoint remoteEndPoint = client.TcpClient.Client.RemoteEndPoint as IPEndPoint;
                    client.IpAddress = remoteEndPoint == null ? "unknown" : remoteEndPoint.Address.ToString();
                    await client.SendAsync(AUTH_OK_MESSAGE, serverCancellationToken).ConfigureAwait(false);
                }

                await broadcaster.BroadcastAsync(null, $"{authenticatedNickname} подключился к хабу!", serverCancellationToken).ConfigureAwait(false);

                while (!serverCancellationToken.IsCancellationRequested)
                {
                    string message = await MessageProtocol.ReadStringAsync(client.Stream, serverCancellationToken).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(message))
                        continue;

                    if (message.Length > MessageProtocol.MaxMessageCharacters)
                    {
                        await client.SendAsync("Сообщение слишком длинное.", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (!client.TryConsumeMessageToken())
                    {
                        await client.SendAsync("Слишком много сообщений. Соединение закрыто.", serverCancellationToken).ConfigureAwait(false);
                        return;
                    }

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
                        await broadcaster.BroadcastAsync(null, $"{authenticatedNickname} отключился от хаба.", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
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
                throw new TimeoutException("Истёк таймаут авторизации.");
            }
        }
    }
}
