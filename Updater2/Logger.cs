using System;
using System.IO;
using System.Text;

namespace DS4Updater
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logPath = Path.Combine(Path.GetTempPath(), "DS4Updater.log");

        public static string LogPath => _logPath;

        // Ensure all file logs go through this method. Prefixes local timestamp [yyyy/MM/dd HH:mm:ss]
        public static void Log(string message)
        {
            try
            {
                string ts = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
                string line = $"[{ts}] {message}" + Environment.NewLine;
                lock (_lock)
                {
                    File.AppendAllText(_logPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Best-effort logging; swallowing exceptions to avoid affecting app flow
            }
        }

        public static void LogException(Exception ex, string context = "")
        {
            try
            {
                string ts = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
                string header = string.IsNullOrEmpty(context) ? "EXCEPTION" : $"EXCEPTION ({context})";
                string line = $"[{ts}] {header}: {ex.GetType().FullName}: {ex.Message} | Stack: {ex.StackTrace}" + Environment.NewLine;
                lock (_lock)
                {
                    File.AppendAllText(_logPath, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
