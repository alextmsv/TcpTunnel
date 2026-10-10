using System;

namespace TCPTunnel
{
    internal static class CommandHelp
    {
        private sealed record Entry(string Name, LocalizedText Summary, LocalizedText Details);

        private static readonly Entry[] entries =
        {
            new Entry("help", new LocalizedText("Показать справку по командам.", "Show command help."), new LocalizedText(
                """
                /help — справка по командам
                Использование: /help [название]
                Без аргументов показывает команды с коротким описанием. С названием объясняет конкретную команду: что она делает, какие аргументы принимает и когда доступна.
                Примеры: /help invite, /help kick, /help approve. Можно также написать /help /invite.
                Справка доступна всем участникам, даже для команд владельца. Чтение справки не выполняет саму команду.
                """,
                """
                /help — command help
                Usage: /help [name]
                Without arguments, shows commands with short descriptions. With a name, explains what that command does, its arguments and when it is available.
                Examples: /help invite, /help kick, /help approve. /help /invite also works.
                Everyone can read help, including help for owner commands. Reading help does not execute the command.
                """)),
            new Entry("invite", new LocalizedText("Создать приглашение в свой E2E Hub; для владельца.", "Create an invitation to your E2E Hub; owner only."), new LocalizedText(
                """
                /invite — приглашение в защищённый чат
                Использование: /invite
                Аргументы не нужны. Команда доступна владельцу из чата своего Hub с включённым E2E.
                Копирует приглашение tct2: в буфер обмена и показывает публичный отпечаток владельца. Приглашение содержит адрес подключения и сведения для проверки личности владельца.
                Передай приглашение через доверенный канал. Получатель вставляет его в поле адреса сервера при подключении. Для Bluetooth-only Hub приглашение указывает Bluetooth-подключение.
                Приглашение не содержит личных ключей и само по себе не даёт доступ к переписке: нового участника ещё нужно подтвердить через /approve.
                Пример: /invite. Затем проверь запрос через /pending и прочитай /help approve.
                """,
                """
                /invite — invite someone to encrypted chat
                Usage: /invite
                No arguments. Available to the owner while connected to their own Hub with E2E enabled.
                Copies a tct2: invitation to clipboard and shows the owner's public fingerprint. The invitation contains connection details and information for verifying the owner's identity.
                Share it through a trusted channel. The recipient pastes it into the server-address prompt. Bluetooth-only invitations specify a Bluetooth connection.
                An invitation contains no private keys and does not grant permission to read the chat: a new participant still needs approval through /approve.
                Example: /invite. Then check /pending and read /help approve.
                """)),
            new Entry("pending", new LocalizedText("Показать запросы на вход в E2E Hub; для владельца.", "Show pending E2E Hub join requests; owner only."), new LocalizedText(
                """
                /pending — запросы на вход
                Использование: /pending [номер]
                Доступна владельцу из чата своего E2E Hub. Без аргументов показывает все ожидающие запросы; с номером показывает выбранный запрос и копирует его ключ в буфер обмена.
                Показывает ник и публичный отпечаток каждого участника, который сейчас ожидает подтверждения.
                Сверь отпечаток с человеком через доверенный канал, затем используй /approve номер или /approve ник отпечаток. Одного знакомого ника недостаточно для проверки личности.
                Запрос действует ограниченное время. Если он исчез, попроси участника подключиться снова. Пустой результат означает, что ожидающих запросов сейчас нет.
                Примеры: /pending; /pending 1 — скопировать ключ запроса [1]. Порядок подтверждения: /help approve.
                """,
                """
                /pending — pending join requests
                Usage: /pending [number]
                Available to the owner in their own E2E Hub chat. Without arguments, lists pending requests; with a number, shows that request and copies its key to clipboard.
                Shows the nickname and public fingerprint of each participant currently waiting for approval.
                Verify the fingerprint with that person through a trusted channel, then use /approve number or /approve nickname fingerprint. A familiar nickname alone does not verify identity.
                Requests expire. If one disappears, ask the participant to reconnect. An empty result means no requests are currently pending.
                Examples: /pending; /pending 1 copies the key of request [1]. Read /help approve for the approval procedure.
                """)),
            new Entry("approve", new LocalizedText("Подтвердить участника по нику и ключу; для владельца E2E Hub.", "Approve a nickname and key; E2E Hub owner only."), new LocalizedText(
                """
                /approve — подтвердить личность и разрешить вход
                Использование: /approve номер ИЛИ /approve ник отпечаток
                Доступна владельцу из чата своего E2E Hub. Выбери один из двух вариантов команды.
                номер — число в квадратных скобках из /pending. Например, /approve 1 разрешает вход именно по ключу запроса [1].
                Сверь ключ через доверенный канал. /pending 1 копирует его без рамок и переносов строк.
                ник — имя из ожидающего запроса /pending; перед именем можно поставить @.
                отпечаток — полный публичный отпечаток ключа из 64 шестнадцатеричных символов, без пробелов. Участник видит его при подключении и через /identity.
                Сначала сверяй отпечаток с человеком через доверенный канал. Затем подтверди номер запроса или укажи проверенные ник и отпечаток в команде.
                Пример: /approve alex ОТПЕЧАТОК. Вместо ОТПЕЧАТОК вставь все 64 символа проверенного ключа.
                Команда разрешает читать новые сообщения комнаты. Привязка ника к ключу сохраняется после выхода и перезапуска Hub; при возвращении с тем же ключом повторное подтверждение обычно не требуется.
                Команда требует текущий ожидающий запрос и не заменяет чужой ключ у уже занятого ника. Старую переписку новому участнику она не выдаёт. Номера завершённых запросов повторно не используются в текущем запуске приложения.
                """,
                """
                /approve — verify identity and allow entry
                Usage: /approve number OR /approve nickname fingerprint
                Available to the owner in their own E2E Hub chat. Choose either command form.
                number — the number in square brackets shown by /pending. For example, /approve 1 authorizes exactly the key in request [1].
                Verify the key through a trusted channel. /pending 1 copies it without borders or line breaks.
                nickname — the name in a pending /pending request; a leading @ is optional.
                fingerprint — the complete public key fingerprint: 64 hexadecimal characters without spaces. The participant sees it while connecting and through /identity.
                First verify the fingerprint with that person through a trusted channel. Then approve the request number or enter the verified nickname and fingerprint in the command.
                Example: /approve alex FINGERPRINT. Replace FINGERPRINT with all 64 characters of the verified key.
                Approval allows the participant to read new room messages. The nickname-to-key binding survives disconnects and Hub restarts; returning with the same key normally does not require another approval.
                A current pending request is required. This command cannot replace another key already bound to a nickname, and it does not provide old chat history. Finished request numbers are not reused during the current application run.
                """)),
            new Entry("identity", new LocalizedText("Показать публичный отпечаток ключа своего профиля.", "Show your profile's public key fingerprint."), new LocalizedText(
                """
                /identity — проверить свою личность
                Использование: /identity
                Аргументы не нужны. Показывает и копирует в буфер обмена публичный отпечаток ключа текущего профиля — постоянный идентификатор, которым E2E Hub отличает тебя от человека с таким же ником.
                Передай этот отпечаток владельцу через доверенный канал для проверки и подтверждения входа. Публичный отпечаток можно сообщать другим; это не личный ключ.
                Если профиль ещё не использовал E2E, команда создаёт его ключ личности. Если сохранённый ключ потерян или повреждён, автоматической замены не будет.
                Пример: /identity. Для допуска участника: /help approve.
                """,
                """
                /identity — check your identity
                Usage: /identity
                No arguments. Shows and copies the current profile's public key fingerprint to clipboard: a persistent identifier that lets an E2E Hub distinguish you from someone using the same nickname.
                Share it with the owner through a trusted channel for identity verification and admission. The public fingerprint can be shared; it is not your private key.
                If the profile has never used E2E, this command creates its identity key. Missing or damaged saved keys are not automatically replaced.
                Example: /identity. Read /help approve for participant admission.
                """)),
            new Entry("kick", new LocalizedText("Отключить участника; для администратора своего Hub.", "Disconnect a participant; your Hub administrator only."), new LocalizedText(
                """
                /kick — отключить участника
                Использование: /kick ник [причина]
                Доступна администратору из чата своего локального Hub.
                ник — имя подключённого участника; @ перед именем необязателен. Нельзя отключить себя этой командой.
                причина — необязательное объяснение, которое увидит отключённый участник. Допускается несколько слов, с кавычками или без них; максимум 200 символов.
                Примеры: /kick alex; /kick @alex Нарушение правил; /kick alex "Нарушение правил чата".
                В E2E-комнате сначала меняются ключи группы, чтобы участник не мог читать следующие сообщения. Также отзывается его разрешение на будущий вход; ник остаётся закреплён за прежним ключом.
                В обычном чате команда только отключает текущее соединение. Уже полученные сообщения удалить у участника она не может.
                """,
                """
                /kick — disconnect a participant
                Usage: /kick nickname [reason]
                Available to the administrator while connected to their own local Hub.
                nickname — a connected participant's name; a leading @ is optional. You cannot kick yourself.
                reason — an optional explanation shown to the disconnected participant. Multiple words are allowed, with or without quotes; maximum 200 characters.
                Examples: /kick alex; /kick @alex Breaking the rules; /kick alex "Breaking chat rules".
                In an E2E room, group keys change first so the participant cannot read subsequent messages. Their permission to rejoin is also revoked; their nickname stays bound to the original key.
                In plaintext chat, only the current connection is closed. The command cannot erase messages the participant has already received.
                """)),
            new Entry("status", new LocalizedText("Показать текущий Hub, E2E, участников и состояние соединения.", "Show the current Hub, E2E, participants and connection state."), new LocalizedText(
                """
                /status — состояние подключения и Hub
                Использование: /status
                Аргументы не нужны. Доступна всем участникам.
                Показывает адрес текущего Hub, владельца, число участников и состояние соединения. Для TCP показывает доступность и задержку, для Bluetooth — доступный уровень сигнала.
                E2E: TRUE означает, что текущий сеанс прошёл проверку сквозного шифрования; FALSE — что чат работает без E2E.
                Если у тебя запущен свой Hub, дополнительно показывает его состояние, службу шифрования и проброс портов, когда он используется.
                Пример: /status. Полезна для проверки соединения и режима защиты.
                """,
                """
                /status — connection and Hub state
                Usage: /status
                No arguments. Available to everyone.
                Shows the current Hub address, owner, participant count and connection state. For TCP, shows reachability and latency; for Bluetooth, the available signal level.
                E2E: TRUE means the current session established verified end-to-end encryption; FALSE means the chat operates without E2E.
                If you are also running your own Hub, shows its state, encryption service and port mapping when applicable.
                Example: /status. Use it to check your connection and protection mode.
                """)),
            new Entry("whois", new LocalizedText("Показать доступные сведения об участнике.", "Show available information about a participant."), new LocalizedText(
                """
                /whois — сведения об участнике
                Использование: /whois ник
                ник — имя подключённого участника; можно написать @ник. Кавычки вокруг имени не нужны.
                Показывает доступные сведения: транспорт, задержку или сигнал Bluetooth, размер окна, состояние рамки-змейки и число сообщений.
                IP показывается только если это разрешено Hub. При включённом скрытии адресов команда не выдаёт настоящий IP.
                Участник может получить уведомление о запросе. Команда не показывает личные ключи и не заменяет проверку отпечатка личности.
                Примеры: /whois alex; /whois @alex.
                """,
                """
                /whois — participant information
                Usage: /whois nickname
                nickname — a connected participant's name; @nickname also works. Do not quote the name.
                Shows available information: transport, latency or Bluetooth signal, window size, border-snake state and message count.
                An IP address is shown only when the Hub permits it. When address hiding is enabled, this command does not disclose the real IP.
                The participant may receive a notification about the request. The command does not show private keys or replace fingerprint verification.
                Examples: /whois alex; /whois @alex.
                """)),
            new Entry("ping", new LocalizedText("Проверить доступность TCP-адреса и задержку.", "Check TCP endpoint reachability and latency."), new LocalizedText(
                """
                /ping — проверить TCP-подключение
                Использование: /ping адрес:порт
                адрес — имя сервера или IP; порт — число от 1 до 65535. Укажи их вместе через двоеточие.
                Проверяет, принимает ли указанный адрес TCP-соединение, и измеряет время подключения. Команда не входит в чат и не подтверждает личность сервера или работу E2E.
                Доступна всем участникам. Для Bluetooth-подключения используй /status.
                Примеры: /ping localhost:9091; /ping 192.168.1.10:9091.
                """,
                """
                /ping — test TCP connectivity
                Usage: /ping address:port
                address — a hostname or IP; port — a number from 1 to 65535. Separate them with a colon.
                Checks whether the endpoint accepts a TCP connection and measures connection time. It does not join the chat or verify server identity or E2E.
                Available to everyone. For a Bluetooth connection, use /status.
                Examples: /ping localhost:9091; /ping 192.168.1.10:9091.
                """)),
            new Entry("clear", new LocalizedText("Очистить отображаемую историю только у себя.", "Clear the displayed chat history locally."), new LocalizedText(
                """
                /clear — очистить свой экран чата
                Использование: /clear
                Аргументы не нужны. Убирает отображаемую историю чата в твоём приложении, чтобы освободить экран.
                Доступна всем участникам. Соединение сохраняется; у других участников сообщения не удаляются.
                Пример: /clear.
                """,
                """
                /clear — clear your chat display
                Usage: /clear
                No arguments. Removes the displayed chat history in your application to clear the screen.
                Available to everyone. You remain connected; other participants' messages are not deleted.
                Example: /clear.
                """)),
            new Entry("look", new LocalizedText("Открыть последнее доступное крупное изображение или GIF.", "Open the last available large image or GIF."), new LocalizedText(
                """
                /look — открыть крупное изображение или GIF
                Использование: /look
                Аргументы не нужны. Открывает в отдельном окне просмотра последнее доступное крупное изображение или GIF из чата.
                Полезна, когда предпросмотр в чате слишком маленький. Если подходящего изображения нет, приложение сообщит об этом.
                Доступна всем участникам. Нажми клавишу в окне просмотра, чтобы закрыть его и вернуться к чату.
                Пример: /look.
                """,
                """
                /look — open a large image or GIF
                Usage: /look
                No arguments. Opens the last available large image or GIF from the chat in a separate viewer window.
                Use it when the chat preview is too small. The application reports when no suitable image is available.
                Available to everyone. Press a key in the viewer to close it and return to chat.
                Example: /look.
                """)),
            new Entry("stop", new LocalizedText("Остановить свой Hub или приостановить рамку-змейку.", "Stop your Hub or pause the border snake."), new LocalizedText(
                """
                /stop — остановить Hub или рамку-змейку
                Использование: /stop
                Аргументы не нужны. Поведение зависит от твоей роли в текущем чате.
                Администратор своего локального Hub: останавливает Hub, отключает всех его участников и завершает текущий сеанс чата.
                Обычный участник: приостанавливает или возобновляет анимацию своей рамки-змейки. Для этого нужна активная графическая рамка; чужой Hub не останавливается.
                Пример: /stop. Если нужно только выйти из чата, прочитай /help exit.
                """,
                """
                /stop — stop the Hub or border snake
                Usage: /stop
                No arguments. Its behavior depends on your role in the current chat.
                Administrator of your own local Hub: stops the Hub, disconnects all its participants and ends your current chat session.
                Regular participant: pauses or resumes your border-snake animation. An active graphical border is required; someone else's Hub is not stopped.
                Example: /stop. If you only want to leave the chat, read /help exit.
                """)),
            new Entry("exit", new LocalizedText("Выйти из текущего чата и вернуться в меню.", "Leave the current chat and return to the menu."), new LocalizedText(
                """
                /exit — выйти из чата
                Использование: /exit
                Аргументы не нужны. Завершает твоё текущее подключение и возвращает в меню приложения.
                Доступна всем участникам. Если у тебя работает свой Hub, он продолжает работать в фоне; для его остановки используй /stop из чата своего Hub.
                В E2E-комнате разрешённая привязка ника к ключу сохраняется для следующего входа. Сообщения за время отсутствия автоматически не доставляются.
                Пример: /exit.
                """,
                """
                /exit — leave the chat
                Usage: /exit
                No arguments. Ends your current connection and returns to the application menu.
                Available to everyone. Your own Hub, if running, continues in the background; use /stop in your own Hub chat to shut it down.
                In an E2E room, the approved nickname-to-key binding remains for your next visit. Messages sent while you are away are not automatically delivered.
                Example: /exit.
                """))
        };

        internal static void Show(string command, Action<string, ConsoleColor?> write)
        {
            if (command == null)
            {
                write(Lang.Get(TextId.CommandHelp), ConsoleColor.DarkGreen);
                foreach (var entry in entries) write("/" + entry.Name + " — " + Select(entry.Summary), null);
                return;
            }
            string name = command.TrimStart('/');
            foreach (var entry in entries)
            {
                if (!entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string line in Select(entry.Details).Split('\n')) write(line.TrimEnd('\r'), null);
                return;
            }
            write(Lang.Get(TextId.HelpUnknown, command), ConsoleColor.Yellow);
        }

        private static string Select(LocalizedText text) => Lang.Current == AppLanguage.Russian ? text.Russian : text.English;
    }
}
