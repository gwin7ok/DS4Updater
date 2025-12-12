using System;
using System.Text.Json;

namespace DS4Updater
{
    public static class UpdaterResult
    {
        // 0 == success, non-zero == failure codes
        public static int ExitCode { get; set; } = 0;
        public static string Message { get; set; } = "";
        public static bool IsCiMode { get; set; } = false;

        public static void WriteAndApply()
        {
            try
            {
                if (IsCiMode)
                {
                    var obj = new { exit = ExitCode, message = Message };
                    string json = JsonSerializer.Serialize(obj);
                    Console.Out.WriteLine(json);
                }
            }
            catch { }
            Environment.ExitCode = ExitCode;
        }
    }
}
