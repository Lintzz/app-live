using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Websocket.Client;

namespace RadminStreamApp
{
    public class SignalingClient
    {
        private WebsocketClient _client;

        public event Action<string> OnMessageReceived = delegate {};
        public event Action<byte[]> OnBinaryReceived = delegate {};
        public event Action OnConnected = delegate {};
        public event Action OnDisconnected = delegate {};

        public async Task StartAsync(string ipAddress, int port = 8080)
        {
            var url = new Uri($"ws://{ipAddress}:{port}");
            _client = new WebsocketClient(url);

            _client.MessageReceived.Subscribe(msg =>
            {
                if (msg.MessageType == System.Net.WebSockets.WebSocketMessageType.Text && msg.Text != null)
                {
                    Debug.WriteLine($"[Client] Message received: {msg.Text.Substring(0, Math.Min(msg.Text.Length, 50))}...");
                    OnMessageReceived?.Invoke(msg.Text);
                }
                else if (msg.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary && msg.Binary != null)
                {
                    OnBinaryReceived?.Invoke(msg.Binary);
                }
            });

            _client.ReconnectionHappened.Subscribe(info =>
            {
                Debug.WriteLine($"[Client] Reconnection happened, type: {info.Type}");
                OnConnected?.Invoke();
            });

            _client.DisconnectionHappened.Subscribe(info =>
            {
                Debug.WriteLine($"[Client] Disconnected: {info.CloseStatus}");
                OnDisconnected?.Invoke();
            });

            await _client.Start();
        }

        public void SendMessage(string message)
        {
            if (_client != null && _client.IsRunning)
            {
                _client.Send(message);
            }
        }

        public void SendBinary(byte[] data)
        {
            if (_client != null && _client.IsRunning)
            {
                _client.Send(data);
            }
        }

        public void Stop()
        {
            _client?.Dispose();
        }
    }
}
