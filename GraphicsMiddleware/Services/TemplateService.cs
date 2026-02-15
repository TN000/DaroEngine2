using System.Text.Json;
using GraphicsMiddleware.Models;

namespace GraphicsMiddleware.Services;

/// <summary>
/// Service for managing template files (.dtemplate).
/// Scans configured templates directory and provides template loading.
/// </summary>
public interface ITemplateService
{
    /// <summary>
    /// Gets the configured templates directory path.
    /// </summary>
    string TemplatesPath { get; }

    /// <summary>
    /// Gets list of all available templates.
    /// </summary>
    Task<TemplateListResponse> GetAvailableTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed information about a specific template.
    /// </summary>
    Task<TemplateDetailResponse?> GetTemplateDetailAsync(string templateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads full template model from file.
    /// </summary>
    Task<TemplateModel?> LoadTemplateAsync(string templateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds template file path by ID.
    /// </summary>
    string? FindTemplateFilePath(string templateId);

    /// <summary>
    /// Resolves the linked scene path (makes it absolute if relative).
    /// Returns null if the path is invalid, outside allowed directories, or has wrong extension.
    /// </summary>
    string? ResolveLinkedScenePath(string templateFilePath, string? linkedScenePath);
}

public sealed class TemplateService : ITemplateService
{
    private readonly ILogger<TemplateService> _logger;
    private readonly string _templatesPath;
    private readonly string _scenesPath;
    private readonly JsonSerializerOptions _jsonOptions;

    // Valid file extensions
    private const string TemplateExtension = ".dtemplate";
    private const string SceneExtension = ".daro";

    // Cache for template file paths: templateId -> filePath
    private readonly Dictionary<string, string> _templatePathCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCacheScan = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration;
    private readonly Lock _cacheLock = new();

    public string TemplatesPath => _templatesPath;

    public TemplateService(IConfiguration configuration, ILogger<TemplateService> logger)
    {
        _logger = logger;

        // Get templates path from config or use default
        var configPath = configuration.GetValue<string>("Templates:Path");

        if (!string.IsNullOrEmpty(configPath))
        {
            _templatesPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configPath));
        }
        else
        {
            // Default: Documents\DaroEngine\Templates (same as Designer)
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _templatesPath = Path.GetFullPath(Path.Combine(documentsPath, "DaroEngine", "Templates"));
        }

        // Get scenes path from config or use default (sibling to templates)
        var scenesConfigPath = configuration.GetValue<string>("Scenes:Path");
        if (!string.IsNullOrEmpty(scenesConfigPath))
        {
            _scenesPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(scenesConfigPath));
        }
        else
        {
            // Default: Documents\DaroEngine\Scenes
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _scenesPath = Path.GetFullPath(Path.Combine(documentsPath, "DaroEngine", "Scenes"));
        }

        // Configurable cache duration (default 30 seconds)
        _cacheDuration = TimeSpan.FromSeconds(configuration.GetValue("Templates:CacheDurationSeconds", 30));

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            MaxDepth = 32 // Prevent stack overflow from deeply nested JSON
        };

        _logger.LogInformation("Templates path configured: {Path}", _templatesPath);
        _logger.LogInformation("Scenes path configured: {Path}", _scenesPath);

        // Ensure directory exists
        if (!Directory.Exists(_templatesPath))
        {
            try
            {
                Directory.CreateDirectory(_templatesPath);
                _logger.LogInformation("Created templates directory: {Path}", _templatesPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied creating templates directory: {Path}", _templatesPath);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IO error creating templates directory: {Path}", _templatesPath);
            }
        }
    }

    public async Task<TemplateListResponse> GetAvailableTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var templates = new List<TemplateInfo>();

        if (!Directory.Exists(_templatesPath))
        {
            _logger.LogWarning("Templates directory does not exist: {Path}", _templatesPath);
            return new TemplateListResponse { Templates = templates, TotalCount = 0 };
        }

        try
        {
            // Scan for .dtemplate files recursively
            var templateFiles = Directory.GetFiles(_templatesPath, $"*{TemplateExtension}", SearchOption.AllDirectories);

            foreach (var filePath in templateFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Validate each file path stays within templates directory
                if (!IsPathWithinDirectory(filePath, _templatesPath))
                {
                    _logger.LogError("SECURITY: Skipping template file outside templates directory: {FilePath}", filePath);
                    continue;
                }

                try
                {
                    var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                    var template = JsonSerializer.Deserialize<TemplateModel>(json, _jsonOptions);

                    if (template == null) continue;

                    // Validate element and take counts (DARO_MAX_LAYERS = 64)
                    const int MaxElements = 64;
                    const int MaxTakes = 64;
                    if ((template.Elements?.Count ?? 0) > MaxElements)
                    {
                        _logger.LogWarning("Template exceeds max elements ({Count} > {Max}): {FilePath}",
                            template.Elements?.Count, MaxElements, filePath);
                        continue;
                    }
                    if ((template.Takes?.Count ?? 0) > MaxTakes)
                    {
                        _logger.LogWarning("Template exceeds max takes ({Count} > {Max}): {FilePath}",
                            template.Takes?.Count, MaxTakes, filePath);
                        continue;
                    }

                    // Generate ID from relative path or use template's own ID
                    var relativePath = Path.GetRelativePath(_templatesPath, filePath);
                    var templateId = template.Id ?? Path.GetFileNameWithoutExtension(relativePath);
                    var folderPath = Path.GetDirectoryName(filePath) ?? _templatesPath;

                    // Resolve linked scene path with security validation
                    var linkedScenePath = ResolveLinkedScenePath(filePath, template.LinkedScenePath);
                    var hasLinkedScene = !string.IsNullOrEmpty(linkedScenePath) && File.Exists(linkedScenePath);

                    templates.Add(new TemplateInfo
                    {
                        Id = templateId,
                        Name = template.Name ?? Path.GetFileNameWithoutExtension(filePath),
                        FilePath = filePath,
                        FolderPath = folderPath,
                        ElementCount = template.Elements?.Count ?? 0,
                        TakeCount = template.Takes?.Count ?? 0,
                        LinkedScenePath = linkedScenePath,
                        HasLinkedScene = hasLinkedScene
                    });

                    // Update cache
                    lock (_cacheLock)
                    {
                        _templatePathCache[templateId] = filePath;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse template JSON: {FilePath}", filePath);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to read template file: {FilePath}", filePath);
                }
            }

            lock (_cacheLock)
            {
                _lastCacheScan = DateTime.UtcNow;
            }

            _logger.LogDebug("Found {Count} templates in {Path}", templates.Count, _templatesPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied scanning templates directory: {Path}", _templatesPath);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error scanning templates directory: {Path}", _templatesPath);
        }

        return new TemplateListResponse
        {
            Templates = templates.OrderBy(t => t.Name).ToList(),
            TotalCount = templates.Count
        };
    }

    public async Task<TemplateDetailResponse?> GetTemplateDetailAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var template = await LoadTemplateAsync(templateId, cancellationToken).ConfigureAwait(false);
        if (template == null) return null;

        var filePath = FindTemplateFilePath(templateId);
        if (filePath == null) return null;

        var linkedScenePath = ResolveLinkedScenePath(filePath, template.LinkedScenePath);

        var elements = template.Elements
            .Select(e => new TemplateElementInfo
            {
                Id = e.Id ?? Guid.NewGuid().ToString(),
                Name = e.Name ?? "Unnamed",
                ElementType = e.ElementType,
                DefaultText = e.DefaultText,
                Placeholder = e.Placeholder,
                IsRequired = e.IsRequired,
                MaxLength = e.MaxLength,
                LinkedTransfunctionerId = e.LinkedTransfunctionerId,
                X = e.X,
                Y = e.Y,
                Width = e.Width,
                Height = e.Height,
                FontFamily = e.FontFamily,
                FontSize = e.FontSize,
                ForegroundColor = e.ForegroundColor,
                BackgroundColor = e.BackgroundColor
            })
            .ToList();

        var takes = template.Takes
            .Select(t => new TemplateTakeInfo
            {
                Id = t.Id ?? Guid.NewGuid().ToString(),
                Name = t.Name ?? "Unnamed",
                DurationFrames = t.TimelineDurationFrames,
                TargetAnimations = t.Actions
                    .Where(a => a.ActionType == 0) // Play actions
                    .SelectMany(a => a.TargetAnimationNames)
                    .Distinct()
                    .ToList()
            })
            .ToList();

        return new TemplateDetailResponse
        {
            Id = templateId,
            Name = template.Name ?? templateId,
            FilePath = filePath,
            LinkedScenePath = linkedScenePath,
            LinkedSceneExists = !string.IsNullOrEmpty(linkedScenePath) && File.Exists(linkedScenePath),
            Elements = elements,
            Takes = takes,
            CanvasWidth = template.CanvasWidth,
            CanvasHeight = template.CanvasHeight,
            BackgroundColor = template.BackgroundColor
        };
    }

    public async Task<TemplateModel?> LoadTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var filePath = FindTemplateFilePath(templateId);
        if (filePath == null)
        {
            _logger.LogWarning("Template not found: {TemplateId}", templateId);
            return null;
        }

        if (!File.Exists(filePath))
        {
            _logger.LogError("Template file does not exist: {FilePath}", filePath);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            var template = JsonSerializer.Deserialize<TemplateModel>(json, _jsonOptions);

            if (template == null)
            {
                _logger.LogError("Failed to deserialize template: {FilePath}", filePath);
                return null;
            }

            // Validate element and take counts (DARO_MAX_LAYERS = 64)
            const int MaxElements = 64;
            const int MaxTakes = 64;
            if ((template.Elements?.Count ?? 0) > MaxElements)
            {
                _logger.LogError("Template exceeds max elements ({Count} > {Max}): {FilePath}",
                    template.Elements?.Count, MaxElements, filePath);
                return null;
            }
            if ((template.Takes?.Count ?? 0) > MaxTakes)
            {
                _logger.LogError("Template exceeds max takes ({Count} > {Max}): {FilePath}",
                    template.Takes?.Count, MaxTakes, filePath);
                return null;
            }

            _logger.LogDebug("Loaded template: {Name} from {FilePath}", template.Name, filePath);
            return template;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error loading template: {FilePath}", filePath);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error loading template: {FilePath}", filePath);
            return null;
        }
    }

    public string? FindTemplateFilePath(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        // Security: Check for URL-encoded path traversal (double encoding attack)
        // Decode and re-check to catch %2e%2e%2f (../) or %252e%252e%252f
        string decodedId;
        try
        {
            decodedId = Uri.UnescapeDataString(templateId);
            // Also try double-decode for double encoding attacks
            var doubleDecoded = Uri.UnescapeDataString(decodedId);
            if (doubleDecoded != decodedId)
            {
                _logger.LogError("SECURITY: Double-encoded template ID detected: {TemplateId}", templateId);
                return null;
            }
        }
        catch (Exception)
        {
            _logger.LogError("SECURITY: Invalid encoding in template ID: {TemplateId}", templateId);
            return null;
        }

        // Validate templateId doesn't contain path traversal characters (check both original and decoded)
        if (templateId.Contains("..") || templateId.Contains('/') || templateId.Contains('\\') ||
            decodedId.Contains("..") || decodedId.Contains('/') || decodedId.Contains('\\'))
        {
            _logger.LogError("SECURITY: Invalid template ID with path characters: {TemplateId}", templateId);
            return null;
        }

        // Additional check for null bytes (can be used to truncate paths)
        if (templateId.Contains('\0') || decodedId.Contains('\0'))
        {
            _logger.LogError("SECURITY: Null byte in template ID: {TemplateId}", templateId);
            return null;
        }

        // Check cache first
        lock (_cacheLock)
        {
            if (_templatePathCache.TryGetValue(templateId, out var cachedPath))
            {
                if (File.Exists(cachedPath) && IsPathWithinDirectory(cachedPath, _templatesPath))
                    return cachedPath;

                // Remove stale entry
                _templatePathCache.Remove(templateId);
            }
        }

        // Note: Cache refresh happens during full scan below if needed
        // Removed fire-and-forget to prevent unobserved task exceptions

        // Try direct lookup patterns
        var possiblePaths = new[]
        {
            Path.Combine(_templatesPath, $"{templateId}{TemplateExtension}"),
            Path.Combine(_templatesPath, templateId, $"{templateId}{TemplateExtension}"),
            Path.Combine(_templatesPath, templateId, $"{Path.GetFileName(templateId)}{TemplateExtension}"),
        };

        foreach (var path in possiblePaths)
        {
            // Validate path stays within templates directory
            var normalizedPath = NormalizePath(path);
            if (normalizedPath == null || !IsPathWithinDirectory(normalizedPath, _templatesPath))
                continue;

            if (File.Exists(normalizedPath))
            {
                lock (_cacheLock)
                {
                    _templatePathCache[templateId] = normalizedPath;
                }
                return normalizedPath;
            }
        }

        // Full scan as last resort - check both filename and JSON Id field
        if (Directory.Exists(_templatesPath))
        {
            try
            {
                var files = Directory.GetFiles(_templatesPath, $"*{TemplateExtension}", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    // Validate path stays within templates directory
                    if (!IsPathWithinDirectory(file, _templatesPath))
                    {
                        _logger.LogError("SECURITY: Found template file outside templates directory: {FilePath}", file);
                        continue;
                    }

                    // First check filename match
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.Equals(templateId, StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_cacheLock)
                        {
                            _templatePathCache[templateId] = file;
                        }
                        return file;
                    }

                    // Also check JSON Id field (templates use GUID as Id, filename may differ)
                    try
                    {
                        var json = File.ReadAllText(file);
                        var template = JsonSerializer.Deserialize<TemplateModel>(json, _jsonOptions);
                        if (template?.Id != null)
                        {
                            // Cache this mapping for future lookups
                            lock (_cacheLock)
                            {
                                _templatePathCache[template.Id] = file;
                            }

                            if (template.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase))
                            {
                                return file;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip files with invalid JSON
                    }
                    catch (IOException)
                    {
                        // Skip files that can't be read
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied searching for template: {TemplateId}", templateId);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IO error searching for template: {TemplateId}", templateId);
            }
        }

        _logger.LogWarning("Template not found: {TemplateId}", templateId);
        return null;
    }

    public string? ResolveLinkedScenePath(string templateFilePath, string? linkedScenePath)
    {
        if (string.IsNullOrEmpty(linkedScenePath))
            return null;

        // Validate file extension
        if (!linkedScenePath.EndsWith(SceneExtension, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("SECURITY: Invalid scene file extension (must be {Extension}): {Path}",
                SceneExtension, linkedScenePath);
            return null;
        }

        string? resolvedPath;

        if (Path.IsPathRooted(linkedScenePath))
        {
            // Absolute path - normalize it
            resolvedPath = NormalizePath(linkedScenePath);
        }
        else
        {
            // Relative path - resolve relative to template file location
            var templateDir = Path.GetDirectoryName(templateFilePath);
            if (string.IsNullOrEmpty(templateDir))
            {
                _logger.LogError("SECURITY: Could not determine template directory for: {Path}", templateFilePath);
                return null;
            }

            resolvedPath = NormalizePath(Path.Combine(templateDir, linkedScenePath));
        }

        if (resolvedPath == null)
        {
            _logger.LogError("SECURITY: Failed to normalize scene path: {Path}", linkedScenePath);
            return null;
        }

        // Validate the path doesn't contain traversal sequences after normalization
        // (GetFullPath already resolves ".." but double-check the original input)
        if (linkedScenePath.Contains(".."))
        {
            _logger.LogWarning(
                "Security: Path traversal sequence detected in linked scene path: {LinkedScenePath}",
                linkedScenePath);
            return null;
        }

        // Log if path is outside the default scenes/templates directories (informational only)
        if (!IsPathWithinAllowedDirectories(resolvedPath))
        {
            _logger.LogDebug(
                "Linked scene path '{ResolvedPath}' is outside default directories (Templates: {TemplatesPath}, Scenes: {ScenesPath}). This is allowed for user-specified scene locations.",
                resolvedPath, _templatesPath, _scenesPath);
        }

        return resolvedPath;
    }

    /// <summary>
    /// Normalizes a path by resolving it to an absolute path and removing any .. or . components.
    /// Returns null if the path is invalid.
    /// </summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            // GetFullPath resolves relative paths and removes ../ components
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a path is within a specified directory (prevents path traversal).
    /// Also handles symbolic links by resolving them before comparison.
    /// </summary>
    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedDirectory = NormalizePath(directory);

        if (normalizedPath == null || normalizedDirectory == null)
            return false;

        // Check for symbolic links and resolve them
        var resolvedPath = ResolveFinalTarget(normalizedPath);
        if (resolvedPath == null)
            return false;

        // Ensure directory ends with separator for proper prefix comparison
        if (!normalizedDirectory.EndsWith(Path.DirectorySeparatorChar))
            normalizedDirectory += Path.DirectorySeparatorChar;

        return resolvedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves symbolic links in a path to get the final target path.
    /// Returns the original path if it's not a symlink, or null if resolution fails.
    /// </summary>
    private static string? ResolveFinalTarget(string path)
    {
        try
        {
            // Check if the file/directory exists
            if (!File.Exists(path) && !Directory.Exists(path))
                return path; // Path doesn't exist yet, return as-is

            // Try to resolve symlink (handles both file and directory symlinks)
            var fileInfo = new FileInfo(path);
            if (fileInfo.LinkTarget != null)
            {
                // It's a symbolic link - resolve to final target
                var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    return Path.GetFullPath(target.FullName);
                }
            }

            // Check directory symlinks
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.LinkTarget != null)
            {
                var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    return Path.GetFullPath(target.FullName);
                }
            }

            // Also check each component of the path for symlinks
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var currentPath = "";

            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                {
                    currentPath += Path.DirectorySeparatorChar;
                    continue;
                }

                currentPath = Path.Combine(currentPath, parts[i]);

                if (Directory.Exists(currentPath))
                {
                    var di = new DirectoryInfo(currentPath);
                    if (di.LinkTarget != null)
                    {
                        // This component is a symlink - resolve and reconstruct path
                        var resolved = di.ResolveLinkTarget(returnFinalTarget: true);
                        if (resolved != null)
                        {
                            // Rebuild remaining path
                            var remaining = string.Join(Path.DirectorySeparatorChar.ToString(),
                                parts.Skip(i + 1));
                            return Path.GetFullPath(Path.Combine(resolved.FullName, remaining));
                        }
                    }
                }
            }

            return path;
        }
        catch (Exception)
        {
            // If resolution fails, reject the path for safety
            return null;
        }
    }

    /// <summary>
    /// Checks if a path is within any of the allowed directories (templates or scenes).
    /// </summary>
    private bool IsPathWithinAllowedDirectories(string path)
    {
        return IsPathWithinDirectory(path, _templatesPath) ||
               IsPathWithinDirectory(path, _scenesPath);
    }
}
