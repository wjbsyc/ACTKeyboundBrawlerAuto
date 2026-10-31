using System;
using System.IO;
using System.Text;

namespace ACTKeyboundBrawlerAuto
{
    public class BrawlerLogger : IDisposable
    {
        private StreamWriter _writer;
        private readonly object _lock = new object();

        public string LogFilePath { get; private set; }

        public void Open(string logDirectory)
        {
            Close();

            if (!Directory.Exists(logDirectory))
                Directory.CreateDirectory(logDirectory);

            string fileName = "ACTKeyboundBrawlerAuto_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log";
            LogFilePath = Path.Combine(logDirectory, fileName);
            _writer = new StreamWriter(LogFilePath, false, Encoding.UTF8) { AutoFlush = true };

            WriteLine("INFO", "ACTKeyboundBrawlerAuto 独立日志 - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            WriteLine("INFO", "协议: 0x01E6/10000008=词下发; 0x01E6/1000000A=词消失; 0x00BD/C16C-C170=打字回执");
            WriteLine("INFO", new string('-', 100));
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_writer == null) return;
                try { _writer.Flush(); _writer.Close(); }
                catch { }
                _writer = null;
            }
        }

        public void WriteLine(string tag, string message)
        {
            lock (_lock)
            {
                if (_writer == null) return;
                try
                {
                    _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "|" + tag + "|" + message);
                }
                catch { }
            }
        }

        public void WriteRaw(string rawLine)
        {
            WriteLine("RAW", rawLine);
        }

        public void Dispose()
        {
            Close();
        }
    }
}
