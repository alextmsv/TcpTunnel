using System;
using System.Linq;
using System.Threading;
using System.Net;

namespace TCPTunnel
{
    public partial class UserInterface
    {
        private static bool TryConnectLan(ConnectionSecurityMode securityMode)
        {
            LanDiscoveredHub selectedHub = null;
            try
            {
                using var scanner = new LanHubScanner();
                string selectedId = null, lastRows = null; int selected = 0; long redraw = 0;
                while (true)
                {
                    if (!scanner.Available) throw new InvalidOperationException(scanner.Failure ?? Lang.Get(TextId.LanNoHubs));
                    var hubs = scanner.Snapshot();
                    if (selectedId != null) { int preserved = Array.FindIndex(hubs, hub => hub.Id == selectedId); if (preserved >= 0) selected = preserved; }
                    selected = Math.Clamp(selected, 0, Math.Max(0, hubs.Length - 1));
                    string rows = String.Join("|", hubs.Select(hub => hub.Id + ":" + hub.Advertisement.Owner + ":" + hub.Advertisement.Participants));
                    if (rows != lastRows || Environment.TickCount64 >= redraw)
                    {
                        try
                        {
                        lock (consoleLock)
                        {
                            graphic.Clear();
                            int left = ConsoleGraphic.ContentLeft, top = ConsoleGraphic.ContentTop, width = GetContentWidth();
                            void Line(int row, string text, ConsoleColor color) { if (row > ConsoleGraphic.ContentBottom) return; Console.SetCursorPosition(left, row); ConsoleGraphic.ApplyContentColors(color); Console.Write(text.Length > width ? text.Substring(0, width) : text); Console.ResetColor(); }
                            Line(top, Lang.Get(TextId.LanSearching), ConsoleTheme.SystemText);
                            Line(top + 1, Lang.Get(TextId.LanSearchHint), ConsoleTheme.SystemText);
                            if (hubs.Length == 0) Line(top + 3, Lang.Get(TextId.LanNoHubs), ConsoleTheme.MenuText);
                            int visible = Math.Max(1, ConsoleGraphic.ContentBottom - top - 3);
                            int first = Math.Max(0, selected - visible + 1);
                            for (int i = first; i < Math.Min(hubs.Length, first + visible); i++)
                            {
                                var ad = hubs[i].Advertisement;
                                string label = (i == selected ? "> " : "  ") + ad.Owner + "  " + hubs[i].Address + ":" + ad.Port + "  " + ad.Participants + "/" + ad.Limit + "  E2E: " + (ad.Encrypted ? "TRUE" : "FALSE");
                                Line(top + 3 + i - first, label, i == selected ? ConsoleTheme.SelectionBackground : ConsoleTheme.MenuText);
                            }
                        }
                        lastRows = rows; redraw = Environment.TickCount64 + 700;
                        }
                        catch (ArgumentOutOfRangeException) { lastRows = null; Thread.Sleep(30); continue; }
                    }
                    if (!ConsoleInput.TryRead(out var input)) { Thread.Sleep(30); continue; }
                    bool choose = false;
                    if (input.Kind == ConsoleInputKind.Key)
                    {
                        if (input.Key.Key == ConsoleKey.Escape) return false;
                        if (input.Key.Key == ConsoleKey.DownArrow) selected = Math.Min(hubs.Length - 1, selected + 1);
                        if (input.Key.Key == ConsoleKey.UpArrow) selected = Math.Max(0, selected - 1);
                        choose = input.Key.Key == ConsoleKey.Enter;
                    }
                    else if (ConsoleInput.MouseMode)
                    {
                        int visible = Math.Max(1, ConsoleGraphic.ContentBottom - ConsoleGraphic.ContentTop - 3);
                        int first = Math.Max(0, selected - visible + 1), index = first + input.Y - ConsoleGraphic.ContentTop - 3;
                        if (index >= first && index < hubs.Length && index < first + visible) { selected = index; choose = input.Kind == ConsoleInputKind.Click; }
                    }
                    if (hubs.Length != 0 && selected >= 0 && selected < hubs.Length) selectedId = hubs[selected].Id;
                    lastRows = null;
                    if (choose && hubs.Length > 0) { selectedHub = hubs[selected]; break; }
                }
            }
            catch (Exception error) when (error is System.Net.Sockets.SocketException or System.IO.IOException or InvalidOperationException)
            { WriteSystemChatLine(Lang.Get(TextId.LanDiscoveryUnavailable, error.Message)); return false; }
            if (ServerInterface.IsRunning && selectedHub.Advertisement.HubId == ServerInterface.LanHubId && selectedHub.Advertisement.Port == ServerInterface.ListeningPort)
                return EnterOwnHub();
            return DoConnectWithSecurity(selectedHub.Address.ToString(), selectedHub.Advertisement.Port, securityMode);
        }
    }
}
