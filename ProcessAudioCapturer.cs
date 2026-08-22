using System;
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

        // Keep a reference to prevent GC from collecting the delegate
        private AudioCallbackDelegate _callbackDelegate;
        private bool _isCapturing = false;
        private bool _disposed = false;

        /// <summary>
        /// Fired when a chunk of PCM audio data is available.
        /// The byte[] contains raw PCM data at 44100 Hz, 16-bit, stereo.
        /// </summary>
        public event Action<byte[]> OnAudioFrameReady;

        /// <summary>
        /// Fired when the process audio capture encounters an error
        /// (e.g., OS doesn't support the API).
        /// </summary>
        public event Action<string> OnCaptureError;

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
                SetAudioCallback(_callbackDelegate);

                // Start capture: 2 channels, 44100 Hz, 16-bit
                var result = StartCaptureAsync(processId, includeProcessTree, 2, 44100, 16);

                if (result == IntPtr.Zero)
                {
                    OnCaptureError?.Invoke("Falha ao iniciar captura de áudio do processo. " +
                        "Verifique se o Windows suporta essa funcionalidade (Windows 10 Build 20348+ ou Windows 11).");
                    return false;
                }

                _isCapturing = true;
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
            if (!_isCapturing) return;

            try
            {
                StopCaptureAsync();
            }
            catch { }

            _isCapturing = false;
        }

        /// <summary>
        /// Called by the native DLL when audio data is available.
        /// </summary>
        private void OnAudioDataReceived(IntPtr data, int length)
        {
            if (length <= 0 || data == IntPtr.Zero) return;

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
            _callbackDelegate = null;
        }
    }
}
