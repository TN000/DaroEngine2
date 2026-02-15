using Microsoft.Data.Sqlite;

namespace GraphicsMiddleware.Repositories;

/// <summary>
/// Factory for creating SQLite connections with proper configuration.
/// Implements connection pooling through Microsoft.Data.Sqlite.
/// </summary>
public interface IDatabaseConnectionFactory
{
    /// <summary>
    /// Creates a new open database connection.
    /// </summary>
    SqliteConnection CreateConnection();

    /// <summary>
    /// Initializes the database schema.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class DatabaseConnectionFactory : IDatabaseConnectionFactory, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseConnectionFactory> _logger;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public DatabaseConnectionFactory(
        IConfiguration configuration,
        ILogger<DatabaseConnectionFactory> logger)
    {
        var dbPath = configuration.GetValue<string>("Database:Path") ?? "graphics_middleware.db";

        // Connection string with pooling and WAL mode preparation
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        _logger = logger;
    }

    public SqliteConnection CreateConnection()
    {
        try
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Failed to create database connection. Connection string: {ConnectionString}",
                _connectionString.Replace(";Password=", ";Password=***")); // Mask any password
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing database schema...");

            await using var connection = CreateConnection();

            // Enable WAL mode for better concurrent read/write performance
            await using (var walCommand = connection.CreateCommand())
            {
                walCommand.CommandText = "PRAGMA journal_mode=WAL;";
                var result = await walCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("SQLite journal mode set to: {Mode}", result);
            }

            // Set synchronous mode to NORMAL for better performance while maintaining safety
            await using (var syncCommand = connection.CreateCommand())
            {
                syncCommand.CommandText = "PRAGMA synchronous=NORMAL;";
                await syncCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Create PlaylistItems table (main table for Mosart integration)
            await using (var createCommand = connection.CreateCommand())
            {
                createCommand.CommandText = """
                    CREATE TABLE IF NOT EXISTS PlaylistItems (
                        Id TEXT PRIMARY KEY NOT NULL,
                        Name TEXT,
                        TemplateId TEXT,
                        TemplateName TEXT,
                        TemplateFilePath TEXT,
                        LinkedScenePath TEXT NOT NULL,
                        FilledDataJson TEXT,
                        TakesJson TEXT,
                        CreatedAt TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_PlaylistItems_CreatedAt ON PlaylistItems(CreatedAt);
                    CREATE INDEX IF NOT EXISTS IX_PlaylistItems_TemplateId ON PlaylistItems(TemplateId);
                    """;
                await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            _logger.LogInformation("Database schema initialized successfully");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        // Clear connection pool on shutdown (synchronous operation)
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
