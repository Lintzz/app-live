using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client;

namespace RadminStreamApp
{
    public class SignalingClient
    {
        private const int MaxReconnectAttempts = 10;
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(3);

        private WebsocketClient _client;
        private bool _intentionalStop = false;
        private int _reconnectAttempts = 0;
        private CancellationTokenSource _reconnectCts;
        private System.Threading.Timer _pingTimer;
        private byte[] _encryptionKey;

        public event Action<string> OnMessageReceived = delegate {};
        public event Action<byte[]> OnBinaryReceived = delegate {};
        public event Action<bool> OnConnected = delegate {}; // bool = isReconnect
        public event Action OnDisconnected = delegate {};
        public event Action<int> OnReconnecting = delegate {}; // attempt number
        public event Action OnReconnectFailed = delegate {};
        public event Action<int> OnLatencyUpdated = delegate {}; // milliseconds

        public void EnableEncryption(string password)
        {
            _encryptionKey = CryptoHelper.DeriveKey(password);
        }

        public void DisableEncryption()
        {
            _encryptionKey = null;
        }

        public async Task StartAsync(string ipAddress, int port = 8080)
        {
            var url = new Uri($"ws://{ipAddress}:{port}");
            _client = new WebsocketClient(url);
            _client.IsReconnectionEnabled = false;

            _client.MessageReceived.Subscribe(msg =>
            {
                if (msg.MessageType == System.Net.WebSockets.WebSocketMessageType.Text && msg.Text != null)
                {
                    HandleTextMessage(msg.Text);
                }
                else if (msg.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary && msg.Binary != null)
                {
                    var data = msg.Binary;
                    if (_encryptionKey != null)
                    {
                        var decrypted = CryptoHelper.TryDecryptBytes(data, _encryptionKey);
                        if (decrypted != null) data = decrypted;
                    }
                    OnBinaryReceived?.Invoke(data);
                }
            });

            _client.ReconnectionHappened.Subscribe(info =>
            {
                Debug.WriteLine($"[Client] Reconnection happened, type: {info.Type}");
                _reconnectCts?.Cancel();
                _reconnectAttempts = 0;
                OnConnected?.Invoke(info.Type != ReconnectionType.Initial);
            });

            _client.DisconnectionHappened.Subscribe(info =>
            {
                Debug.WriteLine($"[Client] Disconnected: {info.Type}");
                OnDisconnected?.Invoke();

                if (_intentionalStop || info.Type == DisconnectionType.ByUser)
                {
                    return;
                }

                _ = TryReconnectLoop();
            });

            _pingTimer = new System.Threading.Timer(SendPing, null, PingInterval, PingInterval);

            await _client.Start();
        }

        private void HandleTextMessage(string text)
        {
            Debug.WriteLine($"[Client] Message received: {text.Substring(0, Math.Min(text.Length, 50))}...");

            var controlMsg = SignalingMessage.Deserialize(text);
            if (controlMsg != null && controlMsg.Type == "PONG")
            {
                if (long.TryParse(controlMsg.Data, out var sentTicks))
                {
                    var rttMs = (int)((DateTime.UtcNow.Ticks - sentTicks) / TimeSpan.TicksPerMillisecond);
                    OnLatencyUpdated?.Invoke(Math.Max(rttMs, 0));
                }
                return;
            }

            var plain = text;
            if (_encryptionKey != null)
            {
                var decrypted = CryptoHelper.TryDecryptText(text, _encryptionKey);
                if (decrypted != null) plain = decrypted;
            }

            OnMessageReceived?.Invoke(plain);
        }

        private void SendPing(object state)
        {
            try
            {
                if (_client == null || !_client.IsRunning) return;
                var ping = new SignalingMessage { Type = "PING", Data = DateTime.UtcNow.Ticks.ToString() };
                _client.Send(SignalingMessage.Serialize(ping));
            }
            catch { }
        }

        private async Task TryReconnectLoop()
        {
            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;

            while (_reconnectAttempts < MaxReconnectAttempts && !token.IsCancellationRequested)
            {
                _reconnectAttempts++;
                OnReconnecting?.Invoke(_reconnectAttempts);

                try
                {
                    await Task.Delay(ReconnectDelay, token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested) return;

                try
                {
                    await _client.Reconnect();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Client] Reconnect attempt {_reconnectAttempts} failed: {ex.Message}");
                }

                if (_client != null && _client.IsRunning)
                {
                    return; // ReconnectionHappened will fire and reset state
                }
            }

            if (!token.IsCancellationRequested)
            {
                OnReconnectFailed?.Invoke();
            }
        }

        /// <summary>
        /// Impede novas tentativas de reconexão. Usado quando o host encerra a live de propósito:
        /// sem isso o viewer fica 10 tentativas tentando voltar para uma stream que acabou.
        /// </summary>
        public void SuppressReconnect()
        {
            _intentionalStop = true;
            _reconnectCts?.Cancel();
        }

        /// <summary>Reabilita a reconexão automática (host voltou a transmitir).</summary>
        public void AllowReconnect()
        {
            _intentionalStop = false;
            _reconnectAttempts = 0;
        }

        public void SendMessage(string message)
        {
            if (_client == null || !_client.IsRunning) return;

            if (_encryptionKey != null)
            {
                message = CryptoHelper.EncryptText(message, _encryptionKey);
            }
            _client.Send(message);
        }

        public void SendBinary(byte[] data)
        {
            if (_client == null || !_client.IsRunning) return;

            if (_encryptionKey != null)
            {
                data = CryptoHelper.EncryptBytes(data, _encryptionKey);
            }
            _client.Send(data);
        }

        public void Stop()
        {
            _intentionalStop = true;
            _reconnectCts?.Cancel();
            _pingTimer?.Dispose();
            _client?.Dispose();
        }
    }
}
