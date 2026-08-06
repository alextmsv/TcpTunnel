using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TCPTunnel
{
    internal enum AppLanguage
    {
        Russian,
        English
    }

    internal enum TextId
    {
        SelfTestOk, SelfTestFailed, Goodbye, Welcome,
        MenuTitle, HostServer, EnterOwnHub, ConnectToHub, EnterNickname,
        ChangeNickname, GraphicsOptions, Exit, LanguageMenu,
        PingArgument, PingCommandUsage, ConnectArgument, ServerAlive, ServerDead,
        ChangeNicknameWelcome, ChangeIdentity, EnterNewNickname, CheckingName,
        NicknameRules, TryAgain, IdentityUpdated, GoodName, MyName, TookYourTime,
        GraphicsEnabled, GraphicsDisabled, Customization, Back, Snake,
        SnakeCustomization, Speed, Color, SpeedFast, SpeedNormal, SpeedCalm,
        SpeedSlow, ColorGreen, ColorCyan, ColorYellow, ColorRed, ColorWhite, ColorBlue,
        HubSetup, ChooseTcpPort, EnterServerPort, InvalidPortNumber, StartingListener,
        CreateHubFailed, ListenerStarted, ConfiguringNat, HubStarted,
        LocalClientBackground, LocalClientConnecting, PortOutOfRange, HubAlreadyRunning,
        NatNotStarted, NatTrying, NatPortMapped, NatPortMappedRenewable, NatFailed,
        NatCancelled, NatRouterTimeout, NatUnavailable, NatDeviceNotFound,
        NatRuleTimeout, NatRuleRejected, NatNoActiveRule, NatPortClosed,
        NatDeleteFailed, NatError, UnexpectedError,
        EnterServerAddress, MissingServerAddress, ConnectionInProgress, ConnectingCompact,
        ConnectingAttempt, SessionStartFailed, ConnectFailed, HubUnavailableCompact,
        HubUnavailableAttempts, EnterYourNickname, ConnectionTimedOut,
        UnknownAuthProtocol, NicknameRejected, ConnectedCommands, LocalHubNotRunning,
        StoppingLocalHub, NoActiveSnake, SnakePaused, SnakeResumed,
        SendFailedClosed, ServerDidNotRespond, HubConnectionLost,
        UserJoined, UserLeft, MessageTooLong, TooManyMessages,
        AuthInvalidRequest, AuthInvalidNickname, AuthNicknameTaken, AuthTimedOut,
        ClientNotReceiving, FrameTooLarge, InvalidFrameLength, InvalidFramePrefix,
        EmbeddedLibraryReadFailed, HubOnline, HubOffline
    }

    internal sealed class LocalizedText
    {
        public LocalizedText(string russian, string english)
        {
            Russian = russian;
            English = english;
        }

        public string Russian { get; }
        public string English { get; }
    }

    internal static class Lang
    {
        private static readonly Dictionary<TextId, LocalizedText> catalog =
            new Dictionary<TextId, LocalizedText>
            {
                { TextId.SelfTestOk, T("Самопроверка TCPTunnel: OK", "TCPTunnel self-test: OK") },
                { TextId.SelfTestFailed, T("Самопроверка TCPTunnel: ОШИБКА", "TCPTunnel self-test: FAILED") },
                { TextId.Goodbye, T("До свидания", "Goodbye") },
                { TextId.Welcome, T("Добро пожаловать в чат", "Welcome to the chat") },
                { TextId.MenuTitle, T("Меню", "Menu") },
                { TextId.HostServer, T("Создать сервер", "Host a server") },
                { TextId.EnterOwnHub, T("Войти в свой хаб", "Enter your hub") },
                { TextId.ConnectToHub, T("Войти на сервер", "Connect to a hub") },
                { TextId.EnterNickname, T("Ввести псевдоним?", "Set a nickname?") },
                { TextId.ChangeNickname, T("Сменить псевдоним", "Change nickname") },
                { TextId.GraphicsOptions, T("Настройки ConsoleGraphics", "ConsoleGraphics Options") },
                { TextId.Exit, T("Выход", "Exit") },
                { TextId.LanguageMenu, T("Язык (Language)", "Language (язык)") },
                { TextId.PingArgument, T("Параметр -ping ожидает адрес в формате host:port.", "The -ping option expects an address in host:port format.") },
                { TextId.PingCommandUsage, T("Использование: /ping host:port", "Usage: /ping host:port") },
                { TextId.ConnectArgument, T("Параметр -connect ожидает адрес в формате host:port.", "The -connect option expects an address in host:port format.") },
                { TextId.ServerAlive, T("Сервер {0}:{1} работает!", "Server {0}:{1} is online!") },
                { TextId.ServerDead, T("Сервер {0}:{1} недоступен.", "Server {0}:{1} is offline.") },
                { TextId.ChangeNicknameWelcome, T("Добро пожаловать в процедуру смены ника в TCPTunnel", "Welcome to TCPTunnel nickname setup") },
                { TextId.ChangeIdentity, T("СМЕНА ПСЕВДОНИМА", "CHANGE IDENTITY") },
                { TextId.EnterNewNickname, T("Введите новый псевдоним", "Enter a new nickname") },
                { TextId.CheckingName, T("Проверка имени{0}", "Checking nickname{0}") },
                { TextId.NicknameRules, T("От 3 до 20 символов, без пробелов и спецсимволов", "3–20 characters, no spaces or special characters") },
                { TextId.TryAgain, T("Попробуйте ещё раз", "Please try again") },
                { TextId.IdentityUpdated, T("ПСЕВДОНИМ ИЗМЕНЁН", "IDENTITY UPDATED") },
                { TextId.GoodName, T("Хорошее имя", "Nice name") },
                { TextId.MyName, T("Это моё имя >:(", "This is MY name >:(") },
                { TextId.TookYourTime, T("Долго придумывал лол ", "That took a while lol ") },
                { TextId.GraphicsEnabled, T("ConsoleGraphics: включена", "ConsoleGraphics: enabled") },
                { TextId.GraphicsDisabled, T("ConsoleGraphics: выключена", "ConsoleGraphics: disabled") },
                { TextId.Customization, T("Кастомизация", "Customization") },
                { TextId.Back, T("Назад", "Back") },
                { TextId.Snake, T("Змейка", "Snake") },
                { TextId.SnakeCustomization, T("Кастомизация змейки", "Snake customization") },
                { TextId.Speed, T("Скорость: {0}", "Speed: {0}") },
                { TextId.Color, T("Цвет: {0}", "Color: {0}") },
                { TextId.SpeedFast, T("быстрая (35 мс)", "fast (35 ms)") },
                { TextId.SpeedNormal, T("обычная (75 мс)", "normal (75 ms)") },
                { TextId.SpeedCalm, T("спокойная (125 мс)", "calm (125 ms)") },
                { TextId.SpeedSlow, T("медленная (200 мс)", "slow (200 ms)") },
                { TextId.ColorGreen, T("зелёный", "green") },
                { TextId.ColorCyan, T("голубой", "cyan") },
                { TextId.ColorYellow, T("жёлтый", "yellow") },
                { TextId.ColorRed, T("красный", "red") },
                { TextId.ColorWhite, T("белый", "white") },
                { TextId.ColorBlue, T("синий", "blue") },
                { TextId.HubSetup, T("НАСТРОЙКА ХАБА", "HUB SETUP") },
                { TextId.ChooseTcpPort, T("Выберите TCP-порт", "Choose a TCP port") },
                { TextId.EnterServerPort, T("Введите порт сервера [9091]: ", "Enter server port [9091]: ") },
                { TextId.InvalidPortNumber, T("Порт должен быть числом от 1 до 65535.", "Port must be a number from 1 to 65535.") },
                { TextId.StartingListener, T("Запуск TCP-слушателя...", "Starting TCP listener...") },
                { TextId.CreateHubFailed, T("Не удалось создать хаб: {0}", "Could not create hub: {0}") },
                { TextId.ListenerStarted, T("[+] TCP-слушатель запущен", "[+] TCP listener started") },
                { TextId.ConfiguringNat, T("Настройка UPnP / NAT-PMP...", "Configuring UPnP / NAT-PMP...") },
                { TextId.HubStarted, T("Хаб запущен на TCP-порту {0}.", "Hub started on TCP port {0}.") },
                { TextId.LocalClientBackground, T("Локальный клиент подключается к 127.0.0.1; проброс порта настраивается в фоне.", "The local client is connecting to 127.0.0.1; port mapping continues in the background.") },
                { TextId.LocalClientConnecting, T("Локальный клиент подключается...", "Connecting local client...") },
                { TextId.PortOutOfRange, T("порт должен быть в диапазоне от 1 до 65535", "port must be in the range 1 to 65535") },
                { TextId.HubAlreadyRunning, T("хаб уже работает на порту {0}", "a hub is already running on port {0}") },
                { TextId.NatNotStarted, T("Автопроброс портов ещё не запускался.", "Automatic port mapping has not started yet.") },
                { TextId.NatTrying, T("Автопроброс: сначала UPnP, затем NAT-PMP...", "Automatic mapping: trying UPnP, then NAT-PMP...") },
                { TextId.NatPortMapped, T("{0}: TCP-порт {1} успешно проброшен.", "{0}: TCP port {1} mapped successfully.") },
                { TextId.NatPortMappedRenewable, T("{0}: TCP-порт {1} успешно проброшен; аренда продлевается автоматически.", "{0}: TCP port {1} mapped successfully; the lease renews automatically.") },
                { TextId.NatFailed, T("Автопроброс не удался. UPnP: {0}; NAT-PMP: {1}", "Automatic port mapping failed. UPnP: {0}; NAT-PMP: {1}") },
                { TextId.NatCancelled, T("Автопроброс: настройка отменена.", "Automatic port mapping: setup cancelled.") },
                { TextId.NatRouterTimeout, T("Автопроброс: роутер не ответил вовремя.", "Automatic port mapping: router did not respond in time.") },
                { TextId.NatUnavailable, T("Автопроброс недоступен: {0}", "Automatic port mapping unavailable: {0}") },
                { TextId.NatDeviceNotFound, T("устройство не найдено за {0} секунд", "device not found within {0} seconds") },
                { TextId.NatRuleTimeout, T("истёк таймаут создания правила", "port mapping rule creation timed out") },
                { TextId.NatRuleRejected, T("роутер отклонил правило", "router rejected the mapping rule") },
                { TextId.NatNoActiveRule, T("Автопроброс: активного правила нет.", "Automatic port mapping: no active rule.") },
                { TextId.NatPortClosed, T("{0}: TCP-порт {1} закрыт.", "{0}: TCP port {1} closed.") },
                { TextId.NatDeleteFailed, T("Автопроброс: не удалось удалить правило: {0}", "Automatic port mapping: failed to remove rule: {0}") },
                { TextId.NatError, T("ошибка {0}: {1}", "error {0}: {1}") },
                { TextId.UnexpectedError, T("Упс... {0}", "Oops... {0}") },
                { TextId.EnterServerAddress, T("Введите IP-адрес или имя сервера [localhost]: ", "Enter server IP address or host name [localhost]: ") },
                { TextId.MissingServerAddress, T("Не указано имя или IP-адрес сервера.", "Server name or IP address is missing.") },
                { TextId.ConnectionInProgress, T("Подключение уже выполняется.", "A connection attempt is already in progress.") },
                { TextId.ConnectingCompact, T("Подключение к {0}:{1} [{2}/{3}]", "Connecting to {0}:{1} [{2}/{3}]") },
                { TextId.ConnectingAttempt, T(">>> Подключение к {0}:{1}, попытка {2} из {3}...", ">>> Connecting to {0}:{1}, attempt {2} of {3}...") },
                { TextId.SessionStartFailed, T("Не удалось начать сеанс: {0}", "Could not start session: {0}") },
                { TextId.ConnectFailed, T("Не удалось подключиться: {0}", "Could not connect: {0}") },
                { TextId.HubUnavailableCompact, T("Хаб {0}:{1} недоступен. Возвращаюсь в меню", "Hub {0}:{1} is unavailable. Returning to menu") },
                { TextId.HubUnavailableAttempts, T("Хаб {0}:{1} недоступен после {2} попыток. Возвращаюсь в меню.", "Hub {0}:{1} is unavailable after {2} attempts. Returning to menu.") },
                { TextId.EnterYourNickname, T("Введите свой псевдоним: ", "Enter your nickname: ") },
                { TextId.ConnectionTimedOut, T("превышено время ожидания", "connection timed out") },
                { TextId.UnknownAuthProtocol, T("Сервер использует неизвестный протокол авторизации.", "The server uses an unknown authentication protocol.") },
                { TextId.NicknameRejected, T("Сервер отклонил псевдоним.", "The server rejected the nickname.") },
                { TextId.ConnectedCommands, T("Подключено к {0}. Команды: /status, /ping, /clear, /stop, /exit.", "Connected to {0}. Commands: /status, /ping, /clear, /stop, /exit.") },
                { TextId.LocalHubNotRunning, T("В этом процессе локальный хаб не запущен.", "No local hub is running in this process.") },
                { TextId.StoppingLocalHub, T("Останавливаю локальный хаб...", "Stopping local hub...") },
                { TextId.NoActiveSnake, T("ConsoleGraphics выключена: активной змейки нет.", "ConsoleGraphics is disabled: there is no active snake.") },
                { TextId.SnakePaused, T("Личная змейка остановлена и синхронизирована.", "Your snake has been paused and synchronized.") },
                { TextId.SnakeResumed, T("Личная змейка продолжила движение и синхронизирована.", "Your snake has resumed and synchronized.") },
                { TextId.SendFailedClosed, T("Не удалось отправить сообщение: соединение закрыто.", "Could not send message: connection is closed.") },
                { TextId.ServerDidNotRespond, T("Сервер не ответил вовремя.", "The server did not respond in time.") },
                { TextId.HubConnectionLost, T("Соединение с хабом потеряно.", "Connection to the hub was lost.") },
                { TextId.UserJoined, T("{0} подключился к хабу!", "{0} joined the hub!") },
                { TextId.UserLeft, T("{0} отключился от хаба.", "{0} left the hub.") },
                { TextId.MessageTooLong, T("Сообщение слишком длинное.", "Message is too long.") },
                { TextId.TooManyMessages, T("Слишком много сообщений. Соединение закрыто.", "Too many messages. Connection closed.") },
                { TextId.AuthInvalidRequest, T("Неверный запрос авторизации.", "Invalid authentication request.") },
                { TextId.AuthInvalidNickname, T("Некорректный псевдоним.", "Invalid nickname.") },
                { TextId.AuthNicknameTaken, T("Псевдоним уже занят.", "Nickname is already in use.") },
                { TextId.AuthTimedOut, T("Истёк таймаут авторизации.", "Authentication timed out.") },
                { TextId.ClientNotReceiving, T("Клиент не принимает сообщения.", "Client is not receiving messages.") },
                { TextId.FrameTooLarge, T("Размер сообщения {0} байт превышает лимит {1} байт.", "Message size of {0} bytes exceeds the {1}-byte limit.") },
                { TextId.InvalidFrameLength, T("Некорректная длина сообщения.", "Invalid message length.") },
                { TextId.InvalidFramePrefix, T("Некорректный префикс длины сообщения.", "Invalid message length prefix.") },
                { TextId.EmbeddedLibraryReadFailed, T("Не удалось прочитать встроенную библиотеку Open.Nat.", "Could not read the embedded Open.Nat library.") },
                { TextId.HubOnline, T("ХАБ В СЕТИ", "HUB ONLINE") },
                { TextId.HubOffline, T("ХАБ ОТКЛЮЧЁН", "HUB OFFLINE") }
            };

        private static AppLanguage current = DetectLanguage();

        public static AppLanguage Current => current;

        public static string Get(TextId id, params object[] arguments)
        {
            LocalizedText value;
            if (!catalog.TryGetValue(id, out value))
                return "[" + id + "]";

            string text = current == AppLanguage.Russian ? value.Russian : value.English;
            return arguments == null || arguments.Length == 0
                ? text
                : String.Format(CurrentCulture, text, arguments);
        }

        public static void Toggle()
        {
            current = current == AppLanguage.Russian ? AppLanguage.English : AppLanguage.Russian;
        }

        public static void ApplyArguments(IList<string> arguments)
        {
            for (int index = 0; index < arguments.Count; index++)
            {
                if (!String.Equals(arguments[index], "-lang", StringComparison.OrdinalIgnoreCase) || index + 1 >= arguments.Count)
                    continue;

                string value = arguments[index + 1];
                if (value.Equals("ru", StringComparison.OrdinalIgnoreCase) || value.Equals("russian", StringComparison.OrdinalIgnoreCase))
                    current = AppLanguage.Russian;
                else if (value.Equals("en", StringComparison.OrdinalIgnoreCase) || value.Equals("english", StringComparison.OrdinalIgnoreCase))
                    current = AppLanguage.English;
            }
        }

        public static bool RunSelfTest()
        {
            Array ids = Enum.GetValues(typeof(TextId));
            if (catalog.Count != ids.Length)
                return false;

            foreach (TextId id in ids)
            {
                LocalizedText value;
                if (!catalog.TryGetValue(id, out value) ||
                    String.IsNullOrWhiteSpace(value.Russian) ||
                    String.IsNullOrWhiteSpace(value.English) ||
                    !SamePlaceholders(value.Russian, value.English))
                    return false;
            }

            return true;
        }

        private static LocalizedText T(string russian, string english)
        {
            return new LocalizedText(russian, english);
        }

        private static AppLanguage DetectLanguage()
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.Russian
                : AppLanguage.English;
        }

        private static CultureInfo CurrentCulture => current == AppLanguage.Russian
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-US");

        private static bool SamePlaceholders(string first, string second)
        {
            var firstSet = new HashSet<string>();
            var secondSet = new HashSet<string>();
            foreach (Match match in Regex.Matches(first, @"\{\d+"))
                firstSet.Add(match.Value);
            foreach (Match match in Regex.Matches(second, @"\{\d+"))
                secondSet.Add(match.Value);
            return firstSet.SetEquals(secondSet);
        }
    }
}
