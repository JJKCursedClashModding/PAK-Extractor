using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JjkccUnpacker
{
    /// <summary>Tiny INI-style settings store plus the on-disk run log.</summary>
    internal static class AppSettings
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JJKCC-Unpacker");

        private static readonly string SettingsFile = Path.Combine(DataDir, "settings.ini");
        private static readonly string LogsDir = Path.Combine(DataDir, "logs");

        public static string LastInput;
        public static string LastOutput;

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (string raw in File.ReadAllLines(SettingsFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    if (key == "input") LastInput = value;
                    else if (key == "output") LastOutput = value;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var sb = new StringBuilder();
                sb.AppendLine("[paths]");
                sb.AppendLine("input=" + (LastInput ?? string.Empty));
                sb.AppendLine("output=" + (LastOutput ?? string.Empty));
                File.WriteAllText(SettingsFile, sb.ToString());
            }
            catch { }
        }

        /// <summary>Creates a new log file for a run and returns its path (never throws).</summary>
        public static string CreateLogFile()
        {
            try
            {
                Directory.CreateDirectory(LogsDir);
                string name = "import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
                string path = Path.Combine(LogsDir, name);
                File.WriteAllText(path, "JJK CC — Import Game Assets" + Environment.NewLine +
                                        "started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                                        new string('-', 72) + Environment.NewLine);
                return path;
            }
            catch { return null; }
        }
    }

    /// <summary>Writes the message to the console only when a run log file exists.</summary>
    internal sealed class RunLog
    {
        private readonly object _gate = new object();
        private readonly string _path;

        public RunLog(string path) { _path = path; }

        public void Write(string message)
        {
            if (string.IsNullOrEmpty(_path)) return;
            lock (_gate)
            {
                try
                {
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
                }
                catch { }
            }
        }

        public string Path { get { return _path; } }
    }
}
