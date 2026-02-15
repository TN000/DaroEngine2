using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using DaroDesigner.Models;

namespace DaroDesigner.Services
{
    /// <summary>
    /// Database record for a playlist item stored by Graphics Middleware.
    /// </summary>
    public class StoredPlaylistItem
    {
        /// <summary>
        /// Current schema version expected by this code.
        /// </summary>
        public const int ExpectedSchemaVersion = 1;

        public string Id { get; set; }
        public string Name { get; set; }
        public string TemplateId { get; set; }
        public string TemplateName { get; set; }
        public string TemplateFilePath { get; set; }
        public string LinkedScenePath { get; set; }
        public string FilledDataJson { get; set; }
        public string TakesJson { get; set; }
        public string CreatedAt { get; set; }

        /// <summary>
        /// Parses the filled data JSON into a dictionary.
        /// </summary>
        public Dictionary<string, string> GetFilledData()
        {
            if (string.IsNullOrEmpty(FilledDataJson))
                return new Dictionary<string, string>();

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(FilledDataJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = AppConstants.MaxJsonDepth })
                    ?? new Dictionary<string, string>();
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StoredPlaylistItem] Failed to parse FilledDataJson: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Parses the takes JSON into a collection of TemplateTakeModel.
        /// Returns null if TakesJson is null/empty or parsing fails.
        /// </summary>
        public ObservableCollection<TemplateTakeModel> GetTakes(out string errorMessage)
        {
            errorMessage = null;

            if (string.IsNullOrEmpty(TakesJson))
            {
                return null;
            }

            try
            {
                var list = JsonSerializer.Deserialize<List<TemplateTakeModel>>(TakesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = AppConstants.MaxJsonDepth });

                return list != null ? new ObservableCollection<TemplateTakeModel>(list) : null;
            }
            catch (JsonException ex)
            {
                errorMessage = $"Failed to parse TakesJson: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Returns true if TakesJson contains valid data.
        /// </summary>
        public bool HasTakesJson => !string.IsNullOrEmpty(TakesJson);
    }

    /// <summary>
    /// Service for reading playlist items from the shared SQLite database.
    /// The database is created and populated by Graphics Middleware.
    /// </summary>
    public sealed class PlaylistDatabase
    {
        private readonly string _databasePath;
        private readonly int _connectionTimeoutSeconds;
        private volatile bool _isAvailable; // volatile for thread-safe availability checks
        private int? _schemaVersion;

        // Security: Allowed base directories for template/scene file access
        private static readonly Lazy<string[]> AllowedBasePaths = new Lazy<string[]>(() => new[]
        {
            // User's Documents folder
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DaroEngine")),
            // Application directory
            Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),
            // Common app data
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DaroEngine")),
            // Local app data
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaroEngine"))
        });

        /// <summary>
        /// Default connection timeout in seconds.
        /// </summary>
        public const int DefaultConnectionTimeoutSeconds = 30;

        /// <summary>
        /// Event raised for logging.
        /// </summary>
        public event EventHandler<string> LogMessage;

        public bool IsAvailable => _isAvailable;
        public string DatabasePath => _databasePath;

        /// <summary>
        /// The schema version of the database, or null if not yet read.
        /// </summary>
        public int? SchemaVersion => _schemaVersion;

        /// <summary>
        /// Creates a new PlaylistDatabase instance.
        /// </summary>
        /// <param name="databasePath">Path to SQLite database file. If null, uses default location.</param>
        /// <param name="connectionTimeoutSeconds">Connection timeout in seconds.</param>
        public PlaylistDatabase(string databasePath = null, int connectionTimeoutSeconds = DefaultConnectionTimeoutSeconds)
        {
            // Default: same location as Graphics Middleware
            _databasePath = databasePath ?? GetDefaultDatabasePath();
            _connectionTimeoutSeconds = connectionTimeoutSeconds;

            CheckAvailability();
        }

        private string BuildConnectionString(SqliteOpenMode mode)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = mode,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = _connectionTimeoutSeconds
            }.ToString();
        }

        private static string GetDefaultDatabasePath()
        {
            // Graphics Middleware runs from its own folder, database is relative to it
            // Try several locations (including _dev suffix for development):
            var candidates = new[]
            {
                // GraphicsMiddleware project folder (development) - with _dev suffix
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GraphicsMiddleware", "graphics_middleware_dev.db"),
                // GraphicsMiddleware project folder (development) - without suffix
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GraphicsMiddleware", "graphics_middleware.db"),
                // Same folder as Designer exe
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "graphics_middleware.db"),
                // User's Documents folder
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DaroEngine", "graphics_middleware.db"),
                // GraphicsMiddleware bin folder
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GraphicsMiddleware", "bin", "Debug", "net9.0", "graphics_middleware.db")
            };

            foreach (var path in candidates)
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                    return fullPath;
            }

            // Default to Documents folder
            var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DaroEngine", "graphics_middleware.db");

            // Ensure directory exists
            var dir = Path.GetDirectoryName(defaultPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return defaultPath;
        }

        /// <summary>
        /// Checks if the database file exists (quick sync check).
        /// Call CheckAvailabilityAsync for full validation including schema.
        /// </summary>
        public void CheckAvailability()
        {
            _isAvailable = File.Exists(_databasePath);
            if (_isAvailable)
            {
                Log($"Database found: {_databasePath}");
            }
            else
            {
                Log($"Database not found: {_databasePath}");
            }
        }

        /// <summary>
        /// Asynchronously checks if the database is available and validates schema.
        /// This should be called after construction for full validation.
        /// </summary>
        public async Task CheckAvailabilityAsync()
        {
            _isAvailable = File.Exists(_databasePath);
            if (_isAvailable)
            {
                Log($"Database found: {_databasePath}");

                // Validate schema version
                try
                {
                    await ValidateSchemaVersionAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"Schema validation failed: {ex.Message}");
                    _isAvailable = false;
                }
            }
            else
            {
                Log($"Database not found: {_databasePath}");
            }
        }

        /// <summary>
        /// Validates that the database schema version is compatible.
        /// </summary>
        private async Task ValidateSchemaVersionAsync()
        {
            using (var connection = new SqliteConnection(BuildConnectionString(SqliteOpenMode.ReadOnly)))
            {
                await connection.OpenAsync().ConfigureAwait(false);

                // Check if SchemaInfo table exists
                const string checkTableSql = @"
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type='table' AND name='SchemaInfo'";

                var tableExists = await connection.ExecuteScalarAsync<int>(checkTableSql).ConfigureAwait(false) > 0;

                if (!tableExists)
                {
                    // Older database without schema versioning - assume version 1
                    _schemaVersion = 1;
                    Log("Database has no SchemaInfo table, assuming schema version 1");
                    return;
                }

                // Read schema version
                const string versionSql = "SELECT Version FROM SchemaInfo ORDER BY Version DESC LIMIT 1";
                _schemaVersion = await connection.ExecuteScalarAsync<int?>(versionSql).ConfigureAwait(false);

                if (_schemaVersion == null)
                {
                    _schemaVersion = 1;
                    Log("SchemaInfo table is empty, assuming schema version 1");
                    return;
                }

                Log($"Database schema version: {_schemaVersion}");

                if (_schemaVersion > StoredPlaylistItem.ExpectedSchemaVersion)
                {
                    throw new InvalidOperationException(
                        $"Database schema version {_schemaVersion} is newer than expected version {StoredPlaylistItem.ExpectedSchemaVersion}. " +
                        "Please update the Designer application.");
                }
            }
        }

        /// <summary>
        /// Gets a playlist item by its ID (GUID).
        /// </summary>
        public async Task<StoredPlaylistItem> GetItemByIdAsync(string itemId)
        {
            if (!_isAvailable)
            {
                CheckAvailability();
                if (!_isAvailable)
                {
                    Log("Database not available");
                    return null;
                }
            }

            try
            {
                using (var connection = new SqliteConnection(BuildConnectionString(SqliteOpenMode.ReadOnly)))
                {
                    await connection.OpenAsync().ConfigureAwait(false);

                    const string sql = @"
                        SELECT Id, Name, TemplateId, TemplateName, TemplateFilePath,
                               LinkedScenePath, FilledDataJson, TakesJson, CreatedAt
                        FROM PlaylistItems
                        WHERE Id = @Id";

                    var item = await connection.QuerySingleOrDefaultAsync<StoredPlaylistItem>(sql, new { Id = itemId }).ConfigureAwait(false);

                    if (item != null)
                    {
                        Log($"Loaded item: {item.Id} ({item.Name})");
                    }
                    else
                    {
                        Log($"Item not found: {itemId}");
                    }

                    return item;
                }
            }
            catch (SqliteException ex)
            {
                Log($"SQLite error loading item {itemId}: {ex.Message} (ErrorCode: {ex.SqliteErrorCode})");
                return null;
            }
            catch (Exception ex)
            {
                Log($"Error loading item {itemId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets a playlist item by ID synchronously.
        /// WARNING: Prefer GetItemByIdAsync to avoid UI thread blocking.
        /// This method uses Task.Run to prevent deadlocks on UI thread.
        /// </summary>
        public StoredPlaylistItem GetItemById(string itemId)
        {
            // Use Task.Run to execute on thread pool, avoiding UI thread deadlock
            return Task.Run(() => GetItemByIdAsync(itemId)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Gets all playlist items from the database.
        /// </summary>
        public async Task<IEnumerable<StoredPlaylistItem>> GetAllItemsAsync(int limit = 100)
        {
            if (!_isAvailable)
            {
                CheckAvailability();
                if (!_isAvailable)
                {
                    Log("Database not available");
                    return Array.Empty<StoredPlaylistItem>();
                }
            }

            try
            {
                using (var connection = new SqliteConnection(BuildConnectionString(SqliteOpenMode.ReadOnly)))
                {
                    await connection.OpenAsync().ConfigureAwait(false);

                    const string sql = @"
                        SELECT Id, Name, TemplateId, TemplateName, TemplateFilePath,
                               LinkedScenePath, FilledDataJson, TakesJson, CreatedAt
                        FROM PlaylistItems
                        ORDER BY CreatedAt DESC
                        LIMIT @Limit";

                    var items = await connection.QueryAsync<StoredPlaylistItem>(sql, new { Limit = limit }).ConfigureAwait(false);
                    return items;
                }
            }
            catch (SqliteException ex)
            {
                Log($"SQLite error loading items: {ex.Message} (ErrorCode: {ex.SqliteErrorCode})");
                return Array.Empty<StoredPlaylistItem>();
            }
            catch (Exception ex)
            {
                Log($"Error loading items: {ex.Message}");
                return Array.Empty<StoredPlaylistItem>();
            }
        }

        /// <summary>
        /// Deletes an item from the database.
        /// </summary>
        public async Task<bool> DeleteItemAsync(string itemId)
        {
            if (!_isAvailable) return false;

            try
            {
                using (var connection = new SqliteConnection(BuildConnectionString(SqliteOpenMode.ReadWrite)))
                {
                    await connection.OpenAsync().ConfigureAwait(false);

                    const string sql = "DELETE FROM PlaylistItems WHERE Id = @Id";
                    var rows = await connection.ExecuteAsync(sql, new { Id = itemId }).ConfigureAwait(false);

                    if (rows > 0)
                    {
                        Log($"Deleted item: {itemId}");
                        return true;
                    }

                    Log($"Delete failed - item not found: {itemId}");
                    return false;
                }
            }
            catch (SqliteException ex)
            {
                Log($"SQLite error deleting item {itemId}: {ex.Message} (ErrorCode: {ex.SqliteErrorCode})");
                return false;
            }
            catch (Exception ex)
            {
                Log($"Error deleting item {itemId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Validates that a file path is within allowed directories to prevent path traversal attacks.
        /// </summary>
        /// <param name="filePath">The file path to validate.</param>
        /// <returns>True if the path is safe to access, false otherwise.</returns>
        private bool IsPathAllowed(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            // Check centralized PathValidator first
            if (PathValidator.IsPathAllowed(filePath))
                return true;

            try
            {
                // Check for traversal sequences
                if (PathValidator.ContainsTraversal(filePath))
                    return false;

                // Also allow database-specific directories (CommonApplicationData, LocalApplicationData)
                var fullPath = Path.GetFullPath(filePath);
                foreach (var basePath in AllowedBasePaths.Value)
                {
                    if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var relativePath = fullPath.Substring(basePath.Length);
                        if (!relativePath.Contains(".."))
                        {
                            return true;
                        }
                    }
                }

                Log($"Path traversal blocked: {filePath} is not within allowed directories");
                return false;
            }
            catch (Exception ex)
            {
                Log($"Path validation error for {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Converts a stored item to a PlaylistItemModel for use in the playlist.
        /// Uses TakesJson from database, falling back to template file only if needed.
        /// </summary>
        public PlaylistItemModel ConvertToPlaylistItem(StoredPlaylistItem stored)
        {
            if (stored == null) return null;

            try
            {
                // Get filled data
                var filledData = stored.GetFilledData();

                // Create playlist item
                var item = new PlaylistItemModel
                {
                    Id = stored.Id,
                    Name = stored.Name ?? stored.TemplateName ?? "Unknown",
                    TemplateId = stored.TemplateId,
                    TemplateName = stored.TemplateName,
                    TemplateFilePath = stored.TemplateFilePath,
                    LinkedScenePath = stored.LinkedScenePath,
                    Status = PlaylistItemStatus.Ready
                };

                // Copy filled data
                foreach (var kvp in filledData)
                {
                    item.FilledData[kvp.Key] = kvp.Value;
                }

                // First, try to get Takes from stored TakesJson
                var takes = stored.GetTakes(out string takesParseError);

                if (takes != null && takes.Count > 0)
                {
                    // Successfully parsed Takes from database
                    foreach (var take in takes)
                    {
                        item.Takes.Add(take);
                    }
                    Log($"Loaded {takes.Count} take(s) from database for item: {item.Id}");
                }
                else
                {
                    // Log why we're falling back
                    if (!stored.HasTakesJson)
                    {
                        Log($"TakesJson is empty for item {item.Id}, falling back to template file");
                    }
                    else if (takesParseError != null)
                    {
                        Log($"Error parsing TakesJson for item {item.Id}: {takesParseError}, falling back to template file");
                    }
                    else
                    {
                        Log($"TakesJson parsed but empty for item {item.Id}, falling back to template file");
                    }

                    // Fall back to reading from template file (with path traversal protection)
                    if (!string.IsNullOrEmpty(stored.TemplateFilePath))
                    {
                        // Security: Validate path before access
                        if (!IsPathAllowed(stored.TemplateFilePath))
                        {
                            Log($"Template file path rejected for item {item.Id}: {stored.TemplateFilePath}");
                        }
                        else if (!File.Exists(stored.TemplateFilePath))
                        {
                            Log($"Template file not found for item {item.Id}: {stored.TemplateFilePath}");
                        }
                        else
                        {
                            try
                            {
                                var json = File.ReadAllText(stored.TemplateFilePath);
                                var template = JsonSerializer.Deserialize<TemplateModel>(json,
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = AppConstants.MaxJsonDepth });

                                if (template?.Takes != null)
                                {
                                    foreach (var take in template.Takes)
                                    {
                                        item.Takes.Add(take);
                                    }
                                    Log($"Loaded {template.Takes.Count} take(s) from template file for item: {item.Id}");
                                }
                            }
                            catch (JsonException ex)
                            {
                                Log($"Error parsing template file for item {item.Id}: {ex.Message}");
                            }
                            catch (IOException ex)
                            {
                                Log($"Error reading template file for item {item.Id}: {ex.Message}");
                            }
                        }
                    }
                }

                Log($"Converted item: {item.Id} with {item.FilledData.Count} filled values, {item.Takes.Count} takes");
                return item;
            }
            catch (Exception ex)
            {
                Log($"Error converting item {stored?.Id}: {ex.Message}");
                return null;
            }
        }

        private void Log(string message)
        {
            LogMessage?.Invoke(this, $"[DB] {message}");
        }

        // No IDisposable needed - connections are per-operation via Dapper
    }
}
