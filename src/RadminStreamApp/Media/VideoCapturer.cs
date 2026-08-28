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
    /// <remarks>
    /// Não implementa <c>IVideoSource</c>: o quadro é entregue à mão ao encoder pelo
    /// StreamManager, nunca através da interface. Enquanto ela existiu, arrastava uma dúzia
    /// de membros stub que ninguém chamava.
    /// </remarks>
    public class VideoCapturer : IDisposable
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
        // Contextos GDI+ vivem junto dos bitmaps. Antes cada quadro criava (e descartava) um
        // Graphics para o cursor, outro para o BitBlt e outro para a escala — três alocações
        // de contexto GDI+ por quadro, 60x por segundo, na thread de captura.
        private Graphics? _captureGraphics;
        private Graphics? _gdiGraphics;
        private Graphics? _scaledGraphics;
        // Anel de buffers de saída. O encoder consome o quadro numa Task separada e a UI
        // copia o mesmo array no dispatcher — com um único buffer, a captura seguinte
        // sobrescrevia os bytes embaixo de ambos, corrompendo o vídeo transmitido.
        private const int OutputBufferCount = 3;

        /// <summary>O quadro sai em BGRA de 32 bits — o formato que o DXGI entrega.</summary>
        public const int BytesPerPixel = 4;

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

        // O DXGI só entrega quadro quando a imagem muda, então com a tela parada o
        // capturador reemite o último. Este é o piso do ritmo dessa reemissão — e, na
        // prática, o piso de fps da transmissão inteira.
        //
        // Ele já foi 300ms, para poupar CPU: a transmissão caía para ~9 fps com a tela
        // parada e dava a impressão de ter travado. Igualado à cadência da captura, o
        // fluxo fica constante. Medido, tela parada, 1080p:
        //
        //   300ms -> 8,7 fps a 5,5% de um núcleo
        //    16ms -> 33,3 fps a 17,6% de um núcleo
        //
        // O caminho GDI, que nunca teve freio, dava 30,8 fps a 42,7% — ou seja, isto entrega
        // mais quadros do que ele por menos da metade da CPU. E o custo só existe com a tela
        // parada: com o conteúdo mudando, o DXGI entrega quadro real e a reemissão nem roda.
        private static readonly TimeSpan MinimumEmitInterval = TimeSpan.FromMilliseconds(16);

        private TimeSpan _lastEmit = TimeSpan.MinValue;
        private bool _hasRealFrame;   // já veio pelo menos um quadro de verdade da tela

        // Pixels sob o cursor, guardados entre o desenho e a restauração. Reaproveitado:
        // são poucos KB, mas seriam alocados 30x por segundo.
        private byte[] _cursorPatch = Array.Empty<byte>();

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

        public void StartVideo()
        {
            _isCapturing = true;
            _lastFrameTicks = _frameClock.ElapsedTicks;
            // 60 FPS approx (16ms) - Mais fluido
            _timer = new System.Threading.Timer(CaptureFrame, null, 0, 16);
        }

        public void CloseVideo()
        {
            _isCapturing = false;
            _timer?.Dispose();
            _timer = null;
            ReleaseBuffers();
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

            _captureGraphics = Graphics.FromImage(_captureBitmap);
            _gdiGraphics = Graphics.FromImage(_gdiBitmap);
        }

        private void ReleaseBuffers()
        {
            lock (_bufferLock)
            {
                // Os contextos saem antes dos bitmaps que eles desenham.
                _captureGraphics?.Dispose();
                _captureGraphics = null;
                _gdiGraphics?.Dispose();
                _gdiGraphics = null;
                _scaledGraphics?.Dispose();
                _scaledGraphics = null;

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

            Bitmap source;
            Graphics sourceGraphics;

            if (!_duplicationUnavailable && TryCaptureWithDuplication(bounds))
            {
                source = _captureBitmap!;
                sourceGraphics = _captureGraphics!;
                _hasRealFrame = true;
            }
            else if (_duplicationUnavailable)
            {
                if (!CaptureWithGdi(left, top, width, height)) return;
                source = _gdiBitmap!;
                sourceGraphics = _gdiGraphics!;
                _hasRealFrame = true;
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
                if (_lastEmit != TimeSpan.MinValue && !ShouldReemit(_frameClock.Elapsed - _lastEmit)) return;

                source = _captureBitmap!;
                sourceGraphics = _captureGraphics!;
            }

            // O cursor entra em TODO quadro emitido, inclusive nas reemissões — é o que faz o
            // mouse andar liso sobre uma tela parada.
            //
            // O buffer do DXGI só é reescrito quando há imagem nova, então desenhar nele
            // deixaria um rastro de cursores acumulados nas reemissões. Por isso o retângulo
            // sob o cursor é guardado antes e devolvido depois: o buffer volta intacto e a
            // próxima reemissão parte de novo da imagem limpa.
            // Um único instantâneo do cursor por quadro. Guardar a área e desenhar
            // consultavam a posição em chamadas separadas; entre as duas o mouse podia andar,
            // e aí o cursor era desenhado fora do retângulo que seria devolvido — sobrando
            // para sempre no buffer. Com a reemissão agora a cada quadro, isso viraria rastro.
            bool hasCursor = TryGetCursor(out var cursor);

            bool restoreCursorArea = hasCursor && ReferenceEquals(source, _captureBitmap) && _captureBuffer != null;
            Rectangle savedRect = Rectangle.Empty;
            if (restoreCursorArea) restoreCursorArea = TrySaveUnderCursor(cursor, left, top, out savedRect);

            try
            {
                if (hasCursor) DrawCursor(sourceGraphics, cursor, left, top);
                ComposeAndEmit(source, width, height);
            }
            finally
            {
                if (restoreCursorArea) RestoreUnderCursor(savedRect);
            }
        }

        /// <summary>
        /// Escala se preciso, copia para o buffer de saída e entrega o quadro. Separado do
        /// <see cref="CaptureFrameCore"/> para o retângulo do cursor poder ser devolvido num
        /// finally, sem que os vários returns daqui pulem a restauração.
        /// </summary>
        private void ComposeAndEmit(Bitmap source, int width, int height)
        {
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
                    _scaledGraphics?.Dispose();
                    _scaledBitmap?.Dispose();
                    _scaledBitmap = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppRgb);
                    _scaledGraphics = Graphics.FromImage(_scaledBitmap);
                }

                _scaledGraphics!.InterpolationMode = _isMaxPerformance
                    ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                    : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                _scaledGraphics.DrawImage(source, 0, 0, outWidth, outHeight);
                finalBitmap = _scaledBitmap;
            }

            var handler = OnVideoSourceRawSample;
            if (handler == null) return;

            int outputSize = outWidth * outHeight * BytesPerPixel;

            _outputBufferIndex = (_outputBufferIndex + 1) % OutputBufferCount;
            var output = _outputBuffers[_outputBufferIndex];
            if (output == null || output.Length != outputSize)
            {
                output = new byte[outputSize];
                _outputBuffers[_outputBufferIndex] = output;
            }

            CopyBgra32(finalBitmap, outWidth, outHeight, output);

            // Duração real desde o quadro anterior, em milissegundos.
            long nowTicks = _frameClock.ElapsedTicks;
            uint durationMs = (uint)Math.Clamp(
                (nowTicks - _lastFrameTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency, 1, 1000);
            _lastFrameTicks = nowTicks;

            _lastEmit = _frameClock.Elapsed;
            handler(durationMs, outWidth, outHeight, output, VideoPixelFormatsEnum.Bgra);
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

            var frame = _duplication.TryGetFrame(_captureBuffer!, _bufferWidth * 4, _bufferHeight);
            int timeouts = frame == DuplicationFrame.Timeout ? _duplicationFailures + 1 : 0;

            switch (DecideDuplicationAction(frame, timeouts, _hasRealFrame))
            {
                case DuplicationAction.Use:
                    _duplicationFailures = 0;
                    return true;

                case DuplicationAction.Recreate:
                    System.Diagnostics.Debug.WriteLine("[Capture] Duplicação perdida; recriando.");
                    _duplication.Dispose();
                    _duplication = null;
                    _duplicationFailures = 0;
                    return false;

                case DuplicationAction.FallBackToGdi:
                    System.Diagnostics.Debug.WriteLine("[Capture] Duplicação sem quadros; voltando para GDI.");
                    _duplication.Dispose();
                    _duplication = null;
                    _duplicationUnavailable = true;
                    return false;

                default: // Retry
                    _duplicationFailures = timeouts;
                    return false;
            }
        }

        /// <summary>O que fazer depois de uma tentativa de pegar quadro do DXGI.</summary>
        internal enum DuplicationAction
        {
            /// <summary>Veio quadro novo: usar.</summary>
            Use,

            /// <summary>Nada mudou; tentar de novo no próximo tique.</summary>
            Retry,

            /// <summary>Duplicação morta: descartar e criar outra.</summary>
            Recreate,

            /// <summary>A duplicação nunca entregou nada; o GDI assume de vez.</summary>
            FallBackToGdi
        }

        /// <summary>
        /// Decide o destino de cada tentativa. Isolado e testável porque foi exatamente aqui
        /// que a transmissão congelava: perda e timeout chegavam como o mesmo <c>false</c>, e
        /// a recuperação estava presa a <c>!hasRealFrame</c> — depois do primeiro quadro, uma
        /// duplicação perdida nunca mais era recriada e a live ficava travada no último quadro.
        /// </summary>
        internal static DuplicationAction DecideDuplicationAction(
            DuplicationFrame frame, int consecutiveTimeouts, bool hasRealFrame)
        {
            if (frame == DuplicationFrame.Frame) return DuplicationAction.Use;

            // Perda é sempre recuperável recriando, tenha ou não vindo quadro antes.
            if (frame == DuplicationFrame.Lost) return DuplicationAction.Recreate;

            // Timeout: normal com a tela parada. Só vira desistência se NUNCA veio quadro —
            // aí a duplicação existe mas não funciona nesta máquina.
            return consecutiveTimeouts >= DuplicationFailureLimit && !hasRealFrame
                ? DuplicationAction.FallBackToGdi
                : DuplicationAction.Retry;
        }

        private bool CaptureWithGdi(int left, int top, int width, int height)
        {
            if (_gdiGraphics == null) return false;

            _gdiGraphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return true;
        }

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXCURSOR = 13;
        private const int SM_CYCURSOR = 14;

        /// <summary>
        /// Instantâneo do cursor: posição e handle do ícone juntos. Falso quando ele está
        /// escondido — nesse caso o quadro sai sem cursor.
        /// </summary>
        private static bool TryGetCursor(out CURSORINFO cursor)
        {
            cursor = default;
            cursor.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            return GetCursorInfo(out cursor) && cursor.flags == CURSOR_SHOWING;
        }

        /// <summary>
        /// Vale reemitir o último quadro agora? O piso é a própria cadência da captura, então
        /// na prática toda passagem sem quadro novo reemite — é o que mantém o fps constante
        /// com a tela parada, em vez de despencar.
        /// </summary>
        internal static bool ShouldReemit(TimeSpan sinceLastEmit)
            => sinceLastEmit >= MinimumEmitInterval;

        /// <summary>
        /// Guarda os pixels que o cursor vai cobrir, para devolvê-los depois de o quadro sair.
        /// Sem isso, cada reemissão empilharia mais um cursor no buffer do DXGI.
        /// </summary>
        private bool TrySaveUnderCursor(CURSORINFO cursor, int left, int top, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (_captureBuffer == null) return false;

            // Uma margem generosa em volta do ponto: cobre o hotspot e cursores grandes
            // (lupa, acessibilidade) sem depender de medir o ícone a cada quadro.
            int w = Math.Max(GetSystemMetrics(SM_CXCURSOR), 32) * 2;
            int h = Math.Max(GetSystemMetrics(SM_CYCURSOR), 32) * 2;

            var candidate = new Rectangle(
                cursor.ptScreenPos.x - left - w / 2,
                cursor.ptScreenPos.y - top - h / 2, w, h);
            candidate.Intersect(new Rectangle(0, 0, _bufferWidth, _bufferHeight));
            if (candidate.Width <= 0 || candidate.Height <= 0) return false;

            int needed = candidate.Width * candidate.Height * BytesPerPixel;
            if (_cursorPatch.Length < needed) _cursorPatch = new byte[needed];

            CopyRect(_captureBuffer, _bufferWidth, candidate, _cursorPatch, toPatch: true);
            rect = candidate;
            return true;
        }

        private void RestoreUnderCursor(Rectangle rect)
        {
            if (_captureBuffer == null || rect.Width <= 0) return;
            CopyRect(_captureBuffer, _bufferWidth, rect, _cursorPatch, toPatch: false);
        }

        /// <summary>
        /// Move um retângulo entre o buffer de captura e o guardado, nos dois sentidos.
        /// Estático e testável de propósito: é aritmética de stride, e um deslize aqui não
        /// quebra o build — corrompe a imagem transmitida.
        /// </summary>
        internal static void CopyRect(byte[] buffer, int bufferWidth, Rectangle rect, byte[] patch, bool toPatch)
        {
            int stride = bufferWidth * BytesPerPixel;
            int rowBytes = rect.Width * BytesPerPixel;

            for (int y = 0; y < rect.Height; y++)
            {
                int bufferOffset = (rect.Y + y) * stride + rect.X * BytesPerPixel;
                int patchOffset = y * rowBytes;

                if (toPatch) Buffer.BlockCopy(buffer, bufferOffset, patch, patchOffset, rowBytes);
                else Buffer.BlockCopy(patch, patchOffset, buffer, bufferOffset, rowBytes);
            }
        }

        private static void DrawCursor(Graphics g, CURSORINFO pci, int left, int top)
        {
            int cursorX = pci.ptScreenPos.x - left;
            int cursorY = pci.ptScreenPos.y - top;

            if (GetIconInfo(pci.hCursor, out ICONINFO ii))
            {
                cursorX -= ii.xHotspot;
                cursorY -= ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }

            IntPtr hdc = g.GetHdc();
            try { DrawIcon(hdc, cursorX, cursorY, pci.hCursor); }
            finally { g.ReleaseHdc(hdc); }
        }

        /// <summary>
        /// Copia o quadro BGRA de 32 bits para o buffer de saída, sem tocar nos pixels.
        ///
        /// Aqui havia uma conversão BGRA→BGR24 escrita à mão, byte a byte. Ela era redundante:
        /// o encoder recebe o formato declarado e converte para I420 com o swscale do FFmpeg,
        /// então entregar BGRA (o formato que o DXGI já produz) tira uma etapa do caminho.
        ///
        /// Medido nesta máquina, 1920x1080, 300 quadros por caminho:
        ///   antigo   7,53 ms de encode + 3,63 ms de conversão = 11,17 ms/quadro (89,6 fps)
        ///   atual   10,28 ms de encode                        = 10,28 ms/quadro (97,2 fps)
        /// São ~8% — real, mas modesto: o swscale gasta um pouco mais partindo de BGRA do que
        /// de BGR24, e isso come boa parte do que a conversão manual custava. O ganho maior é
        /// de qualidade: uma conversão a menos arredonda menos (PSNR contra a imagem original
        /// 45,8 dB, contra 41,2 dB do caminho antigo).
        ///
        /// O que sobra é uma cópia linear: uma única memcpy quando não há padding de stride,
        /// que é o caso normal em 32 bits, e linha a linha quando há.
        /// </summary>
        private static unsafe void CopyBgra32(Bitmap source, int width, int height, byte[] destination)
        {
            var data = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                long rowBytes = (long)width * BytesPerPixel;

                fixed (byte* dstBase = destination)
                {
                    if (data.Stride == rowBytes)
                    {
                        Buffer.MemoryCopy((byte*)data.Scan0, dstBase, destination.Length, rowBytes * height);
                        return;
                    }

                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            (byte*)data.Scan0 + (long)y * data.Stride,
                            dstBase + y * rowBytes,
                            rowBytes,
                            rowBytes);
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

    }
}
