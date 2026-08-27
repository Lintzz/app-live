using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RadminStreamApp
{
    public class CaptureSource
    {
        public string Title { get; set; } = string.Empty;
        public System.Drawing.Rectangle ScreenBounds { get; set; }

        public override string ToString() => Title;
    }

    public static class WindowHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        /// <summary>
        /// Lista os monitores disponíveis para captura. A transmissão é sempre de tela inteira;
        /// captura de janelas individuais não é suportada.
        /// </summary>
        public static List<CaptureSource> GetCapturableScreens()
        {
            var sources = new List<CaptureSource>();

            int screenIndex = 1;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
                {
                    sources.Add(new CaptureSource
                    {
                        Title = $"Tela {screenIndex}",
                        ScreenBounds = new System.Drawing.Rectangle(
                            lprcMonitor.left,
                            lprcMonitor.top,
                            lprcMonitor.right - lprcMonitor.left,
                            lprcMonitor.bottom - lprcMonitor.top)
                    });
                    screenIndex++;
                    return true;
                }, IntPtr.Zero);

            return sources;
        }
    }
}
