using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using GraphicsMiddleware.Models;
using GraphicsMiddleware.Repositories;
using GraphicsMiddleware.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using Serilog.Events;

// ============================================================================
// GRAPHICS MIDDLEWARE - Octopus NRCS <-> Mosart <-> DaroEngine Integration
// ============================================================================

// Writable data directory (avoids Program Files write restrictions)
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DaroEngine2", "Middleware");
Directory.CreateDirectory(dataDir);

// Pre-build minimal config to read log path before full app startup
var bootstrapConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .Build();
var logPath = bootstrapConfig.GetValue("Logging:LogPath", "logs/middleware-.log");

// Resolve relative log path against writable data directory
if (!Path.IsPathRooted(logPath))
    logPath = Path.Combine(dataDir, logPath);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithThreadId()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{ThreadId}] [{RequestId}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] [{RequestId}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting Graphics Middleware...");

    var builder = WebApplication.CreateBuilder(args);

    // Configure Serilog
    builder.Host.UseSerilog();

    // Configure services
    builder.Services.AddSingleton<IDatabaseConnectionFactory, DatabaseConnectionFactory>();
    builder.Services.AddScoped<IPlaylistItemRepository, PlaylistItemRepository>();

    // Template service - scans for .dtemplate files
    builder.Services.AddSingleton<ITemplateService, TemplateService>();

    // Mosart client - forwards commands to DARO PLAYOUT (Designer)
    // Note: Engine rendering is handled by Designer's PlayoutWindow, not this middleware
    builder.Services.AddSingleton<IMosartClient, MosartClient>();

    // Metrics service for performance monitoring
    builder.Services.AddSingleton<IMetricsService, MetricsService>();

    // Configure CORS for HTML5 plugin (configurable via appsettings.json)
    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? new[] { "http://localhost:3000", "http://127.0.0.1:3000" };

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("OctopusPlugin", policy =>
        {
            policy.WithOrigins(corsOrigins)
                  .WithMethods("GET", "POST", "PUT", "DELETE")
                  .WithHeaders("Content-Type", "Accept", "Authorization", "X-Session-Id")
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        });
    });

    // Configure rate limiting to prevent API abuse
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Global rate limit: 100 requests per minute per IP
        options.AddPolicy("global", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 10
                }));

        // Strict limit for control endpoints: 30 requests per minute per IP
        options.AddPolicy("control", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 5
                }));

        // Write operations limit: 20 creates per minute per IP
        options.AddPolicy("create", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 2
                }));

        options.OnRejected = async (context, _) =>
        {
            Log.Error("SECURITY: Rate limit exceeded for {IP} on {Path}",
                context.HttpContext.Connection.RemoteIpAddress,
                context.HttpContext.Request.Path);

            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsync("Rate limit exceeded. Please try again later.");
        };
    });

    // Add health checks with custom implementations
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "db", "critical" })
        .AddCheck<MosartConnectionHealthCheck>("mosart", tags: new[] { "network", "playout" })
        .AddCheck<TemplateDirectoryHealthCheck>("templates", tags: new[] { "filesystem" })
        .AddCheck<SceneDirectoryHealthCheck>("scenes", tags: new[] { "filesystem" });

    var app = builder.Build();

    // Initialize database on startup
    var dbFactory = app.Services.GetRequiredService<IDatabaseConnectionFactory>();
    await dbFactory.InitializeAsync();

    // Configure middleware pipeline with correlation ID enrichment
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000}ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestId", httpContext.TraceIdentifier);
            diagnosticContext.Set("ClientIP", httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        };
    });

    // Add correlation ID to all log entries in the request scope
    app.Use(async (context, next) =>
    {
        using (Serilog.Context.LogContext.PushProperty("RequestId", context.TraceIdentifier))
        {
            await next();
        }
    });

    // Metrics tracking middleware
    var metricsService = app.Services.GetRequiredService<IMetricsService>();
    app.Use(async (context, next) =>
    {
        // Skip metrics for static files and health/metrics endpoints
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/health") || path.StartsWith("/metrics") ||
            path.EndsWith(".html") || path.EndsWith(".js") || path.EndsWith(".css"))
        {
            await next();
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await next();
            sw.Stop();
            var endpoint = $"{context.Request.Method} {path}";
            var success = context.Response.StatusCode < 400;
            metricsService.RecordRequest(endpoint, sw.ElapsedMilliseconds, success);
        }
        catch (Exception)
        {
            sw.Stop();
            var endpoint = $"{context.Request.Method} {path}";
            metricsService.RecordRequest(endpoint, sw.ElapsedMilliseconds, false);
            throw;
        }
    });

    app.UseCors("OctopusPlugin");
    app.UseRateLimiter();
    app.UseDefaultFiles(); // Serve index.html as default
    app.UseStaticFiles(); // Serve wwwroot content

    // Health check endpoint with detailed JSON response
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var result = new
            {
                status = report.Status.ToString(),
                totalDuration = report.TotalDuration.TotalMilliseconds,
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    duration = e.Value.Duration.TotalMilliseconds,
                    tags = e.Value.Tags,
                    data = e.Value.Data.Count > 0 ? e.Value.Data : null,
                    exception = e.Value.Exception?.Message
                })
            };
            await context.Response.WriteAsJsonAsync(result);
        }
    });

    // Metrics endpoint for performance monitoring
    app.MapGet("/metrics", (IMetricsService metrics) =>
    {
        return TypedResults.Ok(metrics.GetSnapshot());
    })
    .WithName("GetMetrics")
    .WithSummary("Get performance metrics")
    .WithTags("Monitoring")
    .Produces<MetricsSnapshot>(StatusCodes.Status200OK);

    // Reset metrics endpoint (for testing)
    app.MapPost("/metrics/reset", (IMetricsService metrics) =>
    {
        metrics.Reset();
        return TypedResults.Ok(new { message = "Metrics reset" });
    })
    .WithName("ResetMetrics")
    .WithSummary("Reset all metrics")
    .WithTags("Monitoring");

    // ========================================================================
    // TEMPLATE ENDPOINTS
    // ========================================================================

    var templates = app.MapGroup("/api/templates").WithTags("Templates").RequireRateLimiting("global");

    // GET /api/templates - List all templates
    templates.MapGet("/", async (
        ITemplateService templateService,
        CancellationToken cancellationToken) =>
    {
        var result = await templateService.GetAvailableTemplatesAsync(cancellationToken);
        return TypedResults.Ok(result);
    })
    .WithName("ListTemplates")
    .WithSummary("List all available templates")
    .Produces<TemplateListResponse>(StatusCodes.Status200OK);

    // GET /api/templates/{id} - Get template detail
    templates.MapGet("/{id}", async Task<Results<Ok<TemplateDetailResponse>, NotFound>> (
        string id,
        ITemplateService templateService,
        CancellationToken cancellationToken) =>
    {
        var detail = await templateService.GetTemplateDetailAsync(id, cancellationToken);
        return detail is not null
            ? TypedResults.Ok(detail)
            : TypedResults.NotFound();
    })
    .WithName("GetTemplateDetail")
    .WithSummary("Get detailed template information including form fields")
    .Produces<TemplateDetailResponse>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status404NotFound);

    // ========================================================================
    // PLAYLIST ITEM ENDPOINTS
    // ========================================================================

    var api = app.MapGroup("/api/items").WithTags("Playlist Items").RequireRateLimiting("global");

    // POST /api/items - Create new playlist item from template
    api.MapPost("/", async Task<Results<Created<CreatePlaylistItemResponse>, BadRequest<string>, NotFound<string>>> (
        CreatePlaylistItemRequest request,
        IPlaylistItemRepository repository,
        ITemplateService templateService,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        // Sanitize and validate input
        var sanitizedTemplateId = InputSanitizer.SanitizeId(request.TemplateId);
        if (string.IsNullOrWhiteSpace(sanitizedTemplateId))
        {
            return TypedResults.BadRequest("TemplateId is required and must contain valid characters");
        }

        // Check for path traversal in template ID
        if (InputSanitizer.ContainsPathTraversal(sanitizedTemplateId))
        {
            logger.LogError("SECURITY: Path traversal attempt in TemplateId: {TemplateId}", request.TemplateId);
            return TypedResults.BadRequest("Invalid TemplateId");
        }

        // Sanitize name and filled data
        var sanitizedName = InputSanitizer.SanitizeName(request.Name);
        var sanitizedFilledData = InputSanitizer.SanitizeFilledData(request.FilledData);

        // Load template
        var template = await templateService.LoadTemplateAsync(sanitizedTemplateId, cancellationToken);
        if (template == null)
        {
            return TypedResults.NotFound($"Template not found: {sanitizedTemplateId}");
        }

        // Get template file path
        var templateFilePath = templateService.FindTemplateFilePath(sanitizedTemplateId);
        if (templateFilePath == null)
        {
            return TypedResults.NotFound($"Template file not found: {sanitizedTemplateId}");
        }

        // Resolve linked scene path
        var linkedScenePath = templateService.ResolveLinkedScenePath(templateFilePath, template.LinkedScenePath);
        if (string.IsNullOrEmpty(linkedScenePath))
        {
            return TypedResults.BadRequest($"Template has no linked scene: {sanitizedTemplateId}");
        }

        if (!File.Exists(linkedScenePath))
        {
            return TypedResults.BadRequest($"Linked scene file not found: {linkedScenePath}");
        }

        try
        {
            // Sanitize template name from loaded template
            var sanitizedTemplateName = InputSanitizer.SanitizeName(template.Name) ?? sanitizedTemplateId;

            var id = await repository.CreateAsync(
                templateId: sanitizedTemplateId,
                templateName: sanitizedTemplateName,
                templateFilePath: templateFilePath,
                linkedScenePath: linkedScenePath,
                takes: template.Takes,
                filledData: sanitizedFilledData,
                name: sanitizedName,
                cancellationToken: cancellationToken);

            var createdAt = DateTime.UtcNow.ToString("O");

            logger.LogInformation(
                "Created playlist item: {Id}, Template: {Template}",
                id, sanitizedTemplateId);

            return TypedResults.Created(
                $"/api/items/{id}",
                new CreatePlaylistItemResponse(
                    id,
                    sanitizedName ?? sanitizedTemplateName,
                    sanitizedTemplateId,
                    createdAt));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create playlist item");
            return TypedResults.BadRequest("Failed to create item");
        }
    })
    .WithName("CreateItem")
    .WithSummary("Create a new playlist item from a template")
    .RequireRateLimiting("create")
    .Produces<CreatePlaylistItemResponse>(StatusCodes.Status201Created)
    .Produces<string>(StatusCodes.Status400BadRequest)
    .Produces<string>(StatusCodes.Status404NotFound);

    // GET /api/items/{id} - Get item by ID
    api.MapGet("/{id}", async Task<Results<Ok<StoredPlaylistItem>, NotFound, BadRequest<string>>> (
        string id,
        IPlaylistItemRepository repository,
        CancellationToken cancellationToken) =>
    {
        if (!Guid.TryParse(id, out _))
            return TypedResults.BadRequest("Invalid item ID format");

        var item = await repository.GetByIdAsync(id, cancellationToken);
        return item is not null
            ? TypedResults.Ok(item)
            : TypedResults.NotFound();
    })
    .WithName("GetItem")
    .WithSummary("Get a playlist item by ID")
    .Produces<StoredPlaylistItem>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status404NotFound);

    // GET /api/items - List all items
    api.MapGet("/", async (
        IPlaylistItemRepository repository,
        int? limit,
        CancellationToken cancellationToken) =>
    {
        var clampedLimit = Math.Clamp(limit ?? 100, 1, 500);
        var items = await repository.GetAllAsync(clampedLimit, cancellationToken);
        return TypedResults.Ok(items);
    })
    .WithName("ListItems")
    .WithSummary("List all playlist items")
    .Produces<IEnumerable<StoredPlaylistItem>>(StatusCodes.Status200OK);

    // DELETE /api/items/{id} - Delete item
    api.MapDelete("/{id}", async Task<Results<NoContent, NotFound, BadRequest<string>>> (
        string id,
        IPlaylistItemRepository repository,
        CancellationToken cancellationToken) =>
    {
        if (!Guid.TryParse(id, out _))
            return TypedResults.BadRequest("Invalid item ID format");

        var success = await repository.DeleteAsync(id, cancellationToken);
        return success ? TypedResults.NoContent() : TypedResults.NotFound();
    })
    .WithName("DeleteItem")
    .WithSummary("Delete a playlist item")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status404NotFound);

    // PUT /api/items/{id}/data - Update filled data
    api.MapPut("/{id}/data", async Task<Results<Ok, NotFound, BadRequest<string>>> (
        string id,
        Dictionary<string, string> filledData,
        IPlaylistItemRepository repository,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        if (filledData == null)
        {
            return TypedResults.BadRequest("FilledData is required");
        }

        if (filledData.Count > InputSanitizer.MaxFilledDataEntries)
        {
            return TypedResults.BadRequest($"FilledData exceeds maximum entry limit ({InputSanitizer.MaxFilledDataEntries})");
        }

        // Validate item ID format (should be a GUID)
        if (!Guid.TryParse(id, out _))
        {
            logger.LogWarning("Invalid item ID format: {Id}", id);
            return TypedResults.BadRequest("Invalid item ID format");
        }

        // Sanitize filled data values
        var sanitizedFilledData = InputSanitizer.SanitizeFilledData(filledData);
        if (sanitizedFilledData == null || sanitizedFilledData.Count == 0)
        {
            return TypedResults.BadRequest("FilledData must contain valid entries");
        }

        var success = await repository.UpdateFilledDataAsync(id, sanitizedFilledData, cancellationToken);
        return success ? TypedResults.Ok() : TypedResults.NotFound();
    })
    .WithName("UpdateItemData")
    .WithSummary("Update filled data for a playlist item")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status404NotFound);

    // ========================================================================
    // ENGINE CONTROL ENDPOINTS (forwarded to DARO PLAYOUT via TCP)
    // ========================================================================

    // Thread-safe state management for cued items per session
    // Key: sessionId (from X-Session-Id header or "default"), Value: (itemId, cuedAt timestamp)
    var cuedItemsState = new ConcurrentDictionary<string, (string ItemId, DateTime CuedAt)>();

    // State cleanup interval - configurable (default 30 minutes)
    var stateExpirationMinutes = app.Configuration.GetValue("State:ExpirationMinutes", 30);

    // Background cleanup task for stale state entries
    var stateCleanupTimer = new Timer(_ =>
    {
        var expiredBefore = DateTime.UtcNow.AddMinutes(-stateExpirationMinutes);
        var expiredKeys = cuedItemsState
            .Where(kvp => kvp.Value.CuedAt < expiredBefore)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            if (cuedItemsState.TryRemove(key, out var removed))
            {
                Log.Debug("Cleaned up stale cued state for session {SessionId}, item {ItemId} (cued at {CuedAt})",
                    key, removed.ItemId, removed.CuedAt);
            }
        }
    }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

    // Helper to get session ID from request
    string GetSessionId(HttpContext context) =>
        context.Request.Headers.TryGetValue("X-Session-Id", out var sessionId) && !string.IsNullOrWhiteSpace(sessionId)
            ? sessionId.ToString()
            : "default";

    // Helper to get last cued item for a session
    string? GetLastCuedItemId(string sessionId) =>
        cuedItemsState.TryGetValue(sessionId, out var state) ? state.ItemId : null;

    // Helper to set cued item for a session
    void SetCuedItem(string sessionId, string itemId)
    {
        var newState = (itemId, DateTime.UtcNow);
        cuedItemsState.AddOrUpdate(sessionId, newState, (_, _) => newState);
        Log.Debug("State change: Session {SessionId} cued item {ItemId}", sessionId, itemId);
    }

    // Helper to clear cued item for a session
    void ClearCuedItem(string sessionId)
    {
        if (cuedItemsState.TryRemove(sessionId, out var removed))
        {
            Log.Debug("State change: Session {SessionId} cleared cued item {ItemId}", sessionId, removed.ItemId);
        }
    }

    var control = app.MapGroup("/api/control").WithTags("Engine Control").RequireRateLimiting("control");

    // POST /api/control/cue/{itemId} - Cue a playlist item (forwards to DARO PLAYOUT)
    control.MapPost("/cue/{itemId}", async Task<Results<Ok<string>, NotFound<string>, BadRequest<string>>> (
        string itemId,
        HttpContext httpContext,
        IPlaylistItemRepository repository,
        IMosartClient mosartClient,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        if (!Guid.TryParse(itemId, out _))
            return TypedResults.BadRequest("Invalid item ID format");

        var sessionId = GetSessionId(httpContext);

        // Verify item exists in database
        var item = await repository.GetByIdAsync(itemId, cancellationToken);
        if (item == null)
        {
            return TypedResults.NotFound($"Item not found: {itemId}");
        }

        // Forward CUE command to DARO PLAYOUT
        var response = await mosartClient.CueAsync(itemId, cancellationToken);
        SetCuedItem(sessionId, itemId);

        logger.LogInformation("CUE command forwarded to DARO PLAYOUT: Session={SessionId}, ItemId={ItemId}, Response={Response}",
            sessionId, itemId, response);

        if (response.StartsWith("OK"))
        {
            return TypedResults.Ok(response);
        }

        return TypedResults.BadRequest(response);
    })
    .WithName("CueItem")
    .WithSummary("Cue a playlist item (forwards to DARO PLAYOUT)");

    // POST /api/control/play - Play current item (forwards to DARO PLAYOUT)
    control.MapPost("/play", async (HttpContext httpContext, IMosartClient mosartClient, ILogger<Program> logger, CancellationToken cancellationToken) =>
    {
        var sessionId = GetSessionId(httpContext);
        var cuedItemId = GetLastCuedItemId(sessionId);

        if (string.IsNullOrEmpty(cuedItemId))
        {
            return Results.BadRequest(new { Error = "No item cued. Call /cue/{itemId} first." });
        }

        var response = await mosartClient.PlayAsync(cuedItemId, cancellationToken);
        logger.LogInformation("PLAY command forwarded to DARO PLAYOUT: Session={SessionId}, ItemId={ItemId}, Response={Response}",
            sessionId, cuedItemId, response);

        if (response.StartsWith("OK"))
        {
            return Results.Ok(new { Status = "PLAYING", Response = response });
        }

        return Results.BadRequest(new { Error = response });
    })
    .WithName("Play")
    .WithSummary("Start playback (forwards to DARO PLAYOUT)");

    // POST /api/control/stop - Stop playback (forwards to DARO PLAYOUT)
    control.MapPost("/stop", async (HttpContext httpContext, IMosartClient mosartClient, ILogger<Program> logger, CancellationToken cancellationToken) =>
    {
        var sessionId = GetSessionId(httpContext);
        var cuedItemId = GetLastCuedItemId(sessionId);

        if (string.IsNullOrEmpty(cuedItemId))
        {
            return Results.BadRequest(new { Error = "No item to stop" });
        }

        var response = await mosartClient.StopAsync(cuedItemId, cancellationToken);
        logger.LogInformation("STOP command forwarded to DARO PLAYOUT: Session={SessionId}, ItemId={ItemId}, Response={Response}",
            sessionId, cuedItemId, response);

        ClearCuedItem(sessionId);

        if (response.StartsWith("OK"))
        {
            return Results.Ok(new { Status = "STOPPED", ItemId = cuedItemId });
        }

        return Results.BadRequest(new { Error = response });
    })
    .WithName("Stop")
    .WithSummary("Stop playback (forwards to DARO PLAYOUT)");

    // POST /api/control/pause - Pause playback (forwards to DARO PLAYOUT)
    control.MapPost("/pause", async (HttpContext httpContext, IMosartClient mosartClient, ILogger<Program> logger, CancellationToken cancellationToken) =>
    {
        var sessionId = GetSessionId(httpContext);
        var cuedItemId = GetLastCuedItemId(sessionId);

        if (string.IsNullOrEmpty(cuedItemId))
        {
            return Results.BadRequest(new { Error = "No item playing" });
        }

        var response = await mosartClient.PauseAsync(cuedItemId, cancellationToken);
        logger.LogInformation("PAUSE command forwarded to DARO PLAYOUT: Session={SessionId}, ItemId={ItemId}, Response={Response}",
            sessionId, cuedItemId, response);

        if (response.StartsWith("OK"))
        {
            return Results.Ok(new { Status = "PAUSED" });
        }

        return Results.BadRequest(new { Error = response });
    })
    .WithName("Pause")
    .WithSummary("Pause playback (forwards to DARO PLAYOUT)");

    // POST /api/control/continue - Continue playback (forwards to DARO PLAYOUT)
    control.MapPost("/continue", async (HttpContext httpContext, IMosartClient mosartClient, ILogger<Program> logger, CancellationToken cancellationToken) =>
    {
        var sessionId = GetSessionId(httpContext);
        var cuedItemId = GetLastCuedItemId(sessionId);

        if (string.IsNullOrEmpty(cuedItemId))
        {
            return Results.BadRequest(new { Error = "No item to continue" });
        }

        // Continue uses same command as Play (1)
        var response = await mosartClient.PlayAsync(cuedItemId, cancellationToken);
        logger.LogInformation("CONTINUE command forwarded to DARO PLAYOUT: Session={SessionId}, ItemId={ItemId}, Response={Response}",
            sessionId, cuedItemId, response);

        if (response.StartsWith("OK"))
        {
            return Results.Ok(new { Status = "CONTINUED" });
        }

        return Results.BadRequest(new { Error = response });
    })
    .WithName("Continue")
    .WithSummary("Continue playback (forwards to DARO PLAYOUT)");

    // GET /api/control/status - Get connection status
    control.MapGet("/status", (HttpContext httpContext, IMosartClient mosartClient) =>
    {
        var sessionId = GetSessionId(httpContext);
        var cuedItemId = GetLastCuedItemId(sessionId);

        return Results.Ok(new
        {
            Connected = mosartClient.IsConnected,
            SessionId = sessionId,
            LastCuedItemId = cuedItemId,
            ActiveSessions = cuedItemsState.Count
        });
    })
    .WithName("Status")
    .WithSummary("Get DARO PLAYOUT connection status");

    // ========================================================================
    // Run Application
    // ========================================================================

    var httpPort = builder.Configuration.GetValue("Kestrel:Endpoints:Http:Url", "http://localhost:5000");
    var mosartPort = builder.Configuration.GetValue("Mosart:Port", 5555);

    Log.Information("Graphics Middleware started. API: {HttpPort}, forwarding to DARO PLAYOUT on port {MosartPort}",
        httpPort, mosartPort);

    // Graceful shutdown
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        Log.Information("Shutting down Graphics Middleware...");

        // Dispose state cleanup timer
        stateCleanupTimer.Dispose();
        Log.Debug("State cleanup timer disposed");

        // Gracefully disconnect MosartClient
        var mosartClient = app.Services.GetRequiredService<IMosartClient>();
        try
        {
            mosartClient.Dispose();
            Log.Information("MosartClient disposed successfully");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing MosartClient");
        }
    });

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

// ============================================================================
// Request Models
// ============================================================================

/// <summary>
/// Request for seek operation.
/// </summary>
public record SeekRequest(int Frame);

/// <summary>
/// Request for Spout configuration.
/// </summary>
public record SpoutRequest(string? SenderName);
