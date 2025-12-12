using System;
using System.Runtime.InteropServices;

namespace DS4Updater
{
    internal static class DpiHelper
    {
        private static bool _initialized = false;
        public static void EnablePerMonitorDpiAwareness()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                // Try SetProcessDpiAwarenessContext (Windows 10 1703+)
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return;
            }
            catch { }

            try
            {
                // Fallback to shcore.dll SetProcessDpiAwareness
                SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
            }
            catch { }
        }

        private const int PROCESS_PER_MONITOR_DPI_AWARE = 2;
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);
    }
}
