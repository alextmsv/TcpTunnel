
using System.Net.Sockets;

namespace TCPTunnel
{
    public class Client
    {
        public Client(TcpClient tcpClient)
        {
            TcpClient = tcpClient;
        }

        public Client(TcpClient tcpClient, string ipAddress, string nickname)
        {
            TcpClient = tcpClient;
            this.ipAddress = ipAddress;
            this.nickname = nickname;
        }

        public TcpClient TcpClient { get; set; }
        public string ipAddress { get; set; }
        public string nickname { get; set; }
    }
}
