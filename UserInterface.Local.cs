using System;
using System.IO;
using System.Threading;

namespace TCPTunnel
{
    public partial class UserInterface
    {
        private static bool chatUiSuspended;
        private static void RunChatCustomization(bool snakeOnly, Stream stream, CancellationToken token)
        {
            if (snakeOnly && !ConsoleGraphic.Enabled) { WriteSystemChatLine(Lang.Get(TextId.NoActiveSnake)); return; }
            SnakeProfile previous = OwnSnakeProfile();
            int previousRawDelay = ConsoleGraphic.BorderAnimationDelayMilliseconds;
            RunChatModal(() => new Menu().ShowChatCustomization(snakeOnly));
            SnakeProfile current = OwnSnakeProfile();
            SnakeProfile comparison = current; comparison.DelayMilliseconds = previous.DelayMilliseconds;
            if (snakeOnly && connected && (previousRawDelay != ConsoleGraphic.BorderAnimationDelayMilliseconds || !SnakeProtocol.SameConfiguration(previous, comparison)))
                MessageProtocol.WriteStringAsync(stream, SnakeProtocol.CreateClientProfile(current), token).GetAwaiter().GetResult();
        }

        private static void RunChatModal(Action show)
        {
            lock (consoleLock) { FinishMatrixEffectsLocked(); chatUiSuspended = true; EraseRenderedInput(); InvalidateAnimationTargetsLocked(); FinishMentionBlinkLocked(); }
            try
            {
                Menu.ChatMenuAlive = () => connected;
                show();
            }
            finally
            {
                Menu.ChatMenuAlive = null; ConsoleGraphic.SetMenuScreen(false);
                lock (consoleLock)
                {
                    chatUiSuspended = false; hasKnownConsoleGeometry = false;
                    if (connected && !RedrawChatLayoutLocked()) MarkConsoleResizePendingLocked();
                }
            }
        }

        private static SnakeProfile OwnSnakeProfile() => new SnakeProfile {
            Enabled = ConsoleGraphic.Enabled, Paused = ConsoleGraphic.BorderSnakePaused,
            DelayMilliseconds = ConsoleGraphic.CurrentBorderSnakeReferenceDelayMilliseconds, Color = ConsoleGraphic.BorderSnakeColor,
            Step = ConsoleGraphic.CurrentBorderSnakeReferenceStep, Glyph = ConsoleGraphic.BorderSnakeGlyph,
            Nickname = nicknameSnakeSupported && ApplicationSettings.Current.NicknameSnake
        };

        private static void ChangeChatLanguage(AppLanguage language)
        {
            Lang.Set(language);
            ApplicationSettings.CaptureAndSave();
            lock (consoleLock)
            {
                ConsoleGraphic.InvalidateVisualTheme();
                if (!RedrawChatLayoutLocked()) MarkConsoleResizePendingLocked();
            }
            WriteSystemChatLine(Lang.Get(TextId.LanguageChanged));
        }
    }
}
