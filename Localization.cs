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
        MenuTitle, MenuTagline, SelectionStyle, SelectionColor, SelectionFill, SelectionArrow, SelectionBrackets,
        CustomColor, CustomSelectionColor, CustomColorOff, CustomColorPrompt, CustomColorInvalid,
        BackgroundColorRow, BackgroundModeWindowsTerminal, CustomBackgroundColor,
        WindowsTerminalNotFound, WindowsTerminalSchemeNotFound, WindowsTerminalNotRunning, BackgroundApplyFailed,
        BluetoothUnsupportedOs, BluetoothNoAdapter, BluetoothRadioOff, BluetoothRoleUnsupported, BluetoothAccessDenied,
        BluetoothFailed, BluetoothReady, BluetoothHubGone, BluetoothElevationFailed, BluetoothElevationDeclined,
        BluetoothHubStarting, BluetoothRequestingElevation, BluetoothStillBlocked, BluetoothEndpoint, BluetoothHubStarted,
        HubOptionsInvalid, BluetoothAdvertisingBlocked, BluetoothHubStartFailed,
        HubModePublicSide, HubModeLanSide, HubBluetoothRow, HubBluetoothUnavailableRow,
        HubExplainPublic, HubExplainLan, HubExplainBluetooth, HubCreate, HubOptionsTitle,
        BluetoothModuleFound, BluetoothModuleMissing,
        StatusSignal, StatusSignalUnavailable, StatusLocalLink, StatusAccess, StatusOwnBluetooth, OwnHubStartedBluetooth,
        WhoisViaBluetooth, WhoisSignal, WhoisSignalUnavailable, WhoisLocalOwner,
        ParticipantsOne, ParticipantsFew, ParticipantsMany,
        BluetoothAskConnect, BluetoothNoThanks, BluetoothApiError, BluetoothNoBeacons, BluetoothTryAgain,
        BluetoothRetry, BluetoothUseIp, BluetoothDiscoveryHint, BluetoothSearching, BluetoothBeaconVanished,
        BluetoothFoundOne, BluetoothFoundMany, BluetoothSignalGood, BluetoothSignalMedium, BluetoothSignalWeak,
        BluetoothHosts, BluetoothConnecting, BluetoothConnectFailed, BluetoothPressAnyKey,
        AnimationDownloading, AnimationDownloadingPlain, AnimationUploading, AnimationUploadingPlain,
        HostServer, EnterOwnHub, ConnectToHub, EnterNickname,
        ChangeNickname, GraphicsOptions, Exit, LanguageMenu,
        PingArgument, PingCommandUsage, ConnectArgument, ServerAlive, ServerDead, ServerPing,
        ChangeNicknameWelcome, ChangeIdentity, EnterNewNickname, CheckingName,
        NicknameRules, TryAgain, IdentityUpdated, GoodName, MyName, TookYourTime,
        GraphicsEnabled, GraphicsDisabled, Customization, Back, Snake,
        SnakeCustomization, Speed, Color, SpeedFast, SpeedNormal, SpeedCalm,
        SpeedSlow, ColorGreen, ColorCyan, ColorYellow, ColorRed, ColorWhite, ColorBlue,
        ColorBlack, ColorDarkBlue, ColorDarkGreen, ColorDarkCyan, ColorDarkRed,
        ColorDarkMagenta, ColorDarkYellow, ColorGray, ColorMagenta,
        HubSetup, ChooseTcpPort, EnterServerPort, InvalidPortNumber, StartingListener,
        CreateHubFailed, ListenerStarted, ConfiguringNat, HubStarted,
        LocalClientBackground, LocalClientConnecting, PortOutOfRange, HubAlreadyRunning,
        NatNotStarted, NatTrying, NatPortMapped, NatPortMappedRenewable, NatFailed,
        NatCancelled, NatRouterTimeout, NatUnavailable, NatDeviceNotFound,
        NatRuleTimeout, NatRuleRejected, NatNoActiveRule, NatPortClosed,
        NatDeleteFailed, NatError, UnexpectedError,
        MissingServerAddress, ConnectionInProgress, ConnectingCompact,
        ConnectingAttempt, SessionStartFailed, ConnectFailed, HubUnavailableCompact,
        HubUnavailableAttempts, EnterYourNickname, ConnectionTimedOut,
        UnknownAuthProtocol, NicknameRejected, ConnectedCommands, PublicIPv4Unavailable,
        LocalHubNotRunning,
        StoppingLocalHub, NoActiveSnake, SnakePaused, SnakeResumed,
        SendFailedClosed, ServerDidNotRespond, HubConnectionLost, DisconnectReturn, DisconnectInvalidFrame,
        UserJoined, UserLeft, MessageTooLong, TooManyMessages, InvalidImagePacket, TooManyImages,
        AuthInvalidRequest, AuthInvalidNickname, AuthNicknameTaken, AuthTimedOut,
        ClientNotReceiving, FrameTooLarge, FrameReadTimedOut, InvalidFrameLength, InvalidFramePrefix,
        HubOnline, HubOffline,
        CommandHelp, UnknownCommand, CommandNoPermission,
        KickUsage, KickUserNotFound, KickCannotSelf, KickSucceeded,
        KickedDefault, KickedReason, HubStatusWithClients,
        ImportProfilePrompt, Yes, No, AlwaysImport,
        SavedProfilesCount, CreateOwnProfile, OpenProfileList, UseLatestProfile, AlwaysLoadChosenProfile, ClearAllProfiles, ProfileLoadError,
        ResetSettings, SettingsReset, InterfaceColors, IncomingMessages,
        OutgoingMessages, InputField, SystemMessages, Border,
        GlyphValue, EnterServerAddressSaved, EnterServerPortSaved,
        MenuTextColor, PreviewMessage, PreviewSystem, PreviewMenuItem,
        PreparingImage, PreparingAnimation, ImageInvalidFile, ImageFileTooLarge, ImageDimensionsTooLarge,
        ImageAnimationTooLarge, ImageCodecUnavailable, ImageDecodeFailed, ImageLabel, AnimationLabel, ImageTooLargePrompt,
        ImageStronglyCompressedPrompt,
        NoLargeImage, ImageViewerFailed, ImageViewerClose, ImageViewerTooSmall,
        StatusUnavailable, StatusCurrent, StatusAdministrator, StatusConnected, StatusDisconnected,
        StatusPing, StatusParticipants, StatusOwn, StatusRunning, StatusNat, StatusPingUnavailable, NicknamePrompt,
        OwnHubStarted, OwnHubStopped, OwnHubNat, OwnHubParticipants,
        WhoisUsage, WhoisUnavailable, WhoisNotFound, WhoisPublicIpUnavailable, WhoisPing, WhoisPingUnavailable,
        WhoisWindow, WhoisSnake, WhoisSnakeOff, WhoisSnakePaused, WhoisSnakeMoving, WhoisSnakeUnavailable, WhoisMessages, WhoisNotice, WhoisUnreadSummary
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
                { TextId.StatusUnavailable, T("недоступно", "unavailable") },
                { TextId.StatusCurrent, T("Текущий Hub: {0}", "Current Hub: {0}") },
                { TextId.StatusAdministrator, T("Администратор: {0}", "Administrator: {0}") },
                { TextId.StatusConnected, T("Подключение: установлено", "Connection: established") },
                { TextId.StatusDisconnected, T("Подключение: потеряно", "Connection: lost") },
                { TextId.StatusPing, T("TCP-пинг: {0} мс", "TCP ping: {0} ms") },
                { TextId.StatusPingUnavailable, T("TCP-пинг: недоступен", "TCP ping: unavailable") },
                { TextId.OwnHubStarted, T("Ваш Hub: работает, порт {0}", "Your Hub: running, port {0}") },
                { TextId.OwnHubStopped, T("Ваш Hub: остановлен", "Your Hub: stopped") },
                { TextId.OwnHubNat, T("Ваш Hub — проброс портов: {0}", "Your Hub — port mapping: {0}") },
                { TextId.OwnHubParticipants, T("Ваш Hub — участников: {0}", "Your Hub — participants: {0}") },
                { TextId.WhoisUsage, T("Использование: /whois псевдоним (можно с @)", "Usage: /whois nickname (@ is optional)") },
                { TextId.WhoisUnavailable, T("/whois недоступен: сервер не поддерживает команду или не ответил.", "/whois unavailable: the hub does not support it or did not respond.") },
                { TextId.WhoisNotFound, T("Пользователь не найден", "User not found") },
                { TextId.WhoisPublicIpUnavailable, T("Публичный IPv4 недоступен", "Public IPv4 unavailable") },
                { TextId.WhoisPing, T("Пинг до Hub: {0} мс", "Ping to Hub: {0} ms") },
                { TextId.WhoisPingUnavailable, T("Пинг до Hub: недоступен", "Ping to Hub: unavailable") },
                { TextId.WhoisWindow, T("Размер окна TCPTunnel: {0}", "TCPTunnel window size: {0}") },
                { TextId.WhoisSnake, T("Змейка: {0}  Скорость: {1} мс/шаг", "Snake: {0}  Speed: {1} ms/step") },
                { TextId.WhoisSnakeOff, T("Состояние: выключена", "State: disabled") },
                { TextId.WhoisSnakePaused, T("Состояние: на паузе", "State: paused") },
                { TextId.WhoisSnakeMoving, T("Состояние: движется", "State: moving") },
                { TextId.WhoisSnakeUnavailable, T("Змейка: недоступно", "Snake: unavailable") },
                { TextId.WhoisMessages, T("Отправил сообщений: {0}", "Messages sent: {0}") },
                { TextId.WhoisNotice, T("@{0} использовал /whois на вас!", "@{0} used /whois on you!") },
                { TextId.WhoisUnreadSummary, T("Непрочитанные уведомления /whois: {0}", "Unread /whois notices: {0}") },
                { TextId.NicknamePrompt, T("Введите ваш псевдоним (от 3 до 20 символов, без пробелов и спецсимволов)", "Enter your nickname (3 to 20 characters, no spaces or special characters)") },
                { TextId.StatusParticipants, T("Участников: {0}", "Participants: {0}") },
                { TextId.StatusOwn, T("Ваш Hub: порт {0}", "Your Hub: port {0}") },
                { TextId.StatusRunning, T("Состояние: работает", "State: running") },
                { TextId.StatusNat, T("Проброс портов: {0}", "Port mapping: {0}") },
                { TextId.SelfTestFailed, T("Самопроверка TCPTunnel: ОШИБКА", "TCPTunnel self-test: FAILED") },
                { TextId.Goodbye, T("До свидания", "Goodbye") },
                { TextId.Welcome, T("Добро пожаловать в чат", "Welcome to the chat") },
                { TextId.MenuTitle, T("Меню", "Menu") },
                { TextId.MenuTagline, T("Лёгкий и истинный хаб мессенджер", "Lightweight true hub-messenger") },
                { TextId.SelectionStyle, T("Выделение", "Selection style") },
                { TextId.SelectionColor, T("Цвет выделения", "Selection color") },
                { TextId.SelectionFill, T("Заливка", "Fill") },
                { TextId.SelectionArrow, T("Стрелка", "Arrow") },
                { TextId.SelectionBrackets, T("Скобки", "Brackets") },
                { TextId.CustomColor, T("Свой цвет", "Custom") },
                { TextId.CustomSelectionColor, T("Свой цвет выделения", "Custom selection color") },
                { TextId.CustomColorOff, T("выкл", "off") },
                { TextId.CustomColorPrompt, T("Введите цвет в формате RRGGBB (Enter без ввода — выключить):", "Enter a color as RRGGBB (Enter with nothing — turn off):") },
                { TextId.CustomColorInvalid, T("Неверный формат. Нужны 6 шестнадцатеричных цифр, например 2E7DE0.", "Invalid format. Use 6 hex digits, e.g. 2E7DE0.") },
                { TextId.BackgroundColorRow, T("Фон", "Background") },
                { TextId.BackgroundModeWindowsTerminal, T("Windows Terminal", "Windows Terminal") },
                { TextId.CustomBackgroundColor, T("Свой цвет фона", "Custom background color") },
                { TextId.WindowsTerminalNotFound, T("settings.json Windows Terminal не найден", "Windows Terminal settings.json not found") },
                { TextId.WindowsTerminalSchemeNotFound, T("не удалось определить активную цветовую схему", "could not determine the active color scheme") },
                { TextId.WindowsTerminalNotRunning, T("приложение запущено не в Windows Terminal", "not running inside Windows Terminal") },
                { TextId.BackgroundApplyFailed, T("Не удалось применить фон: {0}", "Failed to apply background: {0}") },
                { TextId.BluetoothUnsupportedOs, T("нужна Windows 10 1809 или новее", "Windows 10 1809 or newer is required") },
                { TextId.BluetoothNoAdapter, T("Bluetooth-адаптер не найден", "no Bluetooth adapter found") },
                { TextId.BluetoothRadioOff, T("Bluetooth выключен", "Bluetooth is turned off") },
                { TextId.BluetoothRoleUnsupported, T("адаптер не поддерживает нужный режим", "the adapter does not support the required role") },
                { TextId.BluetoothAccessDenied, T("Windows запретила доступ к Bluetooth", "Windows denied access to Bluetooth") },
                { TextId.BluetoothFailed, T("ошибка Bluetooth-API Windows", "Windows Bluetooth API error") },
                { TextId.BluetoothReady, T("Bluetooth готов", "Bluetooth is ready") },
                { TextId.BluetoothHubGone, T("хаб больше недоступен по Bluetooth", "the hub is no longer reachable over Bluetooth") },
                { TextId.BluetoothElevationFailed, T("не удалось разрешить Bluetooth-маяк в Windows", "could not allow the Bluetooth beacon in Windows") },
                { TextId.BluetoothElevationDeclined, T("запрос прав администратора отклонён", "the administrator prompt was declined") },
                { TextId.BluetoothHubStarting, T("Запускаю Bluetooth-хаб...", "Starting the Bluetooth hub...") },
                { TextId.BluetoothRequestingElevation, T("Windows запрещает Bluetooth-маяк. Запрашиваю права администратора, чтобы разрешить...", "Windows blocks the Bluetooth beacon. Requesting administrator rights to allow it...") },
                { TextId.BluetoothStillBlocked, T("Windows всё ещё запрещает Bluetooth-маяк. Перезагрузите компьютер и попробуйте снова", "Windows still blocks the Bluetooth beacon. Restart the computer and try again") },
                { TextId.BluetoothEndpoint, T("Bluetooth", "Bluetooth") },
                { TextId.BluetoothHubStarted, T("Bluetooth-хаб запущен.", "Bluetooth hub started.") },
                { TextId.HubOptionsInvalid, T("недопустимые параметры хаба", "invalid hub options") },
                { TextId.BluetoothAdvertisingBlocked, T("Windows запрещает Bluetooth-маяк (AllowAdvertising)", "Windows blocks the Bluetooth beacon (AllowAdvertising)") },
                { TextId.BluetoothHubStartFailed, T("Bluetooth-хаб не запустился: {0}", "the Bluetooth hub did not start: {0}") },
                { TextId.HubModePublicSide, T("Публичный WWW-хаб", "Public WWW hub") },
                { TextId.HubModeLanSide, T("LAN-only хаб", "LAN-only hub") },
                { TextId.HubBluetoothRow, T("Bluetooth-хаб TCPTunnel", "TCPTunnel Bluetooth hub") },
                { TextId.HubBluetoothUnavailableRow, T("Bluetooth-хаб недоступен", "Bluetooth hub unavailable") },
                { TextId.HubExplainPublic, T("WWW - World Wide Web, любой TCPTunnel-юзер знающий данные вашего сервера сможет подключиться при успешном пробросе портов.", "WWW - World Wide Web: any TCPTunnel user who knows your server details can connect if port forwarding succeeds.") },
                { TextId.HubExplainLan, T("LAN - Local Area Network, юзер TCPTunnel находящийся ТОЛЬКО с вами в одной сети сможет подключиться к вашему хабу.", "LAN - Local Area Network: ONLY TCPTunnel users on the same network as you can connect to your hub.") },
                { TextId.HubExplainBluetooth, T("BT - Сервер - Bluetooth-маяк. Юзер с BT-модулем и актуальной версией TCPTunnel сможет обнаружить радио-сигнал вашего сервера и подключиться как к обычному.", "BT - the server is a Bluetooth beacon. A user with a Bluetooth module and a current TCPTunnel can discover your server's radio signal and connect as usual.") },
                { TextId.HubCreate, T("Создать хаб", "Create hub") },
                { TextId.HubOptionsTitle, T("Параметры хаба", "Hub options") },
                { TextId.BluetoothModuleFound, T("Обнаружен рабочий Bluetooth модуль!", "A working Bluetooth module was found!") },
                { TextId.BluetoothModuleMissing, T("Bluetooth недоступен: {0}", "Bluetooth unavailable: {0}") },
                { TextId.StatusSignal, T("Сигнал до Hub: {0} dBm", "Signal to Hub: {0} dBm") },
                { TextId.StatusSignalUnavailable, T("Сигнал до Hub: недоступен", "Signal to Hub: unavailable") },
                { TextId.StatusLocalLink, T("Подключение: локальное", "Link: local") },
                { TextId.StatusAccess, T("Вход: {0}", "Access: {0}") },
                { TextId.StatusOwnBluetooth, T("Ваш Hub: Bluetooth", "Your Hub: Bluetooth") },
                { TextId.OwnHubStartedBluetooth, T("Ваш Hub: работает по Bluetooth", "Your Hub: running over Bluetooth") },
                { TextId.WhoisViaBluetooth, T("Подключен по Bluetooth", "Connected over Bluetooth") },
                { TextId.WhoisSignal, T("Сигнал до Hub: {0} dBm", "Signal to Hub: {0} dBm") },
                { TextId.WhoisSignalUnavailable, T("Сигнал до Hub: недоступен", "Signal to Hub: unavailable") },
                { TextId.WhoisLocalOwner, T("Подключен локально (владелец хаба)", "Connected locally (hub owner)") },
                { TextId.ParticipantsOne, T("{0} участник", "{0} participant") },
                { TextId.ParticipantsFew, T("{0} участника", "{0} participants") },
                { TextId.ParticipantsMany, T("{0} участников", "{0} participants") },
                { TextId.BluetoothAskConnect, T("Хотите попробовать подключение по Bluetooth?", "Do you want to try connecting over Bluetooth?") },
                { TextId.BluetoothNoThanks, T("Ну и ладно :P", "Fine then :P") },
                { TextId.BluetoothApiError, T("О нет.. Bluetooth-API Windows выдал ошибку: {0}", "Oh no.. the Windows Bluetooth API reported an error: {0}") },
                { TextId.BluetoothNoBeacons, T("Bluetooth-маячки не найдены :(", "No Bluetooth beacons found :(") },
                { TextId.BluetoothTryAgain, T("Попробовать ещё раз?", "Try again?") },
                { TextId.BluetoothRetry, T("Ещё раз", "Again") },
                { TextId.BluetoothUseIp, T("Подключиться по IP-адресу", "Connect by IP address") },
                { TextId.BluetoothDiscoveryHint, T("↑/↓ — выбор, Enter — подключиться, Esc — назад", "↑/↓ select, Enter connect, Esc back") },
                { TextId.BluetoothSearching, T("Ищу Bluetooth-маяки от TCPTunnel", "Looking for TCPTunnel Bluetooth beacons") },
                { TextId.BluetoothBeaconVanished, T("Выбранный маяк пропал — выберите другой", "The selected beacon disappeared — choose another one") },
                { TextId.BluetoothFoundOne, T("Найден маяк:", "Beacon found:") },
                { TextId.BluetoothFoundMany, T("Найдены маяки:", "Beacons found:") },
                { TextId.BluetoothSignalGood, T("хороший", "good") },
                { TextId.BluetoothSignalMedium, T("средний", "fair") },
                { TextId.BluetoothSignalWeak, T("слабый", "weak") },
                { TextId.BluetoothHosts, T("Хостит на {0}", "Hosting on {0}") },
                { TextId.BluetoothConnecting, T("Подключаюсь к {0} по Bluetooth...", "Connecting to {0} over Bluetooth...") },
                { TextId.BluetoothConnectFailed, T("Не удалось подключиться по Bluetooth: {0}", "Bluetooth connection failed: {0}") },
                { TextId.BluetoothPressAnyKey, T("Нажмите любую клавишу", "Press any key") },
                { TextId.AnimationDownloading, T("@{0} отправил GIF. Загрузка {1}%", "@{0} sent a GIF. Loading {1}%") },
                { TextId.AnimationDownloadingPlain, T("@{0} отправил GIF. Загрузка...", "@{0} sent a GIF. Loading...") },
                { TextId.AnimationUploading, T("Отправка GIF: {0}%", "Sending GIF: {0}%") },
                { TextId.AnimationUploadingPlain, T("Отправка GIF...", "Sending GIF...") },
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
                { TextId.ServerPing, T("Сервер {0}:{1} доступен. TCP-пинг: {2} мс.", "Server {0}:{1} is reachable. TCP ping: {2} ms.") },
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
                { TextId.ColorBlack, T("чёрный", "black") },
                { TextId.ColorDarkBlue, T("тёмно-синий", "dark blue") },
                { TextId.ColorDarkGreen, T("тёмно-зелёный", "dark green") },
                { TextId.ColorDarkCyan, T("тёмно-голубой", "dark cyan") },
                { TextId.ColorDarkRed, T("тёмно-красный", "dark red") },
                { TextId.ColorDarkMagenta, T("тёмно-пурпурный", "dark magenta") },
                { TextId.ColorDarkYellow, T("тёмно-жёлтый", "dark yellow") },
                { TextId.ColorGray, T("серый", "gray") },
                { TextId.ColorMagenta, T("пурпурный", "magenta") },
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
                { TextId.ConnectedCommands, T("Подключено к {0}. Команды: /help, /status, /whois, /ping, /clear, /look, /stop, /exit.", "Connected to {0}. Commands: /help, /status, /whois, /ping, /clear, /look, /stop, /exit.") },
                { TextId.PublicIPv4Unavailable, T("Публичный IPv4 определить не удалось: доступ к интернету или сервис определения адреса недоступен. Используется локальный IPv4: {0}.", "The public IPv4 address could not be determined: internet access or the address lookup service is unavailable. Using local IPv4: {0}.") },
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
                { TextId.InvalidImagePacket, T("Некорректный пакет изображения отклонён.", "Malformed image packet rejected.") },
                { TextId.TooManyImages, T("Изображения отправляются слишком часто. Подождите несколько секунд.", "Images are being sent too quickly. Wait a few seconds.") },
                { TextId.AuthInvalidRequest, T("Неверный запрос авторизации.", "Invalid authentication request.") },
                { TextId.AuthInvalidNickname, T("Некорректный псевдоним.", "Invalid nickname.") },
                { TextId.AuthNicknameTaken, T("Псевдоним уже занят.", "Nickname is already in use.") },
                { TextId.AuthTimedOut, T("Истёк таймаут авторизации.", "Authentication timed out.") },
                { TextId.ClientNotReceiving, T("Клиент не принимает сообщения.", "Client is not receiving messages.") },
                { TextId.FrameTooLarge, T("Размер сообщения {0} байт превышает лимит {1} байт.", "Message size of {0} bytes exceeds the {1}-byte limit.") },
                { TextId.FrameReadTimedOut, T("Передача сообщения не завершена вовремя.", "Message payload transfer timed out.") },
                { TextId.InvalidFrameLength, T("Некорректная длина сообщения.", "Invalid message length.") },
                { TextId.InvalidFramePrefix, T("Некорректный префикс длины сообщения.", "Invalid message length prefix.") },
                { TextId.HubOnline, T("ХАБ В СЕТИ", "HUB ONLINE") },
                { TextId.HubOffline, T("ХАБ ОТКЛЮЧЁН", "HUB OFFLINE") },
                { TextId.DisconnectReturn, T("Enter — вернуться в меню", "Enter — return to the menu") },
                { TextId.DisconnectInvalidFrame, T("Соединение закрыто: некорректный пакет сервера.", "Connection closed: invalid server frame.") },
                { TextId.CommandHelp, T("Команды: /help, /status, /whois псевдоним, /ping host:port, /clear, /look, /stop, /exit. Администратор локального хаба: /kick псевдоним [причина]. @ и кавычки необязательны.", "Commands: /help, /status, /whois nickname, /ping host:port, /clear, /look, /stop, /exit. Local hub administrator: /kick nickname [reason]. @ and quotes are optional.") },
                { TextId.UnknownCommand, T("Неизвестная команда: {0}. Используйте /help.", "Unknown command: {0}. Use /help.") },
                { TextId.CommandNoPermission, T("Эта команда доступна только администратору локального хаба.", "This command is available only to the local hub administrator.") },
                { TextId.KickUsage, T("Использование: /kick псевдоним [причина]. @ и кавычки необязательны.", "Usage: /kick nickname [reason]. @ and quotes are optional.") },
                { TextId.KickUserNotFound, T("Участник {0} не найден.", "Participant {0} was not found.") },
                { TextId.KickCannotSelf, T("Нельзя выгнать собственный локальный клиент этой командой.", "You cannot kick your own local client with this command.") },
                { TextId.KickSucceeded, T("Участник {0} отключён от хаба.", "Participant {0} was disconnected from the hub.") },
                { TextId.KickedDefault, T("Вы были выгнаны администратором хаба.", "You were kicked by the hub administrator.") },
                { TextId.KickedReason, T("Вы были выгнаны администратором хаба. Причина: {0}", "You were kicked by the hub administrator. Reason: {0}") },
                { TextId.HubStatusWithClients, T("{0} Подключено клиентов: {1}.", "{0} Connected clients: {1}.") },
                { TextId.ImportProfilePrompt, T("Импортировать настройки пользователя {0}?", "Import settings for {0}?") },
                { TextId.SavedProfilesCount, T("На этом ПК сохранено профилей: {0}", "Profiles saved on this PC: {0}") },
                { TextId.CreateOwnProfile, T("Создать свой", "Create my own") },
                { TextId.OpenProfileList, T("Открыть список", "Open profile list") },
                { TextId.UseLatestProfile, T("Использовать последний: ", "Use latest: ") },
                { TextId.AlwaysLoadChosenProfile, T("Всегда загружать профиль, который я выберу", "Always load the profile I choose") },
                { TextId.ClearAllProfiles, T("Очистить все конфиги", "Clear all configs") },
                { TextId.ProfileLoadError, T("Не удалось применить выбор профиля: {0}", "Could not apply profile selection: {0}") },
                { TextId.Yes, T("Да", "Yes") },
                { TextId.No, T("Нет", "No") },
                { TextId.AlwaysImport, T("Всегда импортировать {0}", "Always import {0}") },
                { TextId.ResetSettings, T("Сбросить настройки", "Reset settings") },
                { TextId.SettingsReset, T("Настройки восстановлены", "Settings restored") },
                { TextId.InterfaceColors, T("Цвета интерфейса", "Interface colors") },
                { TextId.IncomingMessages, T("Входящие сообщения", "Incoming messages") },
                { TextId.OutgoingMessages, T("Исходящие сообщения", "Outgoing messages") },
                { TextId.InputField, T("Поле ввода", "Input field") },
                { TextId.SystemMessages, T("Системные сообщения", "System messages") },
                { TextId.Border, T("Граница", "Border") },
                { TextId.GlyphValue, T("Символ: {0}", "Symbol: {0}") },
                { TextId.EnterServerAddressSaved, T("Введите IP-адрес или имя сервера [{0}]: ", "Enter server IP address or host name [{0}]: ") },
                { TextId.EnterServerPortSaved, T("Введите порт сервера [{0}]: ", "Enter server port [{0}]: ") },
                { TextId.MenuTextColor, T("Текст меню", "Menu text") },
                { TextId.PreviewMessage, T("Тестовое сообщение!", "Test message!") },
                { TextId.PreviewSystem, T("[+] Хаб запущен", "[+] Hub started") },
                { TextId.PreviewMenuItem, T("Пункт меню", "Menu item") },
                { TextId.PreparingImage, T("Подготавливаю изображение...", "Preparing image...") },
                { TextId.PreparingAnimation, T("Подготавливаю GIF-анимацию...", "Preparing GIF animation...") },
                { TextId.ImageInvalidFile, T("Не удалось прочитать файл изображения.", "Could not read the image file.") },
                { TextId.ImageFileTooLarge, T("Файл изображения превышает лимит 32 МиБ.", "The image file exceeds the 32 MiB limit.") },
                { TextId.ImageDimensionsTooLarge, T("Размеры изображения превышают безопасный лимит.", "The image dimensions exceed the safe limit.") },
                { TextId.ImageAnimationTooLarge, T("GIF превышает лимит: 500 кадров или 60 секунд.", "The GIF exceeds the limit of 500 frames or 60 seconds.") },
                { TextId.ImageCodecUnavailable, T("WebP-кодек WIC не установлен в этой системе.", "A WIC WebP codec is not installed on this system.") },
                { TextId.ImageDecodeFailed, T("Не удалось декодировать изображение.", "Could not decode the image.") },
                { TextId.ImageLabel, T("изображение", "image") },
                { TextId.AnimationLabel, T("GIF-анимация", "GIF animation") },
                { TextId.ImageTooLargePrompt, T("Картинка слишком большая, всё равно посмотреть? /look", "This image is very large. View it anyway? /look") },
                { TextId.ImageStronglyCompressedPrompt, T("Изображение сильно уменьшено. Открыть подробнее? /look", "This image was heavily reduced. Open a larger view? /look") },
                { TextId.NoLargeImage, T("Нет изображения, доступного для /look.", "There is no image available for /look.") },
                { TextId.ImageViewerFailed, T("Не удалось открыть отдельное окно изображения.", "Could not open the separate image window.") },
                { TextId.ImageViewerClose, T("Нажмите любую клавишу, чтобы закрыть окно.", "Press any key to close this window.") },
                { TextId.ImageViewerTooSmall, T("Окно консоли слишком мало для изображения.", "The console window is too small for this image.") }
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

        public static void Set(AppLanguage language)
        {
            current = language;
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
