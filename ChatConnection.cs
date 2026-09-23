using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TCPTunnel
{
    internal interface IChatConnection : IDisposable
    {
        Stream Stream { get; }
        IPAddress RemoteIpAddress { get; }
    }

    internal sealed class TcpChatConnection : IChatConnection
    {
        private int disposed;
        internal TcpClient Client { get; }
        public Stream Stream { get; }
        public IPAddress RemoteIpAddress { get; }

        internal TcpChatConnection(TcpClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            Client.NoDelay = true;
            Stream = Client.GetStream();
            RemoteIpAddress = (Client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Client.Dispose();
        }
    }
}
