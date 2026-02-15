// Designer/Services/AutosaveService.cs
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using DaroDesigner.Models;

namespace DaroDesigner.Services
{
    /// <summary>
    /// Autosave service that periodically saves unsaved project changes to a backup file.
    /// </summary>
    public class AutosaveService : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private ProjectModel _project;
        private Func<ProjectData> _getProjectData;
        private bool _disposed;

        // Autosave settings
        public int IntervalMinutes { get; set; } = 5;
        public bool IsEnabled { get; set; } = true;
        public string AutosaveDirectory { get; private set; }

        public event Action<string> OnAutosaveComplete;
        public event Action<Exception> OnAutosaveFailed;

        public AutosaveService()
        {
            AutosaveDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DaroEngine", "autosave");

            if (!Directory.Exists(AutosaveDirectory))
                Directory.CreateDirectory(AutosaveDirectory);

            _timer = new DispatcherTimer();
            _timer.Tick += OnTimerTick;
        }

        /// <summary>
        /// Start autosave monitoring for a project.
        /// </summary>
        /// <param name="project">The project model to monitor.</param>
        /// <param name="getProjectData">Function to get serializable project data.</param>
        public void Start(ProjectModel project, Func<ProjectData> getProjectData)
        {
            _project = project;
            _getProjectData = getProjectData;

            _timer.Interval = TimeSpan.FromMinutes(IntervalMinutes);
            _timer.Start();

            Logger.Info($"Autosave enabled: every {IntervalMinutes} minutes");
        }

        /// <summary>
        /// Stop autosave monitoring.
        /// </summary>
        public void Stop()
        {
            _timer.Stop();
            _project = null;
            _getProjectData = null;
        }

        private async void OnTimerTick(object sender, EventArgs e)
        {
            // Note: async void is required for event handlers, but we must catch all exceptions
            // to prevent unhandled exceptions from crashing the application
            try
            {
                if (!IsEnabled || _project == null || !_project.IsDirty || _getProjectData == null || _disposed)
                    return;

                await SaveBackupAsync();
            }
            catch (Exception ex)
            {
                // Log but don't rethrow - async void exceptions would crash the app
                Logger.Error("Autosave timer tick failed", ex);
            }
        }

        /// <summary>
        /// Manually trigger an autosave.
        /// </summary>
        public async Task SaveBackupAsync()
        {
            // Capture to locals to prevent TOCTOU race with Stop()
            var project = _project;
            var getProjectData = _getProjectData;
            if (project == null || getProjectData == null)
                return;

            try
            {
                var projectData = getProjectData();
                if (projectData == null) return;

                // Generate backup filename
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var projectName = !string.IsNullOrEmpty(project.FilePath)
                    ? Path.GetFileNameWithoutExtension(project.FilePath)
                    : "Untitled";
                var backupPath = Path.Combine(AutosaveDirectory, $"{projectName}_autosave_{timestamp}.daro");

                // Serialize and save atomically (write to temp, then rename)
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(projectData, options);
                var tempPath = backupPath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
                File.Move(tempPath, backupPath, overwrite: true);

                // Clean up old autosaves (keep last 5)
                CleanupOldAutosaves(projectName);

                Logger.Info($"Autosave complete: {backupPath}");
                OnAutosaveComplete?.Invoke(backupPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Autosave failed", ex);
                OnAutosaveFailed?.Invoke(ex);
            }
        }

        private void CleanupOldAutosaves(string projectName)
        {
            try
            {
                var pattern = $"{projectName}_autosave_*.daro";
                var files = Directory.GetFiles(AutosaveDirectory, pattern);

                if (files.Length <= 5) return;

                // Sort by creation time and delete oldest
                Array.Sort(files, (a, b) => File.GetCreationTime(a).CompareTo(File.GetCreationTime(b)));

                for (int i = 0; i < files.Length - 5; i++)
                {
                    File.Delete(files[i]);
                    Logger.Debug($"Deleted old autosave: {files[i]}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to cleanup old autosaves: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the most recent autosave file for a project.
        /// </summary>
        public string GetLatestAutosave(string projectName)
        {
            try
            {
                var pattern = $"{projectName}_autosave_*.daro";
                var files = Directory.GetFiles(AutosaveDirectory, pattern);

                if (files.Length == 0) return null;

                Array.Sort(files, (a, b) => File.GetCreationTime(b).CompareTo(File.GetCreationTime(a)));
                return files[0];
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to get latest autosave for '{projectName}': {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _timer.Tick -= OnTimerTick;
            GC.SuppressFinalize(this);
        }
    }
}
