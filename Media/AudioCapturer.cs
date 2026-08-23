using System;
using NAudio.Wave;
using SIPSorceryMedia.Abstractions;

namespace RadminStreamApp
{
    public class AudioCapturer : IAudioSource, IDisposable
    {
        private WasapiLoopbackCapture _loopbackCapture;
        private BufferedWaveProvider _bufferedProvider;
        private MediaFoundationResampler _resampler;
        private byte[] _resampleBuffer;
        
        private ProcessAudioCapturer _processAudioCapturer;
        private bool _useProcessCapture = false;
        private uint _targetProcessId = 0;
        
        public AudioCapturer()
        {
            // Initialize loopback capture as fallback
            _loopbackCapture = new WasapiLoopbackCapture();
            _bufferedProvider = new BufferedWaveProvider(_loopbackCapture.WaveFormat);
            _bufferedProvider.DiscardOnBufferOverflow = true;
            
            _resampler = new MediaFoundationResampler(_bufferedProvider, new WaveFormat(44100, 16, 2));
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
        /// Configures the capturer to capture audio from a specific process.
        /// Must be called BEFORE StartAudio().
        /// </summary>
        /// <param name="processId">The PID of the process to capture audio from.</param>
        public void SetTargetProcess(uint processId)
        {
            _targetProcessId = processId;
            _useProcessCapture = processId > 0;
        }

        /// <summary>
        /// Fired when process audio capture encounters an error.
        /// </summary>
        public event Action<string> OnCaptureError;

        public System.Threading.Tasks.Task StartAudio()
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
                    if (_loopbackCapture.CaptureState == NAudio.CoreAudioApi.CaptureState.Stopped)
                        _loopbackCapture.StartRecording();
                    return System.Threading.Tasks.Task.CompletedTask;
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
                    try
                    {
                        if (_loopbackCapture.CaptureState == NAudio.CoreAudioApi.CaptureState.Stopped)
                            _loopbackCapture.StartRecording();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Loopback capture already started or failed: " + ex.Message);
                    }
                }
            }
            else
            {
                // No specific process — use system-wide loopback
                try
                {
                    if (_loopbackCapture.CaptureState == NAudio.CoreAudioApi.CaptureState.Stopped)
                        _loopbackCapture.StartRecording();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Loopback capture already started or failed: " + ex.Message);
                }
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task CloseAudio()
        {
            if (_processAudioCapturer != null)
            {
                _processAudioCapturer.StopCapture();
                _processAudioCapturer.Dispose();
                _processAudioCapturer = null;
            }
            
            _loopbackCapture?.StopRecording();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task PauseAudio() { return System.Threading.Tasks.Task.CompletedTask; }
        public System.Threading.Tasks.Task ResumeAudio() { return System.Threading.Tasks.Task.CompletedTask; }
        
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) { }

        public bool HasEncodedAudioSubscribers() => false;
        public bool IsAudioSourcePaused() => false;
        public void RestrictFormats(Func<AudioFormat, bool> filter) { }
        public System.Collections.Generic.List<AudioFormat> GetAudioSourceFormats() => new System.Collections.Generic.List<AudioFormat>();
        public void SetAudioSourceFormat(AudioFormat audioFormat) { }

        public event EncodedSampleDelegate OnAudioSourceEncodedSample = delegate {};
        public event RawAudioSampleDelegate OnAudioSourceRawSample = delegate {};
        public event SourceErrorDelegate OnAudioSourceError = delegate {};
        public event Action<byte[]> OnAudioFrameReady;

        public void Dispose()
        {
            _processAudioCapturer?.Dispose();
            _resampler?.Dispose();
            _loopbackCapture?.Dispose();
        }
    }
}
