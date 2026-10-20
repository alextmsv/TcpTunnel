using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TCPTunnel;

internal sealed class ProfileWriteLease : IDisposable
{
    private readonly Mutex mutex;
    internal ProfileWriteLease(string path)
    {
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        mutex = new Mutex(false, @"Local\TCPTunnel.Config." + name);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(2))) throw new IOException("The profile is being updated by another application instance."); }
            catch (AbandonedMutexException) { }
        }
        catch { mutex.Dispose(); throw; }
    }
    public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
}
