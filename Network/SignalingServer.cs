using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Fleck;

namespace RadminStreamApp
{
    public class SignalingServer
    {
        private WebSocketServer _server;
        private List<IWebSocketConnection> _clients = new List<IWebSocketConnection>();
        private readonly object _clientsLock = new object();
        private HashSet<Guid> _authenticatedClients = new HashSet<Guid>();

        // Só entra aqui quem mandou CLIENT_CONNECTED, ou seja, quem realmente veio assistir.
        // Conexões passageiras (o teste de status dos amigos) ficam de fora do broadcast
        // e da contagem — senão recebem áudio binário no lugar do STATUS_RESPONSE.
        private HashSet<Guid> _viewers = new HashSet<Guid>();

        public bool IsStreaming { get; set; } = false;
        public string RoomPassword { get; set; } = string.Empty;

        private byte[] EncryptionKey => string.IsNullOrEmpty(RoomPassword) ? null : CryptoHelper.DeriveKey(RoomPassword);

        public event Action<IWebSocketConnection, string> OnMessageReceived;
        public event Action<IWebSocketConnection, byte[]> OnBinaryReceived;
        public event Action<IWebSocketConnection> OnClientConnected;
        public event Action<IWebSocketConnection> OnClientDisconnected;

        public int ConnectedClientsCount
        {
            get
            {
                lock (_clientsLock)
                {
                    return _viewers.Count;
                }
            }
        }

        /// <summary>IPs dos viewers conectados, normalizados (IPv4 puro quando possível).</summary>
        public IReadOnlyList<string> ConnectedClientIps
        {
            get
            {
                lock (_clientsLock)
                {
                    return _clients
                        .Where(c => _viewers.Contains(c.ConnectionInfo.Id))
                        .Select(c => NormalizeIp(c.ConnectionInfo.ClientIpAddress))
                        .ToList()
                        .AsReadOnly();
                }
            }
        }

        /// <summary>
        /// O Fleck entrega IPv4 mapeado em IPv6 ("::ffff:26.10.0.5") e loopback como "::1".
        /// Sem normalizar, o IP nunca casa com o que o usuário salvou na lista de amigos.
        /// </summary>
        public static string NormalizeIp(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return string.Empty;

            ip = ip.Trim();
            if (ip == "::1") return "127.0.0.1";

            const string v4MappedPrefix = "::ffff:";
            if (ip.StartsWith(v4MappedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = ip.Substring(v4MappedPrefix.Length);
                if (candidate.Contains('.')) return candidate;
            }

            return ip;
        }

        public IReadOnlyList<IWebSocketConnection> Clients
        {
            get
            {
                lock (_clientsLock)
                {
                    return _clients.ToList().AsReadOnly();
                }
            }
        }

        /// <summary>
        /// Sobe o servidor de sinalização. Retorna false quando a porta já está em uso
        /// (outra instância do app aberta) — sem isso a falha passava despercebida e
        /// ninguém conseguia se conectar.
        /// </summary>
        public bool Start(string ipAddress = "0.0.0.0", int port = 8080)
        {
            try
            {
                StartInternal(ipAddress, port);
                IsRunning = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Server] Falha ao iniciar em {ipAddress}:{port} - {ex.Message}");
                IsRunning = false;
                try { _server?.Dispose(); } catch { }
                _server = null;
                return false;
            }
        }

        public bool IsRunning { get; private set; }

        private void StartInternal(string ipAddress, int port)
        {
            _server = new WebSocketServer($"ws://{ipAddress}:{port}");
            _server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    Debug.WriteLine($"[Server] Client connected: {socket.ConnectionInfo.ClientIpAddress}");
                    lock (_clientsLock)
                    {
                        _clients.Add(socket);
                    }
                };

                socket.OnClose = () =>
                {
                    Debug.WriteLine($"[Server] Client disconnected: {socket.ConnectionInfo.ClientIpAddress}");
                    lock (_clientsLock)
                    {
                        _clients.Remove(socket);
                        _authenticatedClients.Remove(socket.ConnectionInfo.Id);
                        _viewers.Remove(socket.ConnectionInfo.Id);
                    }
                    OnClientDisconnected?.Invoke(socket);
                };

                socket.OnMessage = message =>
                {
                    Debug.WriteLine($"[Server] Message received from {socket.ConnectionInfo.ClientIpAddress}: {message.Substring(0, Math.Min(message.Length, 50))}...");

                    var msgObj = SignalingMessage.Deserialize(message);
                    if (msgObj != null)
                    {
                        if (msgObj.Type == "STATUS_CHECK")
                        {
                            var response = new SignalingMessage { Type = "STATUS_RESPONSE", Data = IsStreaming ? "STREAMING" : "IDLE" };
                            socket.Send(SignalingMessage.Serialize(response));
                            return;
                        }

                        if (msgObj.Type == "PING")
                        {
                            var pong = new SignalingMessage { Type = "PONG", Data = msgObj.Data };
                            socket.Send(SignalingMessage.Serialize(pong));
                            return;
                        }

                        if (!string.IsNullOrEmpty(RoomPassword) && msgObj.Type == "AUTH")
                        {
                            if (msgObj.Data == RoomPassword)
                            {
                                lock (_clientsLock) { _authenticatedClients.Add(socket.ConnectionInfo.Id); }
                                socket.Send("AUTH_OK");
                            }
                            else
                            {
                                socket.Send("AUTH_FAIL");
                            }
                            return;
                        }
                    }

                    if (!string.IsNullOrEmpty(RoomPassword))
                    {
                        bool isAuth;
                        lock (_clientsLock) { isAuth = _authenticatedClients.Contains(socket.ConnectionInfo.Id); }
                        if (!isAuth)
                        {
                            socket.Send("AUTH_REQUIRED");
                            return;
                        }
                    }

                    var plainMessage = message;
                    var key = EncryptionKey;
                    if (key != null)
                    {
                        var decrypted = CryptoHelper.TryDecryptText(message, key);
                        if (decrypted != null) plainMessage = decrypted;
                    }

                    var effectiveMsg = ReferenceEquals(plainMessage, message)
                        ? msgObj
                        : SignalingMessage.Deserialize(plainMessage);

                    if (effectiveMsg != null)
                    {
                        if (effectiveMsg.Type == "STATUS_CHECK")
                        {
                            var innerResponse = new SignalingMessage { Type = "STATUS_RESPONSE", Data = IsStreaming ? "STREAMING" : "IDLE" };
                            socket.Send(SignalingMessage.Serialize(innerResponse));
                            return;
                        }

                        if (effectiveMsg.Type == "CLIENT_CONNECTED") RegisterViewer(socket);
                    }

                    OnMessageReceived?.Invoke(socket, plainMessage);
                };

                socket.OnBinary = bytes =>
                {
                    var plainBytes = bytes;
                    var key = EncryptionKey;
                    if (key != null)
                    {
                        bool isAuth;
                        lock (_clientsLock) { isAuth = _authenticatedClients.Contains(socket.ConnectionInfo.Id); }
                        if (isAuth)
                        {
                            var decrypted = CryptoHelper.TryDecryptBytes(bytes, key);
                            if (decrypted != null) plainBytes = decrypted;
                        }
                    }
                    OnBinaryReceived?.Invoke(socket, plainBytes);
                };
            });

            Debug.WriteLine($"[Server] Started on ws://{ipAddress}:{port}");
        }

        /// <summary>Marca a conexão como viewer de verdade e avisa a UI uma única vez.</summary>
        private void RegisterViewer(IWebSocketConnection socket)
        {
            bool isNew;
            lock (_clientsLock)
            {
                isNew = _viewers.Add(socket.ConnectionInfo.Id);
            }

            if (isNew) OnClientConnected?.Invoke(socket);
        }

        public void SendMessage(IWebSocketConnection client, string message)
        {
            client.Send(message);
        }

        public void SendToClient(string clientId, string message)
        {
            IWebSocketConnection client;
            lock (_clientsLock)
            {
                client = _clients.FirstOrDefault(c => c.ConnectionInfo.Id.ToString() == clientId);
            }
            if (client != null)
            {
                var key = EncryptionKey;
                client.Send(key != null ? CryptoHelper.EncryptText(message, key) : message);
            }
        }

        public void BroadcastBinary(byte[] data)
        {
            List<IWebSocketConnection> clientsCopy;
            lock (_clientsLock)
            {
                clientsCopy = _clients.Where(c => _viewers.Contains(c.ConnectionInfo.Id)).ToList();
                if (!string.IsNullOrEmpty(RoomPassword))
                    clientsCopy = clientsCopy.Where(c => _authenticatedClients.Contains(c.ConnectionInfo.Id)).ToList();
            }
            var key = EncryptionKey;
            var payload = key != null ? CryptoHelper.EncryptBytes(data, key) : data;
            foreach (var client in clientsCopy)
            {
                client.Send(payload);
            }
        }

        public void BroadcastMessage(string message)
        {
            List<IWebSocketConnection> clientsCopy;
            lock (_clientsLock)
            {
                clientsCopy = _clients.Where(c => _viewers.Contains(c.ConnectionInfo.Id)).ToList();
                if (!string.IsNullOrEmpty(RoomPassword))
                    clientsCopy = clientsCopy.Where(c => _authenticatedClients.Contains(c.ConnectionInfo.Id)).ToList();
            }
            var key = EncryptionKey;
            var payload = key != null ? CryptoHelper.EncryptText(message, key) : message;
            foreach (var client in clientsCopy)
            {
                client.Send(payload);
            }
        }

        public void Stop()
        {
            List<IWebSocketConnection> clientsCopy;
            lock (_clientsLock)
            {
                clientsCopy = _clients.ToList();
            }
            foreach (var client in clientsCopy)
            {
                client.Close();
            }
            _server?.Dispose();
        }
    }
}
