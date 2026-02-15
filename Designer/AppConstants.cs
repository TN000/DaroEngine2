// Designer/AppConstants.cs
// Application-wide constants to avoid magic numbers
namespace DaroDesigner
{
    public static class AppConstants
    {
        // Rendering
        public const int FrameWidth = 1920;
        public const int FrameHeight = 1080;
        public const double TargetFps = 50.0;
        public const int PlaybackIntervalMs = 20;  // 1000ms / 50 FPS = 20ms

        // UI Update Intervals
        public const int StatsUpdateIntervalMs = 250;
        public const int StatusUpdateIntervalMs = 100;

        // Timeline
        public const double TimelineStartOffset = 10;
        public const double TimelineKeyframeSize = 10;
        public const double TimelineRulerHeight = 25;
        public const double TimelineRowHeight = 24;
        public const int DefaultAnimationLength = 250;  // frames

        // Cache Limits
        public const int MaxTextureCacheSize = 100;
        public const int MaxSpoutReceiverCacheSize = 16;

        // Layer Properties
        public const float DefaultOpacity = 1.0f;
        public const float DefaultFontSize = 24.0f;
        public const float DefaultLineHeight = 1.2f;

        // Logging
        public const int LogRetentionDays = 7;
        public const string LogFilePattern = "DaroDesigner_*.log";

        // File Patterns
        public const string ProjectFileFilter = "Daro Project (*.daro)|*.daro|All Files (*.*)|*.*";
        public const string TemplateFileFilter = "Daro Template (*.dtemplate)|*.dtemplate|All Files (*.*)|*.*";
        public const string ImageFileFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All Files (*.*)|*.*";

        // Security Limits
        public const int MaxJsonDepth = 64;
        public const int MaxMessageLength = 1024;
        public const int MaxConnectionsPerIp = 10;
        public const int RateLimitPerSecond = 100;

        // Mosart Protocol
        public const int MosartPort = 5555;
        public const int MosartReadTimeoutMs = 10000;
        public const int MosartConnectionTimeoutMs = 30000;

        // Graphics Middleware
        public const string MiddlewareExeName = "GraphicsMiddleware.exe";
        public const string MiddlewareSubDir = "Middleware";
        public const string MiddlewareHealthUrl = "http://localhost:5000/health";
        public const string MiddlewareBrowseUrl = "http://localhost:5000";
        public const int MiddlewareHealthPollMs = 500;
        public const int MiddlewareHealthTimeoutMs = 15000;
        public const int MiddlewareShutdownTimeoutMs = 5000;

        // File Extensions
        public const string ProjectExtension = ".daro";
        public const string TemplateExtension = ".dtemplate";

        // Native DLL
        public const string EngineDllName = "DaroEngine.dll";
    }
}
