using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadminStreamApp
{
    public class ViewerSession : INotifyPropertyChanged, IDisposable
    {
        public string FriendName { get; }
        public string Ip { get; }
        private readonly string _password;

        private SignalingClient _client;
        private StreamManager _streamManager;
        private float _volume = 1.0f;
        private bool _showingSourceChangedMessage = false;

        public event PropertyChangedEventHandler PropertyChanged;

        private WriteableBitmap _videoBitmap;
        public WriteableBitmap VideoBitmap { get => _videoBitmap; private set => SetProperty(ref _videoBitmap, value); }

        private string _statusText = "Conectando...";
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

        private bool _isConnected;
        public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }

        private int _fps;
        public int Fps { get => _fps; private set { if (SetProperty(ref _fps, value)) RaisePropertyChanged(nameof(StatsText)); } }

        private int _latencyMs;
        public int LatencyMs { get => _latencyMs; private set { if (SetProperty(ref _latencyMs, value)) RaisePropertyChanged(nameof(StatsText)); } }

        public string StatsText => $"📥 {Fps}fps | {LatencyMs}ms";

        public ViewerSession(string friendName, string ip, string password)
        {
            FriendName = string.IsNullOrWhiteSpace(friendName) ? ip : friendName;
            Ip = ip;
            _password = password ?? string.Empty;
        }

        public async Task ConnectAsync()
        {
            _client = new SignalingClient();

            _client.OnMessageReceived += async (message) =>
            {
                if (message == "AUTH_REQUIRED")
                {
                    if (string.IsNullOrEmpty(_password))
                    {
                        StatusText = "Sala requer senha";
                        Disconnect();
                    }
                    else
                    {
                        var authMsg = new SignalingMessage { Type = "AUTH", Data = _password };
                        _client.SendMessage(SignalingMessage.Serialize(authMsg));
                    }
                    return;
                }
                if (message == "AUTH_FAIL")
                {
                    StatusText = "Senha incorreta";
                    Disconnect();
                    return;
                }
                if (message == "AUTH_OK")
                {
                    _client.EnableEncryption(_password);
                    SendHello();
                    return;
                }
                if (message == "SOURCE_CHANGED")
                {
                    _showingSourceChangedMessage = true;
                    StatusText = "Host trocou de tela...";
                    _ = ClearSourceChangedMessageAfterDelay();
                    return;
                }
                if (message == "STREAM_STOPPED")
                {
                    try { _streamManager?.Stop(); } catch { }
                    StatusText = "Transmissão Encerrada";
                    return;
                }
                if (message == "STREAM_STARTED")
                {
                    await SetupStreamManagerAsync();
                    SendHello();
                    return;
                }
                var parsed = SignalingMessage.Deserialize(message);
                if (parsed != null && parsed.Type == "STATUS_RESPONSE")
                {
                    if (parsed.Data == "IDLE") StatusText = "Host não está em live";
                    return;
                }

                if (_streamManager != null)
                    await _streamManager.HandleSignalingMessage("host", message);
            };

            _client.OnBinaryReceived += (data) => _streamManager?.ProcessReceivedBinary(data);

            _client.OnConnected += async (isReconnect) =>
            {
                if (isReconnect)
                {
                    StatusText = "Reconectado!";
                    await SetupStreamManagerAsync();
                }
                SendHello();
            };

            _client.OnReconnecting += (attempt) => StatusText = $"Reconectando... ({attempt}/10)";
            _client.OnReconnectFailed += () => { StatusText = "Falha ao reconectar"; Disconnect(); };
            _client.OnLatencyUpdated += (ms) => LatencyMs = ms;

            await SetupStreamManagerAsync();
            await _client.StartAsync(Ip, 8080);

            IsConnected = true;
            StatusText = "Conectado, aguardando vídeo...";
        }

        private async Task ClearSourceChangedMessageAfterDelay()
        {
            await Task.Delay(2000);
            _showingSourceChangedMessage = false;
            if (StatusText == "Host trocou de tela...") StatusText = string.Empty;
        }

        private void SendHello()
        {
            var statusMsg = new SignalingMessage { Type = "STATUS_CHECK" };
            _client?.SendMessage(SignalingMessage.Serialize(statusMsg));
            var helloMsg = new SignalingMessage { Type = "CLIENT_CONNECTED", Data = "", SenderId = "client" };
            _client?.SendMessage(SignalingMessage.Serialize(helloMsg));
        }

        private async Task SetupStreamManagerAsync()
        {
            if (_streamManager != null)
            {
                try { _streamManager.Stop(); } catch { }
            }

            _streamManager = new StreamManager();
            _streamManager.SetVolume(_volume);

            _streamManager.OnVideoFrameDecoded += (pixelData, width, height, stride) =>
            {
                UpdateBitmap(pixelData, width, height);
                if (!_showingSourceChangedMessage) StatusText = string.Empty;
            };

            _streamManager.OnConnectionStateChanged += (state) =>
            {
                StatusText = $"WebRTC: {state}";
            };

            _streamManager.OnLocalSdpReady += (clientId, sdpJson) => _client?.SendMessage(sdpJson);
            _streamManager.OnViewerFpsUpdated += (fps) => Fps = fps;

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

                _videoBitmap.Lock();
                Marshal.Copy(pixelData, 0, _videoBitmap.BackBuffer, pixelData.Length);
                _videoBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _videoBitmap.Unlock();
            });
        }

        public void SetVolume(float volume)
        {
            _volume = volume;
            _streamManager?.SetVolume(volume);
        }

        public void SetQuality(string quality)
        {
            var msg = new SignalingMessage { Type = "SET_QUALITY", Data = quality, SenderId = "client" };
            _client?.SendMessage(SignalingMessage.Serialize(msg));
        }

        public void Disconnect()
        {
            try { _client?.Stop(); } catch { }
            try { _streamManager?.Stop(); } catch { }
            IsConnected = false;
        }

        public void Dispose() => Disconnect();

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(name);
            return true;
        }

        private void RaisePropertyChanged(string name)
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
