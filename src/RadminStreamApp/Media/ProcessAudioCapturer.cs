using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace RadminStreamApp
{
    /// <summary>
    /// Captures audio from a specific process using the ApplicationLoopback.dll
    /// (Windows 10 Build 20348+ / Windows 11 required).
    /// Uses P/Invoke to call the native C++ DLL that wraps the Windows
    /// ActivateAudioInterfaceAsync process loopback API.
    /// </summary>
    public class ProcessAudioCapturer : IDisposable
    {
        // P/Invoke declarations for ApplicationLoopback.dll
        private delegate void AudioCallbackDelegate(IntPtr data, int length);

        [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern void SetAudioCallback(AudioCallbackDelegate callback);

        [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr StartCaptureAsync(uint processId, bool includeProcessTree, ushort channel,
            uint sampleRate, ushort bitsPerSample);

        [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int StopCaptureAsync();

        // A DLL guarda o ponteiro do callback num slot global e nunca o devolve, nem depois de
        // parar a captura. Soltar a referência aqui deixaria o GC recolher o delegate com o
        // nativo ainda apontando para ele — e aí a falha vira violação de acesso. Por isso a
        // lista é estática e só cresce: são poucos bytes por transmissão.
        private static readonly List<AudioCallbackDelegate> KeepAlive = new();
        private static readonly object KeepAliveLock = new();

        private AudioCallbackDelegate? _callbackDelegate;
        private volatile bool _isCapturing = false;
        private bool _disposed = false;

        // O StartCaptureAsync não é assíncrono apesar do nome: ele prende a thread que o chama
        // durante toda a captura e — medido — nem o StopCaptureAsync o faz voltar. A captura
        // para de verdade, mas a chamada nunca desenrola. Por isso ela vive numa thread própria,
        // de fundo, que simplesmente é abandonada no encerramento.
        private Thread? _captureThread;

        /// <summary>
        /// Janela para flagrar uma recusa do Windows: como o sucesso nunca retorna, uma volta
        /// rápida da chamada nativa é justamente o sinal de falha. Uma recusa mais lenta que
        /// isto ainda é reportada, só que pelo OnCaptureError.
        /// </summary>
        private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Fired when a chunk of PCM audio data is available.
        /// The byte[] contains raw PCM data at 44100 Hz, 16-bit, stereo.
        /// </summary>
        public event Action<byte[]>? OnAudioFrameReady;

        /// <summary>
        /// Fired when the process audio capture encounters an error
        /// (e.g., OS doesn't support the API).
        /// </summary>
        public event Action<string>? OnCaptureError;

        /// <summary>
        /// Starts capturing audio from a specific process.
        /// </summary>
        /// <param name="processId">The PID of the target process.</param>
        /// <param name="includeProcessTree">
        /// true = capture ONLY this process and its children (INCLUDE mode).
        /// false = capture everything EXCEPT this process and its children (EXCLUDE mode).
        /// </param>
        public bool StartCapture(uint processId, bool includeProcessTree = true)
        {
            if (_isCapturing) return true;

            try
            {
                // Set up the callback before starting capture
                _callbackDelegate = new AudioCallbackDelegate(OnAudioDataReceived);
                lock (KeepAliveLock) { KeepAlive.Add(_callbackDelegate); }
                SetAudioCallback(_callbackDelegate);

                _isCapturing = true;
                bool refused = false;

                _captureThread = new Thread(() =>
                {
                    try
                    {
                        // Mesma taxa do loopback, para o viewer receber sempre o mesmo formato
                        // independentemente de qual caminho de captura está ativo.
                        StartCaptureAsync(processId, includeProcessTree,
                            AudioCapturer.Channels, AudioCapturer.SampleRate, 16);
                    }
                    catch { }

                    // Chegar aqui só acontece quando o Windows recusa: no caminho bom a chamada
                    // acima não volta nunca.
                    refused = true;
                    if (_isCapturing)
                    {
                        _isCapturing = false;
                        OnCaptureError?.Invoke(StartFailureMessage);
                    }
                })
                {
                    IsBackground = true,
                    Name = "ProcessAudioCapture"
                };
                _captureThread.Start();

                if (_captureThread.Join(StartupGracePeriod) && refused)
                {
                    _captureThread = null;
                    return false; // a mensagem já saiu pelo OnCaptureError, dentro da thread
                }

                return true;
            }
            catch (DllNotFoundException)
            {
                OnCaptureError?.Invoke("ApplicationLoopback.dll não encontrada. " +
                    "A captura de áudio por processo não está disponível.");
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                OnCaptureError?.Invoke("ApplicationLoopback.dll incompatível. " +
                    "A versão da DLL não possui as funções esperadas.");
                return false;
            }
            catch (Exception ex)
            {
                OnCaptureError?.Invoke($"Erro ao iniciar captura de áudio: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops the audio capture.
        /// </summary>
        public void StopCapture()
        {
            var thread = _captureThread;
            _captureThread = null;

            // Antes de tudo: a partir daqui nenhum quadro sai mais daqui, mesmo que a DLL
            // ainda entregue algum durante o encerramento.
            _isCapturing = false;

            if (thread == null) return;

            try
            {
                // Este volta em ~1 ms e a captura para de fato. A thread do start, porém,
                // continua parada dentro da DLL para sempre — medido, segue presa 10 s depois.
                // Não há o que esperar: ela é de fundo e some com o processo.
                StopCaptureAsync();
            }
            catch { }
        }

        private const string StartFailureMessage =
            "Falha ao iniciar captura de áudio do processo. " +
            "Verifique se o Windows suporta essa funcionalidade (Windows 10 Build 20348+ ou Windows 11).";

        /// <summary>
        /// Called by the native DLL when audio data is available.
        /// </summary>
        private void OnAudioDataReceived(IntPtr data, int length)
        {
            if (!_isCapturing || length <= 0 || data == IntPtr.Zero) return;

            try
            {
                byte[] buffer = new byte[length];
                Marshal.Copy(data, buffer, 0, length);
                OnAudioFrameReady?.Invoke(buffer);
            }
            catch
            {
                // Ignore errors in callback to prevent crashes in native code
            }
        }

        /// <summary>
        /// Checks if the current Windows version supports process-specific audio capture.
        /// Requires Windows 10 Build 20348 or later.
        /// </summary>
        public static bool IsSupported()
        {
            try
            {
                var version = Environment.OSVersion.Version;
                // Windows 10 Build 20348+ or Windows 11
                return version.Major >= 10 && version.Build >= 20348;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopCapture();

            // O _callbackDelegate NÃO é solto aqui: a DLL segue com o ponteiro dele mesmo
            // depois de parar. Quem o mantém vivo é a lista estática lá em cima.
        }
    }
}
