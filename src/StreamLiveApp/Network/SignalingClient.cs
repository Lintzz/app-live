using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client;

namespace StreamLiveApp
{
    public class SignalingClient
    {
        internal const int MaxReconnectAttempts = 8;

        /// <summary>Teto da espera entre tentativas. Passar disso só faz a live demorar a voltar.</summary>
        internal const int MaxBackoffSeconds = 15;

        /// <summary>Variação aplicada à espera (±20%), para dois viewers não voltarem no mesmo instante.</summary>
        internal const double JitterFraction = 0.2;

        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(3);

        // Um socket TCP meio-aberto (Wi-Fi caindo, VPN Radmin oscilando) continua "conectado"
        // para o Windows por minutos. O PING já saía a cada 3s, mas ninguém conferia se o PONG
        // voltava — então a live ficava congelada sem nada na tela até o TCP desistir sozinho.
        // Três pings sem qualquer resposta é o bastante para tratar como queda.
        private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(9);

        private WebsocketClient? _client;
        private bool _intentionalStop = false;
        private int _reconnectAttempts = 0;
        private CancellationTokenSource? _reconnectCts;
        private System.Threading.Timer? _pingTimer;
        private byte[]? _encryptionKey;

        // Tick do último sinal de vida recebido (qualquer mensagem serve, não só o PONG).
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;

        // Impede que o watchdog e o DisconnectionHappened subam dois loops de reconexão.
        private int _reconnecting;

        // A sessão chegou a ficar de pé antes desta queda? Se sim, a contagem de tentativas
        // recomeça do zero: sem isso, a segunda queda de uma sessão longa já nascia perto do
        // teto e desistia quase de imediato.
        private bool _hadHealthySession;

        public event Action<string> OnMessageReceived = delegate {};
        public event Action<byte[]> OnBinaryReceived = delegate {};
        public event Action<bool> OnConnected = delegate {}; // bool = isReconnect
        public event Action OnDisconnected = delegate {};
        public event Action<int, int> OnReconnecting = delegate {}; // tentativa atual, total
        public event Action OnReconnectFailed = delegate {};
        public event Action<int> OnLatencyUpdated = delegate {}; // milliseconds

        public void EnableEncryption(string password)
        {
            _encryptionKey = CryptoHelper.DeriveKey(password);
        }

        /// <summary>
        /// Esquece a chave da sala. A criptografia vale para <em>aquele</em> socket autenticado:
        /// depois de uma queda, o host não reconhece mais este cliente e volta a exigir o AUTH,
        /// que precisa sair em texto claro.
        /// </summary>
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
                MarkActivity();

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
                _hadHealthySession = true;
                Interlocked.Exchange(ref _reconnecting, 0);
                MarkActivity();
                OnConnected?.Invoke(info.Type != ReconnectionType.Initial);
            });

            _client.DisconnectionHappened.Subscribe(info =>
            {
                Debug.WriteLine($"[Client] Disconnected: {info.Type}");

                // A chave só vale enquanto o socket autenticado existir.
                DisableEncryption();
                OnDisconnected?.Invoke();

                if (_intentionalStop || info.Type == DisconnectionType.ByUser)
                {
                    return;
                }

                BeginReconnect();
            });

            _pingTimer = new System.Threading.Timer(PingTick, null, PingInterval, PingInterval);

            MarkActivity();
            await _client.Start();
        }

        private void MarkActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        private TimeSpan SilenceElapsed
            => DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

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

        private void PingTick(object? state)
        {
            try
            {
                var client = _client;
                if (client == null || !client.IsRunning) return;
                if (_intentionalStop || Volatile.Read(ref _reconnecting) != 0) return;

                if (SilenceElapsed > SilenceTimeout)
                {
                    // Zera o relógio antes de sair: senão o watchdog dispara de novo a cada
                    // tick enquanto a reconexão ainda está em andamento.
                    MarkActivity();
                    Debug.WriteLine("[Client] Sem resposta do host; forçando reconexão.");
                    BeginReconnect();
                    return;
                }

                var ping = new SignalingMessage { Type = "PING", Data = DateTime.UtcNow.Ticks.ToString() };
                client.Send(SignalingMessage.Serialize(ping));
            }
            catch { }
        }

        private void BeginReconnect()
        {
            if (_intentionalStop) return;
            if (Interlocked.Exchange(ref _reconnecting, 1) != 0) return;

            if (_hadHealthySession)
            {
                _hadHealthySession = false;
                _reconnectAttempts = 0;
            }

            _ = TryReconnectLoop();
        }

        /// <summary>
        /// Espera antes da tentativa <paramref name="attempt"/> (contada a partir de 1). A
        /// primeira é imediata — a maioria das quedas na VPN dura menos de um segundo, e o
        /// delay fixo de 5s que havia aqui antes penalizava justamente o caso comum.
        /// </summary>
        internal static TimeSpan ComputeBackoffBase(int attempt)
        {
            if (attempt <= 1) return TimeSpan.Zero;

            var shift = Math.Min(attempt - 2, 20);
            var seconds = Math.Min(MaxBackoffSeconds, 1 << shift);
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>Espalha a espera em ±<see cref="JitterFraction"/>. <paramref name="roll"/> vem de [0,1).</summary>
        internal static TimeSpan ApplyJitter(TimeSpan baseDelay, double roll)
        {
            if (baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;

            var factor = 1.0 - JitterFraction + (2 * JitterFraction * Math.Clamp(roll, 0.0, 1.0));
            return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
        }

        private async Task TryReconnectLoop()
        {
            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;

            try
            {
                while (_reconnectAttempts < MaxReconnectAttempts && !token.IsCancellationRequested)
                {
                    _reconnectAttempts++;
                    OnReconnecting?.Invoke(_reconnectAttempts, MaxReconnectAttempts);

                    var delay = ApplyJitter(ComputeBackoffBase(_reconnectAttempts), Random.Shared.NextDouble());
                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, token);
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                    }

                    if (token.IsCancellationRequested) return;

                    try
                    {
                        if (_client != null) await _client.Reconnect();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Client] Reconnect attempt {_reconnectAttempts} failed: {ex.Message}");
                    }

                    if (_client != null && _client.IsRunning)
                    {
                        MarkActivity();
                        return; // ReconnectionHappened will fire and reset state
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    OnReconnectFailed?.Invoke();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
        }

        /// <summary>
        /// Impede novas tentativas de reconexão. Usado quando o host encerra a live de propósito:
        /// sem isso o viewer fica tentando voltar para uma stream que acabou.
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
            MarkActivity();
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

        /// <summary>
        /// Envia sem criptografar. Só o AUTH usa este caminho: ele é a mensagem que <em>abre</em>
        /// a sessão cifrada, então precisa ser legível para o host — que ainda não sabe quem é
        /// este socket. Mandá-lo cifrado (o que acontecia depois de uma reconexão, porque a chave
        /// sobrevivia à queda) prendia viewer e host num ping-pong AUTH_REQUIRED ↔ AUTH sem fim.
        /// </summary>
        public void SendPlain(string message)
        {
            if (_client == null || !_client.IsRunning) return;
            _client.Send(message);
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
