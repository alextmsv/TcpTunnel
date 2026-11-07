using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel;

internal sealed class DuelSession(Stream stream, CancellationToken token, Action<string, ConsoleColor?> notice)
{
    private readonly object gate = new();
    private bool supported, optionsSupported, closed, setupOpen;
    private DuelPacket invitation, authorization;
    private long invitedAt, authorizedAt;
    private DuelWindow window;
    private Guid round;

    private static string Text(string ru, string en) => Lang.Current == AppLanguage.Russian ? ru : en;
    internal void Hello() { Send(new("hello")); Send(new("hello2")); }

    internal bool HandleCommand(string input)
    {
        int space = input.AsSpan().IndexOfAny(' ', '\t');
        if (!(space < 0 ? input : input[..space]).Equals("/duel", StringComparison.OrdinalIgnoreCase)) return false;
        lock (gate)
        {
            if (closed) return true;
            if (!DuelProtocol.TryCommand(input, out var packet))
            {
                notice("/duel @nickname | /duel accept | /duel practice [easy|normal|hard 1m–15m]", null);
                return true;
            }
            if (DuelPractice.Active)
            {
                notice(Text("Сначала закройте окно тренировки.", "Close the practice window first."), null);
                return true;
            }
            if (round != Guid.Empty)
            {
                notice(Text("Дуэль уже идёт.", "A duel is already running."), null);
                return true;
            }
            if (setupOpen) return true;
            if (authorization != null && Stopwatch.GetElapsedTime(authorizedAt).TotalSeconds < 45)
            {
                notice(Text("Предыдущий вызов ещё ожидает ответа.", "The previous challenge is still pending."), null);
                return true;
            }
            if (packet.Kind == "practice")
            {
                DuelPractice.Start(packet);
                return true;
            }
            if (!supported) { notice(Text("Хаб не поддерживает дуэли.", "This Hub does not support duels."), null); return true; }
            if (packet.Kind == "setup")
            {
                if (!optionsSupported)
                    notice(Text("Для окна настроек обновите хаб и оба клиента.", "Update the Hub and both clients to use duel settings."), null);
                else if (!setupOpen)
                {
                    setupOpen = true;
                    _ = OpenSetupAsync(packet.Peer);
                }
                return true;
            }
            if (packet.Kind == "accept")
            {
                if (invitation == null || Stopwatch.GetElapsedTime(invitedAt).TotalSeconds >= 45)
                { notice(Text("Нет активного вызова.", "No active duel invitation."), null); return true; }
                authorization = invitation;
                invitation = null;
                packet = packet with { Id = authorization.Id };
            }
            else
            {
                if (optionsSupported)
                    packet = packet with { Version = 2, Mode = "static", Size = packet.Level switch { "easy" => "large", "hard" => "small", _ => "medium" } };
                authorization = packet;
            }
            authorizedAt = Stopwatch.GetTimestamp();
            Send(packet);
            return true;
        }
    }

    internal bool Receive(string message)
    {
        if (!DuelProtocol.IsControl(message)) return false;
        if (!DuelProtocol.TryDecode(message, out var packet)) return true;
        lock (gate)
        {
            if (closed) return true;
            switch (packet.Kind)
            {
                case "caps": supported = true; break;
                case "caps2": supported = optionsSupported = true; break;
                case "invite" when ValidRules(packet):
                    invitation = packet; invitedAt = Stopwatch.GetTimestamp();
                    string level = packet.Level switch
                    {
                        "hard" => Text("СЛОЖНЫМ", "HARD"), "easy" => Text("ЛЁГКИМ", "EASY"), _ => Text("СРЕДНИМ", "NORMAL")
                    };
                    notice(Text($"@{packet.Peer} вызывает вас на дуэль на {packet.Minutes} МИН. по {level} мишеням",
                        $"@{packet.Peer} challenges you to a duel: {packet.Minutes} MIN, {level} targets")
                        + (packet.Version == 2 ? " | " + ModeSummary(packet) : "") + (packet.Blitz ? " | BLITZ" : "") + " | /duel accept", ConsoleColor.Yellow);
                    break;
                case "sent" when packet.Id != Guid.Empty && authorization is { Kind: "offer" } pending &&
                    packet.Peer.Equals(pending.Peer, StringComparison.OrdinalIgnoreCase):
                    authorization = pending with { Id = packet.Id };
                    notice(Text($"Вызов отправлен @{packet.Peer}.", $"Duel invitation sent to @{packet.Peer}."), null);
                    break;
                case "error":
                    authorization = null;
                    notice(packet.Outcome switch
                    {
                        "busy" => Text("Один из игроков уже занят вызовом или дуэлью.", "One of the players already has an invitation or duel."),
                        "update" => Text("Для этого режима обновите оба клиента.", "Both clients must be updated for this mode."),
                        "no-invite" => Text("Вызов истёк или отменён.", "The invitation expired or was cancelled."),
                        _ => Text("Вызов недоступен. Проверьте ник и поддержку дуэлей.", "Duel unavailable. Check the nickname and duel support.")
                    }, null);
                    break;
                case "start" when ValidRules(packet) && round == Guid.Empty:
                    if (authorization == null || packet.Id != authorization.Id || Stopwatch.GetElapsedTime(authorizedAt).TotalSeconds >= 46 ||
                        packet.Level != authorization.Level || packet.Minutes != authorization.Minutes || packet.Blitz != authorization.Blitz ||
                        packet.Version != authorization.Version || packet.Mode != authorization.Mode || packet.Size != authorization.Size ||
                        packet.Count != authorization.Count || DuelPractice.Active) break;
                    authorization = null;
                    round = packet.Id; window = new DuelWindow(packet, Send); window.Start();
                    break;
                case "target" or "score" when round != Guid.Empty && packet.Id == round:
                    window?.Receive(packet); break;
                case "ping" when round != Guid.Empty && packet.Id == round && packet.Nonce != Guid.Empty:
                    Send(new("pong", round, Nonce: packet.Nonce)); break;
                case "end" when round != Guid.Empty && packet.Id == round:
                    window?.Close(); window = null; round = Guid.Empty;
                    string outcome = packet.Outcome switch
                    {
                        "draw" => Text("ничья", "draw"),
                        "win" => Text("победа", "victory"),
                        "loss" => Text("поражение", "defeat"),
                        "forfeit" => Text("поражение: вы вышли", "defeat: you left"),
                        "opponent-left" => Text("победа: соперник вышел", "victory: opponent left"),
                        _ => null
                    };
                    if (outcome != null) notice(Text("Дуэль", "Duel") + $": {outcome} ({packet.You}:{packet.Them}).", ConsoleColor.Yellow);
                    break;
            }
        }
        return true;
    }

    private static bool ValidRules(DuelPacket packet) => packet.Id != Guid.Empty && NetWorker.IsNicknameValid(packet.Peer) && DuelProtocol.ValidOptions(packet);

    private static string ModeSummary(DuelPacket packet) =>
        (packet.Mode switch
        {
            "moving" => Text("движущиеся", "moving"),
            "minefield" => Text("минное поле", "minefield") + $" ({packet.Count})",
            _ => Text("статичные", "static")
        }) + " / " + (packet.Size switch
        {
            "large" => Text("большие", "large"), "small" => Text("маленькие", "small"),
            "any" => Text("случайный размер", "random size"), _ => Text("средние", "medium")
        });

    private async Task OpenSetupAsync(string peer)
    {
        try
        {
            var packet = await DuelSetup.OpenAsync(peer, Lang.Current == AppLanguage.Russian, token).ConfigureAwait(false);
            lock (gate)
            {
                if (packet == null || closed || token.IsCancellationRequested || round != Guid.Empty || DuelPractice.Active) return;
                authorization = packet;
                authorizedAt = Stopwatch.GetTimestamp();
                Send(packet);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (gate)
                if (!closed) notice(Text("Не удалось открыть окно настроек дуэли.", "Could not open the duel settings window."), ConsoleColor.Yellow);
        }
        finally { lock (gate) setupOpen = false; }
    }

    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            window?.Close(); window = null;
            if (round != Guid.Empty) notice(Text("Дуэль: поражение — соединение закрыто.", "Duel: defeat — connection closed."), ConsoleColor.Yellow);
            round = Guid.Empty;
        }
    }

    private void Send(DuelPacket packet) => _ = SendAsync(packet);
    private async Task SendAsync(DuelPacket packet)
    {
        try { await MessageProtocol.WriteStringAsync(stream, DuelProtocol.Encode(packet), token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
