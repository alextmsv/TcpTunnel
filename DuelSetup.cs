using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel;

internal static class DuelSetup
{
    private sealed record Selection(string Mode, string Size, string Difficulty, int Minutes, int Targets, bool Blitz);
    private const string Bootstrap = "& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::ReadLine()))))";

    internal static async Task<DuelPacket> OpenAsync(string peer, bool russian, CancellationToken token)
    {
        if (!NetWorker.IsNicknameValid(peer)) return null;
        string executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Sta", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap)) }) info.ArgumentList.Add(arg);
        info.Environment["TCPTUNNEL_DUEL_PEER"] = peer;
        info.Environment["TCPTUNNEL_DUEL_LANGUAGE"] = russian ? "ru" : "en";
        using var resource = typeof(DuelSetup).Assembly.GetManifestResourceStream("TCPTunnel.DuelSetup.ps1");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        string script = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        using var child = Process.Start(info) ?? throw new IOException("Could not start the duel settings window.");
        try
        {
            var output = child.StandardOutput.ReadToEndAsync(token);
            var error = child.StandardError.ReadToEndAsync(token);
            await child.StandardInput.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(script)).AsMemory(), token).ConfigureAwait(false);
            child.StandardInput.Close();
            await child.WaitForExitAsync(token).ConfigureAwait(false);
            string result = (await output.ConfigureAwait(false)).Trim();
            await error.ConfigureAwait(false);
            if (child.ExitCode != 0)
                throw new IOException("Could not open the duel settings window.");
            if (result.Length == 0) return null;
            if (result.Length > 512) throw new InvalidDataException("Invalid duel settings.");
            var selection = JsonSerializer.Deserialize<Selection>(result) ?? throw new InvalidDataException("Invalid duel settings.");
            var packet = new DuelPacket("offer", Peer: peer, Level: selection.Mode == "minefield" ? "normal" : selection.Difficulty,
                Minutes: selection.Minutes, Blitz: selection.Blitz, Version: 2, Mode: selection.Mode, Size: selection.Size,
                Count: selection.Mode == "minefield" ? selection.Targets : 0);
            return DuelProtocol.ValidOptions(packet) ? packet : throw new InvalidDataException("Invalid duel settings.");
        }
        finally
        {
            if (!child.HasExited) { try { child.Kill(); } catch (InvalidOperationException) { } }
        }
    }
}
