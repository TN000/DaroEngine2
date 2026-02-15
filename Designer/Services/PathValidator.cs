// Designer/Services/PathValidator.cs
// Centralized path validation to prevent path traversal attacks
using System;
using System.IO;

namespace DaroDesigner.Services
{
    /// <summary>
    /// Provides centralized path validation to prevent path traversal attacks.
    /// </summary>
    public static class PathValidator
    {
        // Lazy-initialized allowed base paths
        private static readonly Lazy<string[]> AllowedBasePaths = new(() =>
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var daroEnginePath = Path.GetFullPath(Path.Combine(documentsPath, "DaroEngine"));
            var appBasePath = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            var tempPath = Path.GetFullPath(Path.GetTempPath());

            return new[]
            {
                daroEnginePath,
                appBasePath,
                tempPath
            };
        });

        /// <summary>
        /// Checks if a path contains path traversal sequences.
        /// </summary>
        public static bool ContainsTraversal(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            return path.Contains("..") ||
                   path.Contains("%2e%2e", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("%252e", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates that a file path is within allowed directories.
        /// </summary>
        /// <param name="filePath">The file path to validate.</param>
        /// <returns>True if the path is safe to access, false otherwise.</returns>
        public static bool IsPathAllowed(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            try
            {
                // Check for obvious traversal attempts first
                if (ContainsTraversal(filePath))
                    return false;

                // Resolve to absolute path and normalize
                var fullPath = Path.GetFullPath(filePath);

                // Check if path is within any allowed base directory
                foreach (var basePath in AllowedBasePaths.Value)
                {
                    if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                    {
                        // Additional check: ensure no path traversal after normalization
                        var relativePath = fullPath.Substring(basePath.Length);
                        if (!relativePath.Contains(".."))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Validates and returns the normalized path, or null if invalid.
        /// </summary>
        /// <param name="filePath">The file path to validate.</param>
        /// <returns>The normalized path if valid, null otherwise.</returns>
        public static string ValidateAndNormalize(string filePath)
        {
            if (!IsPathAllowed(filePath))
                return null;

            return Path.GetFullPath(filePath);
        }

        /// <summary>
        /// Checks if a path is a valid file extension for templates.
        /// </summary>
        public static bool IsValidTemplateExtension(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            var ext = Path.GetExtension(filePath);
            return ext.Equals(".dtemplate", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if a path is a valid file extension for projects/scenes.
        /// </summary>
        public static bool IsValidProjectExtension(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            var ext = Path.GetExtension(filePath);
            return ext.Equals(".daro", StringComparison.OrdinalIgnoreCase);
        }
    }
}
