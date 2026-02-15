using Microsoft.Extensions.Diagnostics.HealthChecks;
using GraphicsMiddleware.Repositories;

namespace GraphicsMiddleware.Services;

/// <summary>
/// Health check for SQLite database connectivity.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(
        IDatabaseConnectionFactory connectionFactory,
        ILogger<DatabaseHealthCheck> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            // Simple query to verify connectivity
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy("Database connection successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy("Database connection failed", ex);
        }
    }
}

/// <summary>
/// Health check for Mosart/DARO PLAYOUT TCP connection.
/// </summary>
public sealed class MosartConnectionHealthCheck : IHealthCheck
{
    private readonly IMosartClient _mosartClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MosartConnectionHealthCheck> _logger;

    public MosartConnectionHealthCheck(
        IMosartClient mosartClient,
        IConfiguration configuration,
        ILogger<MosartConnectionHealthCheck> logger)
    {
        _mosartClient = mosartClient;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var port = _configuration.GetValue("Mosart:Port", 5555);
            var data = new Dictionary<string, object> { ["port"] = port };

            if (_mosartClient.IsConnected)
            {
                return Task.FromResult(HealthCheckResult.Healthy("DARO PLAYOUT connected", data));
            }

            // Not connected is degraded, not unhealthy - system can still operate
            return Task.FromResult(HealthCheckResult.Degraded(
                "DARO PLAYOUT not connected. Commands will fail until connection is established.", data: data));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mosart connection health check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("Mosart health check error", ex));
        }
    }
}

/// <summary>
/// Health check for template directory access.
/// </summary>
public sealed class TemplateDirectoryHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TemplateDirectoryHealthCheck> _logger;

    public TemplateDirectoryHealthCheck(
        IConfiguration configuration,
        ILogger<TemplateDirectoryHealthCheck> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var templatesPath = _configuration["Templates:Path"];
            if (string.IsNullOrEmpty(templatesPath))
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    "Templates:Path not configured in appsettings.json"));
            }

            // Expand environment variables
            var expandedPath = Environment.ExpandEnvironmentVariables(templatesPath);

            if (!Directory.Exists(expandedPath))
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Template directory does not exist: {expandedPath}"));
            }

            // Check if we can read the directory
            try
            {
                var files = Directory.GetFiles(expandedPath, "*.dtemplate", SearchOption.TopDirectoryOnly);
                return Task.FromResult(HealthCheckResult.Healthy(
                    $"Template directory accessible. Found {files.Length} template(s) in root."));
            }
            catch (UnauthorizedAccessException)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Access denied to template directory: {expandedPath}"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Template directory health check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("Template directory check error", ex));
        }
    }
}

/// <summary>
/// Health check for scene directory access.
/// </summary>
public sealed class SceneDirectoryHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SceneDirectoryHealthCheck> _logger;

    public SceneDirectoryHealthCheck(
        IConfiguration configuration,
        ILogger<SceneDirectoryHealthCheck> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scenesPath = _configuration["Scenes:Path"];
            if (string.IsNullOrEmpty(scenesPath))
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    "Scenes:Path not configured in appsettings.json"));
            }

            // Expand environment variables
            var expandedPath = Environment.ExpandEnvironmentVariables(scenesPath);

            if (!Directory.Exists(expandedPath))
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Scene directory does not exist: {expandedPath}"));
            }

            // Check if we can read the directory
            try
            {
                var daroFiles = Directory.GetFiles(expandedPath, "*.daro", SearchOption.TopDirectoryOnly);
                var dsceneFiles = Directory.GetFiles(expandedPath, "*.dscene", SearchOption.TopDirectoryOnly);
                int totalScenes = daroFiles.Length + dsceneFiles.Length;
                return Task.FromResult(HealthCheckResult.Healthy(
                    $"Scene directory accessible. Found {totalScenes} scene(s) in root."));
            }
            catch (UnauthorizedAccessException)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Access denied to scene directory: {expandedPath}"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scene directory health check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("Scene directory check error", ex));
        }
    }
}
