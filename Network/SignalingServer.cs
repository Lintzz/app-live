using System;
using System.Collections.Generic;
using System.Diagnostics;
using Fleck;
using SIPSorcery.Net;

namespace RadminStreamApp
{
    public class SignalingServer
    {
        private WebSocketServer _server;
        private List<IWebSocketConnection> _clients = new List<IWebSocketConnection>();

        public event Action<IWebSocketConnection, string> OnMessageReceived;
        public event Action<IWebSocketConnection, byte[]> OnBinaryReceived;
        public event Action<IWebSocketConnection> OnClientConnected;
        public event Action<IWebSocketConnection> OnClientDisconnected;

        public int ConnectedClientsCount => _clients.Count;
        public IReadOnlyList<IWebSocketConnection> Clients => _clients.AsReadOnly();

        public void Start(string ipAddress = "0.0.0.0", int port = 8080)
        {
            _server = new WebSocketServer($"ws://{ipAddress}:{port}");
            _server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    Debug.WriteLine($"[Server] Client connected: {socket.ConnectionInfo.ClientIpAddress}");
                    _clients.Add(socket);
                    OnClientConnected?.Invoke(socket);
                };

                socket.OnClose = () =>
                {
                    Debug.WriteLine($"[Server] Client disconnected: {socket.ConnectionInfo.ClientIpAddress}");
                    _clients.Remove(socket);
                    OnClientDisconnected?.Invoke(socket);
                };

                socket.OnMessage = message =>
                {
                    Debug.WriteLine($"[Server] Message received from {socket.ConnectionInfo.ClientIpAddress}: {message.Substring(0, Math.Min(message.Length, 50))}...");
                    OnMessageReceived?.Invoke(socket, message);
                };

                socket.OnBinary = bytes =>
                {
                    OnBinaryReceived?.Invoke(socket, bytes);
                };
            });

            Debug.WriteLine($"[Server] Started on ws://{ipAddress}:{port}");
        }

        public void SendMessage(IWebSocketConnection client, string message)
        {
            client.Send(message);
        }

        public void BroadcastBinary(byte[] data)
        {
            foreach (var client in _clients)
            {
                client.Send(data);
            }
        }

        public void BroadcastMessage(string message)
        {
            foreach (var client in _clients)
            {
                client.Send(message);
            }
        }

        public void Stop()
        {
            foreach (var client in _clients)
            {
                client.Close();
            }
            _server?.Dispose();
        }
    }
}
