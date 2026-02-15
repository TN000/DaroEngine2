// Designer/Engine/EngineRenderer.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DaroDesigner.Services;

namespace DaroDesigner.Engine
{
    public class EngineRenderer : IDisposable
    {
        // Windows API for precision timing
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        private WriteableBitmap _bitmap;
        private Thread _renderThread;
        private volatile bool _isRunning;
        private volatile bool _shouldStop;
        private volatile bool _isInitialized;
        private long _lastFrameNumber;
        private Dispatcher _uiDispatcher;

        // Thread synchronization for engine calls
        private readonly object _engineLock = new object();
        private readonly object _bitmapLock = new object();

        // FPS calculation
        private Stopwatch _fpsStopwatch;
        private int _frameCount;
        private double _lastFpsUpdate;

        // Thread-safe texture cache with O(1) LRU eviction
        private readonly Dictionary<string, LinkedListNode<TextureCacheEntry>> _textureCache = new Dictionary<string, LinkedListNode<TextureCacheEntry>>();
        private readonly LinkedList<TextureCacheEntry> _textureLruList = new LinkedList<TextureCacheEntry>();
        private readonly object _textureCacheLock = new object();
        private static int MaxTextureCacheSize => AppConstants.MaxTextureCacheSize;  // Use centralized constant

        // Thread-safe Spout receiver cache: senderName -> (receiverId, refCount)
        private readonly ConcurrentDictionary<string, (int Id, int RefCount)> _spoutReceiverCache = new ConcurrentDictionary<string, (int, int)>();
        private static int MaxSpoutReceiverCacheSize => AppConstants.MaxSpoutReceiverCacheSize;  // Use centralized constant

        // Device lost detection
        private volatile bool _deviceLostReported;

        // Thread-safe event delegate
        private Action _onFrameRendered;

        // Texture cache entry for LRU linked list with reference counting
        private class TextureCacheEntry
        {
            public string FilePath { get; }
            public int TextureId { get; }
            public int RefCount { get; set; }

            public TextureCacheEntry(string filePath, int textureId)
            {
                FilePath = filePath;
                TextureId = textureId;
                RefCount = 1;
            }
        }

        // Use centralized constants from AppConstants
        public static int FrameWidth => AppConstants.FrameWidth;
        public static int FrameHeight => AppConstants.FrameHeight;
        public static double TargetFps => AppConstants.TargetFps;

        public WriteableBitmap Bitmap
        {
            get { lock (_bitmapLock) { return _bitmap; } }
        }

        public bool IsInitialized
        {
            get => _isInitialized;
            private set => _isInitialized = value;
        }

        // FPS properties (read from UI thread, written from render thread)
        // Note: volatile cannot be used with double in C#. For FPS display,
        // slight staleness is acceptable - reads may see partially updated values
        // but this only affects the display, not rendering correctness.
        private double _currentFps;
        private double _frameTime;
        public double CurrentFps => _currentFps;
        public double FrameTime => _frameTime;

        // Thread-safe event
        public event Action OnFrameRendered
        {
            add { lock (_bitmapLock) { _onFrameRendered += value; } }
            remove { lock (_bitmapLock) { _onFrameRendered -= value; } }
        }

        public bool Initialize()
        {
            try
            {
                string dllPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConstants.EngineDllName);

                if (!System.IO.File.Exists(dllPath))
                {
                    MessageBox.Show($"{AppConstants.EngineDllName} not found at:\n{dllPath}", "DLL Missing");
                    return false;
                }

                // Enable high precision timer
                timeBeginPeriod(1);

                int result = DaroEngine.Daro_Initialize(FrameWidth, FrameHeight, TargetFps);

                if (result != DaroEngine.DARO_OK)
                {
                    string errorMsg = DaroEngine.GetErrorString(result);
                    MessageBox.Show($"Engine initialization failed:\n\nError code: {result}\n{errorMsg}", "Engine Init Failed");
                    return false;
                }

                // Validate struct size matches between C# and C++
                int cppStructSize = DaroEngine.Daro_GetStructSize();
                int csharpStructSize = Marshal.SizeOf<DaroLayerNative>();
                if (cppStructSize != csharpStructSize)
                {
                    DaroEngine.Daro_Shutdown();
                    MessageBox.Show(
                        $"CRITICAL: Struct size mismatch!\n\n" +
                        $"C++ DaroLayer: {cppStructSize} bytes\n" +
                        $"C# DaroLayerNative: {csharpStructSize} bytes\n\n" +
                        $"This will cause memory corruption. The interop definitions must be updated.",
                        "Interop Mismatch",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return false;
                }

                _bitmap = new WriteableBitmap(
                    FrameWidth, FrameHeight,
                    96, 96,
                    PixelFormats.Bgra32,
                    null
                );

                _fpsStopwatch = Stopwatch.StartNew();
                _uiDispatcher = Dispatcher.CurrentDispatcher;

                IsInitialized = true;
                return true;
            }
            catch (DllNotFoundException ex)
            {
                MessageBox.Show($"DLL not found:\n{ex.Message}", "DLL Error");
                return false;
            }
            catch (BadImageFormatException ex)
            {
                MessageBox.Show($"DLL architecture mismatch (need x64):\n{ex.Message}", "DLL Error");
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Engine init exception:\n{ex.GetType().Name}\n{ex.Message}", "Engine Error");
                return false;
            }
        }

        public void Shutdown()
        {
            Stop();

            // Clear caches - Daro_Shutdown will clean up engine resources
            lock (_textureCacheLock)
            {
                _textureCache.Clear();
                _textureLruList.Clear();
            }
            lock (_spoutCacheLock)
            {
                _spoutReceiverCache.Clear();
            }

            if (IsInitialized)
            {
                DaroEngine.Daro_Shutdown();
                IsInitialized = false;
            }

            // Release managed resources to prevent memory leaks
            lock (_bitmapLock)
            {
                _bitmap = null;
                _onFrameRendered = null;
            }
            _fpsStopwatch = null;
            _uiDispatcher = null;

            timeEndPeriod(1);
        }

        public void Start()
        {
            if (!IsInitialized || _isRunning) return;

            _shouldStop = false;
            _isRunning = true;
            _frameCount = 0;
            _lastFpsUpdate = _fpsStopwatch.Elapsed.TotalSeconds;

            _renderThread = new Thread(RenderThreadProc)
            {
                Name = "DaroRenderThread",
                Priority = ThreadPriority.Highest,
                IsBackground = true
            };
            _renderThread.Start();
        }

        public void Stop()
        {
            _shouldStop = true;
            _isRunning = false;

            // Wait for render thread to finish - must complete before Shutdown releases engine
            if (_renderThread != null && _renderThread.IsAlive)
            {
                if (!_renderThread.Join(5000)) // Wait up to 5s for render thread
                {
                    Logger.Warn("Render thread did not stop within 5s timeout");
                }
            }
            _renderThread = null;
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        private const uint COINIT_MULTITHREADED = 0x0;

        private void RenderThreadProc()
        {
            // Ensure COM is initialized on render thread for Media Foundation video operations
            int comHr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            bool comInitialized = (comHr == 0); // S_OK means we initialized it
            // S_FALSE (1) = already initialized, RPC_E_CHANGED_MODE = different model - both OK

            double targetFrameTime = 1000.0 / TargetFps; // 20ms for 50 FPS
            var stopwatch = Stopwatch.StartNew();
            double nextFrameTime = 0;

            try
            {
                while (!_shouldStop && IsInitialized)
                {
                    double currentTime = stopwatch.Elapsed.TotalMilliseconds;

                    // Wait until it's time for next frame
                    if (currentTime < nextFrameTime)
                    {
                        double waitTime = nextFrameTime - currentTime;

                        // Sleep for most of the wait time (leave 2ms for spin-wait)
                        if (waitTime > 2.0)
                        {
                            Thread.Sleep((int)(waitTime - 2.0));
                        }

                        // Spin-wait for precise timing
                        while (stopwatch.Elapsed.TotalMilliseconds < nextFrameTime)
                        {
                            Thread.SpinWait(10);
                        }
                    }

                    // Schedule next frame
                    nextFrameTime = stopwatch.Elapsed.TotalMilliseconds + targetFrameTime;

                    // Perform render (engine calls are thread-safe)
                    PerformRenderOnThread();
                }
            }
            catch (Exception ex)
            {
                // Log error and signal thread termination
                // Avoid crash - just stop rendering gracefully
                Logger.Error($"Render thread fatal error: {ex.GetType().Name}: {ex.Message}");
                _isRunning = false;

                // Notify UI thread about render failure (check if dispatcher is still available)
                var dispatcher = _uiDispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show($"Render thread error:\n{ex.Message}", "Render Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }), DispatcherPriority.Normal);
                }
            }
            finally
            {
                // Clean up COM on render thread
                if (comInitialized)
                    CoUninitialize();
            }
        }

        private void PerformRenderOnThread()
        {
            lock (_engineLock)
            {
                // Check for GPU device lost before rendering
                if (DaroEngine.Daro_IsDeviceLost())
                {
                    if (!_deviceLostReported)
                    {
                        _deviceLostReported = true;
                        Logger.Error("GPU device lost detected - rendering stopped");
                        var uiDispatcher = _uiDispatcher;
                        if (uiDispatcher != null && !uiDispatcher.HasShutdownStarted)
                        {
                            uiDispatcher.BeginInvoke(new Action(() =>
                            {
                                MessageBox.Show(
                                    "GPU device lost. Rendering has stopped.\n\n" +
                                    "This can happen after a GPU driver crash or update.\n" +
                                    "Please restart the application.",
                                    "GPU Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }), System.Windows.Threading.DispatcherPriority.Normal);
                        }
                    }
                    _shouldStop = true;
                    return;
                }

                DaroEngine.Daro_BeginFrame();
                DaroEngine.Daro_Render();
                DaroEngine.Daro_Present();
                DaroEngine.Daro_EndFrame();
            }

            // Copy frame to bitmap on UI thread (use Render priority to avoid blocking render thread)
            var dispatcher = _uiDispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(new Action(CopyFrameToBitmap), DispatcherPriority.Render);
            }
        }

        private void CopyFrameToBitmap()
        {
            if (!IsInitialized) return;

            WriteableBitmap bitmap;
            lock (_bitmapLock)
            {
                bitmap = _bitmap;
            }
            if (bitmap == null) return;

            if (DaroEngine.Daro_LockFrameBuffer(out IntPtr pData, out int width, out int height, out int stride))
            {
                try
                {
                    long frameNumber = DaroEngine.Daro_GetFrameNumber();

                    if (frameNumber != _lastFrameNumber)
                    {
                        _lastFrameNumber = frameNumber;
                        _frameCount++;

                        // Validate dimensions match bitmap to prevent buffer overflow
                        if (width != bitmap.PixelWidth || height != bitmap.PixelHeight ||
                            width <= 0 || height <= 0 || stride <= 0)
                        {
                            return;
                        }

                        bitmap.Lock();
                        try
                        {
                            long destSize = (long)bitmap.BackBufferStride * height;
                            long srcSize = (long)stride * height;
                            unsafe
                            {
                                Buffer.MemoryCopy(
                                    (void*)pData,
                                    (void*)bitmap.BackBuffer,
                                    destSize,
                                    srcSize
                                );
                            }
                            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                        }
                        finally
                        {
                            bitmap.Unlock();
                        }

                        // Thread-safe event invocation
                        Action handler;
                        lock (_bitmapLock)
                        {
                            handler = _onFrameRendered;
                        }
                        handler?.Invoke();
                    }
                }
                finally
                {
                    DaroEngine.Daro_UnlockFrameBuffer();
                }
            }

            // Update FPS every second (capture to local for shutdown safety)
            var stopwatch = _fpsStopwatch;
            if (stopwatch != null)
            {
                double now = stopwatch.Elapsed.TotalSeconds;
                if (now - _lastFpsUpdate >= 1.0)
                {
                    _currentFps = _frameCount / (now - _lastFpsUpdate);
                    _frameTime = 1000.0 / Math.Max(_currentFps, 0.001);
                    _frameCount = 0;
                    _lastFpsUpdate = now;
                }
            }
        }

        public void RenderSingleFrame()
        {
            if (!IsInitialized) return;

            // If render thread is running, just let it handle the next frame
            // This avoids race conditions between UI thread and render thread
            if (_isRunning) return;

            // Render on current thread (only when render thread is stopped)
            lock (_engineLock)
            {
                DaroEngine.Daro_BeginFrame();
                DaroEngine.Daro_Render();
                DaroEngine.Daro_Present();
                DaroEngine.Daro_EndFrame();
            }

            // Copy frame directly (we're on UI thread)
            CopyFrameToBitmap();
        }

        public void SetLayerCount(int count)
        {
            if (!IsInitialized) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_SetLayerCount(count);
            }
        }

        public void UpdateLayer(int index, DaroLayerNative layer)
        {
            if (!IsInitialized) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_UpdateLayer(index, ref layer);
            }
        }

        /// <summary>
        /// Updates multiple layers in a single lock acquisition.
        /// This is more efficient than calling UpdateLayer in a loop.
        /// </summary>
        /// <param name="layers">Array of layers to update</param>
        /// <param name="count">Number of layers to update (can be less than array length)</param>
        public void UpdateLayersBatch(DaroLayerNative[] layers, int count)
        {
            if (!IsInitialized || layers == null || count <= 0) return;

            lock (_engineLock)
            {
                DaroEngine.Daro_SetLayerCount(count);
                for (int i = 0; i < count && i < layers.Length; i++)
                {
                    DaroEngine.Daro_UpdateLayer(i, ref layers[i]);
                }
            }
        }

        /// <summary>
        /// Sets layer count and updates all layers in a single lock, then renders.
        /// Combines SetLayerCount + UpdateLayer loop + RenderSingleFrame into one lock.
        /// </summary>
        public void UpdateLayersAndRender(DaroLayerNative[] layers, int count)
        {
            if (!IsInitialized || layers == null || count <= 0) return;
            if (_isRunning) return; // Don't interfere with render thread

            lock (_engineLock)
            {
                DaroEngine.Daro_SetLayerCount(count);
                for (int i = 0; i < count && i < layers.Length; i++)
                {
                    DaroEngine.Daro_UpdateLayer(i, ref layers[i]);
                }
                DaroEngine.Daro_BeginFrame();
                DaroEngine.Daro_Render();
                DaroEngine.Daro_Present();
                DaroEngine.Daro_EndFrame();
            }

            // Copy frame to bitmap on UI thread
            var dispatcher = _uiDispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(new Action(CopyFrameToBitmap), DispatcherPriority.Render);
            }
        }

        public void ClearLayers()
        {
            if (!IsInitialized) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_ClearLayers();
            }
        }

        // ============== Spout Output ==============

        public bool EnableSpout(string name = "DaroEngine")
        {
            if (!IsInitialized) return false;
            lock (_engineLock)
            {
                return DaroEngine.Daro_EnableSpoutOutput(name);
            }
        }

        public void DisableSpout()
        {
            if (!IsInitialized) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_DisableSpoutOutput();
            }
        }

        public bool IsSpoutEnabled()
        {
            if (!IsInitialized) return false;
            lock (_engineLock)
            {
                return DaroEngine.Daro_IsSpoutEnabled();
            }
        }

        // ============== Texture Loading ==============

        public int LoadTexture(string filePath)
        {
            if (!IsInitialized || string.IsNullOrEmpty(filePath)) return -1;

            lock (_textureCacheLock)
            {
                // Check if already in cache - O(1)
                if (_textureCache.TryGetValue(filePath, out var existingNode))
                {
                    // Move to front of LRU list (most recently used) - O(1)
                    _textureLruList.Remove(existingNode);
                    _textureLruList.AddFirst(existingNode);
                    existingNode.Value.RefCount++;
                    return existingNode.Value.TextureId;
                }

                // Evict oldest entries if cache is full - O(1)
                while (_textureCache.Count >= MaxTextureCacheSize)
                {
                    int countBefore = _textureCache.Count;
                    EvictOldestTexture();
                    if (_textureCache.Count >= countBefore)
                        break; // All entries in use, cannot evict - allow cache to grow
                }

                // Load new texture
                int textureId;
                lock (_engineLock)
                {
                    textureId = DaroEngine.Daro_LoadTexture(filePath);
                }

                if (textureId > 0)
                {
                    // Add to cache and LRU list - O(1)
                    var entry = new TextureCacheEntry(filePath, textureId);
                    var node = _textureLruList.AddFirst(entry);
                    _textureCache[filePath] = node;
                }

                return textureId;
            }
        }

        private void EvictOldestTexture()
        {
            // Must be called within _textureCacheLock
            // Walk from tail to find first entry with refcount 0 (safe to evict)
            var node = _textureLruList.Last;
            while (node != null)
            {
                if (node.Value.RefCount <= 0)
                {
                    var entry = node.Value;
                    _textureLruList.Remove(node);
                    _textureCache.Remove(entry.FilePath);

                    lock (_engineLock)
                    {
                        DaroEngine.Daro_UnloadTexture(entry.TextureId);
                    }
                    Logger.Debug($"Evicted texture from cache: {entry.FilePath}");
                    return;
                }
                node = node.Previous;
            }
            // All entries are in use - can't evict, cache will grow beyond limit
            Logger.Warn("Texture cache full but all entries in use, cannot evict");
        }

        public void UnloadTexture(string filePath)
        {
            if (!IsInitialized) return;

            lock (_textureCacheLock)
            {
                if (_textureCache.TryGetValue(filePath, out var node))
                {
                    node.Value.RefCount--;
                    if (node.Value.RefCount <= 0)
                    {
                        _textureLruList.Remove(node);
                        _textureCache.Remove(filePath);

                        lock (_engineLock)
                        {
                            DaroEngine.Daro_UnloadTexture(node.Value.TextureId);
                        }
                    }
                }
            }
        }

        public int GetTextureId(string filePath)
        {
            lock (_textureCacheLock)
            {
                if (_textureCache.TryGetValue(filePath, out var node))
                {
                    // Move to front of LRU list (most recently used) - O(1)
                    _textureLruList.Remove(node);
                    _textureLruList.AddFirst(node);
                    return node.Value.TextureId;
                }
            }
            return -1;
        }

        // ============== Video Playback ==============

        public int LoadVideo(string filePath)
        {
            if (!IsInitialized || string.IsNullOrEmpty(filePath))
            {
                Debug.WriteLine($"[DaroVideo] C# LoadVideo: skip - initialized={IsInitialized}, path='{filePath}'");
                return -1;
            }
            lock (_engineLock)
            {
                int id = DaroEngine.Daro_LoadVideo(filePath);
                Debug.WriteLine($"[DaroVideo] C# LoadVideo: path='{filePath}' -> id={id}");
                return id;
            }
        }

        public void UnloadVideo(int videoId)
        {
            if (!IsInitialized || videoId <= 0) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_UnloadVideo(videoId);
            }
        }

        public void PlayVideo(int videoId)
        {
            if (!IsInitialized || videoId <= 0) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_PlayVideo(videoId);
            }
        }

        public void PauseVideo(int videoId)
        {
            if (!IsInitialized || videoId <= 0) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_PauseVideo(videoId);
            }
        }

        public void StopVideo(int videoId)
        {
            if (!IsInitialized || videoId <= 0) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_StopVideo(videoId);
            }
        }

        public void SeekVideo(int videoId, int frame)
        {
            if (!IsInitialized || videoId <= 0 || frame < 0) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_SeekVideo(videoId, frame);
            }
        }

        public void SeekVideoTime(int videoId, double seconds)
        {
            if (!IsInitialized || videoId <= 0 || seconds < 0) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_SeekVideoTime(videoId, seconds);
            }
        }

        public void SetVideoLoop(int videoId, bool loop)
        {
            if (!IsInitialized || videoId <= 0) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_SetVideoLoop(videoId, loop);
            }
        }

        public void SetVideoAlpha(int videoId, bool alpha)
        {
            if (!IsInitialized || videoId <= 0) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_SetVideoAlpha(videoId, alpha);
            }
        }

        // ============== Edge Antialiasing ==============

        public void SetEdgeSmoothing(float width)
        {
            if (!IsInitialized) return;
            lock (_engineLock)
            {
                DaroEngine.Daro_SetEdgeSmoothing(width);
            }
        }

        // ============== Spout Input ==============

        /// <summary>
        /// Validates a Spout sender name to prevent injection attacks.
        /// Allows alphanumeric characters, spaces, dashes, underscores, and dots.
        /// </summary>
        private static bool IsValidSpoutSenderName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Length > 256) return false; // Spout max name length

            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_' && c != '.')
                    return false;
            }
            return true;
        }

        // Reusable buffer for Spout sender names (avoids per-call allocation)
        private readonly byte[] _spoutNameBuffer = new byte[256];

        public IReadOnlyList<string> GetSpoutSenders()
        {
            var senders = new List<string>();
            if (!IsInitialized) return senders;

            lock (_engineLock)
            {
                int count = DaroEngine.Daro_GetSpoutSenderCount();
                for (int i = 0; i < count; i++)
                {
                    // Reuse buffer to avoid per-sender allocation
                    if (DaroEngine.Daro_GetSpoutSenderName(i, _spoutNameBuffer, 256))
                    {
                        // Find null terminator
                        int len = Array.IndexOf(_spoutNameBuffer, (byte)0);
                        if (len < 0) len = _spoutNameBuffer.Length;

                        string name = Encoding.ASCII.GetString(_spoutNameBuffer, 0, len);
                        if (!string.IsNullOrEmpty(name))
                            senders.Add(name);
                    }
                }
            }
            return senders;
        }

        // Lock for Spout receiver cache compound operations
        private readonly object _spoutCacheLock = new object();

        public int ConnectSpoutReceiver(string senderName)
        {
            if (!IsInitialized || string.IsNullOrEmpty(senderName)) return -1;

            // Security: Validate sender name
            if (!IsValidSpoutSenderName(senderName))
            {
                Logger.Warn($"Invalid Spout sender name rejected: {senderName}");
                return -1;
            }

            lock (_spoutCacheLock)
            {
                // Check if already connected - increment refcount
                if (_spoutReceiverCache.TryGetValue(senderName, out var existing))
                {
                    _spoutReceiverCache[senderName] = (existing.Id, existing.RefCount + 1);
                    return existing.Id;
                }

                // Check cache limit
                if (_spoutReceiverCache.Count >= MaxSpoutReceiverCacheSize)
                {
                    Logger.Warn($"Spout receiver cache full (max {MaxSpoutReceiverCacheSize}), cannot connect to {senderName}");
                    return -1;
                }

                // Connect new receiver
                int receiverId;
                lock (_engineLock)
                {
                    receiverId = DaroEngine.Daro_ConnectSpoutReceiver(senderName);
                }

                if (receiverId > 0)
                {
                    _spoutReceiverCache[senderName] = (receiverId, 1);
                }

                return receiverId;
            }
        }

        public void DisconnectSpoutReceiver(string senderName)
        {
            if (!IsInitialized) return;

            lock (_spoutCacheLock)
            {
                if (_spoutReceiverCache.TryGetValue(senderName, out var entry))
                {
                    if (entry.RefCount <= 1)
                    {
                        _spoutReceiverCache.TryRemove(senderName, out _);
                        lock (_engineLock)
                        {
                            try
                            {
                                DaroEngine.Daro_DisconnectSpoutReceiver(entry.Id);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Failed to disconnect Spout receiver '{senderName}' (id={entry.Id})", ex);
                            }
                        }
                    }
                    else
                    {
                        _spoutReceiverCache[senderName] = (entry.Id, entry.RefCount - 1);
                    }
                }
            }
        }

        public int GetSpoutReceiverId(string senderName)
        {
            lock (_spoutCacheLock)
            {
                if (_spoutReceiverCache.TryGetValue(senderName, out var entry))
                    return entry.Id;
                return -1;
            }
        }

        public void Dispose()
        {
            Shutdown();
            GC.SuppressFinalize(this);
        }
    }
}