using System;
using System.IO;
using System.Threading;

namespace TCPTunnel;

public partial class UserInterface
{
    private static bool ConfirmRemotePrivacy(HubStatusSession status, Stream stream, CancellationToken token, bool localHub)
    {
        if (localHub) return true;
        bool? hidden = status.Privacy.QueryAsync((text, ct) => MessageProtocol.WriteStringAsync(stream, text, ct), token).GetAwaiter().GetResult();
        if (hidden == true) return true;
        string warning = PrivacyWarning(hidden);
        if (Console.IsInputRedirected) { WriteSystemChatLine(warning); return false; }
        bool accepted = false;
        ClearConnectionInput();
        RunChatModal(() => accepted = new Menu().ShowPrivacyWarning(warning));
        if (!accepted || !connected) return false;
        MessageProtocol.WriteStringAsync(stream, PrivacyProtocol.Ack, token).GetAwaiter().GetResult();
        return true;
    }
    internal static string PrivacyWarning(bool? hidden) => Lang.Current == AppLanguage.Russian
        ? (hidden == false ? "Ваш IP адрес будет раскрыт участникам хаба. Продолжить?" : "Хаб не сообщил, скрыты ли IP адреса. Ваш IP может быть раскрыт участникам. Продолжить?") +
          "\nАдминистратор уже видит адрес вашего соединения. Скрытие IP в приложении не скрывает его от администратора или наблюдателя сети."
        : (hidden == false ? "Your IP address will be disclosed to Hub members. Continue?" : "The Hub did not report its IP policy. Your IP may be disclosed to members. Continue?") +
          "\nThe administrator already sees your connection address. The app's IP hiding does not hide it from the administrator or a network observer.";
}
