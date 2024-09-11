using System.Net.Sockets;

namespace TCPTunnel
{
    public class ConnectionContext
    {
        public string[] nicknames;
        public TcpClient client { get; set; }
        public Broadcaster broadcaster { get; set; }

    }
}
