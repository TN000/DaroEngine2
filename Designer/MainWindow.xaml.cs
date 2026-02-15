// Designer/MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using DaroDesigner.Engine;
using DaroDesigner.Models;
using DaroDesigner.Services;

namespace DaroDesigner
{
    public partial class MainWindow : Window
    {
        #region Fields

        private ProjectModel _project;
        private EngineRenderer _engine;
        private DispatcherTimer _playbackTimer;
        private DispatcherTimer _statsTimer;
        private Services.AutosaveService _autosave;

        // UI State
        private bool _isUpdatingUI;

        // Preview interaction
        private bool _isDraggingPreview;
        private Point _previewDragStart;
        private LayerModel _draggedLayer;

        // Timeline interaction
        private bool _isDraggingPlayhead;

        // Timeline offset uses AppConstants.TimelineStartOffset

        // Cached brushes for performance (static readonly to avoid allocation)
        private static readonly SolidColorBrush BrushLayerSelected = new SolidColorBrush(Color.FromRgb(50, 50, 60));
        private static readonly SolidColorBrush BrushLayerNormal = new SolidColorBrush(Color.FromRgb(40, 40, 40));
        private static readonly SolidColorBrush BrushTrackSelected = new SolidColorBrush(Color.FromRgb(45, 45, 55));
        private static readonly SolidColorBrush BrushTrackNormal = new SolidColorBrush(Color.FromRgb(35, 35, 38));
        private static readonly SolidColorBrush BrushKeyframeSelected = Brushes.White;
        private static readonly SolidColorBrush BrushConnectionLine = new SolidColorBrush(Color.FromArgb(102, 0, 122, 204));

        // Additional cached brushes for UI update paths (avoids per-frame allocations)
        private static readonly SolidColorBrush BrushInactive = new SolidColorBrush(Color.FromRgb(60, 60, 60));
        private static readonly SolidColorBrush BrushInactiveText = new SolidColorBrush(Color.FromRgb(204, 204, 204));
        private static readonly SolidColorBrush BrushLayerItemSelected = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x60));
        private static readonly SolidColorBrush BrushLayerItemNormal = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        private static readonly SolidColorBrush BrushLayerBorderSelected = new SolidColorBrush(Color.FromRgb(0x60, 0x80, 0xA0));
        private static readonly SolidColorBrush BrushLayerBorderNormal = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        private static readonly SolidColorBrush BrushDropTargetGroup = new SolidColorBrush(Color.FromRgb(0x40, 0x60, 0x80));
        private static readonly SolidColorBrush BrushDropTargetNormal = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        private static readonly SolidColorBrush BrushKeyframeRed = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
        private static readonly SolidColorBrush BrushTransfunctionerYellow = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
        private static readonly SolidColorBrush BrushLockAspectActive = new SolidColorBrush(Color.FromRgb(46, 125, 50));

        // Static constructor to freeze brushes for better performance
        static MainWindow()
        {
            BrushLayerSelected.Freeze();
            BrushLayerNormal.Freeze();
            BrushTrackSelected.Freeze();
            BrushTrackNormal.Freeze();
            BrushConnectionLine.Freeze();
            BrushInactive.Freeze();
            BrushInactiveText.Freeze();
            BrushLayerItemSelected.Freeze();
            BrushLayerItemNormal.Freeze();
            BrushLayerBorderSelected.Freeze();
            BrushLayerBorderNormal.Freeze();
            BrushDropTargetGroup.Freeze();
            BrushDropTargetNormal.Freeze();
            BrushKeyframeRed.Freeze();
            BrushTransfunctionerYellow.Freeze();
            BrushLockAspectActive.Freeze();
            StepEditBrush.Freeze();
            DefaultEditBrush.Freeze();
        }

        // Timeline element pooling
        private readonly List<Rectangle> _timelineRectPool = new List<Rectangle>();
        private readonly List<TextBlock> _timelineTextPool = new List<TextBlock>();
        private readonly List<Line> _timelineLinePool = new List<Line>();
        private int _rectPoolIndex;
        private int _textPoolIndex;
        private int _linePoolIndex;

        // Ruler element pooling (separate from timeline)
        private readonly List<Line> _rulerLinePool = new List<Line>();
        private readonly List<TextBlock> _rulerTextPool = new List<TextBlock>();
        private Line _rulerPlayhead;
        private int _rulerLinePoolIndex;
        private int _rulerTextPoolIndex;

        // Cached layer state for change detection
        private int _lastSelectedLayerIndex = -1;
        private float _cachedPosX, _cachedPosY, _cachedSizeX, _cachedSizeY;
        private float _cachedRotX, _cachedRotY, _cachedRotZ, _cachedOpacity;

        // Event handler references for proper cleanup
        private SizeChangedEventHandler _sizeChangedHandler;

        // Cached resource brushes (initialized once in Loaded event)
        // Eliminates repeated FindResource calls for better performance at 50 FPS
        private Brush _accentBrush;
        private Brush _keyframeBrush;
        private Brush _textMainBrush;
        private Brush _textSecondaryBrush;
        private Brush _connectedColorBrush;
        private Brush _disconnectedColorBrush;
        private Brush _playColorBrush;
        private Brush _stopColorBrush;
        private Brush _accentBlueBrush;
        private Brush _borderDarkBrush;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();

            // Load app settings
            Models.AppSettingsModel.Load();

            // Initialize project
            _project = new ProjectModel();

            // Initialize engine
            InitializeEngine();

            // Setup timers
            _playbackTimer = new DispatcherTimer(DispatcherPriority.Render);
            _playbackTimer.Interval = TimeSpan.FromMilliseconds(AppConstants.PlaybackIntervalMs);
            _playbackTimer.Tick += PlaybackTimer_Tick;

            _statsTimer = new DispatcherTimer();
            _statsTimer.Interval = TimeSpan.FromMilliseconds(AppConstants.StatsUpdateIntervalMs);
            _statsTimer.Tick += StatsTimer_Tick;
            _statsTimer.Start();

            // Initialize autosave
            _autosave = new Services.AutosaveService();
            _autosave.OnAutosaveComplete += path => Dispatcher.Invoke(() =>
            {
                // Brief status update (non-intrusive)
                var oldTitle = Title;
                Title = $"{oldTitle} (Autosaved)";
                Task.Delay(2000).ContinueWith(_ =>
                {
                    if (!Dispatcher.HasShutdownStarted)
                        Dispatcher.Invoke(() => UpdateTitle());
                }, TaskScheduler.Default);
            });
            _autosave.Start(_project, GetProjectDataForSave);

            // Bind data
            AnimationsList.ItemsSource = _project.Animations;

            // Create default animation
            CreateNewAnimation("Default");
            RefreshTransfunctionersList();

            // Events
            Loaded += MainWindow_Loaded;
            _sizeChangedHandler = (s, e) => DrawTimelineRuler();
            SizeChanged += _sizeChangedHandler;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Delete selected keyframe with Del key
            if (e.Key == Key.Delete)
            {
                var layer = _project?.SelectedLayer;
                if (layer == null) return;

                // Delete numeric keyframe
                if (_selectedKeyframe != null && !string.IsNullOrEmpty(_selectedKeyframeTrackId))
                {
                    var track = layer.GetTrack(_selectedKeyframeTrackId);
                    if (track != null && track.Keyframes.Contains(_selectedKeyframe))
                    {
                        track.Keyframes.Remove(_selectedKeyframe);
                        _selectedKeyframe = null;

                        UpdateTimelineCanvas();
                        UpdateKeyframeButtons();
                        _project.MarkDirty();

                        e.Handled = true;
                    }
                }
                // Delete string/text keyframe
                else if (_selectedStringKeyframe != null && layer.TextTrack != null)
                {
                    if (layer.TextTrack.Keyframes.Contains(_selectedStringKeyframe))
                    {
                        layer.TextTrack.Keyframes.Remove(_selectedStringKeyframe);
                        _selectedStringKeyframe = null;

                        UpdateTimelineCanvas();
                        UpdateKeyframeButtons();
                        _project.MarkDirty();

                        e.Handled = true;
                    }
                }
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Cache ALL resource brushes once at startup (avoid repeated FindResource calls)
            // This eliminates dictionary lookups during the 50 FPS render/update loop
            _accentBrush = (Brush)FindResource("Accent");
            _keyframeBrush = (Brush)FindResource("KeyframeActive");
            _textMainBrush = (Brush)FindResource("TextMain");
            _textSecondaryBrush = (Brush)FindResource("TextSecondary");
            _connectedColorBrush = (Brush)FindResource("ConnectedColor");
            _disconnectedColorBrush = (Brush)FindResource("DisconnectedColor");
            _playColorBrush = (Brush)FindResource("PlayColor");
            _stopColorBrush = (Brush)FindResource("StopColor");
            _accentBlueBrush = (Brush)FindResource("AccentBlue");
            _borderDarkBrush = (Brush)FindResource("BorderDark");

            Dispatcher.BeginInvoke(new Action(() =>
            {
                DrawTimelineRuler();
                UpdateTimelineCanvas();
                SetupNumericTextBoxes();
            }), DispatcherPriority.Loaded);
        }

        private void SetupNumericTextBoxes()
        {
            // List of numeric TextBoxes that should support scroll wheel and arrow keys
            var numericTextBoxes = new TextBox[]
            {
                TxtPosX, TxtPosY, TxtSizeX, TxtSizeY,
                TxtRotX, TxtRotY, TxtRotZ,
                TxtOpacity, TxtColorR, TxtColorG, TxtColorB,
                TxtFontSize, TxtLineHeight, TxtLetterSpacing,
                TxtTexX, TxtTexY, TxtTexW, TxtTexH, TxtTexRot,
                TxtAnimLength, TxtEaseOut, TxtEaseIn, TxtCurrentFrame
            };

            foreach (var tb in numericTextBoxes)
            {
                if (tb == null) continue;
                tb.PreviewMouseWheel += NumericTextBox_MouseWheel;
                tb.PreviewKeyDown += NumericTextBox_KeyDown;
            }
        }

        private void NumericTextBox_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Only adjust value when TextBox is focused (clicked into)
            // This allows normal panel scrolling when not focused
            if (sender is TextBox tb && tb.IsFocused)
            {
                AdjustNumericTextBox(tb, e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            }
        }

        private void NumericTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb)
            {
                if (e.Key == Key.Up)
                {
                    AdjustNumericTextBox(tb, 1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Down)
                {
                    AdjustNumericTextBox(tb, -1);
                    e.Handled = true;
                }
            }
        }

        private void AdjustNumericTextBox(TextBox tb, int direction)
        {
            // Determine step size based on TextBox name/tag
            float step = 1.0f;
            string name = tb.Name ?? "";
            string tag = tb.Tag?.ToString() ?? "";

            // Smaller steps for certain properties
            if (name.Contains("Opacity") || tag.Contains("Opacity") ||
                name.Contains("LineHeight") || tag.Contains("LineHeight") ||
                name.Contains("Ease"))
            {
                step = 0.05f;
            }
            else if (name.Contains("Rot") || tag.Contains("Rot") || tag.Contains("TexRot"))
            {
                step = 1.0f;
            }
            else if (name.Contains("Color") || tag.Contains("Color"))
            {
                step = 5.0f; // 0-255 range
            }
            else if (name.Contains("Spacing") || tag.Contains("Spacing"))
            {
                step = 0.5f;
            }

            // Larger steps with Shift, smaller with Ctrl
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                step *= 10;
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                step *= 0.1f;

            // Parse and adjust value
            if (float.TryParse(tb.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                value += step * direction;

                // Format based on step size
                string format = step < 1 ? "F2" : "F0";
                if (name.Contains("Opacity") || tag.Contains("Opacity"))
                    value = Math.Clamp(value, 0, 1);
                if (name.Contains("Color") || tag.Contains("Color"))
                    value = Math.Clamp(value, 0, 255);

                tb.Text = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

                // Move caret to end
                tb.CaretIndex = tb.Text.Length;
            }
        }

        #endregion

        #region Engine

        private void InitializeEngine()
        {
            try
            {
                _engine = new EngineRenderer();
                
                if (_engine.Initialize())
                {
                    CompareStructureLayouts();
                    PreviewImage.Source = _engine.Bitmap;
                    _engine.OnFrameRendered += OnEngineFrameRendered;
                    _engine.Start();
                    ApplyEdgeSmoothing();
                    UpdateEngineStatus(true, "Engine: Running");
                }
                else
                {
                    UpdateEngineStatus(false, "Engine: Init Failed");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Engine initialization failed", ex);
                UpdateEngineStatus(false, "Engine: Error");
                MessageBox.Show($"Engine error:\n{ex.Message}", "Error");
            }
        }

        private void ApplyEdgeSmoothing()
        {
            if (_engine == null || !_engine.IsInitialized) return;
            float width = Models.AppSettingsModel.EdgeSmoothingToFloat(Models.AppSettingsModel.EdgeSmoothing);
            _engine.SetEdgeSmoothing(width);
        }

        private void CompareStructureLayouts()
        {
#if DEBUG
            int cppSize = DaroEngine.Daro_GetStructSize();
            int csSize = System.Runtime.InteropServices.Marshal.SizeOf<DaroLayerNative>();
            if (cppSize != csSize)
                System.Diagnostics.Debug.WriteLine($"WARNING: Structure size mismatch! C++={cppSize}, C#={csSize}");
#endif
        }

        private void OnEngineFrameRendered()
        {
            // Called after each frame is rendered
        }

        private void UpdateEngineStatus(bool running, string text)
        {
            Dispatcher.Invoke(() =>
            {
                EngineIndicator.Fill = running
                    ? _connectedColorBrush
                    : _disconnectedColorBrush;
                TxtEngineStatus.Text = text;
            });
        }

        private void SendLayersToEngine()
        {
            try
            {
                if (_engine == null || !_engine.IsInitialized || _project?.SelectedAnimation == null)
                    return;

                var layers = _project.SelectedAnimation.Layers;
                int layerCount = layers.Count;

                // Handle empty layers - clear engine
                if (layerCount == 0)
                {
                    _engine.ClearLayers();
                    return;
                }

                // Phase 1: Ensure all resources are loaded (may acquire lock individually)
                for (int i = 0; i < layerCount; i++)
                {
                    EnsureLayerResources(layers[i]);
                }

                // Phase 2: Batch update all layers and render in single lock
                // Reuse array to avoid allocations
                if (_layerBuffer == null || _layerBuffer.Length < layerCount)
                {
                    _layerBuffer = new DaroLayerNative[Math.Max(layerCount, DaroConstants.MAX_LAYERS)];
                }

                for (int i = 0; i < layerCount; i++)
                {
                    _layerBuffer[i] = layers[i].ToNative();
                }

                _engine.UpdateLayersBatch(_layerBuffer, layerCount);
            }
            catch (Exception ex)
            {
                Logger.Error("SendLayersToEngine failed", ex);
            }
        }

        // Reusable buffer for layer updates to avoid allocations
        private DaroLayerNative[] _layerBuffer;
        
        private void EnsureLayerResources(LayerModel layer)
        {
            if (_engine == null || layer == null) return;

            // Load texture if needed
            if (layer.TextureSource == TextureSourceType.ImageFile && 
                !string.IsNullOrEmpty(layer.TexturePath) &&
                layer.TextureId <= 0)
            {
                layer.TextureId = _engine.LoadTexture(layer.TexturePath);
            }
            
            // Connect Spout receiver if needed
            if (layer.TextureSource == TextureSourceType.SpoutInput &&
                !string.IsNullOrEmpty(layer.SpoutSenderName) &&
                layer.SpoutReceiverId <= 0)
            {
                layer.SpoutReceiverId = _engine.ConnectSpoutReceiver(layer.SpoutSenderName);
            }

            // Load video if needed
            if (layer.TextureSource == TextureSourceType.VideoFile &&
                !string.IsNullOrEmpty(layer.TexturePath) &&
                layer.VideoId <= 0)
            {
                layer.VideoId = _engine.LoadVideo(layer.TexturePath);
                if (layer.VideoId > 0)
                {
                    _engine.SetVideoAlpha(layer.VideoId, layer.VideoAlpha);
                    _engine.PlayVideo(layer.VideoId);
                }
            }
        }

        private void StatsTimer_Tick(object sender, EventArgs e)
        {
            if (_engine != null && _engine.IsInitialized)
            {
                TxtFps.Text = $"{_engine.CurrentFps:F1} FPS";
            }
        }

        #endregion

        #region Menu

        private async void Menu_NewProject(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_project != null && _project.IsDirty)
                {
                    var result = MessageBox.Show(
                        "Save changes to current project?",
                        "New Project",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question
                    );

                    if (result == MessageBoxResult.Cancel) return;
                    if (result == MessageBoxResult.Yes)
                    {
                        await SaveProjectAsync(_project.FilePath);
                    }
                }

                // Release all GPU resources from current project before creating new one
                ReleaseAllProjectResources();

                _project = new ProjectModel();
                AnimationsList.ItemsSource = _project.Animations;
                CreateNewAnimation("Default");
                UpdateUI();
                UpdateTitle();
                RefreshTransfunctionersList();
            }
            catch (Exception ex)
            {
                Logger.Error("Error creating new project", ex);
                MessageBox.Show($"Error:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Menu_OpenProject(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Open Project",
                    Filter = "Daro Project (*.daro)|*.daro|All Files (*.*)|*.*",
                    DefaultExt = ".daro"
                };

                if (dlg.ShowDialog() == true)
                {
                    await LoadProjectAsync(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error opening project", ex);
                MessageBox.Show($"Error opening project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Menu_SaveProject(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_project?.FilePath))
                {
                    Menu_SaveProjectAs(sender, e);
                    return;
                }

                await SaveProjectAsync(_project.FilePath);
            }
            catch (Exception ex)
            {
                Logger.Error("Error saving project", ex);
                MessageBox.Show($"Error saving project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Menu_SaveProjectAs(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Save Project As",
                    Filter = "Daro Project (*.daro)|*.daro|All Files (*.*)|*.*",
                    DefaultExt = ".daro",
                    FileName = string.IsNullOrEmpty(_project?.FilePath)
                        ? "NewProject.daro"
                        : System.IO.Path.GetFileName(_project.FilePath)
                };

                if (dlg.ShowDialog() == true)
                {
                    await SaveProjectAsync(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error saving project", ex);
                MessageBox.Show($"Error saving project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private ProjectData GetProjectDataForSave()
        {
            return _project?.ToSerializable();
        }

        private async Task SaveProjectAsync(string filePath)
        {
            // Security: Validate path before file write
            if (!PathValidator.IsPathAllowed(filePath))
            {
                MessageBox.Show("Invalid save path", "Security Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var data = GetProjectDataForSave();
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

                // Atomic write: write to temp file then rename to prevent corruption on crash/power loss
                var tempPath = filePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);

                _project.FilePath = filePath;
                _project.IsDirty = false;
                UpdateTitle();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save project: {filePath}", ex);
                MessageBox.Show($"Failed to save project:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadProjectAsync(string filePath)
        {
            // Security: Validate path before file access
            if (!PathValidator.IsPathAllowed(filePath))
            {
                MessageBox.Show("Invalid project path", "Security Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Stop playback before loading new project
                if (_project != null && _project.IsPlaying)
                {
                    _project.IsPlaying = false;
                    _playbackTimer.Stop();
                    BtnPlay.Content = "\u25b6";
                    BtnPlay.Background = _playColorBrush;
                }

                // Release all GPU resources from current project before loading new one
                ReleaseAllProjectResources();

                var json = await File.ReadAllTextAsync(filePath);
                var data = JsonSerializer.Deserialize<ProjectData>(json, new JsonSerializerOptions
                {
                    MaxDepth = AppConstants.MaxJsonDepth // Prevent stack overflow from deeply nested JSON
                });

                _project = ProjectModel.FromSerializable(data);
                _project.FilePath = filePath;
                _project.IsDirty = false;
                _project.CurrentFrame = 0;
                _project.IsPlaying = false;

                AnimationsList.ItemsSource = _project.Animations;
                if (_project.Animations.Count > 0)
                {
                    AnimationsList.SelectedIndex = 0;
                }

                // Reload textures for all layers
                ReloadAllTextures();

                UpdateUI();
                UpdateTitle();
                SendLayersToEngine();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load project: {filePath}", ex);
                MessageBox.Show($"Failed to load project:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReloadAllTextures()
        {
            foreach (var anim in _project.Animations)
            {
                foreach (var layer in anim.Layers)
                {
                    // Reset IDs so they get reloaded
                    layer.TextureId = -1;
                    layer.SpoutReceiverId = -1;
                }
            }
        }

        private void UpdateTitle()
        {
            string fileName = string.IsNullOrEmpty(_project.FilePath) 
                ? "Untitled" 
                : System.IO.Path.GetFileName(_project.FilePath);
            string dirty = _project.IsDirty ? " *" : "";
            Title = $"DARO DESIGNER v2.0 - {fileName}{dirty}";
        }

        private void Menu_Exit(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion

        #region Mode Switching

        private void ModeDesigner_Click(object sender, MouseButtonEventArgs e)
        {
            // Already in Designer mode - this is the main window
            UpdateModeButtons("Designer");
        }

        private async void ModeTemplate_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Ask to save current project
                if (_project != null && _project.IsDirty)
                {
                    var result = MessageBox.Show(
                        "Do you want to save the current scene before switching to Template Maker?",
                        "Save Scene",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel) return;
                    if (result == MessageBoxResult.Yes)
                    {
                        await SaveProjectAsync(_project.FilePath);
                    }
                }

                // Open Template Window
                var templateWindow = new TemplateWindow();
                templateWindow.Owner = this;
                templateWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Error("Error switching to Template mode", ex);
                MessageBox.Show($"Error:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ModePlayout_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Check if project needs saving
                if (_project != null && _project.IsDirty)
                {
                    var result = MessageBox.Show(
                        "Do you want to save changes before switching to Playout?",
                        "Save Changes",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel)
                        return;

                    if (result == MessageBoxResult.Yes)
                    {
                        if (string.IsNullOrEmpty(_project.FilePath))
                        {
                            var saveDialog = new SaveFileDialog
                            {
                                Filter = "Daro Scene|*.dscene",
                                DefaultExt = "dscene"
                            };
                            if (saveDialog.ShowDialog() == true)
                            {
                                await SaveProjectAsync(saveDialog.FileName);
                            }
                            else
                            {
                                return;
                            }
                        }
                        else
                        {
                            await SaveProjectAsync(_project.FilePath);
                        }
                    }
                }

                // Stop engine before closing
                if (_engine != null)
                {
                    _engine.Stop();
                }

                // Open Playout and close Designer
                var playoutWindow = new PlayoutWindow();
                playoutWindow.Show();
                this.Close();
            }
            catch (Exception ex)
            {
                Logger.Error("Error switching to Playout mode", ex);
                MessageBox.Show($"Error:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateModeButtons(string activeMode)
        {
            var accentBrush = _accentBrush;  // Use cached brush
            var inactiveBrush = BrushInactive;  // Use static frozen brush
            var activeTextBrush = Brushes.White;
            var inactiveTextBrush = BrushInactiveText;  // Use static frozen brush

            BtnModeDesigner.Background = activeMode == "Designer" ? accentBrush : inactiveBrush;
            ((TextBlock)BtnModeDesigner.Child).Foreground = activeMode == "Designer" ? activeTextBrush : inactiveTextBrush;

            BtnModeTemplate.Background = activeMode == "Template" ? accentBrush : inactiveBrush;
            ((TextBlock)BtnModeTemplate.Child).Foreground = activeMode == "Template" ? activeTextBrush : inactiveTextBrush;

            BtnModePlayout.Background = activeMode == "Playout" ? accentBrush : inactiveBrush;
            ((TextBlock)BtnModePlayout.Child).Foreground = activeMode == "Playout" ? activeTextBrush : inactiveTextBrush;
        }

        #endregion

        #region View Menu

        private void Menu_ResetZoom(object sender, RoutedEventArgs e)
        {
            _project.TimelineZoom = 1.0;
            DrawTimelineRuler();
            UpdateTimelineCanvas();
        }

        private void Menu_About(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "DARO ENGINE v2.0\n\n" +
                "Professional Broadcast Graphics System\n\n" +
                "DirectX 11 Render Engine\n" +
                "50 FPS Timeline\n" +
                "Spout I/O Support\n\n" +
                "© 2024",
                "About",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }

        private void Menu_Settings(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow { Owner = this };
            if (settingsWindow.ShowDialog() == true)
            {
                ApplyEdgeSmoothing();
            }
        }

        #endregion

        #region Animations

        private void CreateNewAnimation(string name)
        {
            var anim = new AnimationModel { Name = name };
            _project.Animations.Add(anim);
            AnimationsList.SelectedItem = anim;
            _project.SelectedAnimation = anim;
        }

        private void NewAnimation_Click(object sender, RoutedEventArgs e)
        {
            CreateNewAnimation($"Animation_{_project.Animations.Count + 1}");
            _project.MarkDirty();
        }

        private void DeleteAnimation_Click(object sender, RoutedEventArgs e)
        {
            if (_project.SelectedAnimation == null || _project.Animations.Count <= 1)
                return;

            _project.Animations.Remove(_project.SelectedAnimation);
            AnimationsList.SelectedIndex = 0;
            _project.MarkDirty();
        }

        private void AnimationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_project == null) return;

            if (AnimationsList.SelectedItem is AnimationModel anim)
            {
                _project.SelectedAnimation = anim;
                RefreshLayersTree();
                TxtAnimName.Text = anim.Name;
                TxtAnimLength.Text = anim.LengthFrames.ToString();
                UpdateUI();
                SendLayersToEngine();
                RefreshTransfunctionersList();
            }
        }

        private void AnimName_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedAnimation == null || _isUpdatingUI) return;

            var name = TxtAnimName.Text?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                _project.SelectedAnimation.Name = name;
                _project.MarkDirty();
            }
        }

        private void AnimLength_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedAnimation == null || _isUpdatingUI) return;

            if (int.TryParse(TxtAnimLength.Text, out int frames) && frames > 0)
            {
                _project.SelectedAnimation.LengthFrames = frames;
                UpdateTotalFramesDisplay();
                DrawTimelineRuler();
                UpdateTimelineCanvas();
                _project.MarkDirty();
            }
        }

        #endregion

        #region Layers

        // Drag-drop state for Layers
        private Point _dragStartPoint;

        // Drag from OBJECTS toolbox
        private void ObjectTool_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string typeName)
            {
                var layerType = typeName switch
                {
                    "Rectangle" => LayerType.Rectangle,
                    "Circle" => LayerType.Circle,
                    "Text" => LayerType.Text,
                    "Mask" => LayerType.Mask,
                    "Group" => LayerType.Group,
                    _ => LayerType.Rectangle
                };
                DragDrop.DoDragDrop(border, new DataObject("LayerType", layerType), DragDropEffects.Copy);
            }
        }

        private void AddLayer(LayerType layerType)
        {
            if (_project?.SelectedAnimation == null) return;

            int count = _project.SelectedAnimation.Layers.Count + 1;

            var layer = new LayerModel
            {
                LayerType = layerType,
                PosX = 960,
                PosY = 540,
                SizeX = 400,
                SizeY = 300,
                Opacity = 1.0f,
                ColorR = 1.0f,
                ColorG = 1.0f,
                ColorB = 1.0f,
                ColorA = 1.0f,
                IsVisible = true
            };

            // Set name based on type
            layer.Name = layerType switch
            {
                LayerType.Rectangle => $"Rect_{count}",
                LayerType.Circle => $"Circle_{count}",
                LayerType.Text => $"Text_{count}",
                LayerType.Mask => $"Mask_{count}",
                LayerType.Group => $"Group_{count}",
                _ => $"Layer_{count}"
            };

            // Type-specific defaults
            if (layerType == LayerType.Text)
            {
                layer.TextContent = "Text";
                layer.FontFamily = "Arial";
                layer.FontSize = 72;
                layer.SizeX = 400;
                layer.SizeY = 120;
                // Yellow color for better visibility
                layer.ColorR = 1.0f;
                layer.ColorG = 1.0f;
                layer.ColorB = 0.0f;
            }
            else if (layerType == LayerType.Mask)
            {
                // Mask visible by default - user can hide manually
                // Set semi-transparent color to indicate mask area
                layer.ColorR = 1.0f;
                layer.ColorG = 0.5f;
                layer.ColorB = 0.0f;
                layer.Opacity = 0.8f;
            }

            _project.SelectedAnimation.Layers.Add(layer);

            RefreshLayersTree();
            SelectLayerInTree(layer);

            // Switch to Transform tab by default when adding new object
            PropertiesTabControl.SelectedIndex = 1;

            UpdateTimelineCanvas();
            SendLayersToEngine();
            _project.MarkDirty();
        }

        private void CleanupLayerTreeHandlers()
        {
            foreach (var child in LayersPanel.Children)
            {
                if (child is Border border)
                {
                    border.MouseLeftButtonDown -= LayerItem_MouseDown;
                    border.MouseMove -= LayerItem_MouseMove;
                    if (border.Child is DockPanel panel)
                    {
                        foreach (var panelChild in panel.Children)
                        {
                            if (panelChild is Button btn)
                                btn.Click -= ToggleVisibility_Click;
                        }
                    }
                }
            }
        }

        private void RefreshLayersTree()
        {
            CleanupLayerTreeHandlers();
            LayersPanel.Children.Clear();
            if (_project?.SelectedAnimation == null) return;

            // Build flat list with indentation for hierarchy
            // Use for-loop instead of LINQ to avoid allocation
            var layers = _project.SelectedAnimation.Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i].ParentId == -1)
                {
                    AddLayerItem(layers[i], 0);
                }
            }
        }

        private void AddLayerItem(LayerModel layer, int indent)
        {
            var item = CreateLayerItem(layer, indent);
            LayersPanel.Children.Add(item);

            // Add children for groups
            if (layer.LayerType == LayerType.Group)
            {
                // Use for-loop instead of LINQ to avoid allocation
                var layers = _project.SelectedAnimation.Layers;
                for (int i = 0; i < layers.Count; i++)
                {
                    if (layers[i].ParentId == layer.Id)
                    {
                        AddLayerItem(layers[i], indent + 1);
                    }
                }
            }
        }

        private Border CreateLayerItem(LayerModel layer, int indent)
        {
            var icon = layer.LayerType switch
            {
                LayerType.Rectangle => "▢",
                LayerType.Circle => "○",
                LayerType.Text => "T",
                LayerType.Mask => "◐",
                LayerType.Group => "📁",
                _ => "□"
            };

            var panel = new DockPanel { LastChildFill = true };
            
            // Visibility button
            var visBtn = new Button
            {
                Content = layer.IsVisible ? "👁" : "·",
                Width = 20,
                Height = 18,
                Margin = new Thickness(0, 0, 4, 0),
                Tag = layer,
                Padding = new Thickness(0),
                FontSize = layer.IsVisible ? 10 : 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            visBtn.Click += ToggleVisibility_Click;
            DockPanel.SetDock(visBtn, Dock.Left);
            panel.Children.Add(visBtn);
            
            // Icon and name
            panel.Children.Add(new TextBlock 
            { 
                Text = $"{icon} {layer.Name}", 
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = layer.IsVisible ? Brushes.White : Brushes.Gray,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var isSelected = _project.SelectedLayer == layer;
            var border = new Border
            {
                Background = isSelected ? BrushLayerItemSelected : BrushLayerItemNormal,
                BorderBrush = isSelected ? BrushLayerBorderSelected : BrushLayerBorderNormal,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(indent * 12, 0, 0, 2),
                Tag = layer,
                Cursor = Cursors.Hand,
                Child = panel
            };

            border.MouseLeftButtonDown += LayerItem_MouseDown;
            border.MouseMove += LayerItem_MouseMove;

            return border;
        }

        private void LayerItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is LayerModel layer)
            {
                _dragStartPoint = e.GetPosition(null);
                
                // Select layer
                _project.SelectedLayer = layer;
                RefreshLayersTree();
                
                PropertiesPanel.Visibility = Visibility.Visible;
                PropertiesPanel.IsEnabled = true;
                UpdatePropertiesUI();
                UpdateKeyframeIndicators();
                
                e.Handled = true;
            }
        }

        private void LayerItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(null);
            Vector diff = _dragStartPoint - pos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is Border border && border.Tag is LayerModel layer)
                {
                    _draggedLayer = layer;
                    DragDrop.DoDragDrop(border, new DataObject("Layer", layer), DragDropEffects.Move);
                    _draggedLayer = null;
                }
            }
        }

        private void SelectLayerInTree(LayerModel layer)
        {
            _project.SelectedLayer = layer;
            RefreshLayersTree();
            
            PropertiesPanel.Visibility = Visibility.Visible;
            PropertiesPanel.IsEnabled = true;
            UpdatePropertiesUI();
            UpdateKeyframeIndicators();
        }

        // Old TreeView handlers - now empty stubs for XAML compatibility
        private void LayersTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) { }
        private void LayersTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }
        private void LayersTree_PreviewMouseMove(object sender, MouseEventArgs e) { }

        private Border _dropTargetBorder;

        private void LayersTree_DragOver(object sender, DragEventArgs e)
        {
            // Accept both Layer (move) and LayerType (new from toolbox)
            bool hasLayer = e.Data.GetDataPresent("Layer");
            bool hasLayerType = e.Data.GetDataPresent("LayerType");
            
            if (!hasLayer && !hasLayerType)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Clear previous highlight
            if (_dropTargetBorder != null)
            {
                var layer = _dropTargetBorder.Tag as LayerModel;
                bool isSelected = _project?.SelectedLayer == layer;
                _dropTargetBorder.Background = isSelected ? BrushLayerItemSelected : BrushLayerItemNormal;
                _dropTargetBorder = null;
            }

            var targetBorder = GetLayerBorderUnderMouse(e.OriginalSource as DependencyObject);
            
            if (hasLayerType)
            {
                // Dragging new object from toolbox - always allow
                if (targetBorder?.Tag is LayerModel targetLayer)
                {
                    if (targetLayer.LayerType == LayerType.Group)
                    {
                        targetBorder.Background = BrushDropTargetGroup;
                    }
                    else
                    {
                        targetBorder.Background = BrushDropTargetNormal;
                    }
                    _dropTargetBorder = targetBorder;
                }
                e.Effects = DragDropEffects.Copy;
            }
            else if (hasLayer && targetBorder?.Tag is LayerModel targetLayer2)
            {
                var draggedLayer = e.Data.GetData("Layer") as LayerModel;
                
                // Can't drop on self or own children
                if (draggedLayer != null && targetLayer2.Id != draggedLayer.Id)
                {
                    if (targetLayer2.LayerType == LayerType.Group)
                    {
                        targetBorder.Background = BrushDropTargetGroup;
                    }
                    else
                    {
                        targetBorder.Background = BrushDropTargetNormal;
                    }
                    _dropTargetBorder = targetBorder;
                    e.Effects = DragDropEffects.Move;
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
            }
            else
            {
                e.Effects = hasLayer ? DragDropEffects.Move : DragDropEffects.Copy;
            }

            e.Handled = true;
        }

        private void LayersTree_Drop(object sender, DragEventArgs e)
        {
            // Clear highlight
            if (_dropTargetBorder != null)
            {
                var layer = _dropTargetBorder.Tag as LayerModel;
                bool isSelected = _project?.SelectedLayer == layer;
                _dropTargetBorder.Background = isSelected ? BrushLayerItemSelected : BrushLayerItemNormal;
                _dropTargetBorder = null;
            }

            var targetBorder = GetLayerBorderUnderMouse(e.OriginalSource as DependencyObject);
            var targetLayer = targetBorder?.Tag as LayerModel;

            // Handle new object from toolbox
            if (e.Data.GetDataPresent("LayerType"))
            {
                var layerType = (LayerType)e.Data.GetData("LayerType");
                AddLayer(layerType);
                
                // If dropped on a group, move the new layer into it
                if (targetLayer?.LayerType == LayerType.Group && _project?.SelectedLayer != null)
                {
                    _project.SelectedLayer.ParentId = targetLayer.Id;
                    RefreshLayersTree();
                }
                return;
            }

            // Handle layer move
            if (!e.Data.GetDataPresent("Layer")) return;

            var draggedLayer = e.Data.GetData("Layer") as LayerModel;
            if (draggedLayer == null) return;

            if (targetLayer != null && targetLayer.Id == draggedLayer.Id) return;

            // If dropping on a group, make it a child
            if (targetLayer?.LayerType == LayerType.Group)
            {
                draggedLayer.ParentId = targetLayer.Id;
            }
            else
            {
                // Drop at root level or reorder
                draggedLayer.ParentId = -1;
                
                if (targetLayer != null)
                {
                    // Reorder: move dragged layer to position of target
                    var layers = _project.SelectedAnimation.Layers;
                    int oldIndex = layers.IndexOf(draggedLayer);
                    int newIndex = layers.IndexOf(targetLayer);
                    
                    if (oldIndex != newIndex && oldIndex >= 0 && newIndex >= 0)
                    {
                        layers.Move(oldIndex, newIndex);
                    }
                }
            }

            RefreshLayersTree();
            SelectLayerInTree(draggedLayer);
            UpdateTimelineCanvas();
            SendLayersToEngine();
            _project.MarkDirty();
        }

        private Border GetLayerBorderUnderMouse(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Border border && border.Tag is LayerModel)
                    return border;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        private void ToggleVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LayerModel layer)
            {
                layer.IsVisible = !layer.IsVisible;
                RefreshLayersTree();
                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void MoveLayerUp_Click(object sender, RoutedEventArgs e)
        {
            if (_project.SelectedLayer == null || _project.SelectedAnimation == null) return;

            var layers = _project.SelectedAnimation.Layers;
            int index = layers.IndexOf(_project.SelectedLayer);
            if (index > 0)
            {
                var layer = _project.SelectedLayer;
                layers.Move(index, index - 1);
                RefreshLayersTree();
                SelectLayerInTree(layer);
                UpdateTimelineCanvas();
                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void MoveLayerDown_Click(object sender, RoutedEventArgs e)
        {
            if (_project.SelectedLayer == null || _project.SelectedAnimation == null) return;

            var layers = _project.SelectedAnimation.Layers;
            int index = layers.IndexOf(_project.SelectedLayer);
            if (index < layers.Count - 1)
            {
                var layer = _project.SelectedLayer;
                layers.Move(index, index + 1);
                RefreshLayersTree();
                SelectLayerInTree(layer);
                UpdateTimelineCanvas();
                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void DeleteLayer_Click(object sender, RoutedEventArgs e)
        {
            if (_project.SelectedLayer == null || _project.SelectedAnimation == null) return;

            var layerToDelete = _project.SelectedLayer;

            // Also remove any children if it's a group
            var childrenToRemove = _project.SelectedAnimation.Layers
                .Where(l => l.ParentId == layerToDelete.Id).ToList();
            foreach (var child in childrenToRemove)
            {
                // Release GPU resources before removing child layer
                ReleaseLayerResources(child);
                _project.SelectedAnimation.Layers.Remove(child);
            }

            // Release GPU resources before removing main layer
            ReleaseLayerResources(layerToDelete);
            _project.SelectedAnimation.Layers.Remove(layerToDelete);
            _project.SelectedLayer = null;

            // Hide properties panel
            PropertiesPanel.Visibility = Visibility.Collapsed;
            PropertiesPanel.IsEnabled = false;

            RefreshLayersTree();
            UpdateTimelineCanvas();
            SendLayersToEngine();
            _project.MarkDirty();
        }

        /// <summary>
        /// Releases GPU resources (textures, Spout receivers) associated with a layer.
        /// Call this before removing a layer to prevent GPU memory leaks.
        /// </summary>
        private void ReleaseLayerResources(LayerModel layer)
        {
            if (_engine == null || layer == null) return;

            // Unload texture if this layer has one
            if (!string.IsNullOrEmpty(layer.TexturePath))
            {
                _engine.UnloadTexture(layer.TexturePath);
            }

            // Disconnect Spout receiver if this layer has one
            if (!string.IsNullOrEmpty(layer.SpoutSenderName))
            {
                _engine.DisconnectSpoutReceiver(layer.SpoutSenderName);
            }

            // Unload video if this layer has one
            if (layer.VideoId > 0)
            {
                _engine.UnloadVideo(layer.VideoId);
                layer.VideoId = -1;
            }
        }

        /// <summary>
        /// Releases all GPU resources from the current project.
        /// Call this before loading a new project or creating a new project.
        /// </summary>
        private void ReleaseAllProjectResources()
        {
            if (_project == null || _engine == null) return;

            foreach (var animation in _project.Animations)
            {
                foreach (var layer in animation.Layers)
                {
                    ReleaseLayerResources(layer);
                }
            }
        }

        #endregion

        #region Properties UI

        private void UpdatePropertiesUI()
        {
            var layer = _project.SelectedLayer;
            if (layer == null) return;

            _isUpdatingUI = true;
            try
            {
                TxtLayerName.Text = layer.Name;
                TxtPosX.Text = layer.PosX.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                TxtPosY.Text = layer.PosY.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                TxtSizeX.Text = layer.SizeX.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                TxtSizeY.Text = layer.SizeY.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                TxtRotX.Text = layer.RotX.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                TxtRotY.Text = layer.RotY.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                TxtRotZ.Text = layer.RotZ.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                TxtOpacity.Text = layer.Opacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                UpdateLockAspectVisual();
                ChkVisible.IsChecked = layer.IsVisible;

                // Color
                UpdateColorUI();

                // Texture
                CmbTextureSource.SelectedIndex = (int)layer.TextureSource;
                TxtFilePath.Text = layer.TexturePath;
                TxtVideoPath.Text = !string.IsNullOrEmpty(layer.TexturePath)
                    ? System.IO.Path.GetFileName(layer.TexturePath) : "";
                TxtTexX.Text = layer.TexX.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                TxtTexY.Text = layer.TexY.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                TxtTexW.Text = layer.TexW.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                TxtTexH.Text = layer.TexH.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                TxtTexRot.Text = layer.TexRot.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

                // Text properties
                if (layer.LayerType == LayerType.Text)
                {
                    TxtTextContent.Text = layer.TextContent;
                    TxtFontSize.Text = layer.FontSize.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                    ChkFontBold.IsChecked = layer.FontBold;
                    ChkFontItalic.IsChecked = layer.FontItalic;
                    CmbTextAlignment.SelectedIndex = (int)layer.TextAlignment;
                    TxtLineHeight.Text = layer.LineHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    TxtLetterSpacing.Text = layer.LetterSpacing.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                    CmbTextAntialias.SelectedIndex = (int)layer.TextAntialiasMode;

                    // Update font family combo
                    UpdateFontFamilyCombo(layer.FontFamily);
                }

                // Mask properties
                if (layer.LayerType == LayerType.Mask)
                {
                    CmbMaskMode.SelectedIndex = (int)layer.MaskMode;
                }

                UpdateSourcePanelVisibility();
                UpdateKeyframeButtons();
                UpdateAnchorButtons();
            }
            finally
            {
                _isUpdatingUI = false;
            }
        }

        private void UpdateFontFamilyCombo(string currentFont)
        {
            if (CmbFontFamily.Items.Count == 0)
            {
                // Populate font list once
                foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies.OrderBy(f => f.Source))
                {
                    CmbFontFamily.Items.Add(new ComboBoxItem { Content = family.Source });
                }
            }
            
            // Select current font
            for (int i = 0; i < CmbFontFamily.Items.Count; i++)
            {
                if (CmbFontFamily.Items[i] is ComboBoxItem item && item.Content.ToString() == currentFont)
                {
                    CmbFontFamily.SelectedIndex = i;
                    break;
                }
            }
        }

        private void UpdateColorUI()
        {
            var layer = _project.SelectedLayer;
            if (layer == null) return;

            var color = Color.FromScRgb(1, layer.ColorR, layer.ColorG, layer.ColorB);
            ColorPreview.Fill = new SolidColorBrush(color);
            TxtColor.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            // Display as 0-255
            TxtColorR.Text = ((int)(layer.ColorR * 255)).ToString();
            TxtColorG.Text = ((int)(layer.ColorG * 255)).ToString();
            TxtColorB.Text = ((int)(layer.ColorB * 255)).ToString();
        }

        private void UpdateSourcePanelVisibility()
        {
            if (_project == null) return;
            var layer = _project.SelectedLayer;
            if (layer == null) return;

            // Texture source panels
            FilePathPanel.Visibility = layer.TextureSource == TextureSourceType.ImageFile
                ? Visibility.Visible : Visibility.Collapsed;

            SpoutInputPanel.Visibility = layer.TextureSource == TextureSourceType.SpoutInput
                ? Visibility.Visible : Visibility.Collapsed;
                
            VideoPanel.Visibility = layer.TextureSource == TextureSourceType.VideoFile
                ? Visibility.Visible : Visibility.Collapsed;
            ChkVideoAlpha.IsChecked = layer.VideoAlpha;

            // Layer type specific tabs
            TextTab.Visibility = layer.LayerType == LayerType.Text
                ? Visibility.Visible : Visibility.Collapsed;

            MaskTab.Visibility = layer.LayerType == LayerType.Mask
                ? Visibility.Visible : Visibility.Collapsed;

            // Update mask layers list
            if (layer.LayerType == LayerType.Mask)
            {
                UpdateMaskedLayersList();
            }
        }

        private void UpdateMaskedLayersList()
        {
            MaskedLayersList.Items.Clear();
            
            if (_project?.SelectedAnimation == null || _project?.SelectedLayer == null) return;
            
            var currentLayer = _project.SelectedLayer;
            
            foreach (var layer in _project.SelectedAnimation.Layers)
            {
                if (layer.Id == currentLayer.Id) continue; // Skip self
                if (layer.LayerType == LayerType.Mask || layer.LayerType == LayerType.Group) continue;
                
                var item = new ListBoxItem
                {
                    Content = layer.Name,
                    Tag = layer.Id,
                    IsSelected = currentLayer.MaskedLayerIds.Contains(layer.Id)
                };
                MaskedLayersList.Items.Add(item);
            }
        }

        private void LayerName_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.Name = TxtLayerName.Text;
            RefreshLayersTree();
            UpdateTimelineCanvas();
            _project.MarkDirty();
        }

        private void PropertyValue_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;

            // Skip if in step editing mode
            if (sender is TextBox txt && _propertyStepEditingTextBox == txt) return;

            if (sender is TextBox textBox && textBox.Tag is string propertyId)
            {
                if (float.TryParse(textBox.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value))
                {
                    _project.SelectedLayer.SetPropertyValue(propertyId, value);

                    // Update position TextBoxes if size changed (anchor point adjustment)
                    if (propertyId == "SizeX" || propertyId == "SizeY")
                    {
                        _isUpdatingUI = true;
                        try
                        {
                            TxtPosX.Text = _project.SelectedLayer.PosX.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                            TxtPosY.Text = _project.SelectedLayer.PosY.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                        }
                        finally { _isUpdatingUI = false; }
                    }

                    SendLayersToEngine();
                    _project.MarkDirty();
                }
            }
        }

        private void AnchorPoint_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;

            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            {
                var parts = tag.Split(',');
                if (parts.Length == 2 &&
                    float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
                {
                    _project.SelectedLayer.AnchorX = x;
                    _project.SelectedLayer.AnchorY = y;
                    UpdateAnchorButtons();
                    SendLayersToEngine();
                    _project.MarkDirty();
                }
            }
        }

        private void UpdateAnchorButtons()
        {
            if (_project?.SelectedLayer == null) return;

            var layer = _project.SelectedLayer;
            var buttons = new[] { AnchorTL, AnchorTC, AnchorTR, AnchorML, AnchorMC, AnchorMR, AnchorBL, AnchorBC, AnchorBR };

            foreach (var btn in buttons)
            {
                if (btn.Tag is string tag)
                {
                    var parts = tag.Split(',');
                    if (parts.Length == 2 &&
                        float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
                    {
                        bool isSelected = Math.Abs(layer.AnchorX - x) < 0.01f && Math.Abs(layer.AnchorY - y) < 0.01f;
                        btn.Background = isSelected ?
                            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x50, 0x80, 0x50)) :
                            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3C, 0x3C, 0x3C));
                        btn.Foreground = isSelected ?
                            System.Windows.Media.Brushes.White :
                            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA));
                    }
                }
            }
        }

        private bool _showBounds = false;

        private void ShowBounds_Changed(object sender, RoutedEventArgs e)
        {
            _showBounds = ChkShowBounds.IsChecked == true;
            DaroEngine.Daro_SetShowBounds(_showBounds);
        }

        private void Visible_Changed(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.IsVisible = ChkVisible.IsChecked == true;
            RefreshLayersTree();
            SendLayersToEngine();
            _project.MarkDirty();
        }

        #endregion

        #region Text Properties

        private void TextContent_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.TextContent = TxtTextContent.Text;
            SendLayersToEngine();
            _project.MarkDirty();
        }

        private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            if (CmbFontFamily.SelectedItem is ComboBoxItem item)
            {
                _project.SelectedLayer.FontFamily = item.Content.ToString();
                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void FontSize_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            if (_propertyStepEditingTextBox == TxtFontSize) return;
            if (float.TryParse(TxtFontSize.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float size))
            {
                _project.SelectedLayer.FontSize = size;
                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void FontBold_Changed(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.FontBold = ChkFontBold.IsChecked == true;
            SendLayersToEngine();
            _project.MarkDirty();
        }

        private void FontItalic_Changed(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.FontItalic = ChkFontItalic.IsChecked == true;
            SendLayersToEngine();
            _project.MarkDirty();
        }

        private void TextAlignment_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.TextAlignment = (Models.TextAlignment)CmbTextAlignment.SelectedIndex;
            SendLayersToEngine();
            _project.MarkDirty();
        }

        private void LineHeight_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            if (_propertyStepEditingTextBox == TxtLineHeight) return;
            if (float.TryParse(TxtLineHeight.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                _project.SelectedLayer.LineHeight = value;
                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void LetterSpacing_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            if (_propertyStepEditingTextBox == TxtLetterSpacing) return;
            if (float.TryParse(TxtLetterSpacing.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                _project.SelectedLayer.LetterSpacing = value;
                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void TextAntialias_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.TextAntialiasMode = (TextAntialiasMode)CmbTextAntialias.SelectedIndex;
            SendLayersToEngine();
            _project.MarkDirty();
        }

        #endregion

        #region Mask Properties

        private void MaskMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.MaskMode = (MaskMode)CmbMaskMode.SelectedIndex;
            SendLayersToEngine();
            _project.MarkDirty();
        }

        private void MaskedLayers_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            
            _project.SelectedLayer.MaskedLayerIds.Clear();
            
            foreach (var item in MaskedLayersList.SelectedItems)
            {
                if (item is ListBoxItem listItem && listItem.Tag is int layerId)
                {
                    _project.SelectedLayer.MaskedLayerIds.Add(layerId);
                }
            }
            
            SendLayersToEngine();
            _project.MarkDirty();
        }

        #endregion

        #region Color

        private void ColorHex_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;

            string hex = TxtColor.Text.Trim();
            if (hex.StartsWith("#") && hex.Length > 1) hex = hex.Substring(1);

            if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint colorVal))
            {
                _isUpdatingUI = true;
                try
                {
                    _project.SelectedLayer.ColorR = ((colorVal >> 16) & 0xFF) / 255f;
                    _project.SelectedLayer.ColorG = ((colorVal >> 8) & 0xFF) / 255f;
                    _project.SelectedLayer.ColorB = (colorVal & 0xFF) / 255f;
                    UpdateColorUI();
                }
                finally { _isUpdatingUI = false; }

                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void ColorComponent_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;

            if (sender is TextBox txt && txt.Tag is string tag)
            {
                // Skip if in step editing mode
                if (_propertyStepEditingTextBox == txt) return;

                if (int.TryParse(txt.Text, out int value))
                {
                    value = Math.Clamp(value, 0, 255);
                    float normalized = value / 255f;
                    
                    switch (tag)
                    {
                        case "ColorR": _project.SelectedLayer.ColorR = normalized; break;
                        case "ColorG": _project.SelectedLayer.ColorG = normalized; break;
                        case "ColorB": _project.SelectedLayer.ColorB = normalized; break;
                    }
                    
                    _isUpdatingUI = true;
                    try
                    {
                        // Update preview and hex without changing text boxes
                        var layer = _project.SelectedLayer;
                        var color = Color.FromScRgb(1, layer.ColorR, layer.ColorG, layer.ColorB);
                        ColorPreview.Fill = new SolidColorBrush(color);
                        TxtColor.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                    }
                    finally { _isUpdatingUI = false; }
                    
                    SendLayersToEngine();
                    _project.MarkDirty();
                }
            }
        }

        private void ColorPicker_Click(object sender, RoutedEventArgs e)
        {
            if (_project.SelectedLayer == null) return;

            // Use Windows color dialog
            var dialog = new System.Windows.Forms.ColorDialog();
            dialog.Color = System.Drawing.Color.FromArgb(
                (int)(_project.SelectedLayer.ColorR * 255),
                (int)(_project.SelectedLayer.ColorG * 255),
                (int)(_project.SelectedLayer.ColorB * 255)
            );
            dialog.FullOpen = true;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _project.SelectedLayer.ColorR = dialog.Color.R / 255f;
                _project.SelectedLayer.ColorG = dialog.Color.G / 255f;
                _project.SelectedLayer.ColorB = dialog.Color.B / 255f;
                
                _isUpdatingUI = true;
                try { UpdateColorUI(); }
                finally { _isUpdatingUI = false; }

                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        #endregion

        #region Source / Texture

        private void SourceType_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;

            _project.SelectedLayer.TextureSource = (TextureSourceType)CmbTextureSource.SelectedIndex;
            
            // Reset IDs when source type changes
            _project.SelectedLayer.TextureId = -1;
            _project.SelectedLayer.SpoutReceiverId = -1;
            _project.SelectedLayer.VideoId = -1;
            
            UpdateSourcePanelVisibility();
            SendLayersToEngine();
            _project.MarkDirty();
        }

        private void BrowseFile_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _engine == null) return;

            string filter = _project.SelectedLayer.TextureSource == TextureSourceType.ImageFile
                ? "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.tga)|*.png;*.jpg;*.jpeg;*.bmp;*.tga|All Files (*.*)|*.*"
                : "Video Files (*.mp4;*.avi;*.mov;*.mkv)|*.mp4;*.avi;*.mov;*.mkv|All Files (*.*)|*.*";

            var dlg = new OpenFileDialog
            {
                Title = "Select File",
                Filter = filter
            };

            if (dlg.ShowDialog() == true)
            {
                // Unload old texture to free GPU memory (prevents memory leaks)
                string oldPath = _project.SelectedLayer.TexturePath;
                if (!string.IsNullOrEmpty(oldPath) && oldPath != dlg.FileName)
                {
                    _engine.UnloadTexture(oldPath);
                }

                _project.SelectedLayer.TexturePath = dlg.FileName;
                _project.SelectedLayer.TextureId = -1; // Reset to reload
                TxtFilePath.Text = dlg.FileName;

                var layer = _project.SelectedLayer;
                if (layer.TextureSource == TextureSourceType.ImageFile)
                {
                    layer.TextureId = _engine.LoadTexture(dlg.FileName);
                }
                else if (layer.TextureSource == TextureSourceType.VideoFile)
                {
                    if (layer.VideoId > 0)
                    {
                        _engine.UnloadVideo(layer.VideoId);
                    }
                    layer.VideoId = _engine.LoadVideo(dlg.FileName);
                    if (layer.VideoId > 0)
                    {
                        _engine.PlayVideo(layer.VideoId);
                    }
                }

                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void RefreshSpoutSenders_Click(object sender, RoutedEventArgs e)
        {
            if (_engine == null) return;
            
            CmbSpoutSenders.Items.Clear();
            
            var senders = _engine.GetSpoutSenders();
            if (senders.Count == 0)
            {
                CmbSpoutSenders.Items.Add("(No senders found)");
            }
            else
            {
                foreach (var sender_name in senders)
                {
                    CmbSpoutSenders.Items.Add(sender_name);
                }
                
                // Select current sender if exists
                if (_project.SelectedLayer != null && 
                    !string.IsNullOrEmpty(_project.SelectedLayer.SpoutSenderName))
                {
                    int idx = CmbSpoutSenders.Items.IndexOf(_project.SelectedLayer.SpoutSenderName);
                    if (idx >= 0) CmbSpoutSenders.SelectedIndex = idx;
                }
            }
        }
        
        private void CmbSpoutSenders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI || _engine == null) return;
            if (CmbSpoutSenders.SelectedItem == null) return;

            string senderName = CmbSpoutSenders.SelectedItem.ToString();
            if (senderName == "(No senders found)") return;

            // Disconnect old Spout receiver to free resources
            string oldSenderName = _project.SelectedLayer.SpoutSenderName;
            if (!string.IsNullOrEmpty(oldSenderName) && oldSenderName != senderName)
            {
                _engine.DisconnectSpoutReceiver(oldSenderName);
            }

            _project.SelectedLayer.SpoutSenderName = senderName;
            _project.SelectedLayer.SpoutReceiverId = -1; // Reset to reconnect

            // Connect immediately
            _project.SelectedLayer.SpoutReceiverId = _engine.ConnectSpoutReceiver(senderName);

            SendLayersToEngine();
            _project.MarkDirty();
        }

        #region Video Controls

        private void BrowseVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _engine == null) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Video File",
                Filter = "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.webm|All Files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                var layer = _project.SelectedLayer;

                // Unload previous video if loaded
                if (layer.VideoId > 0)
                {
                    _engine.UnloadVideo(layer.VideoId);
                    layer.VideoId = -1;
                }

                layer.TexturePath = dialog.FileName;
                layer.TextureSource = TextureSourceType.VideoFile;
                TxtVideoPath.Text = System.IO.Path.GetFileName(dialog.FileName);

                // Load video into engine and start playback
                layer.VideoId = _engine.LoadVideo(dialog.FileName);
                if (layer.VideoId > 0)
                {
                    _engine.SetVideoAlpha(layer.VideoId, layer.VideoAlpha);
                    _engine.PlayVideo(layer.VideoId);
                }

                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        // Video playback controls - engine APIs available but UI integration pending
        // Requires VideoId tracking per layer for Daro_LoadVideo/PlayVideo/PauseVideo/StopVideo

        private void VideoPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _engine == null) return;

            var layer = _project.SelectedLayer;
            if (layer.VideoId > 0)
            {
                _engine.PlayVideo(layer.VideoId);
            }

            BtnVideoPlay.Background = _playColorBrush;
            BtnVideoPause.Background = System.Windows.Media.Brushes.Transparent;
        }

        private void VideoPause_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _engine == null) return;

            var layer = _project.SelectedLayer;
            if (layer.VideoId > 0)
            {
                _engine.PauseVideo(layer.VideoId);
            }

            BtnVideoPlay.Background = System.Windows.Media.Brushes.Transparent;
            BtnVideoPause.Background = _accentBlueBrush;
        }

        private void VideoStop_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _engine == null) return;

            var layer = _project.SelectedLayer;
            if (layer.VideoId > 0)
            {
                _engine.StopVideo(layer.VideoId);
            }

            BtnVideoPlay.Background = System.Windows.Media.Brushes.Transparent;
            BtnVideoPause.Background = System.Windows.Media.Brushes.Transparent;
        }



        private void VideoLoop_Changed(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _engine == null) return;

            var layer = _project.SelectedLayer;
            if (layer.VideoId > 0)
            {
                _engine.SetVideoLoop(layer.VideoId, ChkVideoLoop.IsChecked == true);
            }
        }

        private void VideoAlpha_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (_project == null || _project.SelectedLayer == null || _engine == null) return;

            var layer = _project.SelectedLayer;
            layer.VideoAlpha = ChkVideoAlpha.IsChecked == true;
            if (layer.VideoId > 0)
            {
                _engine.SetVideoAlpha(layer.VideoId, layer.VideoAlpha);
            }
            _project.MarkDirty();
        }

        #endregion

        #endregion

        #region Keyframes

        private void KeyframeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_project.SelectedLayer == null) return;

            if (sender is Button btn && btn.Tag is string propertyId)
            {
                var layer = _project.SelectedLayer;
                int frame = _project.CurrentFrame;

                if (layer.HasKeyframeAtFrame(propertyId, frame))
                {
                    layer.DeleteKeyframe(propertyId, frame);
                }
                else
                {
                    float value = layer.GetPropertyValue(propertyId);
                    layer.SetKeyframe(propertyId, frame, value);
                }

                UpdateKeyframeIndicators();
                UpdateTimelineCanvas();
                _project.MarkDirty();
            }
        }

        private void UpdateKeyframeIndicators()
        {
            var layer = _project.SelectedLayer;
            if (layer == null) return;

            int frame = _project.CurrentFrame;

            UpdateKeyframeButton(KfPosX, layer.HasKeyframeAtFrame("PosX", frame) || layer.HasKeyframeAtFrame("PosY", frame));
            UpdateKeyframeButton(KfSizeX, layer.HasKeyframeAtFrame("SizeX", frame) || layer.HasKeyframeAtFrame("SizeY", frame));
            UpdateKeyframeButton(KfRotZ, layer.HasKeyframeAtFrame("RotX", frame) || layer.HasKeyframeAtFrame("RotY", frame) || layer.HasKeyframeAtFrame("RotZ", frame));
            UpdateKeyframeButton(KfOpacity, layer.HasKeyframeAtFrame("Opacity", frame));
            UpdateKeyframeButton(KfTexX, layer.HasKeyframeAtFrame("TexX", frame) || layer.HasKeyframeAtFrame("TexY", frame));
            UpdateKeyframeButton(KfTexW, layer.HasKeyframeAtFrame("TexW", frame) || layer.HasKeyframeAtFrame("TexH", frame));
            UpdateKeyframeButton(KfTexRot, layer.HasKeyframeAtFrame("TexRot", frame));
        }

        private void UpdateKeyframeButton(Button btn, bool hasKeyframe)
        {
            btn.Content = hasKeyframe ? "◆" : "◇";
            btn.Foreground = hasKeyframe
                ? _keyframeBrush
                : _textSecondaryBrush;
        }

        #endregion

        #region Preview Interaction

        private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_project.SelectedAnimation == null) return;

            Point pos = e.GetPosition(InteractionCanvas);

            // Find layer at click position
            foreach (var layer in _project.SelectedAnimation.Layers)
            {
                if (!layer.IsVisible) continue;

                if (pos.X >= layer.PosX - layer.SizeX / 2 &&
                    pos.X <= layer.PosX + layer.SizeX / 2 &&
                    pos.Y >= layer.PosY - layer.SizeY / 2 &&
                    pos.Y <= layer.PosY + layer.SizeY / 2)
                {
                    _isDraggingPreview = true;
                    _previewDragStart = pos;
                    _draggedLayer = layer;
                    SelectLayerInTree(layer);
                    InteractionCanvas.CaptureMouse();
                    break;
                }
            }
        }

        private void Preview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingPreview || _draggedLayer == null) return;

            Point pos = e.GetPosition(InteractionCanvas);
            float dx = (float)(pos.X - _previewDragStart.X);
            float dy = (float)(pos.Y - _previewDragStart.Y);

            _draggedLayer.PosX += dx;
            _draggedLayer.PosY += dy;

            _previewDragStart = pos;

            UpdatePropertiesUI();
            SendLayersToEngine();
            _project.MarkDirty();
        }

        private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingPreview = false;
            _draggedLayer = null;
            InteractionCanvas.ReleaseMouseCapture();
        }

        #endregion

        #region Timeline

        private void DrawTimelineRuler()
        {
            if (TimelineRuler == null || _project == null) return;

            // Reset pool indices and hide pooled elements
            _rulerLinePoolIndex = 0;
            _rulerTextPoolIndex = 0;
            foreach (var line in _rulerLinePool) line.Visibility = Visibility.Collapsed;
            foreach (var text in _rulerTextPool) text.Visibility = Visibility.Collapsed;

            double pxPerFrame = _project.TimelineZoom * 4;
            int totalFrames = _project?.SelectedAnimation?.LengthFrames ?? 250;
            double totalWidth = totalFrames * pxPerFrame;

            TimelineCanvas.Width = Math.Max(totalWidth + 100, TimelineScroll.ActualWidth);

            // Use cached brush (initialized in MainWindow_Loaded)
            var textSecondary = _textSecondaryBrush;

            // Draw markers
            int step = pxPerFrame < 2 ? 50 : (pxPerFrame < 5 ? 25 : (pxPerFrame < 10 ? 10 : 5));

            for (int f = 0; f <= totalFrames; f += step)
            {
                double x = AppConstants.TimelineStartOffset + f * pxPerFrame;

                var line = GetPooledRulerLine();
                line.X1 = x;
                line.Y1 = 18;
                line.X2 = x;
                line.Y2 = 24;
                line.Stroke = textSecondary;
                line.StrokeThickness = 1;
                line.Visibility = Visibility.Visible;

                var text = GetPooledRulerTextBlock();
                text.Text = f.ToString();
                text.FontSize = 9;
                text.Foreground = textSecondary;
                Canvas.SetLeft(text, x + 2);
                Canvas.SetTop(text, 2);
                text.Visibility = Visibility.Visible;
            }

            // Playhead marker - reuse single instance
            if (_rulerPlayhead == null)
            {
                _rulerPlayhead = new Line
                {
                    Stroke = Brushes.Red,
                    StrokeThickness = 2
                };
                TimelineRuler.Children.Add(_rulerPlayhead);
            }

            double playheadX = AppConstants.TimelineStartOffset + _project.CurrentFrame * pxPerFrame;
            _rulerPlayhead.X1 = playheadX;
            _rulerPlayhead.Y1 = 0;
            _rulerPlayhead.X2 = playheadX;
            _rulerPlayhead.Y2 = 24;
        }

        private Line GetPooledRulerLine()
        {
            if (_rulerLinePoolIndex < _rulerLinePool.Count)
            {
                return _rulerLinePool[_rulerLinePoolIndex++];
            }

            var line = new Line();
            _rulerLinePool.Add(line);
            TimelineRuler.Children.Add(line);
            _rulerLinePoolIndex++;
            return line;
        }

        private TextBlock GetPooledRulerTextBlock()
        {
            if (_rulerTextPoolIndex < _rulerTextPool.Count)
            {
                return _rulerTextPool[_rulerTextPoolIndex++];
            }

            var text = new TextBlock();
            _rulerTextPool.Add(text);
            TimelineRuler.Children.Add(text);
            _rulerTextPoolIndex++;
            return text;
        }

        private void UpdateTimelineCanvas()
        {
            if (TimelineCanvas == null || _project?.SelectedAnimation == null) return;

            // Reset pool indices
            _rectPoolIndex = 0;
            _textPoolIndex = 0;
            _linePoolIndex = 0;

            // Hide all pooled elements first (faster than removing)
            foreach (var rect in _timelineRectPool) rect.Visibility = Visibility.Collapsed;
            foreach (var text in _timelineTextPool) text.Visibility = Visibility.Collapsed;
            foreach (var line in _timelineLinePool) line.Visibility = Visibility.Collapsed;

            double pxPerFrame = _project.TimelineZoom * 4;
            int layerHeaderHeight = 22;
            int trackHeight = 18;
            int trackY = 4;

            // Use cached brushes (initialized once in MainWindow_Loaded)
            var accentBrush = _accentBrush;
            var keyframeBrush = _keyframeBrush;
            var textBrush = _textMainBrush;
            var textSecondary = _textSecondaryBrush;

            foreach (var layer in _project.SelectedAnimation.Layers)
            {
                bool isSelectedLayer = layer == _project.SelectedLayer;

                // Layer header background - use pooled rectangle
                var layerBg = GetPooledRectangle();
                layerBg.Width = TimelineCanvas.Width;
                layerBg.Height = layerHeaderHeight - 2;
                layerBg.Fill = isSelectedLayer ? BrushLayerSelected : BrushLayerNormal;
                layerBg.Stroke = null;
                layerBg.StrokeThickness = 0;
                layerBg.RenderTransform = null;
                layerBg.Cursor = Cursors.Arrow;
                layerBg.Tag = null;
                Canvas.SetLeft(layerBg, 0);
                Canvas.SetTop(layerBg, trackY);
                layerBg.Visibility = Visibility.Visible;

                // Layer name - use pooled textblock
                var layerNameText = GetPooledTextBlock();
                layerNameText.Text = layer.Name;
                layerNameText.FontSize = 11;
                layerNameText.FontWeight = FontWeights.SemiBold;
                layerNameText.Foreground = textBrush;
                Canvas.SetLeft(layerNameText, 4);
                Canvas.SetTop(layerNameText, trackY + 3);
                layerNameText.Visibility = Visibility.Visible;

                trackY += layerHeaderHeight;

                // Draw each property track that has keyframes
                foreach (var track in layer.Tracks)
                {
                    if (track.Keyframes.Count == 0) continue;

                    bool isSelectedTrack = track.PropertyId == _selectedKeyframeTrackId && isSelectedLayer;

                    // Property track background - use pooled rectangle
                    var propBg = GetPooledRectangle();
                    propBg.Width = TimelineCanvas.Width;
                    propBg.Height = trackHeight - 1;
                    propBg.Fill = isSelectedTrack ? BrushTrackSelected : BrushTrackNormal;
                    propBg.Stroke = null;
                    propBg.StrokeThickness = 0;
                    propBg.RenderTransform = null;
                    propBg.Cursor = Cursors.Arrow;
                    propBg.Tag = null;
                    Canvas.SetLeft(propBg, 0);
                    Canvas.SetTop(propBg, trackY);
                    propBg.Visibility = Visibility.Visible;

                    // Property name - use pooled textblock
                    var propNameText = GetPooledTextBlock();
                    propNameText.Text = "  " + track.PropertyName;
                    propNameText.FontSize = 9;
                    propNameText.FontWeight = FontWeights.Normal;
                    propNameText.Foreground = textSecondary;
                    Canvas.SetLeft(propNameText, 8);
                    Canvas.SetTop(propNameText, trackY + 2);
                    propNameText.Visibility = Visibility.Visible;

                    // Draw keyframes for this property
                    KeyframeModel prevKf = null;
                    foreach (var kf in track.Keyframes)
                    {
                        double x = AppConstants.TimelineStartOffset + kf.Frame * pxPerFrame;

                        // Connection line to previous keyframe - use pooled line
                        if (prevKf != null)
                        {
                            double prevX = AppConstants.TimelineStartOffset + prevKf.Frame * pxPerFrame;
                            var line = GetPooledLine();
                            line.X1 = prevX;
                            line.Y1 = trackY + trackHeight / 2;
                            line.X2 = x;
                            line.Y2 = trackY + trackHeight / 2;
                            line.Stroke = BrushConnectionLine;
                            line.StrokeThickness = 1;
                            line.Visibility = Visibility.Visible;
                        }

                        // Diamond keyframe marker - use pooled rectangle
                        var diamond = GetPooledRectangle();
                        diamond.Width = 8;
                        diamond.Height = 8;
                        diamond.Fill = kf.IsSelected ? BrushKeyframeSelected : keyframeBrush;
                        diamond.Stroke = kf.IsSelected ? accentBrush : null;
                        diamond.StrokeThickness = kf.IsSelected ? 2 : 0;
                        diamond.RenderTransformOrigin = new Point(0.5, 0.5);
                        diamond.RenderTransform = _diamondRotateTransform; // Shared transform
                        diamond.Cursor = Cursors.Hand;
                        diamond.Tag = new Tuple<LayerModel, PropertyTrackModel, KeyframeModel>(layer, track, kf);
                        Canvas.SetLeft(diamond, x - 4);
                        Canvas.SetTop(diamond, trackY + trackHeight / 2 - 4);
                        diamond.Visibility = Visibility.Visible;

                        prevKf = kf;
                    }

                    trackY += trackHeight;
                }

                // Also render TextTrack keyframes (string/text content keyframes)
                if (layer.TextTrack != null && layer.TextTrack.Keyframes.Count > 0)
                {
                    bool isSelectedTrack = layer.TextTrack.PropertyId == _selectedKeyframeTrackId && isSelectedLayer;

                    // Text track background
                    var textTrackBg = GetPooledRectangle();
                    textTrackBg.Width = TimelineCanvas.Width;
                    textTrackBg.Height = trackHeight - 1;
                    textTrackBg.Fill = isSelectedTrack ? BrushTrackSelected : BrushTrackNormal;
                    textTrackBg.Stroke = null;
                    textTrackBg.StrokeThickness = 0;
                    textTrackBg.RenderTransform = null;
                    textTrackBg.Cursor = Cursors.Arrow;
                    textTrackBg.Tag = null;
                    Canvas.SetLeft(textTrackBg, 0);
                    Canvas.SetTop(textTrackBg, trackY);
                    textTrackBg.Visibility = Visibility.Visible;

                    // Text track name
                    var textTrackName = GetPooledTextBlock();
                    textTrackName.Text = "  " + layer.TextTrack.PropertyName;
                    textTrackName.FontSize = 9;
                    textTrackName.FontWeight = FontWeights.Normal;
                    textTrackName.Foreground = textSecondary;
                    Canvas.SetLeft(textTrackName, 8);
                    Canvas.SetTop(textTrackName, trackY + 2);
                    textTrackName.Visibility = Visibility.Visible;

                    // Draw text keyframes (no connection lines - jump interpolation)
                    foreach (var kf in layer.TextTrack.Keyframes)
                    {
                        double x = AppConstants.TimelineStartOffset + kf.Frame * pxPerFrame;

                        // Diamond keyframe marker
                        var diamond = GetPooledRectangle();
                        diamond.Width = 8;
                        diamond.Height = 8;
                        diamond.Fill = kf.IsSelected ? BrushKeyframeSelected : keyframeBrush;
                        diamond.Stroke = kf.IsSelected ? accentBrush : null;
                        diamond.StrokeThickness = kf.IsSelected ? 2 : 0;
                        diamond.RenderTransformOrigin = new Point(0.5, 0.5);
                        diamond.RenderTransform = _diamondRotateTransform;
                        diamond.Cursor = Cursors.Hand;
                        diamond.Tag = new Tuple<LayerModel, StringPropertyTrackModel, StringKeyframeModel>(layer, layer.TextTrack, kf);
                        Canvas.SetLeft(diamond, x - 4);
                        Canvas.SetTop(diamond, trackY + trackHeight / 2 - 4);
                        diamond.Visibility = Visibility.Visible;
                    }

                    trackY += trackHeight;
                }
            }

            TimelineCanvas.Height = Math.Max(trackY + 20, 140);
            UpdatePlayheadPosition();
        }

        // Shared rotate transform for keyframe diamonds (avoids allocation)
        private static readonly RotateTransform _diamondRotateTransform = new RotateTransform(45);

        private Rectangle GetPooledRectangle()
        {
            if (_rectPoolIndex < _timelineRectPool.Count)
            {
                return _timelineRectPool[_rectPoolIndex++];
            }

            var rect = new Rectangle();
            // Single event handler per rectangle - uses Tag to find data
            rect.MouseLeftButtonDown += PooledKeyframe_Click;
            _timelineRectPool.Add(rect);
            TimelineCanvas.Children.Add(rect);
            _rectPoolIndex++;
            return rect;
        }

        private TextBlock GetPooledTextBlock()
        {
            if (_textPoolIndex < _timelineTextPool.Count)
            {
                return _timelineTextPool[_textPoolIndex++];
            }

            var text = new TextBlock();
            _timelineTextPool.Add(text);
            TimelineCanvas.Children.Add(text);
            _textPoolIndex++;
            return text;
        }

        private Line GetPooledLine()
        {
            if (_linePoolIndex < _timelineLinePool.Count)
            {
                return _timelineLinePool[_linePoolIndex++];
            }

            var line = new Line();
            _timelineLinePool.Add(line);
            TimelineCanvas.Children.Add(line);
            _linePoolIndex++;
            return line;
        }

        private void PooledKeyframe_Click(object sender, MouseButtonEventArgs e)
        {
            // Handle clicks on keyframe diamonds (they have Tag set)
            if (sender is Rectangle rect)
            {
                if (rect.Tag is Tuple<LayerModel, PropertyTrackModel, KeyframeModel>)
                {
                    Keyframe_Diamond_Click(sender, e);
                }
                else if (rect.Tag is Tuple<LayerModel, StringPropertyTrackModel, StringKeyframeModel>)
                {
                    StringKeyframe_Diamond_Click(sender, e);
                }
            }
        }

        private void Keyframe_Diamond_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle diamond && diamond.Tag is Tuple<LayerModel, PropertyTrackModel, KeyframeModel> data)
            {
                var layer = data.Item1;
                var track = data.Item2;
                var kf = data.Item3;

                // Deselect previously selected keyframes
                if (_selectedKeyframe != null && _selectedKeyframe != kf)
                {
                    _selectedKeyframe.IsSelected = false;
                }
                if (_selectedStringKeyframe != null)
                {
                    _selectedStringKeyframe.IsSelected = false;
                    _selectedStringKeyframe = null;
                }

                // Select this keyframe
                kf.IsSelected = true;
                _selectedKeyframe = kf;
                _selectedKeyframeTrackId = track.PropertyId;

                // Select the layer
                _project.SelectedLayer = layer;
                SelectLayerInTree(layer);

                // Move playhead to keyframe position
                _project.CurrentFrame = kf.Frame;

                UpdateTimelineCanvas();
                UpdatePropertiesUI();
                UpdateKeyframeButtons();

                e.Handled = true;
            }
        }

        private void StringKeyframe_Diamond_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle diamond && diamond.Tag is Tuple<LayerModel, StringPropertyTrackModel, StringKeyframeModel> data)
            {
                var layer = data.Item1;
                var track = data.Item2;
                var kf = data.Item3;

                // Deselect previously selected keyframes
                if (_selectedKeyframe != null)
                {
                    _selectedKeyframe.IsSelected = false;
                    _selectedKeyframe = null;
                }
                if (_selectedStringKeyframe != null && _selectedStringKeyframe != kf)
                {
                    _selectedStringKeyframe.IsSelected = false;
                }

                // Select this keyframe
                kf.IsSelected = true;
                _selectedStringKeyframe = kf;
                _selectedKeyframeTrackId = track.PropertyId;

                // Select the layer
                _project.SelectedLayer = layer;
                SelectLayerInTree(layer);

                // Move playhead to keyframe position
                _project.CurrentFrame = kf.Frame;

                UpdateTimelineCanvas();
                UpdatePropertiesUI();
                UpdateKeyframeButtons();

                e.Handled = true;
            }
        }

        private void UpdatePlayheadPosition()
        {
            if (Playhead == null || _project == null) return;

            double x = AppConstants.TimelineStartOffset + _project.CurrentFrame * _project.TimelineZoom * 4;
            Canvas.SetLeft(Playhead, x);
            Playhead.Y2 = TimelineCanvas.Height;

            // Also update the ruler playhead without redrawing the entire ruler
            if (_rulerPlayhead != null)
            {
                _rulerPlayhead.X1 = x;
                _rulerPlayhead.X2 = x;
            }
        }

        private void TimelineRuler_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingPlayhead = true;
            TimelineRuler.CaptureMouse();
            SeekToMouse(e.GetPosition(TimelineRuler));
        }

        private void TimelineRuler_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPlayhead) SeekToMouse(e.GetPosition(TimelineRuler));
        }

        private void TimelineRuler_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingPlayhead = false;
            TimelineRuler.ReleaseMouseCapture();
        }

        private void TimelineCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingPlayhead = true;
            TimelineCanvas.CaptureMouse();
            SeekToMouse(e.GetPosition(TimelineCanvas));
        }

        private void TimelineCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPlayhead) SeekToMouse(e.GetPosition(TimelineCanvas));
        }

        private void TimelineCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingPlayhead = false;
            TimelineCanvas.ReleaseMouseCapture();
        }

        private void SeekToMouse(Point pos)
        {
            if (_project == null) return;

            double pxPerFrame = _project.TimelineZoom * 4;
            int frame = (int)((pos.X - AppConstants.TimelineStartOffset) / pxPerFrame);
            int maxFrame = (_project?.SelectedAnimation?.LengthFrames ?? 250) - 1;

            _project.CurrentFrame = Math.Clamp(frame, 0, maxFrame);

            // Apply animation
            if (_project.SelectedAnimation != null)
            {
                foreach (var layer in _project.SelectedAnimation.Layers)
                {
                    layer.ApplyAnimationAtFrame(_project.CurrentFrame);
                }
            }

            UpdateFrameDisplay();
            UpdatePlayheadPosition();
            UpdateKeyframeIndicators();
            UpdatePropertiesUI();
            SendLayersToEngine();
        }

        private void Timeline_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                _project.TimelineZoom *= e.Delta > 0 ? 1.2 : 1 / 1.2;
                DrawTimelineRuler();
                UpdateTimelineCanvas();
                e.Handled = true;
            }
        }

        #endregion

        #region Transport

        private void Rewind_Click(object sender, RoutedEventArgs e)
        {
            _project.CurrentFrame = 0;
            OnFrameChanged();
        }

        private void PrevFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_project.CurrentFrame > 0)
            {
                _project.CurrentFrame--;
                OnFrameChanged();
            }
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            _project.IsPlaying = !_project.IsPlaying;

            if (_project.IsPlaying)
            {
                _playbackTimer.Start();
                BtnPlay.Content = "⏸";
                BtnPlay.Background = _stopColorBrush;
            }
            else
            {
                _playbackTimer.Stop();
                BtnPlay.Content = "▶";
                BtnPlay.Background = _playColorBrush;
            }
        }

        private void NextFrame_Click(object sender, RoutedEventArgs e)
        {
            int maxFrame = (_project?.SelectedAnimation?.LengthFrames ?? 250) - 1;
            if (_project.CurrentFrame < maxFrame)
            {
                _project.CurrentFrame++;
                OnFrameChanged();
            }
        }

        private void Forward_Click(object sender, RoutedEventArgs e)
        {
            _project.CurrentFrame = (_project?.SelectedAnimation?.LengthFrames ?? 250) - 1;
            OnFrameChanged();
        }

        private void FrameDown_Click(object sender, RoutedEventArgs e) => PrevFrame_Click(sender, e);
        private void FrameUp_Click(object sender, RoutedEventArgs e) => NextFrame_Click(sender, e);

        private void CurrentFrame_Changed(object sender, TextChangedEventArgs e)
        {
            if (_project == null || _isUpdatingUI) return;

            if (int.TryParse(TxtCurrentFrame.Text, out int frame))
            {
                int maxFrame = (_project?.SelectedAnimation?.LengthFrames ?? 250) - 1;
                _project.CurrentFrame = Math.Clamp(frame, 0, maxFrame);
                OnFrameChanged();
            }
        }

        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            if (_project == null) return;

            int maxFrame = (_project?.SelectedAnimation?.LengthFrames ?? 250) - 1;
            _project.CurrentFrame = _project.CurrentFrame >= maxFrame ? 0 : _project.CurrentFrame + 1;

            OnFrameChanged();
        }

        private void OnFrameChanged()
        {
            // Apply animation
            if (_project.SelectedAnimation != null)
            {
                foreach (var layer in _project.SelectedAnimation.Layers)
                {
                    layer.ApplyAnimationAtFrame(_project.CurrentFrame);
                }
            }

            UpdateFrameDisplay();
            UpdatePlayheadPosition();
            UpdateKeyframeIndicators();

            // Only update properties UI if layer values actually changed
            UpdatePropertiesUIIfChanged();

            SendLayersToEngine();

            // Sync video layers to current timeline frame
            SyncVideoLayersToFrame(_project.CurrentFrame);
        }

        private void SyncVideoLayersToFrame(int frame)
        {
            if (_engine == null || !_engine.IsInitialized || _project?.SelectedAnimation == null) return;

            bool isPlaying = _project.IsPlaying;

            foreach (var layer in _project.SelectedAnimation.Layers)
            {
                if (layer.TextureSource != TextureSourceType.VideoFile || layer.VideoId <= 0)
                    continue;

                var track = layer.GetTrack("VideoState");
                if (isPlaying && track != null && track.Keyframes.Count > 0)
                {
                    // During playback: evaluate VideoState keyframes (step interpolation, no smoothing)
                    float stateValue = track.GetStepValueAtFrame(frame);
                    int state = (int)stateValue;
                    switch (state)
                    {
                        case 0: // Stop
                            _engine.StopVideo(layer.VideoId);
                            break;
                        case 1: // Play
                            _engine.PlayVideo(layer.VideoId);
                            break;
                        case 2: // Pause
                            _engine.PauseVideo(layer.VideoId);
                            break;
                        case 3: // Stop (legacy Rewind)
                            _engine.StopVideo(layer.VideoId);
                            break;
                    }
                }
                else
                {
                    // Scrubbing or no keyframes: convert timeline frame to time
                    // (timeline runs at 50fps, video may have different frame rate)
                    double seconds = frame / 50.0;
                    _engine.SeekVideoTime(layer.VideoId, seconds);
                }
            }
        }

        private void UpdatePropertiesUIIfChanged()
        {
            var layer = _project.SelectedLayer;
            int selectedIndex = _project.SelectedAnimation?.Layers.IndexOf(layer) ?? -1;

            // Always update if selected layer changed
            if (selectedIndex != _lastSelectedLayerIndex)
            {
                _lastSelectedLayerIndex = selectedIndex;
                UpdatePropertiesUI();
                CacheLayerValues(layer);
                return;
            }

            if (layer == null) return;

            // Check if any animated values changed
            bool changed =
                Math.Abs(layer.PosX - _cachedPosX) > 0.001f ||
                Math.Abs(layer.PosY - _cachedPosY) > 0.001f ||
                Math.Abs(layer.SizeX - _cachedSizeX) > 0.001f ||
                Math.Abs(layer.SizeY - _cachedSizeY) > 0.001f ||
                Math.Abs(layer.RotX - _cachedRotX) > 0.001f ||
                Math.Abs(layer.RotY - _cachedRotY) > 0.001f ||
                Math.Abs(layer.RotZ - _cachedRotZ) > 0.001f ||
                Math.Abs(layer.Opacity - _cachedOpacity) > 0.001f;

            if (changed)
            {
                UpdatePropertiesUI();
                CacheLayerValues(layer);
            }
        }

        private void CacheLayerValues(LayerModel layer)
        {
            if (layer == null) return;
            _cachedPosX = layer.PosX;
            _cachedPosY = layer.PosY;
            _cachedSizeX = layer.SizeX;
            _cachedSizeY = layer.SizeY;
            _cachedRotX = layer.RotX;
            _cachedRotY = layer.RotY;
            _cachedRotZ = layer.RotZ;
            _cachedOpacity = layer.Opacity;
        }

        private void UpdateFrameDisplay()
        {
            _isUpdatingUI = true;
            TxtCurrentFrame.Text = _project.CurrentFrame.ToString();
            _isUpdatingUI = false;
        }

        private void UpdateTotalFramesDisplay()
        {
            int total = _project?.SelectedAnimation?.LengthFrames ?? 250;
            TxtTotalFrames.Text = $"/ {total} fr";
        }

        #endregion

        #region Helpers

        private void UpdateUI()
        {
            UpdatePropertiesUI();
            UpdateFrameDisplay();
            UpdateTotalFramesDisplay();
            DrawTimelineRuler();
            UpdateTimelineCanvas();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_project != null && _project.IsDirty)
            {
                var result = MessageBox.Show(
                    "Save changes before closing?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == MessageBoxResult.Yes)
                {
                    // Must save synchronously - async void Menu_SaveProject would fire-and-forget
                    try
                    {
                        if (!string.IsNullOrEmpty(_project?.FilePath))
                        {
                            SaveProjectAsync(_project.FilePath).GetAwaiter().GetResult();
                        }
                        else
                        {
                            var dlg = new SaveFileDialog
                            {
                                Title = "Save Project",
                                Filter = "Daro Project (*.daro)|*.daro|All Files (*.*)|*.*",
                                DefaultExt = ".daro",
                                FileName = "NewProject.daro"
                            };
                            if (dlg.ShowDialog() == true)
                            {
                                SaveProjectAsync(dlg.FileName).GetAwaiter().GetResult();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Error saving project on close", ex);
                        MessageBox.Show($"Error saving project:\n{ex.Message}", "Save Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }

            // Stop and unsubscribe timers
            if (_playbackTimer != null)
            {
                _playbackTimer.Stop();
                _playbackTimer.Tick -= PlaybackTimer_Tick;
            }

            if (_statsTimer != null)
            {
                _statsTimer.Stop();
                _statsTimer.Tick -= StatsTimer_Tick;
            }

            // Unsubscribe window events
            Loaded -= MainWindow_Loaded;
            SizeChanged -= _sizeChangedHandler;
            PreviewKeyDown -= MainWindow_PreviewKeyDown;

            // Stop autosave
            _autosave?.Dispose();

            // Clean up layer tree UI handlers
            CleanupLayerTreeHandlers();

            // Unsubscribe engine events and release GPU resources
            if (_engine != null)
            {
                _engine.OnFrameRendered -= OnEngineFrameRendered;
                _engine.Stop(); // Stop render thread before releasing Spout/texture resources
                ReleaseAllProjectResources();
                _engine.Dispose();
            }
        }

        #endregion

        #region Missing Event Handlers

        private KeyframeModel _selectedKeyframe;
        private StringKeyframeModel _selectedStringKeyframe;
        private string _selectedKeyframeTrackId;

        private void Keyframe_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string propertyId)
            {
                var menu = new ContextMenu();

                var setKeyItem = new MenuItem { Header = "Set Key", Tag = propertyId };
                setKeyItem.Click += SetKey_Click;

                var deleteKeyItem = new MenuItem { Header = "Delete Key", Tag = propertyId };
                deleteKeyItem.Click += DeleteKey_Click;

                // Check if there's a keyframe at current frame
                var layer = _project?.SelectedLayer;
                if (layer != null)
                {
                    int frame = _project.CurrentFrame;
                    bool hasKeyframe = layer.HasKeyframeAtFrame(propertyId, frame);
                    deleteKeyItem.IsEnabled = hasKeyframe;
                }

                var transfunctionerItem = new MenuItem { Header = "Transfunctioner...", Tag = propertyId };
                transfunctionerItem.Click += CreateTransfunctioner_Click;

                menu.Items.Add(setKeyItem);
                menu.Items.Add(deleteKeyItem);
                menu.Items.Add(new Separator());
                menu.Items.Add(transfunctionerItem);

                // Unsubscribe handlers when menu closes to prevent leak
                menu.Closed += (ms, ma) =>
                {
                    setKeyItem.Click -= SetKey_Click;
                    deleteKeyItem.Click -= DeleteKey_Click;
                    transfunctionerItem.Click -= CreateTransfunctioner_Click;
                };

                btn.ContextMenu = menu;
                menu.IsOpen = true;
                e.Handled = true;
            }
        }

        private void Keyframe_LeftClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string propertyId)
            {
                var layer = _project?.SelectedLayer;
                if (layer == null) return;

                int frame = _project.CurrentFrame;

                // Toggle keyframe at current frame (create if doesn't exist, delete if exists)
                if (layer.HasKeyframeAtFrame(propertyId, frame))
                {
                    layer.DeleteKeyframe(propertyId, frame);
                }
                else
                {
                    float value = layer.GetPropertyValue(propertyId);
                    layer.SetKeyframe(propertyId, frame, value);
                }

                UpdateTimelineCanvas();
                UpdateKeyframeButtons();
                _project.MarkDirty();
            }
        }

        private void SetKey_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag is string propertyId)
            {
                var layer = _project?.SelectedLayer;
                if (layer == null) return;
                
                int frame = _project.CurrentFrame;
                float value = layer.GetPropertyValue(propertyId);
                layer.SetKeyframe(propertyId, frame, value);
                
                UpdateTimelineCanvas();
                UpdateKeyframeButtons();
                _project.MarkDirty();
            }
        }

        private void DeleteKey_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag is string propertyId)
            {
                var layer = _project?.SelectedLayer;
                if (layer == null) return;

                int frame = _project.CurrentFrame;
                layer.DeleteKeyframe(propertyId, frame);

                UpdateTimelineCanvas();
                UpdateKeyframeButtons();
                _project.MarkDirty();
            }
        }

        // Text keyframe handlers (jump interpolation - value changes instantly at keyframe)
        private void TextKeyframe_LeftClick(object sender, RoutedEventArgs e)
        {
            var layer = _project?.SelectedLayer;
            if (layer == null) return;

            int frame = _project.CurrentFrame;

            // Toggle text keyframe at current frame
            if (layer.HasTextKeyframeAtFrame(frame))
            {
                layer.DeleteTextKeyframe(frame);
            }
            else
            {
                layer.SetTextKeyframe(frame, layer.TextContent);
            }

            UpdateTimelineCanvas();
            UpdateKeyframeButtons();
            _project.MarkDirty();
        }

        private void TextKeyframe_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button btn)
            {
                var layer = _project?.SelectedLayer;
                if (layer == null) return;

                int frame = _project.CurrentFrame;

                var menu = new ContextMenu();

                var setKeyItem = new MenuItem { Header = "Set Key (Jump)" };
                setKeyItem.Click += (s, args) =>
                {
                    layer.SetTextKeyframe(frame, layer.TextContent);
                    UpdateTimelineCanvas();
                    UpdateKeyframeButtons();
                    _project.MarkDirty();
                };

                var deleteKeyItem = new MenuItem { Header = "Delete Key", IsEnabled = layer.HasTextKeyframeAtFrame(frame) };
                deleteKeyItem.Click += (s, args) =>
                {
                    layer.DeleteTextKeyframe(frame);
                    UpdateTimelineCanvas();
                    UpdateKeyframeButtons();
                    _project.MarkDirty();
                };

                var transfunctionerItem = new MenuItem { Header = "Transfunctioner...", Tag = "TextContent" };
                transfunctionerItem.Click += CreateTransfunctioner_Click;

                menu.Items.Add(setKeyItem);
                menu.Items.Add(deleteKeyItem);
                menu.Items.Add(new Separator());
                menu.Items.Add(transfunctionerItem);

                // Unsubscribe handlers when menu closes to prevent leak
                menu.Closed += (ms, ma) =>
                {
                    transfunctionerItem.Click -= CreateTransfunctioner_Click;
                };

                btn.ContextMenu = menu;
                menu.IsOpen = true;
                e.Handled = true;
            }
        }

        private void CreateTransfunctioner_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag is string propertyId)
            {
                var layer = _project?.SelectedLayer;
                if (layer == null) return;

                // Create a new transfunctioner binding for this property
                var binding = layer.AddTransfunctioner(propertyId);
                _project.MarkDirty();

                // Refresh the list and switch to Transfunctioners tab
                RefreshTransfunctionersList();
                UpdateKeyframeButtons(); // Update yellow border
                MainPropertiesTabControl.SelectedIndex = 1;
            }
        }

        private void RefreshTransfunctionersList()
        {
            if (_project?.SelectedAnimation == null)
            {
                TransfunctionersList.ItemsSource = null;
                TxtTransfunctionerCount.Text = "(0)";
                return;
            }

            // Collect all transfunctioners from all layers, ensure ParentLayer is set
            var allTransfunctioners = new List<TransfunctionerBindingModel>();
            foreach (var layer in _project.SelectedAnimation.Layers)
            {
                foreach (var tf in layer.Transfunctioners)
                {
                    tf.ParentLayer = layer;
                    allTransfunctioners.Add(tf);
                }
            }

            // Sort by name
            allTransfunctioners = allTransfunctioners.OrderBy(t => t.Name).ToList();

            TransfunctionersList.ItemsSource = allTransfunctioners;
            TxtTransfunctionerCount.Text = $"({allTransfunctioners.Count})";
        }

        private void TransfunctionersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Single click just selects the item, no tab switch
            // Double click switches to Properties tab (see TransfunctionersList_MouseDoubleClick)
        }

        private void TransfunctionersList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TransfunctionersList.SelectedItem is TransfunctionerBindingModel binding)
            {
                // Find the layer that contains this transfunctioner
                var layer = _project?.SelectedAnimation?.Layers
                    .FirstOrDefault(l => l.Transfunctioners.Any(t => t.Id == binding.Id));

                if (layer != null)
                {
                    // Select the layer
                    _project.SelectedLayer = layer;
                    RefreshLayersTree();
                    UpdatePropertiesUI();

                    // Switch to Properties tab
                    MainPropertiesTabControl.SelectedIndex = 0;

                    // Focus the corresponding property control
                    FocusPropertyControl(binding.TargetPropertyId);
                }
            }
        }

        private void FocusPropertyControl(string propertyId)
        {
            // Map property IDs to their corresponding controls
            Control targetControl = propertyId switch
            {
                "PosX" => TxtPosX,
                "PosY" => TxtPosY,
                "SizeX" => TxtSizeX,
                "SizeY" => TxtSizeY,
                "RotX" => TxtRotX,
                "RotY" => TxtRotY,
                "RotZ" => TxtRotZ,
                "Opacity" => TxtOpacity,
                "ColorR" => TxtColorR,
                "ColorG" => TxtColorG,
                "ColorB" => TxtColorB,
                "FontSize" => TxtFontSize,
                "LineHeight" => TxtLineHeight,
                "LetterSpacing" => TxtLetterSpacing,
                "TexX" => TxtTexX,
                "TexY" => TxtTexY,
                "TexW" => TxtTexW,
                "TexH" => TxtTexH,
                "TexRot" => TxtTexRot,
                "TextContent" => TxtTextContent,
                _ => null
            };

            if (targetControl != null)
            {
                // Use Dispatcher to ensure UI is updated before focusing
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    targetControl.BringIntoView();
                    targetControl.Focus();

                    // If it's a TextBox, select all text for easy editing
                    if (targetControl is TextBox textBox)
                    {
                        textBox.SelectAll();
                    }
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void DeleteTransfunctioner_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string transfunctionerId)
            {
                // Find and remove the transfunctioner
                foreach (var layer in _project?.SelectedAnimation?.Layers ?? Enumerable.Empty<LayerModel>())
                {
                    var binding = layer.Transfunctioners.FirstOrDefault(t => t.Id == transfunctionerId);
                    if (binding != null)
                    {
                        layer.RemoveTransfunctioner(transfunctionerId);
                        _project.MarkDirty();
                        RefreshTransfunctionersList();
                        UpdateKeyframeButtons(); // Update yellow border
                        break;
                    }
                }
            }
        }

        private TextBox _stepEditingTextBox = null;
        private static readonly SolidColorBrush StepEditBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x5F));
        private static readonly SolidColorBrush DefaultEditBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

        private void TransfunctionerValue_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is TextBox textBox) || !(textBox.Tag is string transfunctionerId))
                return;

            // Alt+Click enters step editing mode
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                var tfBinding = FindTransfunctionerBinding(transfunctionerId);
                if (tfBinding == null) return;

                // Clear binding first
                BindingOperations.ClearBinding(textBox, TextBox.TextProperty);

                // Enter step editing mode
                _stepEditingTextBox = textBox;
                textBox.Background = StepEditBrush;
                textBox.Text = tfBinding.StepValue.ToString("G");
                textBox.Focus();
                textBox.SelectAll();
                e.Handled = true;
            }
        }

        private void TransfunctionerValue_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                // Skip if entering step editing mode (already handled)
                if (_stepEditingTextBox == textBox)
                    return;

                // Clear the binding to prevent updates during editing
                var currentText = textBox.Text;
                BindingOperations.ClearBinding(textBox, TextBox.TextProperty);
                textBox.Text = currentText;
            }
        }

        private void TransfunctionerValue_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                // Exit step editing mode if active
                if (_stepEditingTextBox == textBox)
                {
                    _stepEditingTextBox = null;
                    textBox.Background = DefaultEditBrush;
                }

                // Restore the binding (resets to current value if Enter wasn't pressed)
                var binding = new Binding("CurrentValue") { Mode = BindingMode.OneWay };
                textBox.SetBinding(TextBox.TextProperty, binding);
            }
        }

        private void TransfunctionerValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (!(sender is TextBox textBox) || !(textBox.Tag is string transfunctionerId))
                return;

            var tfBinding = FindTransfunctionerBinding(transfunctionerId);
            if (tfBinding == null) return;

            // Handle Enter
            if (e.Key == Key.Enter)
            {
                if (_stepEditingTextBox == textBox)
                {
                    // Save step value and exit step editing mode
                    if (float.TryParse(textBox.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float stepValue) && stepValue > 0)
                    {
                        tfBinding.StepValue = stepValue;
                    }
                    _stepEditingTextBox = null;
                    textBox.Background = DefaultEditBrush;
                    textBox.Text = tfBinding.CurrentValue;
                }
                else
                {
                    // Save property value
                    tfBinding.CurrentValue = textBox.Text;
                    _project.MarkDirty();
                    SendLayersToEngine();
                    Keyboard.ClearFocus();
                }
                e.Handled = true;
                return;
            }

            // Handle Escape - cancel step editing
            if (e.Key == Key.Escape && _stepEditingTextBox == textBox)
            {
                _stepEditingTextBox = null;
                textBox.Background = DefaultEditBrush;
                textBox.Text = tfBinding.CurrentValue;
                e.Handled = true;
                return;
            }

            // Handle Up/Down arrows - increment/decrement numeric value (only in value mode)
            if (_stepEditingTextBox != textBox && (e.Key == Key.Up || e.Key == Key.Down))
            {
                if (float.TryParse(textBox.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float currentValue))
                {
                    float step = tfBinding.StepValue;
                    float newValue = e.Key == Key.Up ? currentValue + step : currentValue - step;
                    textBox.Text = newValue.ToString("F2");

                    // Apply immediately
                    tfBinding.CurrentValue = textBox.Text;
                    _project.MarkDirty();
                    SendLayersToEngine();
                }
                e.Handled = true;
            }
        }

        private TransfunctionerBindingModel FindTransfunctionerBinding(string transfunctionerId)
        {
            foreach (var layer in _project?.SelectedAnimation?.Layers ?? Enumerable.Empty<LayerModel>())
            {
                var binding = layer.Transfunctioners.FirstOrDefault(t => t.Id == transfunctionerId);
                if (binding != null)
                    return binding;
            }
            return null;
        }

        private void ApplyTransfunctionerValue(string transfunctionerId, string value)
        {
            var binding = FindTransfunctionerBinding(transfunctionerId);
            if (binding != null)
            {
                binding.CurrentValue = value;
                _project.MarkDirty();
                SendLayersToEngine();
            }
        }

        private void TransfunctionerValue_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(sender is TextBox textBox) || !(textBox.Tag is string transfunctionerId))
                return;

            // Only handle if TextBox has focus and not in step editing mode
            if (!textBox.IsFocused || _stepEditingTextBox == textBox)
                return;

            var tfBinding = FindTransfunctionerBinding(transfunctionerId);
            if (tfBinding == null) return;

            if (float.TryParse(textBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float currentValue))
            {
                float step = tfBinding.StepValue;
                float newValue = e.Delta > 0 ? currentValue + step : currentValue - step;
                textBox.Text = newValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

                // Apply immediately
                tfBinding.CurrentValue = textBox.Text;
                _project.MarkDirty();
                SendLayersToEngine();
            }
            e.Handled = true;
        }

        private void UpdateKeyframeButtons()
        {
            var layer = _project?.SelectedLayer;
            if (layer == null) return;
            
            // Update all keyframe button colors based on whether they have keyframes
            var buttons = new Dictionary<string, Button>
            {
                { "PosX", KfPosX },
                { "PosY", KfPosY },
                { "SizeX", KfSizeX },
                { "SizeY", KfSizeY },
                { "RotX", KfRotX },
                { "RotY", KfRotY },
                { "RotZ", KfRotZ },
                { "Opacity", KfOpacity },
                { "ColorR", KfColorR },
                { "ColorG", KfColorG },
                { "ColorB", KfColorB },
                { "Visible", KfVisible },
                { "FontSize", KfFontSize },
                { "LineHeight", KfLineHeight },
                { "LetterSpacing", KfLetterSpacing },
                { "TexX", KfTexX },
                { "TexY", KfTexY },
                { "TexW", KfTexW },
                { "TexH", KfTexH },
                { "TexRot", KfTexRot }
            };

            // Use static frozen brushes to avoid per-frame allocations
            var defaultBorderBrush = _borderDarkBrush;  // Use cached brush

            foreach (var kvp in buttons)
            {
                if (kvp.Value == null) continue;

                var track = layer.GetTrack(kvp.Key);
                bool hasKeyframes = track != null && track.Keyframes.Count > 0;
                bool hasTransfunctioner = layer.Transfunctioners.Any(t => t.TargetPropertyId == kvp.Key);

                // Find the Ellipse inside the button
                if (kvp.Value.Template?.FindName("KeyframeEllipse", kvp.Value) is System.Windows.Shapes.Ellipse ellipse)
                {
                    ellipse.Fill = hasKeyframes ? BrushKeyframeRed : BrushInactive;
                    // Yellow border if has Transfunctioner
                    ellipse.Stroke = hasTransfunctioner ? BrushTransfunctionerYellow : defaultBorderBrush;
                    ellipse.StrokeThickness = hasTransfunctioner ? 2 : 1;
                }
                else
                {
                    // Fallback - set button background
                    kvp.Value.Background = hasKeyframes ? BrushKeyframeRed : Brushes.Transparent;
                }
            }

            // Update text content keyframe button (separate track with jump interpolation)
            if (KfTextContent != null)
            {
                bool hasTextKeyframes = layer.TextTrack != null && layer.TextTrack.Keyframes.Count > 0;
                bool hasTextTransfunctioner = layer.Transfunctioners.Any(t => t.TargetPropertyId == "TextContent");

                if (KfTextContent.Template?.FindName("KeyframeEllipse", KfTextContent) is System.Windows.Shapes.Ellipse textEllipse)
                {
                    textEllipse.Fill = hasTextKeyframes ? BrushKeyframeRed : BrushInactive;
                    // Yellow border if has Transfunctioner
                    textEllipse.Stroke = hasTextTransfunctioner ? BrushTransfunctionerYellow : defaultBorderBrush;
                    textEllipse.StrokeThickness = hasTextTransfunctioner ? 2 : 1;
                }
                else
                {
                    KfTextContent.Background = hasTextKeyframes ? BrushKeyframeRed : Brushes.Transparent;
                }
            }

            // Update keyframe properties panel
            UpdateKeyframePropertiesUI();
        }

        private void UpdateKeyframePropertiesUI()
        {
            if (_selectedKeyframe != null)
            {
                KeyframePropertiesPanel.Visibility = Visibility.Visible;
                TxtNoKeyframe.Visibility = Visibility.Collapsed;

                TxtKeyframeInfo.Text = $"Frame: {_selectedKeyframe.Frame}";

                // Show easing controls for numeric keyframes
                if (TxtEaseOut != null) TxtEaseOut.IsEnabled = true;
                if (TxtEaseIn != null) TxtEaseIn.IsEnabled = true;

                _isUpdatingUI = true;
                TxtEaseOut.Text = ((int)(_selectedKeyframe.EaseOut * 100)).ToString();
                TxtEaseIn.Text = ((int)(_selectedKeyframe.EaseIn * 100)).ToString();
                _isUpdatingUI = false;
            }
            else if (_selectedStringKeyframe != null)
            {
                // String keyframes use "jump" interpolation - no easing
                KeyframePropertiesPanel.Visibility = Visibility.Visible;
                TxtNoKeyframe.Visibility = Visibility.Collapsed;

                TxtKeyframeInfo.Text = $"Frame: {_selectedStringKeyframe.Frame} (Text)";

                // Disable easing controls for string keyframes (jump interpolation)
                if (TxtEaseOut != null) TxtEaseOut.IsEnabled = false;
                if (TxtEaseIn != null) TxtEaseIn.IsEnabled = false;

                _isUpdatingUI = true;
                TxtEaseOut.Text = "0";
                TxtEaseIn.Text = "0";
                _isUpdatingUI = false;
            }
            else
            {
                KeyframePropertiesPanel.Visibility = Visibility.Collapsed;
                TxtNoKeyframe.Visibility = Visibility.Visible;
            }
        }

        private void EaseOut_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedKeyframe == null) return;
            
            if (int.TryParse(TxtEaseOut.Text, out int value))
            {
                value = Math.Clamp(value, 0, 100);
                _selectedKeyframe.EaseOut = value / 100f;
                _project?.MarkDirty();
            }
        }

        private void EaseIn_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedKeyframe == null) return;
            
            if (int.TryParse(TxtEaseIn.Text, out int value))
            {
                value = Math.Clamp(value, 0, 100);
                _selectedKeyframe.EaseIn = value / 100f;
                _project?.MarkDirty();
            }
        }

        // Property step values - stores custom step for each property
        private Dictionary<string, float> _propertyStepValues = new Dictionary<string, float>();
        private TextBox _propertyStepEditingTextBox = null;

        private float GetPropertyStepValue(string propertyId)
        {
            if (_propertyStepValues.TryGetValue(propertyId, out float step))
                return step;

            // Default steps based on property type
            if (propertyId.StartsWith("Rot") || propertyId == "Opacity" || propertyId == "LineHeight")
                return 0.1f;
            if (propertyId.StartsWith("Tex"))
                return 10f;
            if (propertyId.StartsWith("Color"))
                return 1f;
            return 1f;
        }

        private void NumericInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is TextBox textBox) || !(textBox.Tag is string propertyId))
                return;

            // Alt+Click enters step editing mode
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                _propertyStepEditingTextBox = textBox;
                textBox.Background = StepEditBrush;
                _isUpdatingUI = true;
                textBox.Text = GetPropertyStepValue(propertyId).ToString("G");
                _isUpdatingUI = false;
                textBox.Focus();
                textBox.SelectAll();
                e.Handled = true;
            }
        }

        private void NumericInput_GotFocus(object sender, RoutedEventArgs e)
        {
            // Nothing special needed for regular focus
        }

        private void NumericInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                // Exit step editing mode if active
                if (_propertyStepEditingTextBox == textBox)
                {
                    _propertyStepEditingTextBox = null;
                    textBox.ClearValue(TextBox.BackgroundProperty); // Restore original style
                    // Restore the property value
                    if (textBox.Tag is string propertyId && _project?.SelectedLayer != null)
                    {
                        float currentValue = _project.SelectedLayer.GetPropertyValue(propertyId);
                        // Color components are stored as 0-1 but displayed as 0-255
                        if (propertyId.StartsWith("Color"))
                            currentValue *= 255f;
                        _isUpdatingUI = true;
                        textBox.Text = currentValue.ToString("F2");
                        _isUpdatingUI = false;
                    }
                }
            }
        }

        private void NumericInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (!(sender is TextBox textBox) || !(textBox.Tag is string propertyId))
                return;

            if (_project?.SelectedLayer == null) return;

            // Handle Enter
            if (e.Key == Key.Enter)
            {
                if (_propertyStepEditingTextBox == textBox)
                {
                    // Save step value and exit step editing mode
                    if (float.TryParse(textBox.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float stepValue) && stepValue > 0)
                    {
                        _propertyStepValues[propertyId] = stepValue;
                    }
                    _propertyStepEditingTextBox = null;
                    textBox.ClearValue(TextBox.BackgroundProperty); // Restore original style
                    float currentValue = _project.SelectedLayer.GetPropertyValue(propertyId);
                    // Color components are stored as 0-1 but displayed as 0-255
                    if (propertyId.StartsWith("Color"))
                        currentValue *= 255f;
                    _isUpdatingUI = true;
                    textBox.Text = currentValue.ToString("F2");
                    _isUpdatingUI = false;
                }
                else
                {
                    // Apply the value - color components need conversion
                    if (float.TryParse(textBox.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value))
                    {
                        if (propertyId.StartsWith("Color"))
                            value /= 255f;
                        _project.SelectedLayer.SetPropertyValue(propertyId, value);
                        SendLayersToEngine();
                        _project.MarkDirty();
                    }
                    Keyboard.ClearFocus();
                }
                e.Handled = true;
                return;
            }

            // Handle Escape - cancel step editing
            if (e.Key == Key.Escape && _propertyStepEditingTextBox == textBox)
            {
                _propertyStepEditingTextBox = null;
                textBox.ClearValue(TextBox.BackgroundProperty); // Restore original style
                float currentValue = _project.SelectedLayer.GetPropertyValue(propertyId);
                // Color components are stored as 0-1 but displayed as 0-255
                if (propertyId.StartsWith("Color"))
                    currentValue *= 255f;
                _isUpdatingUI = true;
                textBox.Text = currentValue.ToString("F2");
                _isUpdatingUI = false;
                e.Handled = true;
                return;
            }

            // Handle Up/Down arrows (only in value mode)
            if (_propertyStepEditingTextBox != textBox && (e.Key == Key.Up || e.Key == Key.Down))
            {
                if (float.TryParse(textBox.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value))
                {
                    float step = GetPropertyStepValue(propertyId);
                    value = e.Key == Key.Up ? value + step : value - step;
                    textBox.Text = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

                    // Color components are stored as 0-1 but displayed as 0-255
                    float storeValue = propertyId.StartsWith("Color") ? value / 255f : value;
                    _project.SelectedLayer.SetPropertyValue(propertyId, storeValue);
                    SendLayersToEngine();
                    _project.MarkDirty();
                }
                e.Handled = true;
            }
        }

        private void NumericInput_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null) return;

            if (sender is TextBox txt && txt.Tag is string propertyId)
            {
                // Don't handle in step editing mode
                if (_propertyStepEditingTextBox == txt) return;

                if (float.TryParse(txt.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value))
                {
                    float step = GetPropertyStepValue(propertyId);
                    value += e.Delta > 0 ? step : -step;
                    txt.Text = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

                    // Color components are stored as 0-1 but displayed as 0-255
                    float storeValue = propertyId.StartsWith("Color") ? value / 255f : value;
                    _project.SelectedLayer.SetPropertyValue(propertyId, storeValue);
                    SendLayersToEngine();
                    _project.MarkDirty();
                }
                e.Handled = true;
            }
        }

        private void LockAspect_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.LockAspectRatio = !_project.SelectedLayer.LockAspectRatio;
            UpdateLockAspectVisual();
            _project.MarkDirty();
        }

        private void UpdateLockAspectVisual()
        {
            if (_project?.SelectedLayer == null) return;
            // Use static frozen brushes to avoid per-call allocations
            BtnLockAspect.Background = _project.SelectedLayer.LockAspectRatio
                ? BrushLockAspectActive
                : BrushInactive;
        }

        private void LockTexAspect_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            _project.SelectedLayer.LockTexAspectRatio = !_project.SelectedLayer.LockTexAspectRatio;
            _project.MarkDirty();
        }

        private void OpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            
            if (sender is Slider slider)
            {
                _project.SelectedLayer.Opacity = (float)slider.Value;
                UpdatePropertiesUI();
                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        private void SetVisible_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.SelectedLayer == null || _isUpdatingUI) return;
            
            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                _project.SelectedLayer.IsVisible = (tag == "Visible");
                RefreshLayersTree();
                SendLayersToEngine();
                _project.MarkDirty();
            }
        }

        #endregion
    }
}
