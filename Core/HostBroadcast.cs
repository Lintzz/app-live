using System;
using System.Threading.Tasks;

namespace RadminStreamApp
{
    /// <summary>Ajustes de uma transmissão, resolvidos pela UI antes de subir a live.</summary>
    public sealed class BroadcastSettings
    {
        public required CaptureSource Source { get; init; }
        public string RoomPassword { get; init; } = string.Empty;
        public bool MaxPerformance { get; init; } = true;
        public bool ForceGdiCapture { get; init; }
        public bool LegacyAudio { get; init; }
        public uint ExcludedAudioProcessId { get; init; }
        public int Width { get; init; } = 1920;
        public int Height { get; init; } = 1080;
    }

    /// <summary>
    /// Ciclo de vida de "estar no ar": liga o capturador ao servidor de sinalização, publica
    /// os avisos de início/fim e repassa quadros e estatísticas.
    ///
    /// Isto vivia no code-behind do MainWindow, misturado com a manipulação de botões e
    /// painéis. Aqui não há nada de UI — a janela só assina os eventos.
    /// </summary>
    public sealed class HostBroadcast : IDisposable
    {
        private readonly SignalingServer? _server;
        private StreamManager? _streamManager;

        /// <summary>Quadro local, para o preview do host. (pixels, largura, altura)</summary>
        public event Action<byte[], int, int>? FrameReady;

        /// <summary>(fps, kbps) uma vez por segundo.</summary>
        public event Action<int, double>? StatsUpdated;

        /// <summary>Quadros de audio enviados por segundo. Zero com viewer conectado
        /// significa que o audio nao esta saindo daqui.</summary>
        public event Action<int>? AudioStatsUpdated;

        public event Action<string>? AudioCaptureError;

        /// <summary>Audio PCM para difundir pelo WebSocket (somente no modo legado).</summary>
        public event Action<byte[]>? BinaryAudioReady;

        public bool IsBroadcasting { get; private set; }

        /// <summary>Caminho de captura em uso — "DXGI" ou "GDI".</summary>
        public string ActiveCaptureMode => _streamManager?.ActiveCaptureMode ?? "—";

        public HostBroadcast(SignalingServer? server)
        {
            _server = server;
        }

        public async Task StartAsync(BroadcastSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            StopStreamManager();

            var manager = new StreamManager();
            _streamManager = manager;

            manager.SetMaxPerformanceMode(settings.MaxPerformance);
            manager.OnAudioCaptureError += (error) => AudioCaptureError?.Invoke(error);
            manager.OnLocalSdpReady += (clientId, sdpJson) => _server?.SendToClient(clientId, sdpJson);
            manager.OnLocalVideoFrameReady += (pixels, width, height, stride) => FrameReady?.Invoke(pixels, width, height);
            manager.OnHostStatsUpdated += (fps, kbps) => StatsUpdated?.Invoke(fps, kbps);
            manager.OnAudioStatsUpdated += (frames) => AudioStatsUpdated?.Invoke(frames);
            manager.OnBinaryDataReady += (data) => BinaryAudioReady?.Invoke(data);
            manager.SetLegacyAudio(settings.LegacyAudio);

            if (_server != null)
            {
                _server.RoomPassword = settings.RoomPassword;
                _server.IsStreaming = true;
            }

            // Fora da thread de UI: iniciar a captura e o encoder trava por algumas centenas
            // de milissegundos.
            await Task.Run(() =>
            {
                manager.SetTargetSource(settings.Source);
                manager.SetExcludedAudioProcess(settings.ExcludedAudioProcessId);
                manager.SetForceGdiCapture(settings.ForceGdiCapture);
                manager.SetResolution(settings.Width, settings.Height);
                manager.InitializeHost();
            });

            IsBroadcasting = true;
            _server?.BroadcastMessage("STREAM_STARTED");
        }

        public void Stop()
        {
            if (_server != null)
            {
                _server.IsStreaming = false;
                _server.BroadcastMessage("STREAM_STOPPED");
                _server.RoomPassword = string.Empty;
            }

            StopStreamManager();
            IsBroadcasting = false;
        }

        /// <summary>Reflete mudanças feitas nas configurações com a live já no ar.</summary>
        public void ApplyMaxPerformance(bool maxPerformance) => _streamManager?.SetMaxPerformanceMode(maxPerformance);
        public void ApplyForceGdiCapture(bool forceGdi) => _streamManager?.SetForceGdiCapture(forceGdi);
        public void ApplyExcludedAudioProcess(uint processId) => _streamManager?.SetExcludedAudioProcess(processId);

        /// <summary>
        /// Troca o monitor transmitido sem derrubar a live. O keyframe imediato evita que os
        /// viewers fiquem até 2s com a imagem da tela anterior.
        /// </summary>
        public void ChangeSource(CaptureSource source)
        {
            if (_streamManager == null) return;

            _streamManager.SetTargetSource(source);
            _streamManager.ForceKeyFrame();
            _server?.BroadcastMessage("SOURCE_CHANGED");
        }

        /// <summary>Encaminha a sinalização de um viewer para a conexão WebRTC dele.</summary>
        public Task HandleSignalingAsync(string clientId, string message)
            => _streamManager?.HandleSignalingMessage(clientId, message) ?? Task.CompletedTask;

        public void RemoveClient(string clientId) => _streamManager?.RemoveClient(clientId);

        private void StopStreamManager()
        {
            if (_streamManager == null) return;

            try { _streamManager.Stop(); } catch { }
            _streamManager = null;
        }

        public void Dispose() => StopStreamManager();
    }
}
