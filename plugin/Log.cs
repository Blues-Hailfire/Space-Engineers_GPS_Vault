using System;
using System.IO;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Minimal file logger. Network/game-state errors must never throw back into
    /// the game's Update loop, so every call site catches and routes here instead.
    /// </summary>
    public class Log
    {
        public static Log Instance { get; private set; }

        private readonly string path;
        private readonly object writeLock = new object();

        private Log(string path)
        {
            this.path = path;
        }

        public static void Init()
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "GpsSyncPlugin");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            Instance = new Log(Path.Combine(dir, "gps-sync.log"));
        }

        private void Write(string level, string message)
        {
            var line = $"[{DateTime.UtcNow:O}] [{level}] {message}";
            try
            {
                lock (writeLock)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never crash the plugin.
            }
        }

        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("WARN", message);
        public void Error(string message) => Write("ERROR", message);
    }
}
