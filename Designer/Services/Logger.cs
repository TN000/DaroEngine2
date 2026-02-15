// Designer/Services/Logger.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DaroDesigner.Services
{
    /// <summary>
    /// Simple file-based logger for application diagnostics.
    /// Logs to %AppData%/DaroEngine/logs/ with daily rotation.
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static readonly string _logDirectory;
        private static string _currentLogFile;
        private static DateTime _currentLogDate;
        private static StreamWriter _writer;
        private static bool _initialized;

        static Logger()
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DaroEngine", "logs");
        }

        /// <summary>
        /// Initialize the logger. Call once at application startup.
        /// </summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return;

                try
                {
                    if (!Directory.Exists(_logDirectory))
                        Directory.CreateDirectory(_logDirectory);

                    RotateLogFile();
                    CleanupOldLogs();

                    _initialized = true;
                    Info("Logger initialized");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to initialize logger: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Shutdown the logger. Call at application exit.
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                if (!_initialized) return;

                try
                {
                    Info("Logger shutdown");
                    _writer?.Flush();
                    _writer?.Dispose();
                    _writer = null;
                    _initialized = false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Logger] Shutdown error: {ex.Message}");
                }
            }
        }

        /// <summary>Log informational message.</summary>
        public static void Info(string message) => Log("INFO", message);

        /// <summary>Log warning message.</summary>
        public static void Warn(string message) => Log("WARN", message);

        /// <summary>Log error message.</summary>
        public static void Error(string message) => Log("ERROR", message);

        /// <summary>Log error with exception details.</summary>
        public static void Error(string message, Exception ex)
        {
            Log("ERROR", $"{message}: {ex.GetType().Name} - {ex.Message}");
            if (ex.StackTrace != null)
                Log("ERROR", $"  Stack: {ex.StackTrace}");
            if (ex.InnerException != null)
                Log("ERROR", $"  Inner: {ex.InnerException.Message}");
        }

        /// <summary>Log debug message (only in DEBUG builds).</summary>
        [Conditional("DEBUG")]
        public static void Debug(string message) => Log("DEBUG", message);

        private static void Log(string level, string message)
        {
            if (!_initialized) return;

            lock (_lock)
            {
                try
                {
                    // Check for daily rotation
                    if (DateTime.Today != _currentLogDate)
                        RotateLogFile();

                    if (_writer == null) return;

                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var line = $"[{timestamp}] [{level,-5}] {message}";

                    _writer.WriteLine(line);

                    // Flush immediately for errors and warnings to prevent loss on crash
                    if (level == "ERROR" || level == "WARN")
                        _writer.Flush();

                    // Also write to Debug output
                    System.Diagnostics.Debug.WriteLine($"[DaroEngine] {line}");
                }
                catch (Exception ex)
                {
                    // Can't use Logger.Error here (would cause recursion), so use Debug output
                    System.Diagnostics.Debug.WriteLine($"[Logger] Write error: {ex.Message}");
                }
            }
        }

        private static void RotateLogFile()
        {
            _writer?.Dispose();

            _currentLogDate = DateTime.Today;
            _currentLogFile = Path.Combine(_logDirectory,
                $"DaroDesigner_{_currentLogDate:yyyy-MM-dd}.log");

            _writer = new StreamWriter(_currentLogFile, append: true, Encoding.UTF8)
            {
                AutoFlush = false
            };
        }

        private static void CleanupOldLogs()
        {
            // Keep logs for configured retention period
            var cutoff = DateTime.Today.AddDays(-AppConstants.LogRetentionDays);

            foreach (var file in Directory.GetFiles(_logDirectory, AppConstants.LogFilePattern))
            {
                try
                {
                    var fileDate = File.GetCreationTime(file);
                    if (fileDate < cutoff)
                        File.Delete(file);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Logger] Failed to delete old log {file}: {ex.Message}");
                }
            }
        }
    }
}
