using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Fleck;

namespace RadminStreamApp
{
    public class SignalingServer
    {
        private WebSocketServer? _server;
        private readonly List<IWebSocketConnection> _clients = new List<IWebSocketConnection>();
        private readonly object _clientsLock = new object();
        private readonly HashSet<Guid> _authenticatedClients = new HashSet<Guid>();

        // Desafio pendente por conexão. A senha nunca vai no fio: o viewer prova que a
        // conhece devolvendo o HMAC deste nonce.
        private readonly Dictionary<Guid, string> _challenges = new Dictionary<Guid, string>();

        // Só entra aqui quem mandou CLIENT_CONNECTED, ou seja, quem realmente veio assistir.
        // Conexões passageiras (o teste de status dos amigos) ficam de fora do broadcast
        // e da contagem — senão recebem áudio binário no lugar do STATUS_RESPONSE.
        private readonly HashSet<Guid> _viewers = new HashSet<Guid>();

        // Lista de amigos normalizada. Com a restrição ligada, ninguém de fora dela abre conexão.
        private readonly HashSet<string> _allowedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IsStreaming { get; set; } = false;
        public string RoomPassword { get; set; } = string.Empty;

        /// <summary>
        /// Quando ligado, só IPs da lista de amigos conseguem abrir conexão. É a proteção mais
        /// efetiva do app: sem ela, qualquer máquina da VPN entra numa live sem senha.
        /// </summary>
        public bool RestrictToAllowedIps { get; set; } = true;

        private byte[]? EncryptionKey => string.IsNullOrEmpty(RoomPassword) ? null : CryptoHelper.DeriveKey(RoomPassword);

        public event Action<IWebSocketConnection, string>? OnMessageReceived;
        public event Action<IWebSocketConnection, byte[]>? OnBinaryReceived;
        public event Action<IWebSocketConnection>? OnClientConnected;
        public event Action<IWebSocketConnection>? OnClientDisconnected;

        /// <summary>Conexão recusada por não estar na lista de amigos (IP normalizado).</summary>
        public event Action<string>? OnConnectionRejected;

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

        /// <summary>Substitui a lista de IPs autorizados (chamada quando os amigos mudam).</summary>
        public void SetAllowedIps(IEnumerable<string> ips)
        {
            lock (_clientsLock)
            {
                _allowedIps.Clear();
                foreach (var ip in ips ?? Enumerable.Empty<string>())
                {
                    var normalized = NormalizeIp(ip);
                    if (!string.IsNullOrEmpty(normalized)) _allowedIps.Add(normalized);
                }

                // A própria máquina sempre pode conectar — é assim que o app testa o
                // próprio status e como o usuário assiste a si mesmo durante testes.
                _allowedIps.Add("127.0.0.1");
            }
        }

        private bool IsIpAllowed(string rawIp)
        {
            if (!RestrictToAllowedIps) return true;

            var ip = NormalizeIp(rawIp);

            // A própria máquina sempre passa, mesmo antes de SetAllowedIps ser chamado: é
            // assim que o app consulta o próprio status, e evita que uma ordem de
            // inicialização diferente tranque o usuário para fora de tudo.
            if (ip == "127.0.0.1") return true;

            lock (_clientsLock)
            {
                return _allowedIps.Contains(ip);
            }
        }

        /// <summary>
        /// O Fleck entrega IPv4 mapeado em IPv6 ("::ffff:26.10.0.5") e loopback como "::1".
        /// Sem normalizar, o IP nunca casa com o que o usuário salvou na lista de amigos.
        /// </summary>
        public static string NormalizeIp(string? ip)
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
                    var rawIp = socket.ConnectionInfo.ClientIpAddress;

                    if (!IsIpAllowed(rawIp))
                    {
                        var normalized = NormalizeIp(rawIp);
                        Debug.WriteLine($"[Server] Conexão recusada (fora da lista de amigos): {normalized}");
                        OnConnectionRejected?.Invoke(normalized);
                        try { socket.Close(); } catch { }
                        return;
                    }

                    Debug.WriteLine($"[Server] Client connected: {rawIp}");
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
                        _challenges.Remove(socket.ConnectionInfo.Id);
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
                            HandleAuth(socket, msgObj.Data);
                            return;
                        }
                    }

                    if (!string.IsNullOrEmpty(RoomPassword))
                    {
                        bool isAuth;
                        lock (_clientsLock) { isAuth = _authenticatedClients.Contains(socket.ConnectionInfo.Id); }
                        if (!isAuth)
                        {
                            SendChallenge(socket);
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
                        if (!isAuth) return;

                        var decrypted = CryptoHelper.TryDecryptBytes(bytes, key);
                        // Com AES-GCM, null aqui significa payload adulterado: descarta.
                        if (decrypted == null) return;
                        plainBytes = decrypted;
                    }
                    OnBinaryReceived?.Invoke(socket, plainBytes);
                };
            });

            Debug.WriteLine($"[Server] Started on ws://{ipAddress}:{port}");
        }

        /// <summary>
        /// Manda AUTH_REQUIRED com o desafio pendente desta conexão, criando um se ainda não
        /// houver. Reaproveitar é essencial: o viewer dispara várias mensagens antes de
        /// autenticar (CLIENT_CONNECTED e cada candidato ICE), e cada uma passa por aqui.
        /// Gerando um desafio novo a cada vez, a resposta do viewer chegava referente a um
        /// desafio já substituído e virava "senha incorreta" sem motivo.
        /// </summary>
        private void SendChallenge(IWebSocketConnection socket, bool forceNew = false)
        {
            string challenge;
            lock (_clientsLock)
            {
                if (forceNew || !_challenges.TryGetValue(socket.ConnectionInfo.Id, out challenge!) || string.IsNullOrEmpty(challenge))
                {
                    challenge = CryptoHelper.NewChallenge();
                    _challenges[socket.ConnectionInfo.Id] = challenge;
                }
            }
            socket.Send(SignalingMessage.Serialize(new SignalingMessage { Type = "AUTH_REQUIRED", Data = challenge }));
        }

        /// <summary>Confere o HMAC do desafio contra o esperado, em tempo constante.</summary>
        private void HandleAuth(IWebSocketConnection socket, string? proof)
        {
            string? challenge;
            lock (_clientsLock)
            {
                _challenges.TryGetValue(socket.ConnectionInfo.Id, out challenge);
            }

            if (string.IsNullOrEmpty(challenge))
            {
                // Cliente tentou autenticar sem ter recebido desafio: manda um e espera de novo.
                SendChallenge(socket);
                return;
            }

            var key = CryptoHelper.DeriveKey(RoomPassword);
            var expected = CryptoHelper.ComputeAuthProof(key, challenge);

            if (CryptoHelper.FixedTimeEquals(expected, proof))
            {
                lock (_clientsLock)
                {
                    _authenticatedClients.Add(socket.ConnectionInfo.Id);
                    _challenges.Remove(socket.ConnectionInfo.Id);
                }
                socket.Send(SignalingMessage.Serialize(new SignalingMessage { Type = "AUTH_OK" }));
            }
            else
            {
                // Desafio queima a cada tentativa (impede replay do mesmo HMAC), e o novo vai
                // junto do AUTH_FAIL — senão o viewer ficaria sem desafio para tentar de novo.
                var next = CryptoHelper.NewChallenge();
                lock (_clientsLock) { _challenges[socket.ConnectionInfo.Id] = next; }
                socket.Send(SignalingMessage.Serialize(new SignalingMessage { Type = "AUTH_FAIL", Data = next }));
            }
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
            IWebSocketConnection? client;
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
            var clientsCopy = GetBroadcastTargets();
            if (clientsCopy.Count == 0) return;

            var key = EncryptionKey;
            var payload = key != null ? CryptoHelper.EncryptBytes(data, key) : data;
            foreach (var client in clientsCopy)
            {
                client.Send(payload);
            }
        }

        public void BroadcastMessage(string message)
        {
            var clientsCopy = GetBroadcastTargets();
            if (clientsCopy.Count == 0) return;

            var key = EncryptionKey;
            var payload = key != null ? CryptoHelper.EncryptText(message, key) : message;
            foreach (var client in clientsCopy)
            {
                client.Send(payload);
            }
        }

        private List<IWebSocketConnection> GetBroadcastTargets()
        {
            lock (_clientsLock)
            {
                var targets = _clients.Where(c => _viewers.Contains(c.ConnectionInfo.Id));
                if (!string.IsNullOrEmpty(RoomPassword))
                    targets = targets.Where(c => _authenticatedClients.Contains(c.ConnectionInfo.Id));
                return targets.ToList();
            }
        }

        /// <summary>
        /// Derruba tudo e zera o estado. Sem limpar as listas, um restart na mesma sessão
        /// herdava viewers fantasmas na contagem e clientes ainda marcados como autenticados.
        /// </summary>
        public void Stop()
        {
            List<IWebSocketConnection> clientsCopy;
            lock (_clientsLock)
            {
                clientsCopy = _clients.ToList();
                _clients.Clear();
                _viewers.Clear();
                _authenticatedClients.Clear();
                _challenges.Clear();
            }

            foreach (var client in clientsCopy)
            {
                try { client.Close(); } catch { }
            }

            try { _server?.Dispose(); } catch { }
            _server = null;
            IsRunning = false;
            IsStreaming = false;
        }
    }
}
