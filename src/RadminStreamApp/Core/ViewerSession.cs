using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadminStreamApp.Models;
using SIPSorcery.Net;

namespace RadminStreamApp
{
    /// <summary>
    /// Uma live sendo assistida. Todo o caminho de viewer passa por aqui — tanto quando
    /// há uma única stream aberta quanto quando há várias lado a lado.
    /// </summary>
    public class ViewerSession : INotifyPropertyChanged, IDisposable
    {
        /// <summary>
        /// Quanto tempo sem quadro decodificado antes de admitir que algo está errado. Curto o
        /// bastante para o usuário não achar que a imagem parada é a tela do amigo, e longo o
        /// bastante para não piscar a cada engasgo de rede.
        /// </summary>
        internal static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(1);

        /// <summary>Tentativas de refazer a negociação WebRTC antes de desistir para o botão manual.</summary>
        private const int MaxMediaRestarts = 3;
        private static readonly TimeSpan MediaRestartDelay = TimeSpan.FromSeconds(2);

        public Friend Friend { get; }
        public string Ip => Friend.Ip;
        public string FriendName => Friend.DisplayName;

        private SignalingClient? _client;
        private StreamManager? _streamManager;
        private string _password = string.Empty;
        private string _authChallenge = string.Empty;

        // O host responde AUTH_REQUIRED para CADA mensagem enviada antes de autenticar — o
        // CLIENT_CONNECTED e um por candidato ICE. Sem esta trava, cada resposta abria um
        // modal de senha e o usuário via a mesma caixa quatro vezes seguidas.
        private bool _passwordPromptOpen;
        private bool _streamEnded;
        private bool _disposed;

        // Vigia de vídeo parado. Sem ele, uma queda de rede só aparecia quando o TCP do Windows
        // finalmente desistia — minutos com o último quadro congelado e nada escrito na tela.
        private System.Threading.Timer? _watchdog;
        private long _lastFrameTicks = DateTime.UtcNow.Ticks;

        private int _mediaRestarts;
        private int _restartingMedia;

        /// <summary>Disparado quando o host exige senha. bool = a senha anterior foi recusada.</summary>
        public event Action<ViewerSession, bool> PasswordRequested = delegate {};

        public event PropertyChangedEventHandler? PropertyChanged;

        private WriteableBitmap? _videoBitmap;
        public WriteableBitmap? VideoBitmap { get => _videoBitmap; private set => SetProperty(ref _videoBitmap, value); }

        private string _statusText = "Conectando...";
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

        private ConnectionHealth _health = ConnectionHealth.Conectando;
        /// <summary>
        /// Estado da conexão desta live. É a única fonte para a UI decidir o que mostrar — antes
        /// tudo passava por <see cref="StatusText"/>, que recebia desde progresso de SDP até o
        /// enum cru do SIPSorcery, e por isso não dava para reagir a nada.
        /// </summary>
        public ConnectionHealth Health
        {
            get => _health;
            private set
            {
                if (!SetProperty(ref _health, value)) return;

                RaisePropertyChanged(nameof(ShowOverlay));
                RaisePropertyChanged(nameof(IsVideoStale));
                RaisePropertyChanged(nameof(CanRetry));
                RaisePropertyChanged(nameof(IsBusy));
                RaisePropertyChanged(nameof(IsTroubled));
            }
        }

        /// <summary>Há algo a dizer ao usuário: qualquer estado que não seja imagem fluindo.</summary>
        public bool ShowOverlay => Health != ConnectionHealth.AoVivo;

        /// <summary>
        /// O quadro na tela é antigo. A imagem continua visível (dá contexto do que estava
        /// acontecendo), mas apagada, para não ser confundida com a tela parada do amigo.
        /// </summary>
        public bool IsVideoStale => Health is ConnectionHealth.Instavel
                                             or ConnectionHealth.Reconectando
                                             or ConnectionHealth.Perdida;

        /// <summary>Acabaram as tentativas automáticas; só resta o botão.</summary>
        public bool CanRetry => Health == ConnectionHealth.Perdida;

        /// <summary>
        /// A conexão está em apuros, mas ainda há esperança. Destaca a célula na grade: com
        /// várias lives abertas, é preciso enxergar de longe qual delas é a problemática.
        /// </summary>
        public bool IsTroubled => Health is ConnectionHealth.Instavel or ConnectionHealth.Reconectando;

        /// <summary>
        /// Ainda há algo em andamento. Separa o giro do spinner dos estados parados
        /// (perdida, encerrada), onde animar só sugeriria um progresso que não existe.
        /// </summary>
        public bool IsBusy => Health is ConnectionHealth.Conectando
                                      or ConnectionHealth.Instavel
                                      or ConnectionHealth.Reconectando;

        private bool _isConnected;
        public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }

        private int _fps;
        public int Fps { get => _fps; private set { if (SetProperty(ref _fps, value)) RaisePropertyChanged(nameof(StatsText)); } }

        private int _latencyMs;
        public int LatencyMs
        {
            get => _latencyMs;
            private set
            {
                if (SetProperty(ref _latencyMs, value))
                {
                    RaisePropertyChanged(nameof(StatsText));
                    Friend.SessionInfo = $"{value}ms";
                }
            }
        }

        private double _volume = 100;
        public double Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value)) ApplyVolume();
            }
        }

        private bool _isActive;
        /// <summary>Sessão em foco: recebe o PiP, o controle de qualidade e o destaque na borda.</summary>
        public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (SetProperty(ref _isMuted, value))
                {
                    RaisePropertyChanged(nameof(MuteIcon));
                    ApplyVolume();
                }
            }
        }

        /// <summary>Ícone Segoe MDL2: alto-falante normal ou mudo.</summary>
        public string MuteIcon => IsMuted ? "" : "";

        private int _audioFps;
        /// <summary>Quadros de audio decodificados por segundo. Zero com video rodando aponta
        /// o problema para o audio, e nao para a conexao.</summary>
        public int AudioFps { get => _audioFps; private set { if (SetProperty(ref _audioFps, value)) RaisePropertyChanged(nameof(StatsText)); } }

        private string _diagnostics = string.Empty;
        /// <summary>
        /// Detalhe técnico do caminho WebRTC. Vive na sobreposição de estatísticas, e não no
        /// meio do vídeo: "WebRTC: failed" e "Decode Error: ..." piscavam por cima da imagem
        /// sem dizer nada de útil a quem só quer assistir.
        /// </summary>
        public string Diagnostics
        {
            get => _diagnostics;
            private set { if (SetProperty(ref _diagnostics, value)) RaisePropertyChanged(nameof(StatsText)); }
        }

        public string StatsText
        {
            get
            {
                var stats = $"📥 {Fps}fps | {LatencyMs}ms | 🔊 {AudioFps}/s";
                return string.IsNullOrEmpty(Diagnostics) ? stats : $"{stats} | {Diagnostics}";
            }
        }

        public ViewerSession(Friend friend)
        {
            Friend = friend ?? throw new ArgumentNullException(nameof(friend));
        }

        // ───────────────────────────── Estado da conexão ─────────────────────────────

        private void SetHealth(ConnectionHealth health, string? detail = null)
        {
            Health = health;
            StatusText = detail ?? DefaultStatusText(health);
        }

        private string DefaultStatusText(ConnectionHealth health) => health switch
        {
            ConnectionHealth.Conectando => "Conectando...",
            ConnectionHealth.AoVivo => string.Empty,
            ConnectionHealth.Instavel => "Sinal instável...",
            ConnectionHealth.Reconectando => "Reconectando...",
            ConnectionHealth.Perdida => "Conexão perdida",
            ConnectionHealth.Encerrada => "Transmissão encerrada",
            _ => string.Empty
        };

        /// <summary>
        /// Decide o estado a partir de quanto tempo faz que o último quadro foi decodificado.
        /// Só mexe entre <see cref="ConnectionHealth.AoVivo"/> e <see cref="ConnectionHealth.Instavel"/>:
        /// os demais estados são conclusões de outra coisa (o socket caiu, o host encerrou) e
        /// não podem ser sobrescritas por falta de imagem.
        /// </summary>
        internal static ConnectionHealth DecideHealth(ConnectionHealth atual, TimeSpan idadeDoUltimoFrame)
        {
            if (atual == ConnectionHealth.AoVivo && idadeDoUltimoFrame > StallThreshold)
                return ConnectionHealth.Instavel;

            if (atual == ConnectionHealth.Instavel && idadeDoUltimoFrame <= StallThreshold)
                return ConnectionHealth.AoVivo;

            return atual;
        }

        private void WatchdogTick(object? state)
        {
            if (_disposed) return;

            try
            {
                var age = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);
                var next = DecideHealth(Health, age);
                if (next == Health) return;

                if (next == ConnectionHealth.Instavel)
                {
                    // Se o que quebrou foi só a referência do decoder, um keyframe resolve em
                    // ~300ms — bem melhor do que esperar o ciclo de 2s do host.
                    try { _streamManager?.RequestKeyFrame(); } catch { }
                }

                SetHealth(next);
            }
            catch { }
        }

        // ───────────────────────────── Ciclo de vida ─────────────────────────────

        public async Task ConnectAsync()
        {
            BuildClient();

            await SetupStreamManagerAsync();
            Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);

            _watchdog ??= new System.Threading.Timer(WatchdogTick, null, WatchdogInterval, WatchdogInterval);

            await _client!.StartAsync(Ip, 8080);

            IsConnected = true;
            Friend.IsWatching = true;
            if (Health == ConnectionHealth.Conectando && StatusText == "Conectando...")
                SetHealth(ConnectionHealth.Conectando, "Conectado, aguardando vídeo...");
        }

        /// <summary>
        /// Nova tentativa a pedido do usuário, depois de esgotadas as automáticas. Descarta o
        /// cliente morto e recomeça do zero: o WebsocketClient já foi Dispose-ado pelo
        /// <see cref="Disconnect"/> e não volta a se conectar.
        /// </summary>
        public async Task RetryAsync()
        {
            if (_disposed) return;

            try { _client?.Stop(); } catch { }
            _client = null;

            _mediaRestarts = 0;
            _streamEnded = false;
            _passwordPromptOpen = false;
            SetHealth(ConnectionHealth.Conectando);

            await ConnectAsync();
        }

        private void BuildClient()
        {
            _client = new SignalingClient();

            // Envolvido em try/catch porque é um handler assíncrono de evento: sem isto, uma
            // falha aqui vira exceção não observada e chega no log global sem dizer de qual
            // amigo veio.
            _client.OnMessageReceived += async (message) =>
            {
              try
              {
                var authMsg = SignalingMessage.Deserialize(message);
                if (authMsg != null && authMsg.Type == "AUTH_REQUIRED")
                {
                    _authChallenge = authMsg.Data ?? string.Empty;

                    if (!string.IsNullOrEmpty(_password))
                    {
                        SendAuth();
                    }
                    else
                    {
                        SetHealth(ConnectionHealth.Conectando, "Esta sala pede senha");
                        PromptForPassword(previousAttemptFailed: false);
                    }
                    return;
                }
                if (authMsg != null && authMsg.Type == "AUTH_FAIL")
                {
                    // O host manda o desafio seguinte junto da recusa, para a nova tentativa
                    // já ter com o que responder.
                    _authChallenge = authMsg.Data ?? string.Empty;
                    _password = string.Empty;
                    SetHealth(ConnectionHealth.Conectando, "Senha incorreta");
                    PromptForPassword(previousAttemptFailed: true);
                    return;
                }
                if (authMsg != null && authMsg.Type == "AUTH_OK")
                {
                    _client!.EnableEncryption(_password);
                    SetHealth(ConnectionHealth.Conectando);
                    SendHello();
                    return;
                }
                if (message == "SOURCE_CHANGED")
                {
                    SetHealth(ConnectionHealth.Conectando, "Host trocou de tela...");
                    return;
                }
                if (message == "STREAM_STOPPED")
                {
                    _streamEnded = true;
                    _client?.SuppressReconnect();
                    try { _streamManager?.Stop(); } catch { }
                    VideoBitmap = null;
                    SetHealth(ConnectionHealth.Encerrada);
                    return;
                }
                if (message == "STREAM_STARTED")
                {
                    _streamEnded = false;
                    _mediaRestarts = 0;
                    _client?.AllowReconnect();
                    SetHealth(ConnectionHealth.Conectando);
                    await SetupStreamManagerAsync();
                    SendHello();
                    return;
                }

                var parsed = SignalingMessage.Deserialize(message);
                if (parsed != null && parsed.Type == "STATUS_RESPONSE")
                {
                    if (parsed.Data == "IDLE" && !_streamEnded)
                    {
                        SetHealth(ConnectionHealth.Encerrada, $"{FriendName} não está em live");
                    }
                    return;
                }

                if (_streamManager != null)
                    await _streamManager.HandleSignalingMessage("host", message);
              }
              catch (Exception ex)
              {
                  Diagnostics = $"Sessão: {ex.Message}";
              }
            };

            _client.OnBinaryReceived += (data) => _streamManager?.ProcessReceivedBinary(data);

            _client.OnConnected += async (isReconnect) =>
            {
                try
                {
                    if (isReconnect)
                    {
                        _streamEnded = false;
                        _mediaRestarts = 0;
                        SetHealth(ConnectionHealth.Conectando, "Reconectado, aguardando vídeo...");
                        await SetupStreamManagerAsync();
                    }
                    SendStatusCheck();
                    SendHello();
                }
                catch (Exception ex)
                {
                    Diagnostics = $"Reconexão: {ex.Message}";
                }
            };

            // O instante da queda precisa aparecer na tela. Este evento existia e ninguém o
            // assinava: até o watchdog ou o loop de reconexão se manifestarem, a célula ficava
            // com o quadro congelado e nenhum texto.
            _client.OnDisconnected += () =>
            {
                if (_disposed || _streamEnded) return;
                if (Health is ConnectionHealth.Perdida or ConnectionHealth.Reconectando) return;
                SetHealth(ConnectionHealth.Reconectando);
            };

            _client.OnReconnecting += (attempt, max) =>
                SetHealth(ConnectionHealth.Reconectando, $"Reconectando... ({attempt}/{max})");

            _client.OnReconnectFailed += () =>
            {
                SetHealth(ConnectionHealth.Perdida);
                Disconnect();
            };

            _client.OnLatencyUpdated += (ms) => LatencyMs = ms;
        }

        /// <summary>Abre o modal de senha, no máximo um por vez.</summary>
        private void PromptForPassword(bool previousAttemptFailed)
        {
            if (_passwordPromptOpen) return;
            _passwordPromptOpen = true;

            RaiseOnUi(() => PasswordRequested?.Invoke(this, previousAttemptFailed));
        }

        /// <summary>Senha informada pelo usuário no modal.</summary>
        public void SubmitPassword(string password)
        {
            _passwordPromptOpen = false;
            _password = password ?? string.Empty;
            SendAuth();
        }

        /// <summary>Usuário cancelou o modal de senha: encerra a sessão, mas deixa o caminho de volta.</summary>
        public void CancelPassword()
        {
            _passwordPromptOpen = false;
            SetHealth(ConnectionHealth.Perdida, "Senha necessária");
            Disconnect();
        }

        /// <summary>
        /// Responde ao desafio com o HMAC da senha derivada. A senha em si nunca sai daqui —
        /// antes ela ia em texto claro sobre ws:// e era legível por qualquer um na VPN.
        ///
        /// Vai por <c>SendPlain</c>: o AUTH é o que <em>abre</em> a sessão cifrada, então precisa
        /// ser legível para o host. Mandando cifrado — o que acontecia em toda reconexão, porque
        /// a chave sobrevivia à queda —, host e viewer entravam num ping-pong sem fim.
        /// </summary>
        private void SendAuth()
        {
            if (string.IsNullOrEmpty(_authChallenge)) return;

            var key = CryptoHelper.DeriveKey(_password);
            var proof = CryptoHelper.ComputeAuthProof(key, _authChallenge);
            var authMsg = new SignalingMessage { Type = "AUTH", Data = proof };
            _client?.SendPlain(SignalingMessage.Serialize(authMsg));
        }

        private void SendStatusCheck()
        {
            var statusMsg = new SignalingMessage { Type = "STATUS_CHECK" };
            _client?.SendMessage(SignalingMessage.Serialize(statusMsg));
        }

        private void SendHello()
        {
            var helloMsg = new SignalingMessage { Type = "CLIENT_CONNECTED", Data = "", SenderId = "client" };
            _client?.SendMessage(SignalingMessage.Serialize(helloMsg));
        }

        /// <summary>
        /// Refaz a negociação WebRTC sem derrubar o WebSocket. O caso que isto cobre é o UDP
        /// morrer enquanto o TCP sobrevive: a conexão ia para <c>failed</c>, ninguém tratava, e
        /// a célula ficava presa no último quadro até o usuário fechar e reabrir a live.
        /// </summary>
        private async Task RestartMediaAsync()
        {
            if (_disposed || _streamEnded) return;
            if (Interlocked.Exchange(ref _restartingMedia, 1) != 0) return;

            try
            {
                if (_mediaRestarts >= MaxMediaRestarts)
                {
                    SetHealth(ConnectionHealth.Perdida);
                    return;
                }

                _mediaRestarts++;
                SetHealth(ConnectionHealth.Instavel, $"Recuperando vídeo... ({_mediaRestarts}/{MaxMediaRestarts})");

                // Renegociar em cima de uma rede que ainda está ruim só queima uma das chances.
                await Task.Delay(MediaRestartDelay);
                if (_disposed || _streamEnded) return;

                await SetupStreamManagerAsync();
                SendHello();
            }
            catch (Exception ex)
            {
                Diagnostics = $"Recuperação: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _restartingMedia, 0);
            }
        }

        private async Task SetupStreamManagerAsync()
        {
            if (_streamManager != null)
            {
                try { _streamManager.Stop(); } catch { }
            }

            _streamManager = new StreamManager();
            ApplyVolume();

            _streamManager.OnVideoFrameDecoded += (pixelData, width, height, stride) =>
            {
                Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                _mediaRestarts = 0;

                UpdateBitmap(pixelData, width, height);
                if (!_streamEnded && Health != ConnectionHealth.AoVivo) SetHealth(ConnectionHealth.AoVivo);
            };

            _streamManager.OnConnectionStateChanged += (state) => Diagnostics = state;

            _streamManager.OnPeerStateChanged += (state) =>
            {
                if (_disposed || _streamEnded) return;

                switch (state)
                {
                    case RTCPeerConnectionState.connected:
                        // O ICE reporta conexão antes de a mídia fluir: sem reiniciar o relógio,
                        // o watchdog acusaria travamento no meio de um handshake saudável.
                        Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                        break;

                    case RTCPeerConnectionState.disconnected:
                        if (Health == ConnectionHealth.AoVivo) SetHealth(ConnectionHealth.Instavel);
                        try { _streamManager?.RequestKeyFrame(); } catch { }
                        break;

                    case RTCPeerConnectionState.failed:
                        // Só o failed: o closed é, quase sempre, o Stop() que nós mesmos
                        // acabamos de chamar aqui do lado — reagir a ele viraria laço.
                        _ = RestartMediaAsync();
                        break;
                }
            };

            _streamManager.OnLocalSdpReady += (clientId, sdpJson) => _client?.SendMessage(sdpJson);
            _streamManager.OnViewerFpsUpdated += (fps) => Fps = fps;
            _streamManager.OnAudioStatsUpdated += (frames) => AudioFps = frames;

            await _streamManager.InitializeClient();
        }

        private void UpdateBitmap(byte[] pixelData, int width, int height)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;

            app.Dispatcher.InvokeAsync(() =>
            {
                if (_videoBitmap == null || _videoBitmap.PixelWidth != width || _videoBitmap.PixelHeight != height)
                {
                    VideoBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                }

                if (_videoBitmap == null) return;

                _videoBitmap.Lock();
                Marshal.Copy(pixelData, 0, _videoBitmap.BackBuffer, pixelData.Length);
                _videoBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _videoBitmap.Unlock();
            });
        }

        private void ApplyVolume()
        {
            _streamManager?.SetVolume(IsMuted ? 0f : (float)(_volume / 100.0));
        }

        /// <summary>
        /// True quando o mudo foi imposto pela transmissão, não pelo usuário. Só o mudo automático
        /// é desfeito ao parar de transmitir — o que o usuário mutou na mão continua mudo.
        /// </summary>
        public bool MutedByBroadcast { get; private set; }

        public void ToggleMute()
        {
            IsMuted = !IsMuted;
            MutedByBroadcast = false;
        }

        /// <summary>
        /// Silencia a live enquanto você está no ar: o som dela sairia pelos seus alto-falantes,
        /// seria recapturado pelo loopback e voltaria para os seus viewers (e para o próprio amigo).
        /// </summary>
        public void ApplyBroadcastMute(bool broadcasting)
        {
            if (broadcasting)
            {
                if (IsMuted) return;
                IsMuted = true;
                MutedByBroadcast = true;
            }
            else
            {
                if (!MutedByBroadcast) return;
                IsMuted = false;
                MutedByBroadcast = false;
            }
        }

        /// <summary>O PiP manda 0..2 (slider de 0 a 200%); aqui vira a mesma escala do Volume.</summary>
        public void SetVolumeFromPip(float value)
        {
            Volume = Math.Max(0, Math.Min(200, value * 100.0));
        }

        public void Disconnect()
        {
            try { _client?.Stop(); } catch { }
            try { _streamManager?.Stop(); } catch { }
            IsConnected = false;
            Friend.IsWatching = false;
            Friend.SessionInfo = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _watchdog?.Dispose();
            _watchdog = null;

            Disconnect();
        }

        private void RaiseOnUi(Action action)
        {
            var app = System.Windows.Application.Current;
            if (app == null || app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.InvokeAsync(action);
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(name);
            return true;
        }

        private void RaisePropertyChanged(string? name)
        {
            var app = System.Windows.Application.Current;
            var args = new PropertyChangedEventArgs(name);
            if (app == null || app.Dispatcher.CheckAccess())
            {
                PropertyChanged?.Invoke(this, args);
            }
            else
            {
                app.Dispatcher.InvokeAsync(() => PropertyChanged?.Invoke(this, args));
            }
        }
    }
}
