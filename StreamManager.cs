using System;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace RadminStreamApp
{
    public class StreamManager
    {
        private RTCPeerConnection _peerConnection;
        private VideoCapturer _videoCapturer;
        private AudioCapturer _audioCapturer;

        public event Action<string> OnLocalSdpReady;
        public event Action<byte[]> OnBinaryDataReady;
        public event Action<byte[]> OnJpegFrameReceived;
        
        /// <summary>
        /// Fired when process audio capture encounters an error (e.g., unsupported OS).
        /// </summary>
        public event Action<string> OnAudioCaptureError;
        
        private WaveOutEvent _waveOut;
        private BufferedWaveProvider _waveProvider;
        
        public StreamManager()
        {
            _videoCapturer = new VideoCapturer();
            _audioCapturer = new AudioCapturer();

            _videoCapturer.OnJpegFrameReady += (jpeg) => 
            {
                var data = new byte[jpeg.Length + 1];
                data[0] = 0; // 0 = Video
                Buffer.BlockCopy(jpeg, 0, data, 1, jpeg.Length);
                OnBinaryDataReady?.Invoke(data);
            };

            _audioCapturer.OnAudioFrameReady += (pcm) => 
            {
                var data = new byte[pcm.Length + 1];
                data[0] = 1; // 1 = Audio
                Buffer.BlockCopy(pcm, 0, data, 1, pcm.Length);
                OnBinaryDataReady?.Invoke(data);
            };

            // Forward audio capture errors
            _audioCapturer.OnCaptureError += (error) =>
            {
                OnAudioCaptureError?.Invoke(error);
            };
        }

        public void ProcessReceivedBinary(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            
            if (data[0] == 0) // Video
            {
                var jpeg = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, jpeg, 0, jpeg.Length);
                OnJpegFrameReceived?.Invoke(jpeg);
            }
            else if (data[0] == 1 && _waveProvider != null) // Audio
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

        public void SetTargetSource(CaptureSource source)
        {
            _videoCapturer.SetTargetSource(source);
            
            // Find Discord process to exclude its audio
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
            {
                targetDiscordPid = (uint)discordProcesses[0].Id;
            }

            if (targetDiscordPid > 0)
            {
                _audioCapturer.SetTargetProcess(targetDiscordPid);
            }
            else
            {
                _audioCapturer.SetTargetProcess(0);
            }
        }

        public Task InitializeHost()
        {
            // Start JPEG capture and Audio loopback immediately for WebSocket broadcast
            _videoCapturer.StartVideo();
            _audioCapturer.StartAudio();

            return Task.CompletedTask;
        }

        public Task InitializeClient()
        {
            if (_waveOut == null)
            {
                _waveProvider = new BufferedWaveProvider(new WaveFormat(44100, 16, 2));
                _waveProvider.DiscardOnBufferOverflow = true;
                _waveOut = new WaveOutEvent();
                _waveOut.Init(_waveProvider);
                _waveOut.Volume = 0.0f; // Muted by default as requested
                _waveOut.Play();
            }
            return Task.CompletedTask;
        }

        public Task SetRemoteDescription(string jsonSdp)
        {
            // WebRTC is no longer used, so this is just a stub if called by legacy code
            return Task.CompletedTask;
        }
        
        public void Stop()
        {
            _videoCapturer?.CloseVideo();
            _audioCapturer?.CloseAudio();
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _peerConnection?.Close("Closed by user");
        }
    }
}
