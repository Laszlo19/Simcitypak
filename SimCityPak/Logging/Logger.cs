using System;
using System.IO;

namespace SimCityPak
{
    /// <summary>
    /// Simple thread-safe file logger. Writes to
    /// %USERPROFILE%\Documents\SimCityPak\simcitypak-yyyyMMdd.log so the app's
    /// behaviour (and especially crashes) can be debugged after the fact.
    ///
    /// Every method swallows its own IO errors — logging must never be the thing
    /// that brings the app down.
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logFile;

        /// <summary>The folder logs are written to (created on first use).</summary>
        public static string LogDirectory { get; private set; }

        static Logger()
        {
            try
            {
                LogDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimCityPak");
                Directory.CreateDirectory(LogDirectory);
                _logFile = Path.Combine(LogDirectory, "simcitypak-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

                string bitness = Environment.Is64BitProcess ? "x64" : "x86";
                string ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
                Write("INFO", "================================================================");
                Write("INFO", string.Format("SimCityPak {0} ({1}) starting — OS {2}", ver, bitness,
                    Environment.OSVersion.VersionString));
            }
            catch { /* never throw from the logger */ }
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Debug(string message) { Write("DEBUG", message); }

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + Environment.NewLine + Describe(ex));
        }

        /// <summary>Logs an exception caught in a try/catch (kept non-fatal).</summary>
        public static void Exception(string context, Exception ex)
        {
            Write("ERROR", "Exception in " + context + ":" + Environment.NewLine + Describe(ex));
        }

        private static string Describe(Exception ex)
        {
            // Full type, message and stack, including inner exceptions.
            return ex == null ? "(null)" : ex.ToString();
        }

        private static void Write(string level, string message)
        {
            try
            {
                if (_logFile == null) return;
                string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                    DateTime.Now, level, message, Environment.NewLine);
                lock (_lock)
                {
                    File.AppendAllText(_logFile, line);
                }
            }
            catch { /* logging must never throw */ }
        }
    }
}
