using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.FFmpeg;
using SIPSorceryMedia.Windows;
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
        private Dictionary<string, RTCPeerConnection> _peerConnections = new Dictionary<string, RTCPeerConnection>();
        private VideoCapturer _videoCapturer;
        private AudioCapturer _audioCapturer;
        private IVideoEncoder _videoEncoder;
        private object _encoderLock = new object();
        private int _isEncoding = 0;
        private int _frameCount = 0;
        private bool _isMaxPerformance = true;

        // Signaling events
        public event Action<string, string> OnLocalSdpReady; // clientId, sdp (JSON SignalingMessage)
        
        // Media events
        public event Action<byte[]> OnBinaryDataReady; // Used for Audio via WebSocket
        public event Action<byte[], int, int, int> OnVideoFrameDecoded; // payload, width, height, stride
        public event Action<byte[], int, int, int> OnLocalVideoFrameReady; // raw local pixels
        
        public event Action<string> OnAudioCaptureError;
        public event Action<string> OnConnectionStateChanged; // WebRTC connection state

        public event Action<int, double> OnHostStatsUpdated; // fps, kbps
        public event Action<int> OnViewerFpsUpdated; // fps

        private System.Threading.Timer _hostStatsTimer;
        private System.Threading.Timer _viewerStatsTimer;
        private int _statsEncodedFrames = 0;
        private long _statsEncodedBytes = 0;
        private int _statsDecodedFrames = 0;

        private WaveOutEvent _waveOut;
        private BufferedWaveProvider _waveProvider;
        private NAudio.Wave.SampleProviders.VolumeSampleProvider _volumeProvider;
        private bool _isHost = false;
        
        private void WriteLog(string filename, string content)
        {
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadminStreamApp");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, filename), DateTime.Now.ToString() + ": " + content + "\n");
            }
            catch { }
        }

        public StreamManager()
        {
            // Set current directory to the app directory so FFmpeg can find its binaries if it relies on CWD
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (System.IO.Directory.Exists(baseDir))
            {
                Environment.CurrentDirectory = baseDir;
            }
            
            try { SIPSorceryMedia.FFmpeg.FFmpegInit.Initialise(); } 
            catch (Exception ex) { WriteLog("ffmpeg_error.log", "Init Error: " + ex.ToString()); }

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
                        byte[] encoded = null;
                        lock (_encoderLock)
                        {
                            if (_videoEncoder != null)
                            {
                                try
                                {
                                    _frameCount++;
                                    if (_frameCount % 120 == 0) // Força um KeyFrame a cada 2 segundos (60fps)
                                    {
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

                            lock (_peerConnections)
                            {
                                foreach (var pc in _peerConnections.Values)
                                {
                                    if (pc.connectionState == RTCPeerConnectionState.connected)
                                    {
                                        pc.SendVideo(duration, encoded);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _isEncoding, 0);
                    }
                });
            };

            // Keep audio on WebSockets for reliable multi-client delivery
            _audioCapturer.OnAudioFrameReady += (pcm) => 
            {
                var data = new byte[pcm.Length + 1];
                data[0] = 1; // 1 = Audio
                Buffer.BlockCopy(pcm, 0, data, 1, pcm.Length);
                OnBinaryDataReady?.Invoke(data);
            };

            _audioCapturer.OnCaptureError += (error) =>
            {
                OnAudioCaptureError?.Invoke(error);
            };
        }

        public void ProcessReceivedBinary(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            
            if (data[0] == 1 && _waveProvider != null) // Audio
            {
                var pcm = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, pcm, 0, pcm.Length);
                _waveProvider.AddSamples(pcm, 0, pcm.Length);
            }
        }

        public void SetVolume(float volume)
        {
            if (_volumeProvider != null)
            {
                _volumeProvider.Volume = volume;
            }
        }

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
            
            var discordProcesses = Process.GetProcessesByName("Discord");
            uint targetDiscordPid = 0;
            foreach (var p in discordProcesses)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    targetDiscordPid = (uint)p.Id;
                    break;
                }
            }
            if (targetDiscordPid == 0 && discordProcesses.Length > 0)
                targetDiscordPid = (uint)discordProcesses[0].Id;

            _audioCapturer.SetTargetProcess(targetDiscordPid);
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

        public void ForceKeyFrame()
        {
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

        public Task InitializeClient()
        {
            InitEncoder();
            _isHost = false;
            if (_waveOut == null)
            {
                _waveProvider = new BufferedWaveProvider(new WaveFormat(44100, 16, 2));
                _waveProvider.DiscardOnBufferOverflow = true;
                
                _volumeProvider = new NAudio.Wave.SampleProviders.VolumeSampleProvider(_waveProvider.ToSampleProvider());
                _volumeProvider.Volume = 1.0f;
                
                _waveOut = new WaveOutEvent();
                _waveOut.Init(_volumeProvider);
                _waveOut.Play();
            }
            
            // Client creates a single connection (to the host)
            CreatePeerConnection("host");

            _viewerStatsTimer = new System.Threading.Timer(_ =>
            {
                var fps = System.Threading.Interlocked.Exchange(ref _statsDecodedFrames, 0);
                OnViewerFpsUpdated?.Invoke(fps);
            }, null, 1000, 1000);

            return Task.CompletedTask;
        }

        public async void CreatePeerConnection(string clientId)
        {
            var pc = new RTCPeerConnection(null);
            
            // Both host and client need to know they support H264
            var videoFormat = new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 96));
            var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, new List<SDPAudioVideoMediaFormat> { videoFormat });
            pc.addTrack(videoTrack);

            if (_isHost)
            {
                // Host only logic (no extra init needed for track right now)
            }
            else
            {
                bool firstFrame = true;
                pc.OnVideoFrameReceived += (IPEndPoint rep, uint timestamp, byte[] payload, VideoFormat format) =>
                {
                    if (firstFrame)
                    {
                        firstFrame = false;
                        OnConnectionStateChanged?.Invoke("Decoding Video...");
                    }
                    List<SIPSorceryMedia.Abstractions.VideoSample> samples = null;
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
                if (state == RTCPeerConnectionState.connected && _isHost)
                {
                    lock (_encoderLock)
                    {
                        try { _videoEncoder?.ForceKeyFrame(); } catch { }
                    }
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

            if (msg.Type == "SET_QUALITY")
            {
                if (msg.Data == "1080p") SetResolution(1920, 1080);
                else if (msg.Data == "720p") SetResolution(1280, 720);
                else if (msg.Data == "480p") SetResolution(854, 480);
                return;
            }

            RTCPeerConnection pc;
            lock (_peerConnections)
            {
                if (!_peerConnections.TryGetValue(clientId, out pc))
                {
                    if (_isHost)
                    {
                        CreatePeerConnection(clientId);
                        pc = _peerConnections[clientId];
                    }
                    else
                    {
                        return;
                    }
                }
            }

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

            _videoCapturer?.CloseVideo();
            _audioCapturer?.CloseAudio();
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            
            lock (_peerConnections)
            {
                foreach(var pc in _peerConnections.Values)
                {
                    pc.Close("Closed by user");
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
