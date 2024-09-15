using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;

namespace TCPTunnel
{
    public class Broadcaster
    {

        private List<Client> clients = new List<Client>();

        public void AddClient(Client client)
        {
            clients.Add(client);
        }

        public void Broadcast(Client sender, string message)
        {
            for (int i = 0; i < clients.Count; i++)
            {
                Client client = clients[i];
                TcpClient pipe = client.TcpClient;

                if (!pipe.Connected) 
                    continue;

                try
                {
                    BinaryWriter writer = new BinaryWriter(pipe.GetStream());
                    if (sender != null)
                        writer.Write(sender.nickname + " [" + sender.ipAddress + "]: " + message);
                    else
                        writer.Write(message);
                }
                catch (Exception ex)
                {
                    Program.matrix($"Проблема с отправкой broadcast сообщения :(\n{ex.Message}\n");
                }
            }
        }
    }
}
