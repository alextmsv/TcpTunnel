using System;
using System.IO;
using System.Threading;

namespace TCPTunnel;

public partial class UserInterface
{
    private static void ChangeSessionNickname(Stream stream, HubStatusSession status, CancellationToken token)
    {
        if (!status.Modern) { WriteSystemChatLine(Lang.Get(TextId.NicknameChangeUnavailable)); return; }
        string next = null;
        RunChatModal(() => next = Menu.ReadChatNickname());
        if (!connected || next == null || next == nickname) return;
        if (!NicknamePolicy.IsValid(next)) { WriteSystemChatLine(Lang.Get(TextId.NicknameRules)); return; }
        try
        {
            using (ApplicationSettings.OpenIdentity()) { }
            ApplicationSettings.CheckSessionNickname(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { WriteSystemChatLine(Lang.Get(TextId.NicknameChangeFailed, ex.Message)); return; }
        var reply = status.Nicknames.RenameAsync(next, (frame, ct) => MessageProtocol.WriteStringAsync(stream, frame, ct), token).GetAwaiter().GetResult();
        if (reply.Error != "OK")
        {
            WriteSystemChatLine(Lang.Get(TextId.NicknameChangeFailed, Lang.Get(reply.Error == "TAKEN" ? TextId.AuthNicknameTaken : TextId.StatusUnavailable)));
            return;
        }
        try { ApplicationSettings.SaveSessionNickname(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { WriteSystemChatLine(Lang.Get(TextId.NicknameSaveFailed, ex.Message)); }
    }

    private static void ApplyParticipantNickname(NicknameChange change)
    {
        fileTransfers?.RenamePeer(change.Previous);
        lock (participantsLock)
        {
            activeParticipants.Remove(change.Previous);
            if (activeParticipants.Count < ServerInterface.MaxConnectedClients) activeParticipants.Add(change.Current);
            pendingParticipantSnakes.Remove(change.Previous);
        }
        ConsoleGraphic.RemoveRemoteSnake(change.Previous);
        ConsoleGraphic.InvalidateVisualTheme();
        WriteSystemChatLine(Lang.Get(TextId.NicknameChanged, change.Previous, change.Current));
    }
}
