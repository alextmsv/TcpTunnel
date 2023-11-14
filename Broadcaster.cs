
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace TCPTunnel
{
    public class Broadcaster
    {
        
        private List<TcpClient> clients = new List<TcpClient>();

        public void AddClient(TcpClient client)
        {
            clients.Add(client);
        }

        public void Broadcast(string message)
        {
            for (int i = 0; i < clients.Count; i++)
            {
                TcpClient client = clients[i];
                if (!client.Connected) 
                    continue;

                try
                {
                    BinaryWriter writer = new BinaryWriter(client.GetStream());
                    writer.Write(message);
                }
                catch (Exception ex)
                {
                    Program.matrix("Проблема с отправкой broadcast сообщения! " + ex.Message);
                }
            }
        }
    }
}
