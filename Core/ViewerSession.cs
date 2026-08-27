using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadminStreamApp.Models;

namespace RadminStreamApp
{
    /// <summary>
    /// Uma live sendo assistida. Todo o caminho de viewer passa por aqui — tanto quando
    /// há uma única stream aberta quanto quando há várias lado a lado.
    /// </summary>
    public class ViewerSession : INotifyPropertyChanged, IDisposable
    {
        public Friend Friend { get; }
        public string Ip => Friend.Ip;
        public string FriendName => Friend.DisplayName;

        private SignalingClient _client;
        private StreamManager _streamManager;
        private string _password = string.Empty;
        private bool _streamEnded;
        private bool _disposed;

        /// <summary>Disparado quando o host exige senha. bool = a senha anterior foi recusada.</summary>
        public event Action<ViewerSession, bool> PasswordRequested = delegate {};

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
        public string MuteIcon => IsMuted ? "" : "";

        public string StatsText => $"📥 {Fps}fps | {LatencyMs}ms";

        public ViewerSession(Friend friend)
        {
            Friend = friend ?? throw new ArgumentNullException(nameof(friend));
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
                        StatusText = "Esta sala pede senha";
                        RaiseOnUi(() => PasswordRequested?.Invoke(this, false));
                    }
                    else
                    {
                        SendAuth();
                    }
                    return;
                }
                if (message == "AUTH_FAIL")
                {
                    _password = string.Empty;
                    StatusText = "Senha incorreta";
                    RaiseOnUi(() => PasswordRequested?.Invoke(this, true));
                    return;
                }
                if (message == "AUTH_OK")
                {
                    _client.EnableEncryption(_password);
                    StatusText = "Conectando...";
                    SendHello();
                    return;
                }
                if (message == "SOURCE_CHANGED")
                {
                    StatusText = "Host trocou de tela...";
                    return;
                }
                if (message == "STREAM_STOPPED")
                {
                    _streamEnded = true;
                    _client?.SuppressReconnect();
                    try { _streamManager?.Stop(); } catch { }
                    VideoBitmap = null;
                    StatusText = "Transmissão encerrada";
                    return;
                }
                if (message == "STREAM_STARTED")
                {
                    _streamEnded = false;
                    _client?.AllowReconnect();
                    await SetupStreamManagerAsync();
                    SendHello();
                    return;
                }

                var parsed = SignalingMessage.Deserialize(message);
                if (parsed != null && parsed.Type == "STATUS_RESPONSE")
                {
                    if (parsed.Data == "IDLE" && !_streamEnded)
                    {
                        StatusText = $"{FriendName} não está em live";
                    }
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
                    _streamEnded = false;
                    StatusText = "Reconectado!";
                    await SetupStreamManagerAsync();
                }
                SendStatusCheck();
                SendHello();
            };

            _client.OnReconnecting += (attempt) => StatusText = $"Reconectando... ({attempt}/10)";
            _client.OnReconnectFailed += () => { StatusText = "Falha ao reconectar"; Disconnect(); };
            _client.OnLatencyUpdated += (ms) => LatencyMs = ms;

            await SetupStreamManagerAsync();
            await _client.StartAsync(Ip, 8080);

            IsConnected = true;
            Friend.IsWatching = true;
            if (StatusText == "Conectando...") StatusText = "Conectado, aguardando vídeo...";
        }

        /// <summary>Senha informada pelo usuário no modal.</summary>
        public void SubmitPassword(string password)
        {
            _password = password ?? string.Empty;
            SendAuth();
        }

        /// <summary>Usuário cancelou o modal de senha: encerra a sessão.</summary>
        public void CancelPassword()
        {
            StatusText = "Senha necessária";
            Disconnect();
        }

        private void SendAuth()
        {
            var authMsg = new SignalingMessage { Type = "AUTH", Data = _password };
            _client?.SendMessage(SignalingMessage.Serialize(authMsg));
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

        public void SetQuality(string quality)
        {
            var msg = new SignalingMessage { Type = "SET_QUALITY", Data = quality, SenderId = "client" };
            _client?.SendMessage(SignalingMessage.Serialize(msg));
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
                UpdateBitmap(pixelData, width, height);
                if (!_streamEnded) StatusText = string.Empty;
            };

            _streamManager.OnConnectionStateChanged += (state) =>
            {
                // Depois de "Transmissão encerrada" o WebRTC ainda emite closed/failed;
                // sobrescrever aqui faria o usuário ver um erro no lugar do aviso real.
                if (_streamEnded) return;
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

        private void ApplyVolume()
        {
            _streamManager?.SetVolume(IsMuted ? 0f : (float)(_volume / 100.0));
        }

        public void ToggleMute() => IsMuted = !IsMuted;

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
            Disconnect();
        }

        private void RaiseOnUi(Action action)
        {
            var app = System.Windows.Application.Current;
            if (app == null || app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.InvokeAsync(action);
        }

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
