using System;
using NAudio.Wave;

namespace RadminStreamApp
{
    /// <remarks>
    /// Não implementa <c>IAudioSource</c>: o PCM sai por <see cref="OnAudioFrameReady"/> e é
    /// difundido pelo WebSocket, nunca pela interface. Ela só trazia membros stub vazios.
    /// </remarks>
    public class AudioCapturer : IDisposable
    {
        /// <summary>Taxa do PCM difundido pelo WebSocket.</summary>
        public const int SampleRate = 44100;
        public const int Channels = 2;

        private WasapiLoopbackCapture _loopbackCapture;
        private BufferedWaveProvider _bufferedProvider;
        private MediaFoundationResampler _resampler;
        private byte[] _resampleBuffer;
        
        private ProcessAudioCapturer? _processAudioCapturer;
        private bool _useProcessCapture = false;
        private uint _targetProcessId = 0;

        // Serializa abrir, fechar e reabrir a captura: a troca do programa excluído chega pela
        // thread de UI enquanto a captura roda na sua própria thread.
        private readonly object _captureLock = new object();
        private bool _started;
        
        public AudioCapturer()
        {
            // Initialize loopback capture as fallback
            _loopbackCapture = new WasapiLoopbackCapture();
            _bufferedProvider = new BufferedWaveProvider(_loopbackCapture.WaveFormat);
            _bufferedProvider.DiscardOnBufferOverflow = true;

            // ReadFully (o padrão é true) completa toda leitura com silêncio, então o resampler
            // nunca enxerga o fim da fonte e o laço de leitura abaixo não termina nunca: o callback
            // do WASAPI fica preso nele e o que sai para os viewers é um filete de áudio real
            // seguido de silêncio infinito. Com false, Read devolve só o que foi capturado de
            // verdade e o laço encerra a cada callback.
            _bufferedProvider.ReadFully = false;
            
            _resampler = new MediaFoundationResampler(_bufferedProvider, new WaveFormat(SampleRate, 16, Channels));
            _resampler.ResamplerQuality = 60;
            _resampleBuffer = new byte[8192];

            _loopbackCapture.DataAvailable += (s, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    _bufferedProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    int bytesRead;
                    while ((bytesRead = _resampler.Read(_resampleBuffer, 0, _resampleBuffer.Length)) > 0)
                    {
                        var outBuffer = new byte[bytesRead];
                        Array.Copy(_resampleBuffer, outBuffer, bytesRead);
                        OnAudioFrameReady?.Invoke(outBuffer);
                    }
                }
            };
        }

        /// <summary>
        /// Define o processo cujo áudio fica FORA da captura (0 = capturar todo o sistema).
        ///
        /// Pode ser chamado com a captura já rodando: os parâmetros só chegam ao Windows no
        /// momento em que ela abre, então trocar o alvo exige reabri-la. Antes isto apenas
        /// gravava os campos, e mudar a opção durante a transmissão não fazia efeito nenhum —
        /// quem percebia no meio da live que estava sendo escutado não tinha como corrigir.
        /// </summary>
        /// <param name="processId">PID do processo a excluir.</param>
        public void SetTargetProcess(uint processId)
        {
            bool restart;
            lock (_captureLock)
            {
                if (_targetProcessId == processId) return;

                _targetProcessId = processId;
                _useProcessCapture = processId > 0;
                restart = _started;
            }

            // Fora da thread de quem chamou: a reabertura espera o WASAPI terminar de parar,
            // e isso viria da thread de UI (a troca no menu de configurações).
            if (restart) System.Threading.Tasks.Task.Run(RestartCapture);
        }

        /// <summary>
        /// Fired when process audio capture encounters an error.
        /// </summary>
        public event Action<string>? OnCaptureError;

        public void StartAudio()
        {
            lock (_captureLock)
            {
                StartCaptureLocked();
                _started = true;
            }
        }

        /// <summary>Fecha e reabre a captura para o alvo atual valer imediatamente.</summary>
        private void RestartCapture()
        {
            lock (_captureLock)
            {
                if (!_started) return; // a transmissão terminou enquanto isto era agendado

                StopCaptureLocked();
                StartCaptureLocked();
            }
        }

        private void StartCaptureLocked()
        {
            if (_useProcessCapture && _targetProcessId > 0)
            {
                // Check if the OS supports process-specific audio capture
                if (!ProcessAudioCapturer.IsSupported())
                {
                    OnCaptureError?.Invoke(
                        "Seu Windows não suporta captura de áudio por processo.\n" +
                        "Requer Windows 10 Build 20348+ ou Windows 11.\n" +
                        "O áudio de todo o sistema será capturado.");
                    
                    // Fallback to system-wide loopback
                    _useProcessCapture = false;
                    StartLoopbackLocked();
                    return;
                }

                // Use process-specific audio capture
                _processAudioCapturer = new ProcessAudioCapturer();
                _processAudioCapturer.OnAudioFrameReady += (pcmData) =>
                {
                    OnAudioFrameReady?.Invoke(pcmData);
                };
                _processAudioCapturer.OnCaptureError += (error) =>
                {
                    OnCaptureError?.Invoke(error);
                };

                bool started = _processAudioCapturer.StartCapture(_targetProcessId, includeProcessTree: false);
                if (!started)
                {
                    // Fallback to system-wide loopback
                    _useProcessCapture = false;
                    _processAudioCapturer?.Dispose();
                    _processAudioCapturer = null;
                    StartLoopbackLocked();
                }
            }
            else
            {
                // No specific process — use system-wide loopback
                StartLoopbackLocked();
            }
        }

        /// <summary>
        /// Liga o loopback do sistema inteiro, esperando o WASAPI terminar de parar quando
        /// vem de uma reabertura. O StopRecording é assíncrono: o estado passa por Stopping
        /// antes de voltar a Stopped, e a checagem "== Stopped" sozinha simplesmente pulava o
        /// start — a transmissão ficava muda sem nenhum aviso.
        /// </summary>
        private void StartLoopbackLocked()
        {
            try
            {
                for (int i = 0; i < 50 && _loopbackCapture.CaptureState == NAudio.CoreAudioApi.CaptureState.Stopping; i++)
                {
                    System.Threading.Thread.Sleep(20);
                }

                if (_loopbackCapture.CaptureState == NAudio.CoreAudioApi.CaptureState.Stopped)
                    _loopbackCapture.StartRecording();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Loopback capture already started or failed: " + ex.Message);
            }
        }

        public void CloseAudio()
        {
            lock (_captureLock)
            {
                _started = false;
                StopCaptureLocked();
            }
        }

        private void StopCaptureLocked()
        {
            if (_processAudioCapturer != null)
            {
                _processAudioCapturer.StopCapture();
                _processAudioCapturer.Dispose();
                _processAudioCapturer = null;
            }

            try { _loopbackCapture?.StopRecording(); } catch { }
        }

        public event Action<byte[]>? OnAudioFrameReady;

        public void Dispose()
        {
            // Para a captura antes de soltar os objetos: descartar o loopback com o WASAPI
            // ainda gravando deixa o callback rodando sobre um resampler já liberado.
            CloseAudio();

            _resampler?.Dispose();
            _loopbackCapture?.Dispose();
        }
    }
}
