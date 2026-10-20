#nullable enable
using System.IO;
using System.Threading;

namespace TCPTunnel;

public partial class UserInterface
{
    private static FileTransferSession? fileTransfers;
    private static void StartFileTransfers(Stream stream, CancellationToken token)
    {
        fileTransfers?.Dispose();
        fileTransfers = null;
        try { fileTransfers = new FileTransferSession(() => nickname, (frame, cancellation) => MessageProtocol.WriteStringAsync(stream, frame, cancellation), WriteSystemChatLine, token); }
        catch (System.Exception ex) when (ex is System.ArgumentException or IOException or System.UnauthorizedAccessException)
        { WriteSystemChatLine(Lang.Current == AppLanguage.Russian ? "Передача файлов недоступна: рабочий стол не найден." : "File transfers unavailable: Desktop directory not found."); }
    }
    private static void StopFileTransfers() { fileTransfers?.Dispose(); fileTransfers = null; }
}
