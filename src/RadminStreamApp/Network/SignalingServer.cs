using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        // Estado por conexão usado para conter um viewer com a rede ruim. Vive fora de
        // _clients porque é escrito da thread de captura de áudio, ~50x/s.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ViewerLink> _links = new();
        private System.Threading.Timer? _heartbeatTimer;

        /// <summary>
        /// O áudio sai como PCM cru — ~176 KB/s por viewer. O Send do Fleck é um BeginWrite que
        /// nunca bloqueia e nunca recusa: com o link do viewer congestionado, cada pacote virava
        /// uma escrita pendente e o host crescia ~10 MB/min de buffer por viewer travado, sem
        /// teto. Passando do primeiro limite, o áudio daquele viewer começa a ser descartado;
        /// passando do segundo, a conexão dele é fechada e ele reconecta pelo caminho normal.
        /// </summary>
        private const long AudioBacklogSoftLimit = 350_000;   // ~2s de PCM
        private const long AudioBacklogHardLimit = 1_500_000; // ~8s de PCM

        /// <summary>
        /// O viewer manda PING a cada 3s. Silêncio bem além disso significa conexão zumbi: o
        /// keepalive TCP do Fleck (60s) nem chega a rodar enquanto há dados pendentes na fila,
        /// então sem esta varredura o viewer morto ficava minutos em _viewers recebendo áudio.
        /// </summary>
        private static readonly TimeSpan ViewerSilenceTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan HeartbeatSweepInterval = TimeSpan.FromSeconds(5);

        private long _lastCongestionReportTicks;

        private sealed class ViewerLink
        {
            public long PendingBytes;
            public long LastSeenTicks = DateTime.UtcNow.Ticks;
            public int FailureLogged; // 0/1 — só a primeira falha de envio vai para o log
        }

        public bool IsStreaming { get; set; } = false;
        public string RoomPassword { get; set; } = string.Empty;

        /// <summary>
        /// Quando ligado, só IPs da lista de amigos conseguem abrir conexão. É a proteção mais
        /// efetiva do app: sem ela, qualquer máquina da VPN entra numa live sem senha.
        /// </summary>
        public bool RestrictToAllowedIps { get; set; } = true;

        private byte[]? EncryptionKey => string.IsNullOrEmpty(RoomPassword) ? null : CryptoHelper.DeriveKey(RoomPassword);

        public event Action<IWebSocketConnection, string>? OnMessageReceived;
        public event Action<IWebSocketConnection>? OnClientConnected;
        public event Action<IWebSocketConnection>? OnClientDisconnected;

        /// <summary>Conexão recusada por não estar na lista de amigos (IP normalizado).</summary>
        public event Action<string>? OnConnectionRejected;

        /// <summary>
        /// Quantos viewers estão com o envio atrasado agora. Sem isto, o host só via o áudio
        /// "sumindo" para alguém e nada explicava que o problema era a rede do outro lado.
        /// </summary>
        public event Action<int>? OnViewerCongested;

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
                if (!IsPortFree(ipAddress, port))
                {
                    Debug.WriteLine($"[Server] Porta {port} já está em uso.");
                    IsRunning = false;
                    return false;
                }

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

        /// <summary>
        /// Testa a porta com posse exclusiva antes de entregá-la ao Fleck.
        ///
        /// O Fleck não pede exclusividade ao ligar, e sem isso o Windows deixa duas instâncias
        /// do app ligarem na mesma 8080: as duas achavam que tinham subido, o Start devolvia
        /// true para ambas, o aviso de "porta já em uso" nunca aparecia — e as conexões dos
        /// amigos caíam numa das duas sem critério.
        /// </summary>
        private static bool IsPortFree(string ipAddress, int port)
        {
            try
            {
                var address = ipAddress == "0.0.0.0"
                    ? System.Net.IPAddress.Any
                    : System.Net.IPAddress.Parse(ipAddress);

                var probe = new System.Net.Sockets.TcpListener(address, port) { ExclusiveAddressUse = true };
                try
                {
                    probe.Start();
                    return true;
                }
                finally
                {
                    try { probe.Stop(); } catch { }
                }
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
            catch (FormatException)
            {
                // Endereço inválido não é "porta ocupada": deixa o Fleck falhar e reportar.
                return true;
            }
        }

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
                    GetLink(socket);
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
                    _links.TryRemove(socket.ConnectionInfo.Id, out _);
                    OnClientDisconnected?.Invoke(socket);
                };

                socket.OnMessage = message =>
                {
                    Debug.WriteLine($"[Server] Message received from {socket.ConnectionInfo.ClientIpAddress}: {message.Substring(0, Math.Min(message.Length, 50))}...");

                    MarkSeen(socket);

                    var msgObj = SignalingMessage.Deserialize(message);

                    // A descriptografia vem ANTES de qualquer decisão. Ela ficava depois do
                    // bloco que responde AUTH_REQUIRED, e isso travava toda reconexão em sala
                    // com senha: o viewer mantinha a chave da sessão anterior e mandava o
                    // próprio AUTH cifrado, que aqui não era reconhecido como AUTH — host e
                    // viewer ficavam num ping-pong AUTH_REQUIRED ↔ AUTH na velocidade do RTT,
                    // e a live nunca voltava.
                    var plainMessage = message;
                    if (msgObj == null)
                    {
                        var key = EncryptionKey;
                        if (key != null)
                        {
                            var decrypted = CryptoHelper.TryDecryptText(message, key);
                            if (decrypted != null)
                            {
                                plainMessage = decrypted;
                                msgObj = SignalingMessage.Deserialize(decrypted);
                            }
                        }
                    }

                    if (msgObj != null)
                    {
                        if (msgObj.Type == "STATUS_CHECK")
                        {
                            var response = new SignalingMessage { Type = "STATUS_RESPONSE", Data = IsStreaming ? "STREAMING" : "IDLE" };
                            SafeSend(socket, SignalingMessage.Serialize(response));
                            return;
                        }

                        if (msgObj.Type == "PING")
                        {
                            var pong = new SignalingMessage { Type = "PONG", Data = msgObj.Data };
                            SafeSend(socket, SignalingMessage.Serialize(pong));
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

                    if (msgObj != null && msgObj.Type == "CLIENT_CONNECTED") RegisterViewer(socket);

                    OnMessageReceived?.Invoke(socket, plainMessage);
                };

                // Nao ha socket.OnBinary: nenhum viewer manda binario para o host. O audio
                // viaja so na direcao host -> viewer, via BroadcastBinary.
            });

            _heartbeatTimer = new System.Threading.Timer(
                _ => { try { SweepSilentViewers(); } catch { } },
                null, HeartbeatSweepInterval, HeartbeatSweepInterval);

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
            SafeSend(socket, SignalingMessage.Serialize(new SignalingMessage { Type = "AUTH_REQUIRED", Data = challenge }));
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
                SafeSend(socket, SignalingMessage.Serialize(new SignalingMessage { Type = "AUTH_OK" }));
            }
            else
            {
                // Desafio queima a cada tentativa (impede replay do mesmo HMAC), e o novo vai
                // junto do AUTH_FAIL — senão o viewer ficaria sem desafio para tentar de novo.
                var next = CryptoHelper.NewChallenge();
                lock (_clientsLock) { _challenges[socket.ConnectionInfo.Id] = next; }
                SafeSend(socket, SignalingMessage.Serialize(new SignalingMessage { Type = "AUTH_FAIL", Data = next }));
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
                SafeSend(client, key != null ? CryptoHelper.EncryptText(message, key) : message);
            }
        }

        public void BroadcastBinary(byte[] data)
        {
            var clientsCopy = GetBroadcastTargets();
            if (clientsCopy.Count == 0) return;

            var key = EncryptionKey;
            var payload = key != null ? CryptoHelper.EncryptBytes(data, key) : data;

            int congested = 0;
            foreach (var client in clientsCopy)
            {
                var link = GetLink(client);
                var pending = Interlocked.Read(ref link.PendingBytes);

                if (ShouldDropViewer(pending))
                {
                    // Rede do viewer não dá conta há segundos. Fechar é melhor do que continuar
                    // enfileirando: ele volta pelo caminho normal de reconexão, e o host para de
                    // segurar buffer por alguém que não está recebendo nada mesmo.
                    Debug.WriteLine($"[Server] Viewer {client.ConnectionInfo.ClientIpAddress} sem vazão ({pending} bytes pendentes); fechando.");
                    try { client.Close(); } catch { }
                    continue;
                }

                if (ShouldDropAudio(pending))
                {
                    // Descarte é por viewer: quem está bem continua recebendo tudo.
                    congested++;
                    continue;
                }

                SafeSendBinary(client, link, payload);
            }

            ReportCongestion(congested);
        }

        public void BroadcastMessage(string message)
        {
            var clientsCopy = GetBroadcastTargets();
            if (clientsCopy.Count == 0) return;

            var key = EncryptionKey;
            var payload = key != null ? CryptoHelper.EncryptText(message, key) : message;
            foreach (var client in clientsCopy)
            {
                // Sinalização (STREAM_STARTED/STOPPED, SOURCE_CHANGED) nunca é descartada:
                // perder uma dessas deixa o viewer num estado que ele não tem como corrigir.
                SafeSend(client, payload);
            }
        }

        /// <summary>Áudio pendente demais para este viewer — descarta o pacote só para ele.</summary>
        internal static bool ShouldDropAudio(long pendingBytes) => pendingBytes > AudioBacklogSoftLimit;

        /// <summary>Backlog grande a ponto de não haver recuperação — derruba a conexão.</summary>
        internal static bool ShouldDropViewer(long pendingBytes) => pendingBytes > AudioBacklogHardLimit;

        private ViewerLink GetLink(IWebSocketConnection client)
            => _links.GetOrAdd(client.ConnectionInfo.Id, static _ => new ViewerLink());

        private void MarkSeen(IWebSocketConnection client)
            => Interlocked.Exchange(ref GetLink(client).LastSeenTicks, DateTime.UtcNow.Ticks);

        /// <summary>
        /// Avisa a UI, no máximo uma vez por segundo. O descarte é decidido por pacote de áudio
        /// (~50x/s): sem esta trava, cada aviso viraria um InvokeAsync na thread de UI e o
        /// remédio custaria mais caro que a doença.
        /// </summary>
        private void ReportCongestion(int congested)
        {
            if (congested <= 0) return;

            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastCongestionReportTicks);
            if (now - last < TimeSpan.TicksPerSecond) return;
            if (Interlocked.CompareExchange(ref _lastCongestionReportTicks, now, last) != last) return;

            OnViewerCongested?.Invoke(congested);
        }

        /// <summary>
        /// Envio de texto que nunca deixa a exceção escapar.
        ///
        /// O Send do Fleck lança de forma síncrona quando o handshake ainda não terminou, e o
        /// broadcast de áudio roda na thread de captura do WASAPI — uma única exceção dessas
        /// encerrava a CaptureThread do NAudio e a live seguia muda até o fim, sem log nenhum.
        /// Um throw no meio do foreach também cortava os viewers seguintes daquele pacote.
        /// </summary>
        private void SafeSend(IWebSocketConnection client, string payload)
        {
            try
            {
                Observe(client, client.Send(payload));
            }
            catch (Exception ex)
            {
                LogSendFailure(client, ex);
            }
        }

        private void SafeSendBinary(IWebSocketConnection client, ViewerLink link, byte[] payload)
        {
            Interlocked.Add(ref link.PendingBytes, payload.Length);

            try
            {
                var task = client.Send(payload);
                if (task == null)
                {
                    Interlocked.Add(ref link.PendingBytes, -payload.Length);
                    return;
                }

                task.ContinueWith(t =>
                {
                    Interlocked.Add(ref link.PendingBytes, -payload.Length);
                    if (t.Exception != null) LogSendFailure(client, t.Exception);
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref link.PendingBytes, -payload.Length);
                LogSendFailure(client, ex);
            }
        }

        /// <summary>
        /// Consome a Task do Fleck. Sem isto, uma falha de envio virava UnobservedTaskException
        /// e só aparecia no log global na próxima coleta de lixo, já sem contexto nenhum.
        /// </summary>
        private void Observe(IWebSocketConnection client, System.Threading.Tasks.Task? task)
        {
            if (task == null) return;

            task.ContinueWith(t =>
            {
                if (t.Exception != null) LogSendFailure(client, t.Exception);
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private void LogSendFailure(IWebSocketConnection client, Exception ex)
        {
            var link = GetLink(client);
            if (Interlocked.Exchange(ref link.FailureLogged, 1) != 0) return;

            WriteLog($"Falha ao enviar para {NormalizeIp(client.ConnectionInfo.ClientIpAddress)}: {ex.GetBaseException().Message}");
        }

        private static void WriteLog(string content)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadminStreamApp");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"),
                    $"{DateTime.Now}: [SignalingServer] {content}\n");
            }
            catch { }
        }

        /// <summary>
        /// Fecha conexões que pararam de dar sinal de vida. O viewer manda PING a cada 3s, então
        /// silêncio prolongado só acontece quando ele já não está mais lá.
        /// </summary>
        private void SweepSilentViewers()
        {
            List<IWebSocketConnection> candidates;
            lock (_clientsLock)
            {
                candidates = _clients.ToList();
            }

            var now = DateTime.UtcNow;
            foreach (var client in candidates)
            {
                if (!_links.TryGetValue(client.ConnectionInfo.Id, out var link)) continue;

                var silence = now - new DateTime(Interlocked.Read(ref link.LastSeenTicks), DateTimeKind.Utc);
                if (silence <= ViewerSilenceTimeout) continue;

                Debug.WriteLine($"[Server] Viewer {client.ConnectionInfo.ClientIpAddress} calado há {silence.TotalSeconds:F0}s; fechando.");
                try { client.Close(); } catch { }
            }
        }

        /// <summary>
        /// Ha alguem para receber uma difusao agora? Consultado pelo caminho de audio antes de
        /// empacotar o quadro: sem isto, montavamos ~50 pacotes por segundo para descartar
        /// todos no fim da linha enquanto a sala esta vazia.
        /// </summary>
        public bool HasBroadcastTargets
        {
            get
            {
                lock (_clientsLock)
                {
                    if (_viewers.Count == 0) return false;
                    return CountBroadcastTargetsLocked() > 0;
                }
            }
        }

        private int CountBroadcastTargetsLocked()
        {
            if (string.IsNullOrEmpty(RoomPassword)) return _viewers.Count;

            int count = 0;
            foreach (var id in _viewers)
            {
                if (_authenticatedClients.Contains(id)) count++;
            }
            return count;
        }

        private static readonly List<IWebSocketConnection> NoTargets = new List<IWebSocketConnection>();

        private List<IWebSocketConnection> GetBroadcastTargets()
        {
            lock (_clientsLock)
            {
                // Sala vazia e o estado normal enquanto ninguem entrou: sair antes do LINQ
                // evita alocar uma lista por quadro so para descobrir que ela esta vazia.
                if (_viewers.Count == 0) return NoTargets;

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
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;

            List<IWebSocketConnection> clientsCopy;
            lock (_clientsLock)
            {
                clientsCopy = _clients.ToList();
                _clients.Clear();
                _viewers.Clear();
                _authenticatedClients.Clear();
                _challenges.Clear();
            }
            _links.Clear();

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
