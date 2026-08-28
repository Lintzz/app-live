using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SIPSorceryMedia.Abstractions;

namespace RadminStreamApp
{
    /// <summary>
    /// Captura da tela do host. Duas mudanças grandes em relação à primeira versão:
    /// o quadro vem do Desktop Duplication (DXGI) quando disponível, com o BitBlt do GDI
    /// como reserva; e todos os buffers são reaproveitados entre quadros — antes cada
    /// quadro alocava dois Bitmaps e um array de ~6 MB, o que jogava centenas de MB/s no LOH.
    /// </summary>
    public class VideoCapturer : IVideoSource, IDisposable
    {
        private bool _isCapturing = false;
        private int _isCapturingFrame = 0;
        private System.Threading.Timer? _timer;
        private CaptureSource? _captureSource;

        private int _maxWidth = 1920;
        private int _maxHeight = 1080;
        private bool _isMaxPerformance = true;

        // ── Buffers reaproveitados ────────────────────────────────────────────────
        private readonly object _bufferLock = new object();
        private byte[]? _captureBuffer;      // BGRA da tela inteira (caminho DXGI)
        private GCHandle _captureHandle;     // fixa o buffer para o Bitmap apontar nele
        private Bitmap? _captureBitmap;      // wrapper zero-copy sobre _captureBuffer
        private Bitmap? _gdiBitmap;          // destino do CopyFromScreen (caminho GDI)
        private Bitmap? _scaledBitmap;       // destino do DrawImage quando há redução
        // Anel de buffers de saída. O encoder consome o quadro numa Task separada e a UI
        // copia o mesmo array no dispatcher — com um único buffer, a captura seguinte
        // sobrescrevia os bytes embaixo de ambos, corrompendo o vídeo transmitido.
        private const int OutputBufferCount = 3;
        private byte[]?[] _outputBuffers = new byte[]?[OutputBufferCount];
        private int _outputBufferIndex;
        private int _bufferWidth, _bufferHeight;

        private DesktopDuplicationGrabber? _duplication;
        private bool _duplicationUnavailable;
        private bool _forceGdi;
        private Rectangle _duplicationBounds;

        // Se a duplicação foi criada mas nunca entrega quadro (acontece em sessões remotas e
        // em GPUs híbridas), desistimos dela e voltamos para o GDI em vez de transmitir nada.
        private const int DuplicationFailureLimit = 60;
        private int _duplicationFailures;

        // Mesmo com a tela parada, o quadro precisa ser reenviado de tempos em tempos: é o
        // que garante keyframe para quem acabou de entrar na live (o DXGI só entrega
        // quadro quando algo muda, e sem isso o viewer novo ficava no preto).
        // 300ms e nao 500: com a tela parada, o keyframe pedido por quem acabou de entrar so
        // sai no proximo quadro emitido, entao este intervalo e o piso da espera por imagem.
        private static readonly TimeSpan IdleFrameInterval = TimeSpan.FromMilliseconds(300);
        private TimeSpan _lastEmit = TimeSpan.MinValue;
        private bool _hasRealFrame;   // já veio pelo menos um quadro de verdade da tela

        // O encoder precisa da duração real do quadro. Antes ia 16 ms fixo mesmo quando o
        // intervalo real era 40 ms, o que bagunçava os timestamps RTP no viewer.
        private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
        private long _lastFrameTicks;

        /// <summary>
        /// Força o caminho GDI (o mesmo de antes do DXGI). Válvula de escape para máquinas em
        /// que a duplicação existe mas se comporta mal.
        /// </summary>
        public void SetForceGdiCapture(bool forceGdi)
        {
            lock (_bufferLock)
            {
                _forceGdi = forceGdi;
                if (forceGdi)
                {
                    _duplication?.Dispose();
                    _duplication = null;
                    _duplicationUnavailable = true;
                }
                else
                {
                    _duplicationUnavailable = false;
                    _duplicationFailures = 0;
                }
            }
        }

        /// <summary>Qual caminho está ativo agora — mostrado nas configurações.</summary>
        public string ActiveCaptureMode => _duplicationUnavailable ? "GDI" : (_duplication != null ? "DXGI" : "—");

        public void SetMaxPerformanceMode(bool isMaxPerformance)
        {
            _isMaxPerformance = isMaxPerformance;
        }

        public void SetResolution(int width, int height)
        {
            _maxWidth = width;
            _maxHeight = height;
        }

        public event RawVideoSampleDelegate? OnVideoSourceRawSample;
        public event EncodedSampleDelegate OnVideoSourceEncodedSample = delegate {};

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public Int32 x;
            public Int32 y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO
        {
            public Int32 cbSize;
            public Int32 flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO
        {
            public bool fIcon;
            public Int32 xHotspot;
            public Int32 yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll")]
        static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("user32.dll")]
        static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        private const Int32 CURSOR_SHOWING = 0x00000001;

        public void SetTargetSource(CaptureSource source)
        {
            _captureSource = source;

            // Trocar de monitor invalida o duplicador atual: ele é preso a um output.
            lock (_bufferLock)
            {
                _duplication?.Dispose();
                _duplication = null;
                _duplicationUnavailable = _forceGdi;
                _duplicationFailures = 0;
                _lastEmit = TimeSpan.MinValue;
                _hasRealFrame = false;
            }
        }

        public Task StartVideo()
        {
            _isCapturing = true;
            _lastFrameTicks = _frameClock.ElapsedTicks;
            // 60 FPS approx (16ms) - Mais fluido
            _timer = new System.Threading.Timer(CaptureFrame, null, 0, 16);
            return Task.CompletedTask;
        }

        public Task PauseVideo()
        {
            _isCapturing = false;
            return Task.CompletedTask;
        }

        public Task ResumeVideo()
        {
            _isCapturing = true;
            return Task.CompletedTask;
        }

        public Task CloseVideo()
        {
            _isCapturing = false;
            _timer?.Dispose();
            _timer = null;
            ReleaseBuffers();
            return Task.CompletedTask;
        }

        /// <summary>(Re)aloca os buffers só quando a resolução de captura muda.</summary>
        private void EnsureBuffers(int width, int height)
        {
            if (_bufferWidth == width && _bufferHeight == height && _captureBuffer != null) return;

            ReleaseBuffers();

            _bufferWidth = width;
            _bufferHeight = height;

            _outputBuffers = new byte[]?[OutputBufferCount];
            _captureBuffer = new byte[width * height * 4];
            _captureHandle = GCHandle.Alloc(_captureBuffer, GCHandleType.Pinned);
            _captureBitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb,
                _captureHandle.AddrOfPinnedObject());
            _gdiBitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        }

        private void ReleaseBuffers()
        {
            lock (_bufferLock)
            {
                _captureBitmap?.Dispose();
                _captureBitmap = null;
                if (_captureHandle.IsAllocated) _captureHandle.Free();
                _captureBuffer = null;
                _gdiBitmap?.Dispose();
                _gdiBitmap = null;
                _scaledBitmap?.Dispose();
                _scaledBitmap = null;
                _outputBuffers = new byte[]?[OutputBufferCount];
                _bufferWidth = _bufferHeight = 0;
                _hasRealFrame = false;
                _lastEmit = TimeSpan.MinValue;
                _duplication?.Dispose();
                _duplication = null;
            }
        }

        private void CaptureFrame(object? state)
        {
            if (!_isCapturing || _captureSource == null) return;

            if (System.Threading.Interlocked.CompareExchange(ref _isCapturingFrame, 1, 0) != 0)
            {
                return; // Já existe um frame sendo capturado/enviado, ignora esse
            }

            try
            {
                lock (_bufferLock)
                {
                    CaptureFrameCore();
                }
            }
            catch (Exception)
            {
                // Ignore capture errors (window closed, minimized)
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _isCapturingFrame, 0);
            }
        }

        private void CaptureFrameCore()
        {
            var bounds = _captureSource!.ScreenBounds;
            int left = bounds.Left, top = bounds.Top;
            int width = bounds.Width, height = bounds.Height;
            if (width <= 0 || height <= 0) return;

            EnsureBuffers(width, height);

            bool gotNewFrame;
            Bitmap source;

            if (!_duplicationUnavailable && TryCaptureWithDuplication(bounds))
            {
                source = _captureBitmap!;
                _hasRealFrame = true;
                gotNewFrame = true;
            }
            else if (_duplicationUnavailable)
            {
                if (!CaptureWithGdi(left, top, width, height)) return;
                source = _gdiBitmap!;
                _hasRealFrame = true;
                gotNewFrame = true;
            }
            else
            {
                // DXGI ativo, mas a tela não mudou. Reemitimos o último quadro de vez em
                // quando para manter o fluxo de keyframes para quem entrar agora.
                //
                // Só depois de ter recebido um quadro de verdade: antes disso o buffer está
                // zerado e reemiti-lo transmitiria uma tela preta — e ainda enganaria o
                // watchdog abaixo, que usa _hasRealFrame para decidir se o DXGI funciona.
                if (!_hasRealFrame) return;
                if (_lastEmit != TimeSpan.MinValue && _frameClock.Elapsed - _lastEmit < IdleFrameInterval) return;

                source = _captureBitmap!;
                gotNewFrame = false;
            }

            // Só no quadro novo: o buffer do DXGI só é reescrito quando há imagem nova, então
            // redesenhar na reemissão deixaria um rastro de cursores acumulados.
            if (gotNewFrame) DrawCursor(source, left, top);

            // Escala se passar do limite; dimensões múltiplas de 4 evitam padding de stride.
            float scale = 1.0f;
            if (width > _maxWidth || height > _maxHeight)
            {
                scale = Math.Min((float)_maxWidth / width, (float)_maxHeight / height);
            }

            int outWidth = (int)(width * scale);
            int outHeight = (int)(height * scale);
            outWidth -= outWidth % 4;
            outHeight -= outHeight % 4;
            if (outWidth <= 0 || outHeight <= 0) return;

            Bitmap finalBitmap = source;
            if (scale < 1.0f)
            {
                if (_scaledBitmap == null || _scaledBitmap.Width != outWidth || _scaledBitmap.Height != outHeight)
                {
                    _scaledBitmap?.Dispose();
                    _scaledBitmap = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppRgb);
                }

                using (var g = Graphics.FromImage(_scaledBitmap))
                {
                    g.InterpolationMode = _isMaxPerformance
                        ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                        : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.DrawImage(source, 0, 0, outWidth, outHeight);
                }
                finalBitmap = _scaledBitmap;
            }

            var handler = OnVideoSourceRawSample;
            if (handler == null) return;

            int outputSize = outWidth * outHeight * 3;

            _outputBufferIndex = (_outputBufferIndex + 1) % OutputBufferCount;
            var output = _outputBuffers[_outputBufferIndex];
            if (output == null || output.Length != outputSize)
            {
                output = new byte[outputSize];
                _outputBuffers[_outputBufferIndex] = output;
            }

            ConvertBgraToBgr24(finalBitmap, outWidth, outHeight, output);

            // Duração real desde o quadro anterior, em milissegundos.
            long nowTicks = _frameClock.ElapsedTicks;
            uint durationMs = (uint)Math.Clamp(
                (nowTicks - _lastFrameTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency, 1, 1000);
            _lastFrameTicks = nowTicks;

            _lastEmit = _frameClock.Elapsed;
            handler(durationMs, outWidth, outHeight, output, VideoPixelFormatsEnum.Bgr);
        }

        /// <summary>Tenta o caminho DXGI; marca como indisponível de vez se não der para criar.</summary>
        private bool TryCaptureWithDuplication(Rectangle bounds)
        {
            if (_duplicationUnavailable || _forceGdi) return false;

            if (_duplication == null || _duplicationBounds != bounds)
            {
                _duplication?.Dispose();
                _duplication = DesktopDuplicationGrabber.TryCreate(bounds);
                _duplicationBounds = bounds;

                if (_duplication == null)
                {
                    _duplicationUnavailable = true;
                    return false;
                }
            }

            // O monitor visto pelo DXGI é em pixels físicos; o CaptureSource pode vir em
            // coordenadas escaladas por DPI. Se não baterem, o GDI assume: capturar com
            // dimensões trocadas produziria imagem deslocada (e antes estourava o buffer).
            if (_duplication.Width != _bufferWidth || _duplication.Height != _bufferHeight)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Capture] Duplicação {_duplication.Width}x{_duplication.Height} != esperado " +
                    $"{_bufferWidth}x{_bufferHeight}; usando GDI.");
                _duplication.Dispose();
                _duplication = null;
                _duplicationUnavailable = true;
                return false;
            }

            if (!_duplication.TryGetFrame(_captureBuffer!, _bufferWidth * 4, _bufferHeight))
            {
                // Timeout é normal (tela parada). Uma sequência longa deles não é: aí a
                // duplicação não está funcionando de verdade e o GDI assume.
                if (++_duplicationFailures >= DuplicationFailureLimit && !_hasRealFrame)
                {
                    System.Diagnostics.Debug.WriteLine("[Capture] Duplicação sem quadros; voltando para GDI.");
                    _duplication.Dispose();
                    _duplication = null;
                    _duplicationUnavailable = true;
                }
                return false;
            }

            _duplicationFailures = 0;
            return true;
        }

        private bool CaptureWithGdi(int left, int top, int width, int height)
        {
            if (_gdiBitmap == null) return false;

            using var g = Graphics.FromImage(_gdiBitmap);
            g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return true;
        }

        private static void DrawCursor(Bitmap target, int left, int top)
        {
            CURSORINFO pci;
            pci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (!GetCursorInfo(out pci) || pci.flags != CURSOR_SHOWING) return;

            int cursorX = pci.ptScreenPos.x - left;
            int cursorY = pci.ptScreenPos.y - top;

            if (GetIconInfo(pci.hCursor, out ICONINFO ii))
            {
                cursorX -= ii.xHotspot;
                cursorY -= ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }

            using var g = Graphics.FromImage(target);
            IntPtr hdc = g.GetHdc();
            try { DrawIcon(hdc, cursorX, cursorY, pci.hCursor); }
            finally { g.ReleaseHdc(hdc); }
        }

        /// <summary>
        /// BGRA de 32 bits → BGR de 24 bits, direto no buffer de saída. Antes isso era feito
        /// pedindo Format24bppRgb no LockBits, o que fazia o GDI+ converter o quadro inteiro
        /// a cada captura.
        /// </summary>
        private static unsafe void ConvertBgraToBgr24(Bitmap source, int width, int height, byte[] destination)
        {
            var data = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                fixed (byte* dstBase = destination)
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* src = (byte*)data.Scan0 + (long)y * data.Stride;
                        byte* dst = dstBase + (long)y * width * 3;

                        for (int x = 0; x < width; x++)
                        {
                            dst[0] = src[0]; // B
                            dst[1] = src[1]; // G
                            dst[2] = src[2]; // R
                            src += 4;
                            dst += 3;
                        }
                    }
                }
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        public void Dispose()
        {
            CloseVideo();
        }

        public void ForceKeyFrame() { }
        public bool HasEncodedVideoSubscribers() { return false; }
        public bool IsRestricted { get; } = false;
        public System.Collections.Generic.List<VideoFormat> GetVideoSourceFormats() => new System.Collections.Generic.List<VideoFormat>();
        public void SetVideoSourceFormat(VideoFormat format) { }
        public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat) { }

        public void RestrictFormats(Func<VideoFormat, bool> filter) { }
        public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage sample) { }
        public bool IsVideoSourcePaused() => !_isCapturing;
        public event RawVideoSampleFasterDelegate OnVideoSourceRawSampleFaster = delegate {};
        public event SourceErrorDelegate OnVideoSourceError = delegate {};
    }
}
