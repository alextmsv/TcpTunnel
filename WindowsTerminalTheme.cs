using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TCPTunnel
{
    internal static class WindowsTerminalTheme
    {
        private const string BackupSuffix = ".tcptunnel-bak";
        private const string StateSuffix = ".tcptunnel-theme.json";
        private static readonly JsonDocumentOptions JsonOptions = new()
        { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        private static IntPtr hostingWindow;
        private static bool? windowWasActive;
        private static long nextActivationCheck;
        private static long nextPeriodicReapply;
        private const int PeriodicReapplySeconds = 3;
        private static int pendingRedraw;
        private static readonly Timer activationTimer = new(_ =>
        {
            try { PollActivation(); }
            catch (IOException) { }
        }, null, Timeout.Infinite, Timeout.Infinite);

        private static string HostingExecutable()
        {
            try
            {
                IntPtr window = GetConsoleWindow();
                for (int depth = 0; window != IntPtr.Zero && depth < 8; depth++, window = GetWindow(window, 4))
                {
                    GetWindowThreadProcessId(window, out uint id);
                    using var process = Process.GetProcessById((int)id);
                    if (process.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase))
                    {
                        hostingWindow = window;
                        return process.MainModule?.FileName;
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception ||
                ex is InvalidOperationException || ex is ArgumentException) { }
            return null;
        }

        internal static bool IsRunningInWindowsTerminal() =>
            !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) || HostingExecutable() != null;

        internal static bool TryGetSettingsPath(out string path)
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string host = HostingExecutable();
            if (host != null)
            {
                string portable = Path.Combine(Path.GetDirectoryName(host), "settings", "settings.json");
                if (File.Exists(portable)) { path = portable; return true; }
            }
            string package = host?.Contains("WindowsTerminalPreview", StringComparison.OrdinalIgnoreCase) == true
                ? "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe" : "Microsoft.WindowsTerminal_8wekyb3d8bbwe";
            foreach (string candidate in new[] {
                Path.Combine(root, "Packages", package, "LocalState", "settings.json"),
                Path.Combine(root, "Microsoft", "Windows Terminal", "settings.json")
            })
                if (File.Exists(candidate)) { path = candidate; return true; }
            path = null;
            return false;
        }

        internal static bool HasSessionBackground { get; private set; }
        private static int appliedRgb;

        internal static string BackgroundSequence(int rgb)
        {
            string background = (rgb & 0xFFFFFF).ToString("X6");
            string foreground = ConsoleTheme.ReadableForeground(0xFFFFFF, rgb) == 0 ? "000000" : "FFFFFF";
            return "\u001b]10;#" + foreground + "\u0007\u001b]11;#" + background + "\u0007\u001b[0m";
        }

        internal static bool TryApplyBackground(int rgb, out string error, bool force = false)
        {
            error = null;
            if (Console.IsOutputRedirected || !IsRunningInWindowsTerminal())
            { error = Lang.Get(TextId.WindowsTerminalNotRunning); return false; }
            if (!ConsoleGraphic.EnableVirtualTerminalOutput())
            { error = "Virtual terminal output is unavailable."; return false; }
            if (hostingWindow == IntPtr.Zero) HostingExecutable();
            lock (ConsoleGraphic.borderAnimationLock)
            {
                try
                {
                    rgb &= 0xFFFFFF;
                    if (!force && HasSessionBackground && appliedRgb == rgb) return true;
                    Console.Write(force ? "\u001b7" + BackgroundSequence(rgb) + "\u001b8" : BackgroundSequence(rgb));
                    Console.Out.Flush();
                    appliedRgb = rgb;
                    bool startMonitoring = !HasSessionBackground;
                    HasSessionBackground = true;
                    if (startMonitoring) activationTimer.Change(0, 100);
                    return true;
                }
                catch (IOException ex) { error = ex.Message; return false; }
            }
        }

        internal static bool RefreshAfterActivation()
        {
            PollActivation();
            return Interlocked.Exchange(ref pendingRedraw, 0) != 0;
        }

        private static void PollActivation()
        {
            if (!Monitor.TryEnter(ConsoleGraphic.borderAnimationLock)) return;
            try
            {
                if (!HasSessionBackground || hostingWindow == IntPtr.Zero) return;
                long now = Stopwatch.GetTimestamp();
                if (now < nextActivationCheck) return;
                nextActivationCheck = now + Stopwatch.Frequency / 10;
                if (RefreshForActivation(!IsIconic(hostingWindow) && GetForegroundWindow() == hostingWindow))
                {
                    Interlocked.Exchange(ref pendingRedraw, 1);
                    nextPeriodicReapply = now + Stopwatch.Frequency * PeriodicReapplySeconds;
                }
                else if (now >= nextPeriodicReapply)
                {
                    nextPeriodicReapply = now + Stopwatch.Frequency * PeriodicReapplySeconds;
                    TryApplyBackground(appliedRgb, out _, force: true);
                }
            }
            finally { Monitor.Exit(ConsoleGraphic.borderAnimationLock); }
        }

        internal static bool RefreshForActivation(bool active)
        {
            lock (ConsoleGraphic.borderAnimationLock)
            {
                bool resumed = windowWasActive == false && active;
                windowWasActive = active;
                return HasSessionBackground && resumed && TryApplyBackground(appliedRgb, out _, force: true);
            }
        }

        internal static bool TryClearBackground(out string error)
        {
            error = null;
            lock (ConsoleGraphic.borderAnimationLock)
            {
                if (!HasSessionBackground) return true;
                try
                {
                    Console.Write("\u001b]110\u0007\u001b]111\u0007\u001b[0m");
                    Console.Out.Flush();
                    HasSessionBackground = false;
                    windowWasActive = null;
                    activationTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    Interlocked.Exchange(ref pendingRedraw, 0);
                    return true;
                }
                catch (IOException ex) { error = ex.Message; return false; }
            }
        }
        // Store only the values this feature owns; restoring must not discard other Terminal edits.
        internal sealed class SavedAppearance
        {
            public string ProfileId { get; set; }
            public string Background { get; set; }
            public string Foreground { get; set; }
            public string AppliedBackground { get; set; }
            public string AppliedForeground { get; set; }
        }

        internal static bool TryRestoreFile(string path, out string error)
        {
            error = null;
            try
            {
                string statePath = path + StateSuffix;
                if (!File.Exists(statePath)) return true;
                var saved = JsonSerializer.Deserialize<SavedAppearance>(File.ReadAllText(statePath))
                    ?? throw new JsonException("Invalid Terminal theme state.");
                string json = File.ReadAllText(path);
                using var document = JsonDocument.Parse(json, JsonOptions);
                var profile = FindProfile(document.RootElement, saved.ProfileId, out _);
                string raw = profile.GetRawText(), updated = raw;
                if (profile.TryGetProperty("background", out var bg) && bg.GetRawText() == saved.AppliedBackground)
                    updated = EditProperty(updated, "background", saved.Background);
                if (profile.TryGetProperty("foreground", out var fg) && fg.GetRawText() == saved.AppliedForeground)
                    updated = EditProperty(updated, "foreground", saved.Foreground);
                WriteAtomic(path, ReplaceObject(json, raw, updated));
                File.Delete(statePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is JsonException || ex is InvalidOperationException)
            { error = ex.Message; return false; }
        }

        private static JsonElement FindProfile(JsonElement root, string id, out string resolvedId)
        {
            if (String.IsNullOrEmpty(id) && root.TryGetProperty("defaultProfile", out var defaultProfile))
                id = defaultProfile.GetString();
            resolvedId = id;
            if (root.TryGetProperty("profiles", out var profiles))
            {
                JsonElement list = profiles;
                if (profiles.ValueKind == JsonValueKind.Object) profiles.TryGetProperty("list", out list);
                if (list.ValueKind == JsonValueKind.Array)
                    foreach (var profile in list.EnumerateArray())
                        if (profile.TryGetProperty("guid", out var guid) &&
                            String.Equals(guid.GetString(), id, StringComparison.OrdinalIgnoreCase))
                            return profile;
            }
            throw new InvalidOperationException(Lang.Get(TextId.WindowsTerminalSchemeNotFound));
        }

        private static string ReplaceObject(string json, string original, string replacement)
        {
            int start = json.IndexOf(original, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("Terminal profile changed.");
            return json.Substring(0, start) + replacement + json.Substring(start + original.Length);
        }

        // Edit raw JSON tokens to preserve comments, formatting and unrelated settings.
        internal static string EditProperty(string json, string property, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            bool hasProperties = false;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                hasProperties = true;
                string name = reader.GetString();
                int propertyStart = (int)reader.TokenStartIndex;
                reader.Read();
                int valueStart = (int)reader.TokenStartIndex;
                reader.Skip();
                int valueEnd = (int)reader.BytesConsumed;
                if (name != property) continue;
                if (value != null)
                    return Encoding.UTF8.GetString(bytes, 0, valueStart) + value +
                           Encoding.UTF8.GetString(bytes, valueEnd, bytes.Length - valueEnd);
                reader.Read();
                int next = (int)reader.TokenStartIndex;
                return Encoding.UTF8.GetString(bytes, 0, propertyStart) +
                       Encoding.UTF8.GetString(bytes, next, bytes.Length - next);
            }
            if (value == null) return json;
            int open = json.IndexOf('{') + 1;
            return json.Insert(open, JsonSerializer.Serialize(property) + ": " + value + (hasProperties ? "," : ""));
        }

        private static void WriteAtomic(string path, string contents)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, contents);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
