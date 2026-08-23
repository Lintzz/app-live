using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SIPSorceryMedia.Abstractions;

namespace RadminStreamApp
{
    public class VideoCapturer : IVideoSource, IDisposable
    {
        private bool _isCapturing = false;
        private int _isCapturingFrame = 0;
        private System.Threading.Timer _timer;
        private CaptureSource _captureSource;

        private int _maxWidth = 1920;
        private int _maxHeight = 1080;
        private bool _isMaxPerformance = true;

        public void SetMaxPerformanceMode(bool isMaxPerformance)
        {
            _isMaxPerformance = isMaxPerformance;
        }

        public void SetResolution(int width, int height)
        {
            _maxWidth = width;
            _maxHeight = height;
        }

        public event RawVideoSampleDelegate OnVideoSourceRawSample;
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

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        private const uint PW_RENDERFULLCONTENT = 2;

        public void SetTargetSource(CaptureSource source)
        {
            _captureSource = source;
        }

        public Task StartVideo()
        {
            _isCapturing = true;
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
            return Task.CompletedTask;
        }


        private void CaptureFrame(object state)
        {
            if (!_isCapturing || _captureSource == null) return;
            if (!_captureSource.IsScreen && _captureSource.Hwnd == IntPtr.Zero) return;

            if (System.Threading.Interlocked.CompareExchange(ref _isCapturingFrame, 1, 0) != 0)
            {
                return; // Já existe um frame sendo capturado/enviado, ignora esse
            }

            try
            {
                int left = 0, top = 0, width = 0, height = 0;

                if (_captureSource.IsScreen)
                {
                    left = _captureSource.ScreenBounds.Left;
                    top = _captureSource.ScreenBounds.Top;
                    width = _captureSource.ScreenBounds.Width;
                    height = _captureSource.ScreenBounds.Height;
                }
                else
                {
                    GetWindowRect(_captureSource.Hwnd, out RECT rect);
                    left = rect.Left;
                    top = rect.Top;
                    width = rect.Right - rect.Left;
                    height = rect.Bottom - rect.Top;
                }

                if (width <= 0 || height <= 0) return;

                // Scale down if larger than max resolution
                int maxWidth = _maxWidth;
                int maxHeight = _maxHeight;
                
                float scale = 1.0f;
                if (width > maxWidth || height > maxHeight)
                {
                    float scaleX = (float)maxWidth / width;
                    float scaleY = (float)maxHeight / height;
                    scale = Math.Min(scaleX, scaleY);
                }

                int scaledWidth = (int)(width * scale);
                int scaledHeight = (int)(height * scale);

                // Ensure dimensions are multiples of 4 to avoid stride padding
                scaledWidth = scaledWidth - (scaledWidth % 4);
                scaledHeight = scaledHeight - (scaledHeight % 4);

                using (var fullBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var gFull = Graphics.FromImage(fullBmp))
                    {
                        if (_captureSource.IsScreen)
                        {
                            gFull.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                        }
                        else
                        {
                            gFull.Clear(Color.Black);
                            IntPtr hdc = gFull.GetHdc();
                            try { PrintWindow(_captureSource.Hwnd, hdc, PW_RENDERFULLCONTENT); }
                            finally { gFull.ReleaseHdc(hdc); }
                        }

                        // Draw mouse cursor
                        CURSORINFO pci;
                        pci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                        if (GetCursorInfo(out pci) && pci.flags == CURSOR_SHOWING)
                        {
                            int cursorX = pci.ptScreenPos.x - left;
                            int cursorY = pci.ptScreenPos.y - top;
                            ICONINFO ii;
                            if (GetIconInfo(pci.hCursor, out ii))
                            {
                                cursorX -= ii.xHotspot;
                                cursorY -= ii.yHotspot;
                                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                            }
                            IntPtr hdcCursor = gFull.GetHdc();
                            try { DrawIcon(hdcCursor, cursorX, cursorY, pci.hCursor); }
                            finally { gFull.ReleaseHdc(hdcCursor); }
                        }
                    }

                    Bitmap finalBmp = fullBmp;
                    if (scale < 1.0f)
                    {
                        finalBmp = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
                        using (var gScale = Graphics.FromImage(finalBmp))
                        {
                            gScale.InterpolationMode = _isMaxPerformance 
                                ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor 
                                : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                            gScale.DrawImage(fullBmp, 0, 0, scaledWidth, scaledHeight);
                        }
                    }

                    if (OnVideoSourceRawSample != null)
                    {
                        var bmpData = finalBmp.LockBits(new Rectangle(0, 0, scaledWidth, scaledHeight), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                        int stride = bmpData.Stride;
                        int bytes = Math.Abs(stride) * scaledHeight;
                        byte[] rgbValues = new byte[bytes];
                        Marshal.Copy(bmpData.Scan0, rgbValues, 0, bytes);
                        finalBmp.UnlockBits(bmpData);
                        OnVideoSourceRawSample?.Invoke(16, scaledWidth, scaledHeight, rgbValues, VideoPixelFormatsEnum.Bgr);
                    }

                    if (scale < 1.0f)
                    {
                        finalBmp.Dispose();
                    }
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
