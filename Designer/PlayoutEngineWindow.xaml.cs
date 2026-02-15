// Designer/PlayoutEngineWindow.xaml.cs
// Pure rendering engine window - no UI elements, all status in PlayoutWindow
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DaroDesigner.Engine;
using DaroDesigner.Models;
using DaroDesigner.Services;

namespace DaroDesigner
{
    public partial class PlayoutEngineWindow : Window
    {
        #region Fields

        private EngineRenderer _engine;
        private DispatcherTimer _playbackTimer;
        private ProjectModel _project;
        private volatile AnimationModel _animation; // volatile for cross-thread access
        private volatile int _frame;                 // volatile for cross-thread access
        private volatile bool _playing;              // volatile for cross-thread access
        private volatile bool _closing;              // volatile for shutdown coordination
        private string _spoutName = "DaroPlayout";

        // Time-based animation tracking (fixes drift issue)
        private Stopwatch _playbackStopwatch;

        #endregion

        #region Properties

        /// <summary>Current FPS from engine.</summary>
        public double Fps => _engine?.CurrentFps ?? 0;

        /// <summary>Whether animation is currently playing.</summary>
        public bool IsPlaying => _playing;

        /// <summary>Current playback frame.</summary>
        public int Frame => _frame;

        /// <summary>Total frames in current animation.</summary>
        public int TotalFrames => _animation?.LengthFrames ?? 0;

        /// <summary>Name of current animation.</summary>
        public string AnimationName => _animation?.Name ?? "";

        /// <summary>Whether engine is ready.</summary>
        public bool IsReady => _engine != null && _engine.IsInitialized;

        #endregion

        #region Constructor

        public PlayoutEngineWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeEngine();
        }

        #endregion

        #region Engine Initialization

        private void InitializeEngine()
        {
            try
            {
                _engine = new EngineRenderer();

                if (!_engine.Initialize())
                {
                    MessageBox.Show("Failed to initialize rendering engine.",
                        "Engine Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _engine.OnFrameRendered += OnFrameRendered;

                // Playback timer - 50 FPS
                _playbackTimer = new DispatcherTimer(DispatcherPriority.Render);
                _playbackTimer.Interval = TimeSpan.FromMilliseconds(AppConstants.PlaybackIntervalMs);
                _playbackTimer.Tick += OnPlaybackTick;
                _playbackTimer.Start();

                _engine.Start();

                // Apply edge smoothing from app settings
                float edgeWidth = Models.AppSettingsModel.EdgeSmoothingToFloat(Models.AppSettingsModel.EdgeSmoothing);
                _engine.SetEdgeSmoothing(edgeWidth);
            }
            catch (Exception ex)
            {
                Logger.Error("Engine initialization failed", ex);
                MessageBox.Show($"Engine initialization failed:\n{ex.Message}",
                    "Engine Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnFrameRendered()
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_engine?.Bitmap != null)
                {
                    EnginePreview.Source = _engine.Bitmap;
                }
            }, DispatcherPriority.Render);
        }

        #endregion

        #region Playback

        private void OnPlaybackTick(object sender, EventArgs e)
        {
            // Wrap in try-catch to prevent unhandled exceptions from crashing playback
            try
            {
                // Capture volatile references locally to avoid TOCTOU race conditions
                var animation = _animation;
                var stopwatch = _playbackStopwatch;
                if (!_playing || animation == null || stopwatch == null) return;

                // Calculate frame from elapsed time (fixes timing drift)
                // Clamp to animation length to prevent overflow for very long running times
                var elapsed = stopwatch.Elapsed;
                int calculatedFrame = (int)Math.Min(elapsed.TotalSeconds * AppConstants.TargetFps, animation.LengthFrames);

                // Check if animation has completed based on time
                if (calculatedFrame >= animation.LengthFrames)
                {
                    _frame = animation.LengthFrames - 1;
                    _playing = false;
                    stopwatch.Stop();
                    RenderFrame(_frame); // Render final frame
                    return;
                }

                // Only render if frame has changed (avoids redundant renders)
                if (calculatedFrame != _frame)
                {
                    _frame = calculatedFrame;
                    RenderFrame(_frame);
                }
            }
            catch (Exception ex)
            {
                // Log but don't crash - playback will stop
                Logger.Error("Error in playback tick", ex);
                _playing = false;
                _playbackStopwatch?.Stop();
            }
        }

        // Reusable buffer for layer updates to avoid allocations
        private DaroLayerNative[] _layerBuffer;

        private void RenderFrame(int frame)
        {
            var animation = _animation; // Capture volatile reference locally
            if (_engine == null || !_engine.IsInitialized || animation == null) return;

            var layers = animation.Layers;
            int layerCount = layers.Count;

            // Reuse buffer to avoid per-frame allocations
            if (_layerBuffer == null || _layerBuffer.Length < layerCount)
            {
                _layerBuffer = new DaroLayerNative[Math.Max(layerCount, DaroConstants.MAX_LAYERS)];
            }

            // Apply animation and build batch
            for (int i = 0; i < layerCount; i++)
            {
                layers[i].ApplyAnimationAtFrame(frame);
                _layerBuffer[i] = layers[i].ToNative();
            }

            // Batch update all layers in single lock
            _engine.UpdateLayersBatch(_layerBuffer, layerCount);

            // Sync video layers to animation frame
            for (int i = 0; i < layerCount; i++)
            {
                if (layers[i].TextureSource == TextureSourceType.VideoFile && layers[i].VideoId > 0)
                {
                    double seconds = frame / 50.0;
                    _engine.SeekVideoTime(layers[i].VideoId, seconds);
                }
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Loads a scene and applies filled data. Returns true on success.
        /// </summary>
        public bool LoadScene(string scenePath, PlaylistItemModel item)
        {
            if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath))
            {
                Debug.WriteLine($"[Engine] Scene not found: {scenePath}");
                return false;
            }

            // Security: Validate path before file access
            if (!PathValidator.IsPathAllowed(scenePath))
            {
                Debug.WriteLine($"[Engine] Security: Invalid scene path blocked: {scenePath}");
                return false;
            }

            try
            {
                // Release GPU resources from previous scene before loading new one
                ReleaseAllResources();

                var json = File.ReadAllText(scenePath);
                var data = JsonSerializer.Deserialize<ProjectData>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    MaxDepth = AppConstants.MaxJsonDepth // Prevent stack overflow from deeply nested JSON
                });

                if (data == null)
                {
                    Debug.WriteLine("[Engine] Failed to deserialize scene");
                    return false;
                }

                _project = ProjectModel.FromSerializable(data);

                // Apply transfunctioner values
                if (item?.FilledData != null)
                {
                    foreach (var anim in _project.Animations)
                    {
                        foreach (var layer in anim.Layers)
                        {
                            foreach (var tf in layer.Transfunctioners)
                            {
                                tf.ParentLayer = layer;
                                if (item.FilledData.TryGetValue(tf.Id, out var value))
                                {
                                    tf.CurrentValue = value;
                                }
                            }
                        }
                    }
                }

                // Select first animation
                if (_project.Animations.Count > 0)
                {
                    _animation = _project.Animations[0];
                    SendToEngine(_animation);
                    _frame = 0;
                    RenderFrame(0);
                }

                Debug.WriteLine($"[Engine] Loaded: {_project.Animations.Count} animations");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Scene load error", ex);
                Debug.WriteLine($"[Engine] Load error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Plays the specified animation (or first Play action from take).
        /// </summary>
        public void Play(TemplateTakeModel take = null)
        {
            if (_project == null || _project.Animations.Count == 0)
            {
                Debug.WriteLine("[Engine] Play: No project loaded");
                return;
            }

            // Find target animation
            AnimationModel target = null;

            if (take != null)
            {
                var playAction = take.Actions.FirstOrDefault(a => a.ActionType == TakeActionType.Play);
                if (playAction?.TargetAnimationNames?.Count > 0)
                {
                    var name = playAction.TargetAnimationNames[0];
                    target = _project.Animations.FirstOrDefault(a => a.Name == name);
                }
            }

            target ??= _project.Animations[0];

            _animation = target;
            _frame = 0;
            _playing = true;

            // Start stopwatch for time-based animation
            _playbackStopwatch ??= new Stopwatch();
            _playbackStopwatch.Restart();

            SendToEngine(_animation);
            RenderFrame(0);

            Debug.WriteLine($"[Engine] Playing: {_animation.Name}");
        }

        /// <summary>
        /// Stops playback.
        /// </summary>
        public void Stop()
        {
            _playing = false;
            _playbackStopwatch?.Stop();
            Debug.WriteLine("[Engine] Stopped");
        }

        /// <summary>
        /// Clears the screen.
        /// </summary>
        public void Clear()
        {
            _playing = false;
            _playbackStopwatch?.Stop();
            _playbackStopwatch?.Reset();
            _animation = null;
            _project = null;
            _frame = 0;

            _engine?.ClearLayers();
            Debug.WriteLine("[Engine] Cleared");
        }

        /// <summary>
        /// Configures Spout output.
        /// </summary>
        public void SetSpout(bool enabled, string name)
        {
            _spoutName = name;
            if (_engine != null && _engine.IsInitialized)
            {
                if (enabled)
                    _engine.EnableSpout(name);
                else
                    _engine.DisableSpout();
            }
        }

        #endregion

        #region Helpers

        private void SendToEngine(AnimationModel animation)
        {
            if (_engine == null || !_engine.IsInitialized || animation == null) return;

            var layers = animation.Layers;
            int layerCount = layers.Count;

            if (layerCount == 0)
            {
                _engine.ClearLayers();
                return;
            }

            // Ensure GPU resources are loaded for each layer
            for (int i = 0; i < layerCount; i++)
            {
                EnsureLayerResources(layers[i]);
            }

            // Reuse buffer to avoid allocations
            if (_layerBuffer == null || _layerBuffer.Length < layerCount)
            {
                _layerBuffer = new DaroLayerNative[Math.Max(layerCount, DaroConstants.MAX_LAYERS)];
            }

            for (int i = 0; i < layerCount; i++)
            {
                _layerBuffer[i] = layers[i].ToNative();
            }

            // Batch update all layers in single lock
            _engine.UpdateLayersBatch(_layerBuffer, layerCount);
        }

        private void EnsureLayerResources(LayerModel layer)
        {
            if (layer.TextureSource == TextureSourceType.ImageFile &&
                !string.IsNullOrEmpty(layer.TexturePath) &&
                layer.TextureId <= 0)
            {
                layer.TextureId = _engine.LoadTexture(layer.TexturePath);
            }

            if (layer.TextureSource == TextureSourceType.SpoutInput &&
                !string.IsNullOrEmpty(layer.SpoutSenderName) &&
                layer.SpoutReceiverId <= 0)
            {
                layer.SpoutReceiverId = _engine.ConnectSpoutReceiver(layer.SpoutSenderName);
            }

            if (layer.TextureSource == TextureSourceType.VideoFile &&
                !string.IsNullOrEmpty(layer.TexturePath) &&
                layer.VideoId <= 0)
            {
                layer.VideoId = _engine.LoadVideo(layer.TexturePath);
                if (layer.VideoId > 0)
                {
                    _engine.PlayVideo(layer.VideoId);
                }
            }
        }

        private void ReleaseAllResources()
        {
            if (_engine == null || _project == null) return;

            foreach (var animation in _project.Animations)
            {
                foreach (var layer in animation.Layers)
                {
                    if (!string.IsNullOrEmpty(layer.TexturePath))
                        _engine.UnloadTexture(layer.TexturePath);

                    if (!string.IsNullOrEmpty(layer.SpoutSenderName))
                        _engine.DisconnectSpoutReceiver(layer.SpoutSenderName);

                    if (layer.VideoId > 0)
                    {
                        _engine.UnloadVideo(layer.VideoId);
                        layer.VideoId = -1;
                    }
                }
            }
        }

        #endregion

        #region Window Events

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closing) return;
            _closing = true;

            // Unsubscribe event handlers
            Loaded -= OnLoaded;

            if (_playbackTimer != null)
            {
                _playbackTimer.Stop();
                _playbackTimer.Tick -= OnPlaybackTick;
            }

            if (_engine != null)
            {
                _engine.OnFrameRendered -= OnFrameRendered;
                _engine.Stop();
                ReleaseAllResources();
                _engine.Dispose();
                _engine = null;
            }
        }

        #endregion
    }
}
