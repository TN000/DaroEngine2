// Designer/Models/AppSettingsModel.cs
using System;
using System.IO;
using System.Text.Json;
using DaroDesigner.Services;

namespace DaroDesigner.Models
{
    public enum EdgeSmoothingLevel
    {
        Off = 0,
        Low = 1,
        Medium = 2,
        High = 3
    }

    public class AppSettingsData
    {
        public int EdgeSmoothing { get; set; } = (int)EdgeSmoothingLevel.Medium;
    }

    public static class AppSettingsModel
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaroDesigner");

        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        public static EdgeSmoothingLevel EdgeSmoothing { get; set; } = EdgeSmoothingLevel.Medium;

        public static float EdgeSmoothingToFloat(EdgeSmoothingLevel level) => level switch
        {
            EdgeSmoothingLevel.Off => 0.0f,
            EdgeSmoothingLevel.Low => 0.5f,
            EdgeSmoothingLevel.Medium => 1.0f,
            EdgeSmoothingLevel.High => 1.5f,
            _ => 1.0f
        };

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;

                var json = File.ReadAllText(SettingsPath);
                var data = JsonSerializer.Deserialize<AppSettingsData>(json);
                if (data != null)
                {
                    EdgeSmoothing = (EdgeSmoothingLevel)data.EdgeSmoothing;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load settings: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);

                var data = new AppSettingsData
                {
                    EdgeSmoothing = (int)EdgeSmoothing
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

                // Atomic write: write to temp then move
                var tmpPath = SettingsPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, SettingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save settings: {ex.Message}");
            }
        }
    }
}
