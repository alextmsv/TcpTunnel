using System;
using System.Threading;

namespace TCPTunnel
{
    internal static class ConsoleTitleAnimator
    {
        private const int StepIntervalMilliseconds = 220;
        private const int RightEdgeCharacterGap = 2;
        private const int FallbackConsoleWidth = 71;
        private static readonly object stateLock = new object();
        private static readonly Timer timer = new Timer(Advance, null, Timeout.Infinite, Timeout.Infinite);
        private static string caption = String.Empty;
        private static string title = String.Empty;
        private static int offset;
        private static int callbackActive;
        private static bool animationEnabled;

        public static void SetCaption(string value, bool animate)
        {
            lock (stateLock)
            {
                caption = value ?? String.Empty;
                animationEnabled = animate;
                if (!animationEnabled)
                {
                    title = caption;
                    TryWriteTitle(caption);
                }
                else
                {
                    title = BuildTitle(caption);
                    if (title.Length > 0)
                        offset %= title.Length;
                    else
                        offset = 0;
                        TryWriteTitle(RotateRight(title, offset));
                }
            }

            timer.Change(
                animate ? StepIntervalMilliseconds : Timeout.Infinite,
                animate ? StepIntervalMilliseconds : Timeout.Infinite);
        }

        public static void Stop()
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        internal static bool RunSelfTest()
        {
            string compactTitle = BuildTitle("Menu", 10);
            string resizedTitle = BuildTitle("Menu", 20);
            return compactTitle == "--Menu--" &&
                   resizedTitle.Length == 18 &&
                   RotateRight("ABC", 0) == "ABC" &&
                   RotateRight("ABC", 1) == "CAB" &&
                   RotateRight("ABC", 2) == "BCA" &&
                   RotateRight("ABC", 3) == "ABC" &&
                   RotateRight(String.Empty, 12) == String.Empty;
        }

        private static void Advance(object state)
        {
            if (Interlocked.Exchange(ref callbackActive, 1) != 0)
                return;

            try
            {
                string frame;
                lock (stateLock)
                {
                    if (!animationEnabled)
                        return;
                    
                    title = BuildTitle(caption);

                    if (title.Length <= 1)
                        return;

                    offset = (offset + 1) % title.Length;
                    frame = RotateRight(title, offset);
                    TryWriteTitle(frame);
                }
            }
            finally
            {
                Volatile.Write(ref callbackActive, 0);
            }
        }

        private static string RotateRight(string value, int amount)
        {
            if (String.IsNullOrEmpty(value))
                return String.Empty;

            int normalized = ((amount % value.Length) + value.Length) % value.Length;
            if (normalized == 0)
                return value;

            int split = value.Length - normalized;
            return value.Substring(split) + value.Substring(0, split);
        }

        private static string BuildTitle(string value)
        {
            int width = FallbackConsoleWidth;
            try
            {
                width = Console.WindowWidth;
            }
            catch (Exception)
            {
            }

            return BuildTitle(value, width);
        }

        private static string BuildTitle(string value, int consoleWidth)
        {
            string safeCaption = value ?? String.Empty;
            int targetLength = Math.Max(
                safeCaption.Length + 4,
                Math.Max(4, consoleWidth) - RightEdgeCharacterGap);
            int dashCount = targetLength - safeCaption.Length;
            int leftDashes = dashCount / 2;
            int rightDashes = dashCount - leftDashes;
            return new string('-', leftDashes) + safeCaption + new string('-', rightDashes);
        }

        private static void TryWriteTitle(string value)
        {
            try
            {
                Console.Title = value;
            }
            catch (Exception) { }
        }
    }
}
