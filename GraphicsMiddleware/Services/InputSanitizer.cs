using System.Text.RegularExpressions;

namespace GraphicsMiddleware.Services;

/// <summary>
/// Input sanitization utilities for user-provided data.
/// Prevents injection attacks and ensures data integrity.
/// </summary>
public static partial class InputSanitizer
{
    /// <summary>
    /// Maximum length for item/template names.
    /// </summary>
    public const int MaxNameLength = 256;

    /// <summary>
    /// Maximum length for filled data field values.
    /// </summary>
    public const int MaxFieldValueLength = 4096;

    /// <summary>
    /// Maximum length for template IDs.
    /// </summary>
    public const int MaxIdLength = 128;

    /// <summary>
    /// Maximum number of entries in filled data dictionary.
    /// Prevents DoS attacks via excessive key-value pairs.
    /// </summary>
    public const int MaxFilledDataEntries = 100;

    /// <summary>
    /// Sanitizes an item or template name.
    /// Removes control characters, null bytes, and trims whitespace.
    /// </summary>
    public static string? SanitizeName(string? input, int maxLength = MaxNameLength)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        // Remove null bytes
        var sanitized = input.Replace("\0", string.Empty);

        // Remove control characters (except common whitespace)
        sanitized = ControlCharRegex().Replace(sanitized, string.Empty);

        // Normalize whitespace (collapse multiple spaces)
        sanitized = MultipleSpacesRegex().Replace(sanitized, " ");

        // Trim and enforce max length
        sanitized = sanitized.Trim();
        if (sanitized.Length > maxLength)
            sanitized = sanitized[..maxLength].TrimEnd();

        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    /// <summary>
    /// Sanitizes a template/item ID.
    /// Only allows alphanumeric, dash, underscore, and dot.
    /// </summary>
    public static string? SanitizeId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var sanitized = input.Trim();

        // Remove characters that are not alphanumeric, dash, underscore, or dot
        sanitized = InvalidIdCharRegex().Replace(sanitized, string.Empty);

        // Enforce max length
        if (sanitized.Length > MaxIdLength)
            sanitized = sanitized[..MaxIdLength];

        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    /// <summary>
    /// Sanitizes a filled data field value.
    /// Removes null bytes and control characters, enforces length limit.
    /// </summary>
    public static string SanitizeFieldValue(string? input, int maxLength = MaxFieldValueLength)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // Remove null bytes
        var sanitized = input.Replace("\0", string.Empty);

        // Remove control characters except tab, newline, carriage return
        sanitized = ControlCharExceptNewlineRegex().Replace(sanitized, string.Empty);

        // Enforce max length
        if (sanitized.Length > maxLength)
            sanitized = sanitized[..maxLength];

        return sanitized;
    }

    /// <summary>
    /// Sanitizes all values in a filled data dictionary.
    /// Enforces maximum entry count to prevent DoS attacks.
    /// </summary>
    public static Dictionary<string, string>? SanitizeFilledData(Dictionary<string, string>? filledData)
    {
        if (filledData == null || filledData.Count == 0)
            return filledData;

        var sanitized = new Dictionary<string, string>(Math.Min(filledData.Count, MaxFilledDataEntries));
        var entryCount = 0;

        foreach (var (key, value) in filledData)
        {
            // Enforce maximum entry count
            if (entryCount >= MaxFilledDataEntries)
                break;

            // Sanitize the key (field ID)
            var sanitizedKey = SanitizeId(key);
            if (string.IsNullOrEmpty(sanitizedKey))
                continue;

            // Sanitize the value
            var sanitizedValue = SanitizeFieldValue(value);
            sanitized[sanitizedKey] = sanitizedValue;
            entryCount++;
        }

        return sanitized;
    }

    /// <summary>
    /// Validates that a string doesn't contain path traversal sequences.
    /// </summary>
    public static bool ContainsPathTraversal(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return false;

        // Check for path traversal patterns
        return input.Contains("..") ||
               input.Contains("./") ||
               input.Contains(".\\") ||
               input.Contains("//") ||
               input.Contains("\\\\");
    }

    // Regex for control characters (0x00-0x1F except tab, newline, carriage return)
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlCharRegex();

    // Regex for control characters except tab, newline, carriage return
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlCharExceptNewlineRegex();

    // Regex for multiple consecutive spaces
    [GeneratedRegex(@"  +")]
    private static partial Regex MultipleSpacesRegex();

    // Regex for invalid ID characters (anything not alphanumeric, dash, underscore, dot)
    [GeneratedRegex(@"[^a-zA-Z0-9\-_.]")]
    private static partial Regex InvalidIdCharRegex();
}
