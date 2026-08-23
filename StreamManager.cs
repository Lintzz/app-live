using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
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
        private VpxVideoEncoder _vpxEncoder;
        private object _encoderLock = new object();
        private int _isEncoding = 0;

        // Signaling events
        public event Action<string, string> OnLocalSdpReady; // clientId, sdp (JSON SignalingMessage)
        
        // Media events
        public event Action<byte[]> OnBinaryDataReady; // Used for Audio via WebSocket
        public event Action<byte[], int, int, int> OnVideoFrameDecoded; // payload, width, height, stride
        public event Action<byte[], int, int, int> OnLocalVideoFrameReady; // raw local pixels
        
        public event Action<string> OnAudioCaptureError;
        
        private WaveOutEvent _waveOut;
        private BufferedWaveProvider _waveProvider;
        private bool _isHost = false;
        
        public StreamManager()
        {
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
                            if (_vpxEncoder != null)
                            {
                                try 
                                {
                                    encoded = _vpxEncoder.EncodeVideo(width, height, sample, format, VideoCodecsEnum.VP8);
                                }
                                catch { }
                            }
                        }

                        if (encoded != null && encoded.Length > 0)
                        {
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
            if (_waveOut != null)
            {
                _waveOut.Volume = volume;
            }
        }

        public void SetResolution(int width, int height)
        {
            _videoCapturer?.SetResolution(width, height);
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

        public Task InitializeHost()
        {
            lock (_encoderLock)
            {
                if (_vpxEncoder == null)
                {
                    _vpxEncoder = new VpxVideoEncoder();
                }
            }
            _isHost = true;
            _videoCapturer.StartVideo();
            _audioCapturer.StartAudio();
            return Task.CompletedTask;
        }

        public Task InitializeClient()
        {
            lock (_encoderLock)
            {
                if (_vpxEncoder == null)
                {
                    _vpxEncoder = new VpxVideoEncoder();
                }
            }
            _isHost = false;
            if (_waveOut == null)
            {
                _waveProvider = new BufferedWaveProvider(new WaveFormat(44100, 16, 2));
                _waveProvider.DiscardOnBufferOverflow = true;
                _waveOut = new WaveOutEvent();
                _waveOut.Init(_waveProvider);
                _waveOut.Volume = 0.0f;
                _waveOut.Play();
            }
            
            // Client creates a single connection (to the host)
            CreatePeerConnection("host");
            return Task.CompletedTask;
        }

        public async void CreatePeerConnection(string clientId)
        {
            var pc = new RTCPeerConnection(null);
            
            if (_isHost)
            {
                var videoFormat = new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.VP8, 96));
                var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, new List<SDPAudioVideoMediaFormat> { videoFormat });
                pc.addTrack(videoTrack);
            }
            else
            {
                pc.OnVideoFrameReceived += (IPEndPoint rep, uint timestamp, byte[] payload, VideoFormat format) =>
                {
                    List<SIPSorceryMedia.Abstractions.VideoSample> samples = null;
                    lock (_encoderLock)
                    {
                        if (_vpxEncoder != null)
                        {
                            try
                            {
                                samples = _vpxEncoder.DecodeVideo(payload, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8).ToList();
                            }
                            catch { }
                        }
                    }
                    if (samples != null && samples.Any())
                    {
                        var sample = samples.First();
                        if (sample.Sample != null)
                        {
                            OnVideoFrameDecoded?.Invoke(sample.Sample, (int)sample.Width, (int)sample.Height, (int)(sample.Width * 4));
                        }
                    }
                };
            }

            pc.onicecandidate += (candidate) =>
            {
                var msg = new SignalingMessage { Type = "ice", Data = candidate.toJSON(), SenderId = clientId };
                OnLocalSdpReady?.Invoke(clientId, SignalingMessage.Serialize(msg));
            };

            lock (_peerConnections)
            {
                _peerConnections[clientId] = pc;
            }

            if (_isHost)
            {
                var offer = pc.createOffer(null);
                await pc.setLocalDescription(offer);
                var msg = new SignalingMessage { Type = "offer", Data = offer.toJSON(), SenderId = clientId };
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
                if (RTCSessionDescriptionInit.TryParse(msg.Data, out var init))
                {
                    var result = pc.setRemoteDescription(init);
                    if (result == SetDescriptionResultEnum.OK)
                    {
                        var answer = pc.createAnswer(null);
                        await pc.setLocalDescription(answer);
                        var answerMsg = new SignalingMessage { Type = "answer", Data = answer.toJSON(), SenderId = clientId };
                        OnLocalSdpReady?.Invoke(clientId, SignalingMessage.Serialize(answerMsg));
                    }
                }
            }
            else if (msg.Type == "answer")
            {
                if (RTCSessionDescriptionInit.TryParse(msg.Data, out var init))
                {
                    pc.setRemoteDescription(init);
                }
            }
            else if (msg.Type == "ice")
            {
                if (RTCIceCandidateInit.TryParse(msg.Data, out var candidate))
                {
                    pc.addIceCandidate(candidate);
                }
            }
        }
        
        public void Stop()
        {
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
                _vpxEncoder?.Dispose();
                _vpxEncoder = null;
            }
        }
    }
}
