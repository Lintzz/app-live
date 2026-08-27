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

namespace RadminStreamApp
{
    public class StreamManager
    {
        private readonly Dictionary<string, RTCPeerConnection> _peerConnections = new Dictionary<string, RTCPeerConnection>();
        private readonly VideoCapturer _videoCapturer;
        private readonly AudioCapturer _audioCapturer;
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

        // ───────────────────────────── Áudio (Opus/WebRTC) ─────────────────────────────

        // 20 ms a 48 kHz. O áudio agora viaja pela própria trilha WebRTC em Opus (~40 kbps)
        // no lugar de PCM cru pelo WebSocket (~1,4 Mbps por viewer, e sem sincronia com o vídeo).
        private const int OpusFrameSamples = 960;
        private const int OpusSampleRate = 48000;

        private readonly AudioEncoder _audioEncoder = new AudioEncoder(includeLinearFormats: false, includeOpus: true);
        private readonly AudioFormat _opusFormat;
        private readonly List<byte> _pcmAccumulator = new List<byte>();
        private readonly object _pcmLock = new object();

        // Signaling events
        public event Action<string, string>? OnLocalSdpReady; // clientId, sdp (JSON SignalingMessage)

        // Media events
        public event Action<byte[], int, int, int>? OnVideoFrameDecoded; // payload, width, height, stride
        public event Action<byte[], int, int, int>? OnLocalVideoFrameReady; // raw local pixels

        public event Action<string>? OnAudioCaptureError;
        public event Action<string>? OnConnectionStateChanged; // WebRTC connection state

        public event Action<int, double>? OnHostStatsUpdated; // fps, kbps
        public event Action<int>? OnViewerFpsUpdated; // fps

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
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadminStreamApp");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, filename), DateTime.Now.ToString() + ": " + content + "\n");
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

            _opusFormat = _audioEncoder.SupportedFormats.First(f => f.Codec == AudioCodecsEnum.OPUS);

            InitEncoder();

            _videoCapturer = new VideoCapturer();
            _audioCapturer = new AudioCapturer();

            // Connect raw video from capturer to encoder
            _videoCapturer.OnVideoSourceRawSample += (duration, width, height, sample, format) =>
            {
                OnLocalVideoFrameReady?.Invoke(sample, width, height, width * 3); // 24bpp

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

            _audioCapturer.OnAudioFrameReady += OnCapturedPcm;

            _audioCapturer.OnCaptureError += (error) =>
            {
                OnAudioCaptureError?.Invoke(error);
            };
        }

        /// <summary>
        /// Fatia o PCM capturado (48 kHz estéreo) em quadros Opus de 20 ms e envia pela
        /// trilha de áudio do WebRTC. O downmix para mono é intencional: o Opus do SIPSorcery
        /// expõe a trilha como mono, e o ganho de banda compensa numa transmissão de tela.
        /// </summary>
        private void OnCapturedPcm(byte[] pcm)
        {
            const int frameBytes = OpusFrameSamples * AudioCapturer.Channels * 2; // estéreo 16-bit

            List<byte[]> framesToSend = new List<byte[]>();

            lock (_pcmLock)
            {
                _pcmAccumulator.AddRange(pcm);

                // Se acumulou muito (viewer travado, encoder lento), descarta o excedente
                // antigo em vez de deixar a latência do áudio crescer indefinidamente.
                int maxBacklog = frameBytes * 10; // ~200 ms
                if (_pcmAccumulator.Count > maxBacklog)
                {
                    _pcmAccumulator.RemoveRange(0, _pcmAccumulator.Count - maxBacklog);
                }

                while (_pcmAccumulator.Count >= frameBytes)
                {
                    var frame = new byte[frameBytes];
                    _pcmAccumulator.CopyTo(0, frame, 0, frameBytes);
                    _pcmAccumulator.RemoveRange(0, frameBytes);
                    framesToSend.Add(frame);
                }
            }

            if (framesToSend.Count == 0) return;

            var peers = SnapshotConnectedPeers();
            if (peers.Count == 0) return;

            foreach (var frame in framesToSend)
            {
                var mono = DownmixToMono(frame);

                byte[] encoded;
                try { encoded = _audioEncoder.EncodeAudio(mono, _opusFormat); }
                catch (Exception ex) { WriteLog("opus_encode_error.log", ex.ToString()); continue; }

                System.Threading.Interlocked.Add(ref _statsEncodedBytes, encoded.Length);

                foreach (var pc in peers)
                {
                    try { pc.SendAudio(OpusFrameSamples, encoded); } catch { }
                }
            }
        }

        private static short[] DownmixToMono(byte[] interleavedStereo)
        {
            int samples = interleavedStereo.Length / 4; // 2 canais * 2 bytes
            var mono = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                short left = BitConverter.ToInt16(interleavedStereo, i * 4);
                short right = BitConverter.ToInt16(interleavedStereo, i * 4 + 2);
                mono[i] = (short)((left + right) / 2);
            }
            return mono;
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

        /// <summary>Decodifica um pacote Opus recebido e entrega ao dispositivo de saída.</summary>
        private void OnAudioRtpReceived(RTPPacket packet)
        {
            if (_waveProvider == null || packet?.Payload == null || packet.Payload.Length == 0) return;

            try
            {
                var pcm = _audioEncoder.DecodeAudio(packet.Payload, _opusFormat);
                if (pcm == null || pcm.Length == 0) return;

                var bytes = new byte[pcm.Length * 2];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                _waveProvider.AddSamples(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                WriteLog("opus_decode_error.log", ex.ToString());
            }
        }

        public void SetVolume(float volume)
        {
            if (_volumeProvider != null)
            {
                _volumeProvider.Volume = volume;
            }
        }

        /// <summary>Força o caminho GDI de captura (ver <see cref="VideoCapturer"/>).</summary>
        public void SetForceGdiCapture(bool forceGdi) => _videoCapturer?.SetForceGdiCapture(forceGdi);

        /// <summary>Caminho de captura em uso — "DXGI" ou "GDI".</summary>
        public string ActiveCaptureMode => _videoCapturer?.ActiveCaptureMode ?? "—";

        public void SetResolution(int width, int height)
        {
            _videoCapturer?.SetResolution(width, height);
        }

        public void SetMaxPerformanceMode(bool isMaxPerformance)
        {
            _isMaxPerformance = isMaxPerformance;
            _videoCapturer?.SetMaxPerformanceMode(isMaxPerformance);

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
            _videoCapturer.SetTargetSource(source);
        }

        /// <summary>
        /// Define qual processo fica FORA da captura de áudio (0 = capturar tudo). Antes isto
        /// era fixo no Discord e invisível para o usuário; agora vem da escolha nas configurações.
        /// </summary>
        public void SetExcludedAudioProcess(uint processId)
        {
            _audioCapturer.SetTargetProcess(processId);
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
            _isHost = true;
            _videoCapturer.StartVideo();
            _audioCapturer.StartAudio();

            _hostStatsTimer = new System.Threading.Timer(_ =>
            {
                var fps = System.Threading.Interlocked.Exchange(ref _statsEncodedFrames, 0);
                var bytes = System.Threading.Interlocked.Exchange(ref _statsEncodedBytes, 0);
                OnHostStatsUpdated?.Invoke(fps, bytes * 8.0 / 1000.0);
            }, null, 1000, 1000);

            return Task.CompletedTask;
        }

        public async Task InitializeClient()
        {
            InitEncoder();
            _isHost = false;
            if (_waveOut == null)
            {
                // Mono 48 kHz: é o formato que sai do decodificador Opus.
                _waveProvider = new BufferedWaveProvider(new WaveFormat(OpusSampleRate, 16, 1))
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromMilliseconds(800)
                };

                _latencyTrimmer = new LatencyTrimmingProvider(_waveProvider, MaxAudioLatency, TargetAudioLatency);

                _volumeProvider = new NAudio.Wave.SampleProviders.VolumeSampleProvider(_latencyTrimmer.ToSampleProvider());
                _volumeProvider.Volume = 1.0f;

                _waveOut = new WaveOutEvent();
                _waveOut.Init(_volumeProvider);
                _waveOut.Play();
            }

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
                OnViewerFpsUpdated?.Invoke(fps);
            }, null, 1000, 1000);


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

            var audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false,
                new List<SDPAudioVideoMediaFormat> { new SDPAudioVideoMediaFormat(_opusFormat) });
            pc.addTrack(audioTrack);

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

                pc.OnRtpPacketReceived += (IPEndPoint rep, SDPMediaTypesEnum mediaType, RTPPacket packet) =>
                {
                    if (mediaType == SDPMediaTypesEnum.audio) OnAudioRtpReceived(packet);
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

            _videoCapturer?.CloseVideo();
            _audioCapturer?.CloseAudio();
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;

            lock (_pcmLock) { _pcmAccumulator.Clear(); }

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
    }
}
