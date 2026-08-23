using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RadminStreamApp
{
    public class CaptureSource
    {
        public bool IsScreen { get; set; }
        public string Title { get; set; }
        public IntPtr Hwnd { get; set; }
        public uint ProcessId { get; set; }
        public System.Drawing.Rectangle ScreenBounds { get; set; }
    }

    public static class WindowHelper
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        private const int DWMWA_CLOAKED = 14;
        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }
        private static bool IsWindowCloaked(IntPtr hWnd)
        {
            int isCloaked = 0;
            int hresult = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out isCloaked, sizeof(int));
            if (hresult == 0)
            {
                return isCloaked != 0;
            }
            return false;
        }

        public static List<CaptureSource> GetCapturableWindows()
        {
            var sources = new List<CaptureSource>();

            // 1. Add Monitors (Screens)
            int screenIndex = 1;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
                {
                    sources.Add(new CaptureSource
                    {
                        IsScreen = true,
                        Title = $"Tela {screenIndex}",
                        ScreenBounds = new System.Drawing.Rectangle(lprcMonitor.left, lprcMonitor.top, lprcMonitor.right - lprcMonitor.left, lprcMonitor.bottom - lprcMonitor.top)
                    });
                    screenIndex++;
                    return true;
                }, IntPtr.Zero);

            return sources;
        }
    }
}
