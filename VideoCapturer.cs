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

        public event RawVideoSampleDelegate OnVideoSourceRawSample;
        public event EncodedSampleDelegate OnVideoSourceEncodedSample;

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
            // 24 FPS approx
            _timer = new System.Threading.Timer(CaptureFrame, null, 0, 41);
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

        public event Action<byte[]> OnJpegFrameReady;

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

                // Ensure even dimensions for I420
                width = width % 2 == 0 ? width : width - 1;
                height = height % 2 == 0 ? height : height - 1;

                using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        if (_captureSource.IsScreen)
                        {
                            g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                        }
                        else
                        {
                            // Clear background in case window doesn't draw fully
                            g.Clear(Color.Black);
                            IntPtr hdc = g.GetHdc();
                            try
                            {
                                // Capture window specifically (ignores overlapping windows)
                                PrintWindow(_captureSource.Hwnd, hdc, PW_RENDERFULLCONTENT);
                            }
                            finally
                            {
                                g.ReleaseHdc(hdc);
                            }
                        }

                        // Draw mouse cursor
                        CURSORINFO pci;
                        pci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                        if (GetCursorInfo(out pci))
                        {
                            if (pci.flags == CURSOR_SHOWING)
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

                                IntPtr hdcCursor = g.GetHdc();
                                try
                                {
                                    DrawIcon(hdcCursor, cursorX, cursorY, pci.hCursor);
                                }
                                finally
                                {
                                    g.ReleaseHdc(hdcCursor);
                                }
                            }
                        }
                    }

                    // Convert to raw bytes to send to SIPSorcery Encoder
                    if (OnVideoSourceRawSample != null)
                    {
                        var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                        int stride = bmpData.Stride;
                        int bytes = Math.Abs(stride) * height;
                        byte[] rgbValues = new byte[bytes];
                        Marshal.Copy(bmpData.Scan0, rgbValues, 0, bytes);
                        bmp.UnlockBits(bmpData);
                        OnVideoSourceRawSample?.Invoke((uint)TimeSpan.FromTicks(DateTime.Now.Ticks).TotalMilliseconds, width, height, rgbValues, VideoPixelFormatsEnum.Bgr);
                    }

                    if (OnJpegFrameReady != null)
                    {
                        using (var ms = new System.IO.MemoryStream())
                        {
                            var jpegEncoder = GetEncoderInfo("image/jpeg");
                            if (jpegEncoder != null)
                            {
                                var encoderParams = new EncoderParameters(1);
                                encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
                                bmp.Save(ms, jpegEncoder, encoderParams);
                            }
                            else
                            {
                                bmp.Save(ms, ImageFormat.Jpeg);
                            }
                            OnJpegFrameReady?.Invoke(ms.ToArray());
                        }
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
        
        private System.Drawing.Imaging.ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            var codecs = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.MimeType == mimeType) return codec;
            }
            return null;
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
        public event RawVideoSampleFasterDelegate OnVideoSourceRawSampleFaster;
        public event SourceErrorDelegate OnVideoSourceError;
    }
}
