using System;
using System.IO;
using System.Text;

namespace XiaoLv.Desktop.Core
{
    public static class Logger
    {
        private static readonly object _lock = new object();

        public static void Info(string msg)
        {
            Write("INFO", msg);
        }

        public static void Error(string msg, Exception ex = null)
        {
            Write("ERROR", msg + (ex != null ? ": " + ex.ToString() : ""));
        }

        private static void Write(string level, string msg)
        {
            try
            {
                lock (_lock)
                {
                    var logDir = Path.Combine(EmbeddedResourceManager.AppDataDir, "logs");
                    if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                    var logFile = Path.Combine(logDir, "xiaolv.log");
                    var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [" + level + "] " + msg + "\r\n";
                    File.AppendAllText(logFile, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
