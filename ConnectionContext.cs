using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public class ConnectionContext
    {
        public string[] nicknames;
        public TcpClient client { get; set; }
        public Broadcaster broadcaster { get; set; }

    }
}
