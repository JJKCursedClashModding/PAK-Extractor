using System;
using System.Runtime.InteropServices;

namespace JjkccUnpacker
{
    /// <summary>
    /// DPI helpers. WinForms' own autoscaling is unreliable for fully code-built forms, so the
    /// layout is scaled by hand from the real monitor DPI queried here.
    /// </summary>
    internal static class Dpi
    {
        private const int LogPixelsX = 88;
        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
        [DllImport("user32.dll")] private static extern int GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        /// <summary>
        /// Makes the process DPI aware. Harmless when the manifest already declared awareness
        /// (the call then fails and is ignored).
        /// </summary>
        public static void EnableAwareness()
        {
            try { SetProcessDpiAwarenessContext(PerMonitorAwareV2); }
            catch { }
        }

        /// <summary>DPI of the monitor the window is on, falling back to the system DPI, then 96.</summary>
        public static int ForWindow(IntPtr handle)
        {
            try
            {
                if (handle != IntPtr.Zero)
                {
                    int dpi = GetDpiForWindow(handle);
                    if (dpi > 0) return dpi;
                }
            }
            catch { }
            return System();
        }

        public static int System()
        {
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    try
                    {
                        int dpi = GetDeviceCaps(dc, LogPixelsX);
                        if (dpi > 0) return dpi;
                    }
                    finally { ReleaseDC(IntPtr.Zero, dc); }
                }
            }
            catch { }
            return 96;
        }
    }
}
