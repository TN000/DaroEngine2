using System.Text.Json;
using Dapper;
using GraphicsMiddleware.Models;

namespace GraphicsMiddleware.Repositories;

/// <summary>
/// Repository for playlist item persistence.
/// Stores items in SQLite with filled data for Mosart recall.
/// </summary>
public interface IPlaylistItemRepository
{
    /// <summary>
    /// Creates a new playlist item and returns its ID.
    /// </summary>
    Task<string> CreateAsync(
        string templateId,
        string templateName,
        string templateFilePath,
        string linkedScenePath,
        List<TemplateTakeModel> takes,
        Dictionary<string, string>? filledData = null,
        string? name = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a playlist item by ID.
    /// </summary>
    Task<StoredPlaylistItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all playlist items (with optional limit).
    /// </summary>
    Task<IEnumerable<StoredPlaylistItem>> GetAllAsync(int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a playlist item by ID.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates playlist item filled data.
    /// </summary>
    Task<bool> UpdateFilledDataAsync(string id, Dictionary<string, string> filledData, CancellationToken cancellationToken = default);
}

public sealed class PlaylistItemRepository : IPlaylistItemRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly ILogger<PlaylistItemRepository> _logger;

    public PlaylistItemRepository(
        IDatabaseConnectionFactory connectionFactory,
        ILogger<PlaylistItemRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<string> CreateAsync(
        string templateId,
        string templateName,
        string templateFilePath,
        string linkedScenePath,
        List<TemplateTakeModel> takes,
        Dictionary<string, string>? filledData = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("D");
        var createdAt = DateTime.UtcNow.ToString("O");
        var filledDataJson = filledData != null
            ? JsonSerializer.Serialize(filledData, DaroJsonOptions.Default)
            : "{}";
        var takesJson = JsonSerializer.Serialize(takes, DaroJsonOptions.Default);

        const string sql = """
            INSERT INTO PlaylistItems (
                Id, Name, TemplateId, TemplateName, TemplateFilePath,
                LinkedScenePath, FilledDataJson, TakesJson, CreatedAt
            )
            VALUES (
                @Id, @Name, @TemplateId, @TemplateName, @TemplateFilePath,
                @LinkedScenePath, @FilledDataJson, @TakesJson, @CreatedAt
            )
            """;

        try
        {
            await using var connection = _connectionFactory.CreateConnection();

            var commandDefinition = new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    Name = name ?? templateName,
                    TemplateId = templateId,
                    TemplateName = templateName,
                    TemplateFilePath = templateFilePath,
                    LinkedScenePath = linkedScenePath,
                    FilledDataJson = filledDataJson,
                    TakesJson = takesJson,
                    CreatedAt = createdAt
                },
                cancellationToken: cancellationToken);

            await connection.ExecuteAsync(commandDefinition).ConfigureAwait(false);

            _logger.LogDebug("Created playlist item: {Id}, Template: {Template}", id, templateId);
            return id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Database error creating playlist item. TemplateId: {TemplateId}, TemplateName: {TemplateName}",
                templateId, templateName);
            throw;
        }
    }

    public async Task<StoredPlaylistItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, TemplateId, TemplateName, TemplateFilePath,
                   LinkedScenePath, FilledDataJson, TakesJson, CreatedAt
            FROM PlaylistItems
            WHERE Id = @Id
            """;

        try
        {
            await using var connection = _connectionFactory.CreateConnection();

            var commandDefinition = new CommandDefinition(
                sql,
                new { Id = id },
                cancellationToken: cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<PlaylistItemRow>(commandDefinition).ConfigureAwait(false);

            if (row == null)
            {
                _logger.LogWarning("Playlist item not found: {Id}", id);
                return null;
            }

            return new StoredPlaylistItem
            {
                Id = row.Id,
                Name = row.Name,
                TemplateId = row.TemplateId ?? "",
                TemplateName = row.TemplateName,
                TemplateFilePath = row.TemplateFilePath ?? "",
                LinkedScenePath = row.LinkedScenePath,
                FilledData = ParseFilledData(row.FilledDataJson),
                TakesJson = row.TakesJson,
                CreatedAt = row.CreatedAt
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Database error retrieving playlist item: {Id}", id);
            throw;
        }
    }

    public async Task<IEnumerable<StoredPlaylistItem>> GetAllAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, TemplateId, TemplateName, TemplateFilePath,
                   LinkedScenePath, FilledDataJson, TakesJson, CreatedAt
            FROM PlaylistItems
            ORDER BY CreatedAt DESC
            LIMIT @Limit
            """;

        try
        {
            await using var connection = _connectionFactory.CreateConnection();

            var commandDefinition = new CommandDefinition(
                sql,
                new { Limit = limit },
                cancellationToken: cancellationToken);

            var rows = await connection.QueryAsync<PlaylistItemRow>(commandDefinition).ConfigureAwait(false);

            // Materialize results immediately to avoid deferred execution issues
            return rows.Select(row => new StoredPlaylistItem
            {
                Id = row.Id,
                Name = row.Name,
                TemplateId = row.TemplateId ?? "",
                TemplateName = row.TemplateName,
                TemplateFilePath = row.TemplateFilePath ?? "",
                LinkedScenePath = row.LinkedScenePath,
                FilledData = ParseFilledData(row.FilledDataJson),
                TakesJson = row.TakesJson,
                CreatedAt = row.CreatedAt
            }).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Database error retrieving all playlist items. Limit: {Limit}", limit);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM PlaylistItems WHERE Id = @Id";

        try
        {
            await using var connection = _connectionFactory.CreateConnection();

            var commandDefinition = new CommandDefinition(
                sql,
                new { Id = id },
                cancellationToken: cancellationToken);

            var rowsAffected = await connection.ExecuteAsync(commandDefinition).ConfigureAwait(false);

            if (rowsAffected > 0)
            {
                _logger.LogInformation("Deleted playlist item: {Id}", id);
                return true;
            }

            _logger.LogWarning("Playlist item not found for deletion: {Id}", id);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Database error deleting playlist item: {Id}", id);
            throw;
        }
    }

    public async Task<bool> UpdateFilledDataAsync(string id, Dictionary<string, string> filledData, CancellationToken cancellationToken = default)
    {
        var filledDataJson = JsonSerializer.Serialize(filledData, DaroJsonOptions.Default);

        const string sql = """
            UPDATE PlaylistItems
            SET FilledDataJson = @FilledDataJson
            WHERE Id = @Id
            """;

        try
        {
            await using var connection = _connectionFactory.CreateConnection();

            var commandDefinition = new CommandDefinition(
                sql,
                new { Id = id, FilledDataJson = filledDataJson },
                cancellationToken: cancellationToken);

            var rowsAffected = await connection.ExecuteAsync(commandDefinition).ConfigureAwait(false);
            return rowsAffected > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Database error updating filled data for playlist item: {Id}", id);
            throw;
        }
    }

    private Dictionary<string, string> ParseFilledData(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, DaroJsonOptions.Default)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse FilledDataJson. JSON length: {Length}", json?.Length ?? 0);
            return new Dictionary<string, string>();
        }
    }

    // Internal row model for Dapper
    private sealed class PlaylistItemRow
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string? TemplateId { get; set; }
        public string? TemplateName { get; set; }
        public string? TemplateFilePath { get; set; }
        public string LinkedScenePath { get; set; } = "";
        public string? FilledDataJson { get; set; }
        public string? TakesJson { get; set; }
        public string CreatedAt { get; set; } = "";
    }
}
