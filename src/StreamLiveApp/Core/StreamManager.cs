using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.FFmpeg;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using NAudio.Wave;
using System.Text.Json;
using System.Net;
using System.Linq;

namespace StreamLiveApp
{
    public class StreamManager : IDisposable
    {
        private readonly Dictionary<string, RTCPeerConnection> _peerConnections = new Dictionary<string, RTCPeerConnection>();

        // Nulos até EnsureCapturers() — o caminho de viewer nunca chega a criá-los.
        private VideoCapturer? _videoCapturer;
        private AudioCapturer? _audioCapturer;
        private readonly object _capturerLock = new object();

        private IVideoEncoder? _videoEncoder;
        private readonly object _encoderLock = new object();
        private int _isEncoding = 0;
        private bool _isMaxPerformance = true;

        // Keyframe por tempo decorrido, não por contagem de frames: a taxa real de captura
        // varia bastante, então "a cada 120 frames" dava um intervalo imprevisível.
        private static readonly TimeSpan KeyFrameInterval = TimeSpan.FromSeconds(2);
        private readonly Stopwatch _keyFrameClock = Stopwatch.StartNew();
        private TimeSpan _lastKeyFrame = TimeSpan.Zero;

        // Logo depois que alguem entra, o keyframe sai com frequencia bem maior. O IDR unico
        // disparado no "connected" costuma se perder — o ICE reporta conexao antes de a midia
        // fluir de verdade — e sem isso o viewer ficava o ciclo inteiro sem imagem.
        private static readonly TimeSpan KeyFrameBurstWindow = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan KeyFrameBurstInterval = TimeSpan.FromMilliseconds(400);
        private TimeSpan _burstUntil = TimeSpan.MinValue;

        // Piso entre keyframes atendidos sob demanda: com varios viewers pedindo ao mesmo
        // tempo, sem isto o encoder mandaria so IDR e a banda explodiria.
        private static readonly TimeSpan MinForcedKeyFrameGap = TimeSpan.FromMilliseconds(300);
        private TimeSpan _lastForcedKeyFrame = TimeSpan.MinValue;

        // Viewer: o video chega mas nada decodifica ate vir um keyframe. Se isso durar,
        // pedimos um ao host em vez de esperar o proximo ciclo.
        private volatile bool _videoArriving;
        private volatile bool _videoDecodedEver;
        private System.Threading.Timer? _keyFrameRequestTimer;
        private TimeSpan _lastKeyFrameRequest = TimeSpan.MinValue;

        // ───────────────────────────── Áudio ─────────────────────────────

        // O áudio vai como PCM cru pelo WebSocket de sinalização, com um byte de marcação na
        // frente. Chegou a passar por Opus na trilha do WebRTC (v1.0.18 a v1.0.21), mas
        // nunca funcionou em campo e foi revertido — este é o caminho comprovado.
        //
        // O custo é conhecido: ~1,4 Mbps por viewer e sem sincronia com o vídeo, que viaja
        // por outro transporte. O atraso acumulado é contido pelo LatencyTrimmingProvider.
        private const int AudioSampleRate = 44100;

        // Contadores expostos na sobreposição de estatísticas. Sem eles, "o som não funciona"
        // não distingue captura, transporte e reprodução.
        private int _audioFramesSent;
        private int _audioFramesDecoded;
        private int _audioFailures;
        private string? _audioFailureReason;
        private readonly object _audioLock = new object();

        // Signaling events
        public event Action<string, string>? OnLocalSdpReady; // clientId, sdp (JSON SignalingMessage)

        // Media events
        public event Action<byte[], int, int, int>? OnVideoFrameDecoded; // payload, width, height, stride
        public event Action<byte[], int, int, int>? OnLocalVideoFrameReady; // raw local pixels

        public event Action<string>? OnAudioCaptureError;

        /// <summary>
        /// Texto livre de diagnóstico do caminho WebRTC (progresso do SDP, erro do decoder, o
        /// enum do SIPSorcery). Não serve para decidir nada — quem precisa reagir usa o
        /// <see cref="OnPeerStateChanged"/>.
        /// </summary>
        public event Action<string>? OnConnectionStateChanged;

        /// <summary>
        /// Estado da conexão WebRTC, tipado. Existe porque o viewer precisava distinguir
        /// "conectando" de "morreu": failed/disconnected chegavam só como texto e ninguém
        /// tratava, então a live congelava para sempre no último quadro.
        /// </summary>
        public event Action<RTCPeerConnectionState>? OnPeerStateChanged;

        public event Action<int, double>? OnHostStatsUpdated; // fps, kbps
        public event Action<int>? OnViewerFpsUpdated; // fps

        /// <summary>Quadros de audio por segundo — enviados no host, decodificados no viewer.</summary>
        public event Action<int>? OnAudioStatsUpdated;

        /// <summary>Audio PCM para difusao pelo WebSocket (somente no modo legado).</summary>
        public event Action<byte[]>? OnBinaryDataReady;

        /// <summary>
        /// Consultado antes de empacotar cada quadro de audio. Sem viewer ouvindo, o pacote
        /// era montado (uma alocacao por quadro, ~50x/s) so para ser descartado no fim da
        /// linha — e transmitir para uma sala vazia e o estado normal enquanto os amigos
        /// ainda nao entraram.
        /// </summary>
        public Func<bool>? HasAudioListeners { get; set; }

        private System.Threading.Timer? _hostStatsTimer;
        private System.Threading.Timer? _viewerStatsTimer;
        private int _statsEncodedFrames = 0;
        private long _statsEncodedBytes = 0;
        private int _statsDecodedFrames = 0;

        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider? _waveProvider;
        private LatencyTrimmingProvider? _latencyTrimmer;
        private NAudio.Wave.SampleProviders.VolumeSampleProvider? _volumeProvider;
        private bool _isHost = false;

        // Teto de atraso do áudio. Passou disso, o excedente é descartado até o alvo — é o
        // que impede a live de ir ficando dessincronizada ao longo da sessão.
        private static readonly TimeSpan MaxAudioLatency = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan TargetAudioLatency = TimeSpan.FromMilliseconds(80);

        private static int _mediaInitialized;

        private static void WriteLog(string filename, string content)
        {
            try
            {
                System.IO.File.AppendAllText(AppPaths.GetFilePath(filename), DateTime.Now.ToString() + ": " + content + "\n");
            }
            catch { }
        }

        /// <summary>
        /// Inicialização global de mídia — uma vez por processo. Antes rodava no construtor,
        /// e como existe um StreamManager por live aberta, o FFmpeg era reinicializado (e o
        /// diretório corrente do processo trocado) a cada sessão.
        /// </summary>
        public static void EnsureMediaInitialized()
        {
            if (System.Threading.Interlocked.Exchange(ref _mediaInitialized, 1) != 0) return;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (System.IO.Directory.Exists(baseDir))
            {
                Environment.CurrentDirectory = baseDir;
            }

            try { SIPSorceryMedia.FFmpeg.FFmpegInit.Initialise(); }
            catch (Exception ex) { WriteLog("ffmpeg_error.log", "Init Error: " + ex.ToString()); }
        }

        public StreamManager()
        {
            EnsureMediaInitialized();

            // O encoder serve aos dois lados — o host codifica, o viewer decodifica com ele.
            // Os capturadores, não: quem só assiste nunca captura nada. Ver EnsureCapturers.
            InitEncoder();
        }

        /// <summary>
        /// Cria os capturadores na primeira vez que alguém precisa deles — o que só acontece
        /// no caminho de host.
        ///
        /// Antes eles nasciam no construtor, e como existe um StreamManager por live aberta,
        /// cada aba de viewer abria um <c>WasapiLoopbackCapture</c> e um
        /// <c>MediaFoundationResampler</c> (objetos COM de verdade, criados já no construtor
        /// do AudioCapturer) para nunca usar. Com quatro lives na grade eram quatro
        /// dispositivos de áudio segurados à toa.
        /// </summary>
        private void EnsureCapturers()
        {
            lock (_capturerLock)
            {
                if (_videoCapturer != null) return;

                _videoCapturer = new VideoCapturer();
                _audioCapturer = new AudioCapturer();

                WireCapturers(_videoCapturer, _audioCapturer);
            }
        }

        private void WireCapturers(VideoCapturer videoCapturer, AudioCapturer audioCapturer)
        {
            // Connect raw video from capturer to encoder
            videoCapturer.OnVideoSourceRawSample += (duration, width, height, sample, format) =>
            {
                OnLocalVideoFrameReady?.Invoke(sample, width, height, width * VideoCapturer.BytesPerPixel);

                if (System.Threading.Interlocked.CompareExchange(ref _isEncoding, 1, 0) != 0)
                {
                    return; // Skip encoding if previous frame is still processing
                }

                Task.Run(() =>
                {
                    try
                    {
                        byte[]? encoded = null;
                        lock (_encoderLock)
                        {
                            if (_videoEncoder != null)
                            {
                                try
                                {
                                    var now = _keyFrameClock.Elapsed;
                                    var interval = now <= _burstUntil ? KeyFrameBurstInterval : KeyFrameInterval;
                                    if (now - _lastKeyFrame >= interval)
                                    {
                                        _lastKeyFrame = now;
                                        _videoEncoder.ForceKeyFrame();
                                    }
                                    encoded = _videoEncoder.EncodeVideo(width, height, sample, format, VideoCodecsEnum.H264);
                                }
                                catch (Exception encodeEx)
                                {
                                    WriteLog("ffmpeg_encode_runtime_error.log", "Encode Run Error: " + encodeEx.ToString());
                                }
                            }
                        }

                        if (encoded != null && encoded.Length > 0)
                        {
                            System.Threading.Interlocked.Increment(ref _statsEncodedFrames);
                            System.Threading.Interlocked.Add(ref _statsEncodedBytes, encoded.Length);

                            foreach (var pc in SnapshotConnectedPeers())
                            {
                                try { pc.SendVideo(duration, encoded); } catch { }
                            }
                        }
                    }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _isEncoding, 0);
                    }
                });
            };

            audioCapturer.OnAudioFrameReady += OnCapturedPcm;

            audioCapturer.OnCaptureError += (error) =>
            {
                OnAudioCaptureError?.Invoke(error);
            };
        }

        /// <summary>
        /// Empacota o PCM capturado e entrega para difusão pelo WebSocket. Um byte de
        /// marcação na frente distingue áudio de qualquer outro dado binário.
        /// </summary>
        private void OnCapturedPcm(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            if (HasAudioListeners != null && !HasAudioListeners()) return;

            // O buffer NAO e reaproveitado de proposito: sem senha de sala ele segue direto
            // para o Send do Fleck, que e assincrono. Reciclar o array por baixo de um envio
            // em voo trocaria os bytes no meio do caminho.
            var packet = new byte[pcm.Length + 1];
            packet[0] = 1; // 1 = áudio
            Buffer.BlockCopy(pcm, 0, packet, 1, pcm.Length);

            OnBinaryDataReady?.Invoke(packet);
            System.Threading.Interlocked.Increment(ref _audioFramesSent);
        }

        /// <summary>
        /// Registra a primeira falha de audio e avisa a interface. Antes tudo aqui era
        /// engolido por um catch vazio, entao um problema no audio nao deixava rastro nenhum.
        /// </summary>
        private void ReportAudioFailure(string acao, Exception ex)
        {
            System.Threading.Interlocked.Increment(ref _audioFailures);

            if (_audioFailureReason != null) return;
            _audioFailureReason = $"Falha ao {acao} audio: {ex.Message}";

            WriteLog("audio_error.log", $"[{acao}] {ex}");
            OnAudioCaptureError?.Invoke(_audioFailureReason);
        }

        private List<RTCPeerConnection> SnapshotConnectedPeers()
        {
            lock (_peerConnections)
            {
                return _peerConnections.Values
                    .Where(pc => pc.connectionState == RTCPeerConnectionState.connected)
                    .ToList();
            }
        }

        /// <summary>
        /// Cria a saída de áudio na primeira amostra recebida, no formato que veio do host.
        /// Criar sob demanda evita abrir um dispositivo de áudio para uma live que talvez
        /// nunca traga som.
        /// </summary>
        private void EnsureAudioOutput(WaveFormat format)
        {
            if (_waveOut != null) return;

            lock (_audioLock)
            {
                if (_waveOut != null) return;

                try
                {
                    _waveProvider = new BufferedWaveProvider(format)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromMilliseconds(800)
                    };

                    _latencyTrimmer = new LatencyTrimmingProvider(_waveProvider, MaxAudioLatency, TargetAudioLatency);

                    _volumeProvider = new NAudio.Wave.SampleProviders.VolumeSampleProvider(_latencyTrimmer.ToSampleProvider())
                    {
                        Volume = _pendingVolume
                    };

                    _waveOut = new WaveOutEvent();
                    _waveOut.Init(_volumeProvider);
                    _waveOut.Play();
                }
                catch (Exception ex)
                {
                    // Abrir o dispositivo de saida pode falhar (sem placa, driver ocupado).
                    // Antes a excecao subia ate o handler do WebSocket e sumia no log global:
                    // a live continuava, muda, sem nada explicando por que.
                    _waveProvider = null;
                    _latencyTrimmer = null;
                    _volumeProvider = null;
                    _waveOut = null;

                    ReportAudioFailure("abrir a saida de", ex);
                }
            }
        }

        /// <summary>Áudio PCM recebido pelo WebSocket.</summary>
        public void ProcessReceivedBinary(byte[] data)
        {
            if (data == null || data.Length < 2 || data[0] != 1) return;

            EnsureAudioOutput(new WaveFormat(AudioSampleRate, 16, AudioCapturer.Channels));

            var provider = _waveProvider;
            if (provider == null) return;

            try
            {
                provider.AddSamples(data, 1, data.Length - 1);
                System.Threading.Interlocked.Increment(ref _audioFramesDecoded);
            }
            catch (Exception ex)
            {
                ReportAudioFailure("reproduzir o", ex);
            }
        }

        private float _pendingVolume = 1.0f;

        public void SetVolume(float volume)
        {
            // Guardado tambem quando a saida ainda nao existe: ela e criada sob demanda, e
            // sem isto o volume ajustado antes do primeiro audio se perdia.
            _pendingVolume = volume;

            if (_volumeProvider != null)
            {
                _volumeProvider.Volume = volume;
            }
        }

        // Todo ajuste de captura materializa os capturadores: quem chama qualquer um destes
        // está montando uma transmissão. O viewer não chama nenhum, e é assim que ele escapa
        // de abrir dispositivo de áudio e vídeo que nunca usaria.

        /// <summary>Força o caminho GDI de captura (ver <see cref="VideoCapturer"/>).</summary>
        public void SetForceGdiCapture(bool forceGdi)
        {
            EnsureCapturers();
            _videoCapturer!.SetForceGdiCapture(forceGdi);
        }

        /// <summary>
        /// Caminho de captura em uso — "DXGI" ou "GDI". Leitura pura: não materializa nada,
        /// devolve "—" enquanto não há captura montada.
        /// </summary>
        public string ActiveCaptureMode => _videoCapturer?.ActiveCaptureMode ?? "—";

        public void SetResolution(int width, int height)
        {
            EnsureCapturers();
            _videoCapturer!.SetResolution(width, height);
        }

        public void SetMaxPerformanceMode(bool isMaxPerformance)
        {
            EnsureCapturers();
            _isMaxPerformance = isMaxPerformance;
            _videoCapturer!.SetMaxPerformanceMode(isMaxPerformance);

            lock (_encoderLock)
            {
                if (_videoEncoder != null)
                {
                    _videoEncoder.Dispose();
                    _videoEncoder = null;
                    InitEncoder();
                }
            }
        }

        public void SetTargetSource(CaptureSource source)
        {
            EnsureCapturers();
            _videoCapturer!.SetTargetSource(source);
        }

        /// <summary>
        /// Define qual processo fica FORA da captura de áudio (0 = capturar tudo). Antes isto
        /// era fixo no Discord e invisível para o usuário; agora vem da escolha nas configurações.
        /// </summary>
        public void SetExcludedAudioProcess(uint processId)
        {
            EnsureCapturers();
            _audioCapturer!.SetTargetProcess(processId);
        }

        private void InitEncoder()
        {
            lock (_encoderLock)
            {
                if (_videoEncoder == null)
                {
                    // A versão atual do SIPSorcery para .NET 8 não expõe API para selecionar NVENC/AMF nativamente.
                    // Portanto, usamos o libx264 com perfil 'ultrafast' e 'zerolatency' para garantir baixíssimo uso de CPU,
                    // simulando a performance de uma GPU. Quando não estiver no modo desempenho, usa 'veryfast'.
                    var x264Options = new Dictionary<string, string>
                    {
                        { "preset", _isMaxPerformance ? "ultrafast" : "veryfast" },
                        { "tune", "zerolatency" }
                    };

                    _videoEncoder = new FFmpegVideoEncoder(x264Options);
                }
            }
        }

        public void ForceKeyFrame() => StartKeyFrameBurst();

        /// <summary>Abre uma janela em que os keyframes saem com frequencia bem maior.</summary>
        private void StartKeyFrameBurst()
        {
            var now = _keyFrameClock.Elapsed;
            _burstUntil = now + KeyFrameBurstWindow;

            lock (_encoderLock)
            {
                try { _videoEncoder?.ForceKeyFrame(); } catch { }
            }
            _lastKeyFrame = now;
            _lastForcedKeyFrame = now;
        }

        /// <summary>
        /// Atende ao pedido de keyframe de um viewer, com um piso de tempo entre atendimentos
        /// para que varios viewers pedindo junto nao virem uma enxurrada de IDR.
        /// </summary>
        private void ServeKeyFrameRequest()
        {
            var now = _keyFrameClock.Elapsed;
            if (_lastForcedKeyFrame != TimeSpan.MinValue && now - _lastForcedKeyFrame < MinForcedKeyFrameGap) return;

            _lastForcedKeyFrame = now;
            _lastKeyFrame = now;
            lock (_encoderLock)
            {
                try { _videoEncoder?.ForceKeyFrame(); } catch { }
            }
        }

        public Task InitializeHost()
        {
            InitEncoder();
            EnsureCapturers();
            _isHost = true;
            _videoCapturer!.StartVideo();
            _audioCapturer!.StartAudio();

            _hostStatsTimer = new System.Threading.Timer(_ =>
            {
                var fps = System.Threading.Interlocked.Exchange(ref _statsEncodedFrames, 0);
                var bytes = System.Threading.Interlocked.Exchange(ref _statsEncodedBytes, 0);
                var audio = System.Threading.Interlocked.Exchange(ref _audioFramesSent, 0);
                OnHostStatsUpdated?.Invoke(fps, bytes * 8.0 / 1000.0);
                OnAudioStatsUpdated?.Invoke(audio);
            }, null, 1000, 1000);

            return Task.CompletedTask;
        }

        public async Task InitializeClient()
        {
            InitEncoder();
            _isHost = false;

            // Client creates a single connection (to the host)
            await CreatePeerConnection("host");

            // Enquanto chegar video sem nada decodificar, insiste no pedido de keyframe.
            _keyFrameRequestTimer = new System.Threading.Timer(_ =>
            {
                if (!_videoArriving || _videoDecodedEver) return;

                var request = new SignalingMessage { Type = "REQUEST_KEYFRAME", SenderId = "client" };
                OnLocalSdpReady?.Invoke("host", SignalingMessage.Serialize(request));
            }, null, 600, 600);

            _viewerStatsTimer = new System.Threading.Timer(_ =>
            {
                var fps = System.Threading.Interlocked.Exchange(ref _statsDecodedFrames, 0);
                var audio = System.Threading.Interlocked.Exchange(ref _audioFramesDecoded, 0);
                OnViewerFpsUpdated?.Invoke(fps);
                OnAudioStatsUpdated?.Invoke(audio);
            }, null, 1000, 1000);


        }

        /// <summary>
        /// Pede um keyframe ao host e rearma o timer que insiste no pedido.
        ///
        /// <c>_videoDecodedEver</c> só ia de false para true: depois do primeiro quadro
        /// decodificado na vida da sessão, o timer acima ficava inerte para sempre. Se uma
        /// rajada de perda destruísse a referência, o viewer não pedia nada e ficava com
        /// macrobloco na tela até o keyframe periódico do host — até 2 segundos, toda vez.
        /// </summary>
        public void RequestKeyFrame()
        {
            if (_isHost) return;

            _videoDecodedEver = false;

            // Mesmo piso do lado do host: um viewer em rede ruim não pode virar uma metralhadora
            // de pedidos, porque cada IDR atendido custa banda para todo mundo na live.
            var now = _keyFrameClock.Elapsed;
            if (_lastKeyFrameRequest != TimeSpan.MinValue && now - _lastKeyFrameRequest < MinForcedKeyFrameGap) return;
            _lastKeyFrameRequest = now;

            var request = new SignalingMessage { Type = "REQUEST_KEYFRAME", SenderId = "client" };
            OnLocalSdpReady?.Invoke("host", SignalingMessage.Serialize(request));
        }

        /// <summary>
        /// Cria a conexão WebRTC do peer. Era <c>async void</c>: exceções em createOffer se
        /// perdiam no TaskScheduler e o chamador não tinha como esperar o offer sair.
        /// </summary>
        public async Task CreatePeerConnection(string clientId)
        {
            var pc = new RTCPeerConnection(null);

            // Both host and client need to know they support H264
            var videoFormat = new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 96));
            var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, new List<SDPAudioVideoMediaFormat> { videoFormat });
            pc.addTrack(videoTrack);

            if (!_isHost)
            {
                bool firstFrame = true;
                pc.OnVideoFrameReceived += (IPEndPoint rep, uint timestamp, byte[] payload, VideoFormat format) =>
                {
                    _videoArriving = true;
                    if (firstFrame)
                    {
                        firstFrame = false;
                        OnConnectionStateChanged?.Invoke("Aguardando keyframe...");
                    }
                    List<SIPSorceryMedia.Abstractions.VideoSample>? samples = null;
                    lock (_encoderLock)
                    {
                        if (_videoEncoder != null)
                        {
                            try
                            {
                                samples = _videoEncoder.DecodeVideo(payload, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264).ToList();
                            }
                            catch (Exception ex)
                            {
                                OnConnectionStateChanged?.Invoke($"Decode Error: {ex.Message}");
                            }
                        }
                    }
                    if (samples != null && samples.Any())
                    {
                        var sample = samples.First();
                        if (sample.Sample != null)
                        {
                            _videoDecodedEver = true;
                            System.Threading.Interlocked.Increment(ref _statsDecodedFrames);
                            OnVideoFrameDecoded?.Invoke(sample.Sample, (int)sample.Width, (int)sample.Height, (int)(sample.Width * 3));
                        }
                    }
                };

            }

            pc.onicecandidate += (candidate) =>
            {
                if (candidate == null) return;
                var msg = new SignalingMessage { Type = "ice", Data = candidate.toJSON(), SenderId = clientId };
                OnLocalSdpReady?.Invoke(clientId, SignalingMessage.Serialize(msg));
            };

            pc.onconnectionstatechange += (state) =>
            {
                OnConnectionStateChanged?.Invoke(state.ToString());
                OnPeerStateChanged?.Invoke(state);
                if (state == RTCPeerConnectionState.connected && _isHost)
                {
                    StartKeyFrameBurst();
                }
            };

            lock (_peerConnections)
            {
                _peerConnections[clientId] = pc;
            }

            if (_isHost)
            {
                OnConnectionStateChanged?.Invoke("Creating Offer...");
                var offer = pc.createOffer(null);
                await pc.setLocalDescription(offer);
                var msg = new SignalingMessage { Type = "offer", Data = offer.toJSON(), SenderId = clientId };
                OnConnectionStateChanged?.Invoke("Sending Offer...");
                OnLocalSdpReady?.Invoke(clientId, SignalingMessage.Serialize(msg));
            }
        }

        public async Task HandleSignalingMessage(string clientId, string jsonMsg)
        {
            var msg = SignalingMessage.Deserialize(jsonMsg);
            if (msg == null) return;

            if (msg.Type == "STREAM_STOPPED")
            {
                return;
            }

            // Equivalente ao PLI do WebRTC: o viewer avisa que esta sem imagem e o host manda
            // um keyframe na hora, em vez de o viewer esperar o proximo ciclo.
            if (msg.Type == "REQUEST_KEYFRAME")
            {
                if (_isHost) ServeKeyFrameRequest();
                return;
            }

            // Um CLIENT_CONNECTED é sempre um pedido de negociação nova — o viewer acabou de
            // entrar, ou a mídia dele morreu e ele quer recomeçar. Antes isto caía na busca
            // abaixo, encontrava a conexão antiga e não gerava offer nenhum: o viewer que
            // tentava se recuperar ficava esperando para sempre por um vídeo que nunca vinha.
            if (msg.Type == "CLIENT_CONNECTED")
            {
                if (!_isHost) return;
                RemoveClient(clientId);
                await CreatePeerConnection(clientId);
                return;
            }

            RTCPeerConnection? pc;
            bool needsCreate = false;
            lock (_peerConnections)
            {
                if (!_peerConnections.TryGetValue(clientId, out pc))
                {
                    if (!_isHost) return;
                    needsCreate = true;
                }
            }

            // Fora do lock: CreatePeerConnection tem awaits e não deve segurar o cadeado.
            if (needsCreate)
            {
                await CreatePeerConnection(clientId);
                lock (_peerConnections)
                {
                    if (!_peerConnections.TryGetValue(clientId, out pc)) return;
                }
            }

            if (pc == null) return;

            if (msg.Type == "offer")
            {
                OnConnectionStateChanged?.Invoke("Received Offer");
                if (RTCSessionDescriptionInit.TryParse(msg.Data, out var init))
                {
                    OnConnectionStateChanged?.Invoke("Parsed Offer, Setting Remote...");
                    var result = pc.setRemoteDescription(init);
                    if (result == SetDescriptionResultEnum.OK)
                    {
                        OnConnectionStateChanged?.Invoke("Creating Answer...");
                        var answer = pc.createAnswer(null);
                        await pc.setLocalDescription(answer);
                        var answerMsg = new SignalingMessage { Type = "answer", Data = answer.toJSON(), SenderId = clientId };
                        OnConnectionStateChanged?.Invoke("Sending Answer...");
                        OnLocalSdpReady?.Invoke(clientId, SignalingMessage.Serialize(answerMsg));
                    }
                    else
                    {
                        OnConnectionStateChanged?.Invoke($"Failed to set Remote Offer: {result}");
                    }
                }
                else
                {
                    OnConnectionStateChanged?.Invoke("Failed to parse Offer");
                }
            }
            else if (msg.Type == "answer")
            {
                OnConnectionStateChanged?.Invoke("Received Answer");
                if (RTCSessionDescriptionInit.TryParse(msg.Data, out var init))
                {
                    OnConnectionStateChanged?.Invoke("Setting Remote Answer...");
                    pc.setRemoteDescription(init);
                }
                else
                {
                    OnConnectionStateChanged?.Invoke("Failed to parse Answer");
                }
            }
            else if (msg.Type == "ice")
            {
                OnConnectionStateChanged?.Invoke("Received ICE");
                if (RTCIceCandidateInit.TryParse(msg.Data, out var candidate))
                {
                    pc.addIceCandidate(candidate);
                }
            }
        }

        public void RemoveClient(string clientId)
        {
            lock (_peerConnections)
            {
                if (_peerConnections.TryGetValue(clientId, out var pc))
                {
                    try { pc.Close("Client disconnected"); } catch { }
                    _peerConnections.Remove(clientId);
                }
            }
        }

        public void Stop()
        {
            _hostStatsTimer?.Dispose();
            _hostStatsTimer = null;
            _viewerStatsTimer?.Dispose();
            _viewerStatsTimer = null;
            _keyFrameRequestTimer?.Dispose();
            _keyFrameRequestTimer = null;

            // Dispose, e não só Close: o AudioCapturer segura um WasapiLoopbackCapture e um
            // MediaFoundationResampler que CloseAudio não libera. Como o ViewerSession cria um
            // StreamManager novo a cada conexão, reconexão e STREAM_STARTED, fechar sem
            // liberar acumulava esses objetos ao longo da sessão.
            lock (_capturerLock)
            {
                try { _videoCapturer?.Dispose(); } catch { }
                _videoCapturer = null;
                try { _audioCapturer?.Dispose(); } catch { }
                _audioCapturer = null;
            }

            lock (_audioLock)
            {
                try { _waveOut?.Stop(); } catch { }
                try { _waveOut?.Dispose(); } catch { }
                _waveOut = null;
                _volumeProvider = null;
                _latencyTrimmer = null;
                _waveProvider = null;
            }

            lock (_peerConnections)
            {
                foreach (var pc in _peerConnections.Values)
                {
                    try { pc.Close("Closed by user"); } catch { }
                }
                _peerConnections.Clear();
            }
            lock (_encoderLock)
            {
                _videoEncoder?.Dispose();
                _videoEncoder = null;
            }
        }

        public void Dispose() => Stop();
    }
}
