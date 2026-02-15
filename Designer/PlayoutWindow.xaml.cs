// Designer/PlayoutWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DaroDesigner.Models;
using DaroDesigner.Services;

namespace DaroDesigner
{
    public partial class PlayoutWindow : Window
    {
        #region Fields

        private PlaylistModel _playlist;
        private TemplateModel _loadedTemplate;
        private Dictionary<string, string> _templateFillData;
        private TemplateFolderModel _templateRootFolder;
        private TemplateItemModel _selectedTemplateItem;
        private string _templatesRootPath;

        private PlayoutEngineWindow _engineWindow;
        private DispatcherTimer _statusTimer;
        private bool _closing;

        // Mosart integration
        private MosartServer _mosartServer;
        private PlaylistDatabase _playlistDatabase;
        private volatile string _lastMosartItemId; // Track last CUE'd item for PLAY/STOP (volatile for thread safety)
        private readonly SemaphoreSlim _mosartCommandLock = new SemaphoreSlim(1, 1); // Serialize Mosart command handling

        // Middleware process management
        private Process _middlewareProcess;
        private CancellationTokenSource _middlewareHealthCts;
        private MiddlewareState _middlewareState = MiddlewareState.Stopped;
        private static readonly HttpClient _healthClient = new() { Timeout = TimeSpan.FromSeconds(3) };

        private enum MiddlewareState { Stopped, Starting, Running, Stopping }

        // Cached brushes for performance (static readonly to avoid allocation)
        private static readonly SolidColorBrush BrushFormLabel = new SolidColorBrush(Color.FromRgb(180, 180, 180));
        private static readonly SolidColorBrush BrushFormBackground = new SolidColorBrush(Color.FromRgb(45, 45, 48));
        private static readonly SolidColorBrush BrushFormBorder = new SolidColorBrush(Color.FromRgb(63, 63, 70));
        private static readonly SolidColorBrush BrushFormError = new SolidColorBrush(Color.FromRgb(200, 80, 80));

        // Static constructor to freeze brushes
        static PlayoutWindow()
        {
            BrushFormLabel.Freeze();
            BrushFormBackground.Freeze();
            BrushFormBorder.Freeze();
            BrushFormError.Freeze();
        }

        // Cached resource brushes (initialized in Loaded event)
        private Brush _playColorBrush;
        private Brush _fgSecondaryBrush;

        // Cached status values to avoid unnecessary UI updates
        private string _lastStatusText;
        private string _lastOnAirText;
        private bool _lastOnAirState;

        #endregion

        #region Constructor

        public PlayoutWindow()
        {
            InitializeComponent();

            _playlist = new PlaylistModel();
            PlaylistItems.ItemsSource = _playlist.Items;

            ChkAutoAdvance.IsChecked = _playlist.AutoAdvance;
            ChkLoop.IsChecked = _playlist.Loop;

            Loaded += OnLoaded;

            // Status update timer
            _statusTimer = new DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromMilliseconds(AppConstants.StatusUpdateIntervalMs);
            _statusTimer.Tick += OnStatusTick;
            _statusTimer.Start();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Cache resource brushes once at startup
            _playColorBrush = (Brush)FindResource("PlayColor");
            _fgSecondaryBrush = (Brush)FindResource("FgSecondary");

            RefreshTemplatesTree();
            UpdateUI();

            // Initialize Mosart integration
            InitializeMosartServer();
            InitializePlaylistDatabase();
        }

        private void OnStatusTick(object sender, EventArgs e)
        {
            UpdateEngineStatus();
        }

        #endregion

        #region Engine Window

        private void OpenEngineWindow_Click(object sender, RoutedEventArgs e)
        {
            OpenEngineWindow();
        }

        private void OpenEngineWindow()
        {
            if (_engineWindow == null || !_engineWindow.IsLoaded)
            {
                _engineWindow = new PlayoutEngineWindow();
                _engineWindow.Owner = this;
                _engineWindow.Closed += (s, args) => _engineWindow = null;
                _engineWindow.Show();
            }
            else
            {
                _engineWindow.Activate();
            }
        }

        private void UpdateEngineStatus()
        {
            // Use local copy to avoid race condition (thread safety pattern)
            var engineWindow = _engineWindow;

            // Skip if no engine window
            if (engineWindow == null || !engineWindow.IsLoaded || !engineWindow.IsReady)
                return;

            var fps = engineWindow.Fps;
            var playing = engineWindow.IsPlaying;
            var frame = engineWindow.Frame;
            var total = engineWindow.TotalFrames;
            var anim = engineWindow.AnimationName;

            // Build new status text
            string newStatusText;
            string newOnAirText;
            bool newOnAirState;

            if (playing)
            {
                newStatusText = $"Playing: {anim} [{frame}/{total}] | {fps:F0} FPS";
                newOnAirText = "ON AIR";
                newOnAirState = true;
            }
            else if (_playlist.CurrentItem != null)
            {
                newStatusText = $"Ready: {anim} | {fps:F0} FPS";
                newOnAirText = _lastOnAirText;  // Keep previous on-air state
                newOnAirState = _lastOnAirState;
            }
            else
            {
                return;  // No update needed
            }

            // Only update UI if values changed
            if (newStatusText != _lastStatusText)
            {
                TxtStatus.Text = newStatusText;
                _lastStatusText = newStatusText;
            }

            if (newOnAirText != _lastOnAirText || newOnAirState != _lastOnAirState)
            {
                TxtOnAirStatus.Text = newOnAirText;
                TxtOnAirStatus.Foreground = newOnAirState ? _playColorBrush : _fgSecondaryBrush;
                _lastOnAirText = newOnAirText;
                _lastOnAirState = newOnAirState;
            }
        }

        #endregion

        #region Templates

        private void RefreshTemplates_Click(object sender, RoutedEventArgs e)
        {
            RefreshTemplatesTree();
        }

        private void RefreshTemplatesTree()
        {
            if (string.IsNullOrEmpty(_templatesRootPath))
            {
                _templatesRootPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "DaroEngine", "Templates");
            }

            if (!Directory.Exists(_templatesRootPath))
            {
                Directory.CreateDirectory(_templatesRootPath);
                _templateRootFolder = new TemplateFolderModel { Name = "Templates", Path = _templatesRootPath };
                TemplatesTree.ItemsSource = new[] { _templateRootFolder };
                TxtStatus.Text = "Templates folder created";
                return;
            }

            _templateRootFolder = ScanTemplateFolder(_templatesRootPath, "Templates");
            TemplatesTree.ItemsSource = new[] { _templateRootFolder };

            var count = CountTemplates(_templateRootFolder);
            TxtStatus.Text = $"Found {count} template(s)";
        }

        private TemplateFolderModel ScanTemplateFolder(string path, string name)
        {
            var folder = new TemplateFolderModel { Name = name, Path = path };

            foreach (var subDir in Directory.GetDirectories(path))
            {
                folder.SubFolders.Add(ScanTemplateFolder(subDir, Path.GetFileName(subDir)));
            }

            foreach (var file in Directory.GetFiles(path, "*.dtemplate"))
            {
                folder.Templates.Add(new TemplateItemModel
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    FilePath = file
                });
            }

            folder.NotifyChildrenChanged();
            return folder;
        }

        private int CountTemplates(TemplateFolderModel folder)
        {
            return (folder.Templates?.Count ?? 0) + (folder.SubFolders?.Sum(f => CountTemplates(f)) ?? 0);
        }

        private void TemplatesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TemplateItemModel item)
            {
                _selectedTemplateItem = item;
                TxtSelectedTemplate.Text = item.Name;
                BtnLoadTemplate.IsEnabled = true;
            }
            else
            {
                _selectedTemplateItem = null;
                TxtSelectedTemplate.Text = "No template selected";
                BtnLoadTemplate.IsEnabled = false;
            }
        }

        private async void LoadTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTemplateItem == null) return;

            // Security: Validate path before file access
            if (!PathValidator.IsPathAllowed(_selectedTemplateItem.FilePath))
            {
                MessageBox.Show("Invalid template path", "Security Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_selectedTemplateItem.FilePath);
                _loadedTemplate = JsonSerializer.Deserialize<TemplateModel>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    MaxDepth = AppConstants.MaxJsonDepth // Prevent stack overflow from deeply nested JSON
                });
                if (_loadedTemplate == null)
                {
                    MessageBox.Show("Failed to parse template file", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                _loadedTemplate.FolderPath = Path.GetDirectoryName(_selectedTemplateItem.FilePath);

                // Initialize fill data
                _templateFillData = new Dictionary<string, string>();
                if (_loadedTemplate.Elements != null)
                {
                    foreach (var element in _loadedTemplate.Elements)
                    {
                        _templateFillData[element.Id] = element.DefaultText ?? "";
                    }
                }

                GenerateTemplateFillForm();

                TxtFillTemplateName.Text = _loadedTemplate.Name;
                TxtTakesTemplate.Text = $"Takes: {_loadedTemplate.Name}";
                BtnAddToPlaylist.IsEnabled = true;

                TakesList.ItemsSource = _loadedTemplate.Takes;
                TabFillData.IsSelected = true;

                TxtStatus.Text = $"Loaded: {_loadedTemplate.Name}";
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading template: {_selectedTemplateItem?.FilePath}", ex);
                MessageBox.Show($"Error loading template:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Fill Form

        private void GenerateTemplateFillForm()
        {
            // Unsubscribe event handlers before clearing to prevent memory leaks
            UnsubscribeFormFieldEvents();
            TemplateFillForm.Children.Clear();
            if (_loadedTemplate?.Elements == null) return;

            // Set canvas size to match template dimensions
            TemplateFillForm.Width = _loadedTemplate.CanvasWidth;
            TemplateFillForm.Height = _loadedTemplate.CanvasHeight;

            // Render all elements at their template positions
            foreach (var element in _loadedTemplate.Elements)
            {
                AddFormElement(element);
            }
        }

        private void UnsubscribeFormFieldEvents()
        {
            foreach (var child in TemplateFillForm.Children)
            {
                if (child is Border border && border.Child is TextBox textBox)
                {
                    textBox.TextChanged -= OnFormFieldTextChanged;
                    textBox.LostFocus -= OnFormFieldLostFocus;
                }
            }
        }

        private Brush ParseBrush(string color)
        {
            try
            {
                if (string.IsNullOrEmpty(color)) return Brushes.Transparent;
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                brush.Freeze();
                return brush;
            }
            catch (FormatException)
            {
                return Brushes.Transparent;
            }
        }

        private void AddFormElement(TemplateElementModel element)
        {
            var border = new Border
            {
                Width = element.Width,
                Height = element.Height,
                Background = ParseBrush(element.BackgroundColor),
                BorderBrush = BrushFormBorder,
                BorderThickness = new Thickness(1),
                Tag = element
            };

            FrameworkElement content;

            switch (element.ElementType)
            {
                case TemplateElementType.Label:
                    content = new TextBlock
                    {
                        Text = element.DefaultText ?? "Label",
                        FontFamily = new FontFamily(element.FontFamily ?? "Segoe UI"),
                        FontSize = element.FontSize > 0 ? element.FontSize : 14,
                        Foreground = ParseBrush(element.ForegroundColor),
                        VerticalAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(4, 2, 4, 2),
                        IsHitTestVisible = false
                    };
                    border.BorderThickness = new Thickness(0);
                    break;

                case TemplateElementType.TextBox:
                {
                    var tb = new TextBox
                    {
                        Text = _templateFillData.TryGetValue(element.Id, out var val) ? val : element.DefaultText,
                        FontFamily = new FontFamily(element.FontFamily ?? "Segoe UI"),
                        FontSize = element.FontSize > 0 ? element.FontSize : 14,
                        Foreground = ParseBrush(element.ForegroundColor),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(4, 2, 4, 2),
                        Tag = element,
                        ToolTip = element.MaxLength > 0 ? $"Max {element.MaxLength} characters" : null
                    };
                    if (element.MaxLength > 0) tb.MaxLength = element.MaxLength;
                    tb.TextChanged += OnFormFieldTextChanged;
                    tb.LostFocus += OnFormFieldLostFocus;
                    content = tb;
                    break;
                }

                case TemplateElementType.MultilineTextBox:
                {
                    var tb = new TextBox
                    {
                        Text = _templateFillData.TryGetValue(element.Id, out var val2) ? val2 : element.DefaultText,
                        FontFamily = new FontFamily(element.FontFamily ?? "Segoe UI"),
                        FontSize = element.FontSize > 0 ? element.FontSize : 14,
                        Foreground = ParseBrush(element.ForegroundColor),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Padding = new Thickness(4, 2, 4, 2),
                        Tag = element,
                        ToolTip = element.MaxLength > 0 ? $"Max {element.MaxLength} characters" : null
                    };
                    if (element.MaxLength > 0) tb.MaxLength = element.MaxLength;
                    tb.TextChanged += OnFormFieldTextChanged;
                    tb.LostFocus += OnFormFieldLostFocus;
                    content = tb;
                    break;
                }

                default:
                    content = new TextBlock { Text = "?" };
                    break;
            }

            border.Child = content;

            Canvas.SetLeft(border, element.X);
            Canvas.SetTop(border, element.Y);
            TemplateFillForm.Children.Add(border);
        }

        private void OnFormFieldTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.Tag is TemplateElementModel element)
            {
                _templateFillData[element.Id] = tb.Text;
            }
        }

        private void OnFormFieldLostFocus(object sender, RoutedEventArgs e)
        {
            // Validate on blur and show visual feedback on parent Border
            if (sender is TextBox tb && tb.Tag is TemplateElementModel element && tb.Parent is Border border)
            {
                var error = element.Validate(tb.Text);
                if (error != null)
                {
                    border.BorderBrush = BrushFormError;
                    tb.ToolTip = error;
                }
                else
                {
                    border.BorderBrush = BrushFormBorder;
                    tb.ToolTip = element.MaxLength > 0 ? $"Max {element.MaxLength} characters" : null;
                }
            }
        }

        private void ClearFillForm_Click(object sender, RoutedEventArgs e)
        {
            if (_loadedTemplate == null) return;

            foreach (var element in _loadedTemplate.Elements)
            {
                _templateFillData[element.Id] = element.DefaultText ?? "";
            }
            GenerateTemplateFillForm();
        }

        #endregion

        #region Playlist Management

        private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (_loadedTemplate == null || _templateFillData == null)
            {
                MessageBox.Show("Please load a template first.", "No Template",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validate all fields
            var errors = new List<string>();
            foreach (var element in _loadedTemplate.Elements.Where(e =>
                e.ElementType == TemplateElementType.TextBox ||
                e.ElementType == TemplateElementType.MultilineTextBox))
            {
                var value = _templateFillData.TryGetValue(element.Id, out var val) ? val : "";
                var error = element.Validate(value);
                if (error != null)
                    errors.Add(error);
            }

            if (errors.Count > 0)
            {
                MessageBox.Show(
                    "Please fix the following errors:\n\n" + string.Join("\n", errors),
                    "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Convert to transfunctioner-keyed data
            var filledData = new Dictionary<string, string>();
            foreach (var element in _loadedTemplate.Elements)
            {
                if (!string.IsNullOrEmpty(element.LinkedTransfunctionerId) &&
                    _templateFillData.TryGetValue(element.Id, out var value))
                {
                    filledData[element.LinkedTransfunctionerId] = value;
                }
            }

            var item = PlaylistItemModel.FromTemplate(_loadedTemplate, filledData);
            _playlist.AddItem(item);

            // Auto-cue first item
            if (_playlist.Items.Count == 1)
            {
                CueItem(item);
            }

            UpdateUI();
            TxtStatus.Text = $"Added: {item.Name}";
        }

        private void PlaylistMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistItems.SelectedItem is PlaylistItemModel item)
                _playlist.MoveItemUp(item);
        }

        private void PlaylistMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistItems.SelectedItem is PlaylistItemModel item)
                _playlist.MoveItemDown(item);
        }

        private void PlaylistRemove_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistItems.SelectedItem is PlaylistItemModel item)
            {
                _playlist.RemoveItem(item);
                UpdateUI();
            }
        }

        private void PlaylistDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistItems.SelectedItem is PlaylistItemModel item)
            {
                _playlist.AddItem(item.Clone());
                UpdateUI();
            }
        }

        private void PlaylistEdit_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistItems.SelectedItem is PlaylistItemModel item &&
                _loadedTemplate != null && item.TemplateId == _loadedTemplate.Id)
            {
                _templateFillData = new Dictionary<string, string>(item.FilledData);
                GenerateTemplateFillForm();
                TabFillData.IsSelected = true;
                TxtStatus.Text = $"Editing: {item.Name}";
            }
        }

        private void PlaylistItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlaylistItems.SelectedItem is PlaylistItemModel item)
            {
                TakesList.ItemsSource = item.Takes;
                TxtTakesTemplate.Text = $"Takes: {item.TemplateName}";
                if (item.Takes.Count > 0)
                    TakesList.SelectedIndex = 0;
            }
        }

        private void PlaylistItems_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (PlaylistItems.SelectedItem is PlaylistItemModel item)
            {
                CueItem(item);
            }
        }

        #endregion

        #region Playlist Options

        private void AutoAdvance_Changed(object sender, RoutedEventArgs e)
        {
            if (_playlist != null)
                _playlist.AutoAdvance = ChkAutoAdvance.IsChecked == true;
        }

        private void Loop_Changed(object sender, RoutedEventArgs e)
        {
            if (_playlist != null)
                _playlist.Loop = ChkLoop.IsChecked == true;
        }

        private void ResetPlaylist_Click(object sender, RoutedEventArgs e)
        {
            _playlist.ResetAll();
            UpdateUI();
            TxtStatus.Text = "Playlist reset";
        }

        private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Clear all items?", "Clear Playlist",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _playlist.Clear();
                UpdateUI();
                TxtStatus.Text = "Playlist cleared";
            }
        }

        #endregion

        #region Output Options

        private void SpoutEnabled_Changed(object sender, RoutedEventArgs e)
        {
            _engineWindow?.SetSpout(ChkSpoutEnabled.IsChecked == true, TxtSpoutName.Text);
        }

        private void SpoutName_LostFocus(object sender, RoutedEventArgs e)
        {
            // Re-apply spout settings with new name
            if (ChkSpoutEnabled.IsChecked == true)
            {
                _engineWindow?.SetSpout(true, TxtSpoutName.Text);
            }
        }

        #endregion

        #region Takes

        private void TakesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Take selection ready for Execute
        }

        #endregion

        #region Transport Controls

        private void Take_Click(object sender, RoutedEventArgs e)
        {
            ExecuteTake();
        }

        private void CueNext_Click(object sender, RoutedEventArgs e)
        {
            // Find next Ready or Done item
            var startIndex = _playlist.NextItem != null
                ? _playlist.Items.IndexOf(_playlist.NextItem)
                : (_playlist.CurrentItem != null ? _playlist.Items.IndexOf(_playlist.CurrentItem) : -1);

            for (int i = startIndex + 1; i < _playlist.Items.Count; i++)
            {
                var item = _playlist.Items[i];
                if (item.Status == PlaylistItemStatus.Ready || item.Status == PlaylistItemStatus.Done)
                {
                    CueItem(item);
                    return;
                }
            }

            // Loop back if enabled
            if (_playlist.Loop)
            {
                for (int i = 0; i < _playlist.Items.Count; i++)
                {
                    var item = _playlist.Items[i];
                    if (item.Status == PlaylistItemStatus.Ready || item.Status == PlaylistItemStatus.Done)
                    {
                        CueItem(item);
                        return;
                    }
                }
            }

            TxtStatus.Text = "No more items to cue";
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            ExecuteClear();
        }

        /// <summary>
        /// Cues an item - marks it as next without loading.
        /// </summary>
        private void CueItem(PlaylistItemModel item)
        {
            _playlist.CueItem(item);

            // Update Takes list
            TakesList.ItemsSource = item.Takes;
            TxtTakesTemplate.Text = $"Takes: {item.TemplateName}";
            if (item.Takes.Count > 0)
                TakesList.SelectedIndex = 0;

            TxtStatus.Text = $"Cued: {item.Name}";
        }

        /// <summary>
        /// Executes Take - loads scene and starts playback.
        /// </summary>
        private void ExecuteTake()
        {
            if (_playlist.NextItem == null)
            {
                TxtStatus.Text = "Nothing cued";
                return;
            }

            // Ensure engine window is open
            if (_engineWindow == null || !_engineWindow.IsLoaded)
            {
                OpenEngineWindow();
            }

            var item = _playlist.NextItem;
            var take = TakesList.SelectedItem as TemplateTakeModel;

            // Update playlist state
            _playlist.TakeOnAir();

            // Load and play
            if (_engineWindow != null && _engineWindow.IsReady)
            {
                TxtStatus.Text = $"Loading: {item.Name}...";

                if (_engineWindow.LoadScene(item.LinkedScenePath, item))
                {
                    _engineWindow.Play(take);
                    TxtStatus.Text = take != null
                        ? $"Playing: {item.Name} ({take.Name})"
                        : $"Playing: {item.Name}";
                }
                else
                {
                    TxtStatus.Text = $"Failed to load: {item.Name}";
                }
            }

            UpdateUI();
        }

        /// <summary>
        /// Clears playback.
        /// </summary>
        private void ExecuteClear()
        {
            _playlist.ClearOnAir();
            _engineWindow?.Clear();
            UpdateUI();
            TxtStatus.Text = "Cleared";
        }

        #endregion

        #region UI Updates

        private void UpdateUI()
        {
            TxtPlaylistCount.Text = $"{_playlist.Items.Count} item(s)";

            if (_playlist.CurrentItem != null)
            {
                TxtOnAirStatus.Text = "ON AIR";
                TxtOnAirStatus.Foreground = _playColorBrush;
            }
            else
            {
                TxtOnAirStatus.Text = "OFF AIR";
                TxtOnAirStatus.Foreground = _fgSecondaryBrush;
            }
        }

        #endregion

        #region Mosart Integration

        // Timeout for database operations (2 seconds)
        private static readonly TimeSpan MosartDbTimeout = TimeSpan.FromSeconds(2);

        private void InitializeMosartServer()
        {
            try
            {
                _mosartServer = new MosartServer(5555);
                _mosartServer.CommandReceived += OnMosartCommand;
                _mosartServer.StatusChanged += OnMosartStatusChanged;
                _mosartServer.LogMessage += OnMosartLog;
                _mosartServer.Start();

                UpdateMosartStatusUI();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to initialize Mosart server", ex);
                TxtStatus.Text = $"Mosart error: {ex.Message}";
            }
        }

        private void InitializePlaylistDatabase()
        {
            try
            {
                _playlistDatabase = new PlaylistDatabase();
                _playlistDatabase.LogMessage += OnMosartLog;

                if (_playlistDatabase.IsAvailable)
                {
                    Logger.Info($"Playlist database: {_playlistDatabase.DatabasePath}");
                }
                else
                {
                    Logger.Warn($"Playlist database not found: {_playlistDatabase.DatabasePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to initialize playlist database", ex);
            }
        }

        private void OnMosartCommand(object sender, MosartCommandEventArgs e)
        {
            // Commands arrive on background thread - handle with async lock to prevent race conditions
            _ = HandleMosartCommandAsync(e.Message, e.SendResponse);
        }

        private async Task HandleMosartCommandAsync(MosartMessage message, Action<string> sendResponse)
        {
            // Acquire lock to serialize command handling (prevents race conditions)
            // Lock is held for the ENTIRE operation including async DB lookup + UI dispatch
            if (!await _mosartCommandLock.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Logger.Warn($"Mosart command timeout waiting for lock: {message.Command} for {message.ItemId}");
                sendResponse("ERROR:COMMAND_QUEUE_TIMEOUT");
                return;
            }

            try
            {
                Logger.Info($"Mosart command: {message.Command} for {message.ItemId}");

                switch (message.Command)
                {
                    case MosartCommand.Cue:
                        await HandleMosartCueAsync(message.ItemId, sendResponse);
                        break;

                    case MosartCommand.Play:
                    case MosartCommand.Continue:
                        await HandleMosartPlayAsync(message.ItemId, sendResponse);
                        break;

                    case MosartCommand.Stop:
                        await Dispatcher.InvokeAsync(() => HandleMosartStop(message.ItemId, sendResponse));
                        break;

                    case MosartCommand.Pause:
                        await Dispatcher.InvokeAsync(() => HandleMosartPause(sendResponse));
                        break;

                    default:
                        sendResponse("ERROR:UNKNOWN_COMMAND");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Mosart command error: {message.Command}", ex);
                sendResponse($"ERROR:{ex.Message}");
            }
            finally
            {
                _mosartCommandLock.Release();
            }
        }

        /// <summary>
        /// Handles Mosart CUE command. Fully async - lock is held by caller for entire duration.
        /// </summary>
        private async Task HandleMosartCueAsync(string itemId, Action<string> sendResponse)
        {
            // Validate database availability
            var database = _playlistDatabase;
            if (database == null || !database.IsAvailable)
            {
                sendResponse("ERROR:DATABASE_NOT_AVAILABLE");
                return;
            }

            // Database lookup on background thread with timeout
            var dbTask = database.GetItemByIdAsync(itemId);
            var completedTask = await Task.WhenAny(dbTask, Task.Delay(MosartDbTimeout));

            if (completedTask != dbTask)
            {
                sendResponse("ERROR:DATABASE_TIMEOUT");
                Logger.Warn($"Mosart CUE: Database timeout for item {itemId}");
                return;
            }

            var storedItem = await dbTask;
            if (storedItem == null)
            {
                sendResponse($"ERROR:ITEM_NOT_FOUND:{itemId}");
                return;
            }

            // Security: Validate scene path before access
            if (!PathValidator.IsPathAllowed(storedItem.LinkedScenePath))
            {
                sendResponse("ERROR:INVALID_SCENE_PATH");
                Logger.Warn($"Mosart CUE: Invalid scene path blocked for item {itemId}");
                return;
            }

            // Verify scene file exists (on calling thread - already background)
            if (!File.Exists(storedItem.LinkedScenePath))
            {
                sendResponse($"ERROR:SCENE_NOT_FOUND:{storedItem.LinkedScenePath}");
                return;
            }

            // Convert to PlaylistItemModel (may read template file)
            var playlistItem = database.ConvertToPlaylistItem(storedItem);
            if (playlistItem == null)
            {
                sendResponse("ERROR:CONVERSION_FAILED");
                return;
            }

            // Switch to UI thread for playlist operations
            await Dispatcher.InvokeAsync(() =>
            {
                // Check if item already exists in playlist (don't add duplicates)
                var existingItem = _playlist.Items.FirstOrDefault(i => i.Id == itemId);
                if (existingItem == null)
                {
                    _playlist.AddItem(playlistItem);
                    existingItem = playlistItem;
                }

                CueItem(existingItem);
                _lastMosartItemId = itemId;

                PlaylistItems.Items.Refresh();
                UpdateUI();

                sendResponse($"OK:CUED:{existingItem.Takes.Count}:{existingItem.Name}");
                Logger.Info($"Mosart CUE: Added '{existingItem.Name}' to playlist with {existingItem.FilledData.Count} filled values, total items: {_playlist.Items.Count}");
            });
        }

        /// <summary>
        /// Handles Mosart PLAY/CONTINUE command. Fully async - lock is held by caller for entire duration.
        /// </summary>
        private async Task HandleMosartPlayAsync(string itemId, Action<string> sendResponse)
        {
            // Check on UI thread if item is already cued or in playlist
            bool hasCued = false;
            bool needsDbLookup = false;

            await Dispatcher.InvokeAsync(() =>
            {
                if (_playlist.NextItem != null)
                {
                    // Item already cued - execute immediately
                    ExecuteTakeAndRespond(sendResponse);
                    hasCued = true;
                    return;
                }

                // Try to find item by ID in playlist (fast)
                var existingItem = _playlist.Items.FirstOrDefault(i => i.Id == itemId);
                if (existingItem != null)
                {
                    CueItem(existingItem);
                    ExecuteTakeAndRespond(sendResponse);
                    hasCued = true;
                    return;
                }

                // Need DB lookup
                needsDbLookup = !string.IsNullOrEmpty(itemId) && _playlistDatabase != null && _playlistDatabase.IsAvailable;
            });

            if (hasCued) return;

            if (!needsDbLookup)
            {
                sendResponse("ERROR:NOTHING_CUED");
                return;
            }

            // Database lookup on background thread with timeout
            var database = _playlistDatabase;
            var dbTask = database.GetItemByIdAsync(itemId);
            var completedTask = await Task.WhenAny(dbTask, Task.Delay(MosartDbTimeout));

            if (completedTask != dbTask)
            {
                sendResponse("ERROR:DATABASE_TIMEOUT");
                Logger.Warn($"Mosart PLAY: Database timeout for item {itemId}");
                return;
            }

            var storedItem = await dbTask;
            if (storedItem == null)
            {
                sendResponse("ERROR:NOTHING_CUED");
                return;
            }

            // Security: Validate scene path
            if (!PathValidator.IsPathAllowed(storedItem.LinkedScenePath))
            {
                sendResponse("ERROR:INVALID_SCENE_PATH");
                return;
            }

            if (!File.Exists(storedItem.LinkedScenePath))
            {
                sendResponse($"ERROR:SCENE_NOT_FOUND:{storedItem.LinkedScenePath}");
                return;
            }

            var playlistItem = database.ConvertToPlaylistItem(storedItem);
            if (playlistItem == null)
            {
                sendResponse("ERROR:CONVERSION_FAILED");
                return;
            }

            // Switch to UI thread for playlist operations and playback
            await Dispatcher.InvokeAsync(() =>
            {
                _playlist.AddItem(playlistItem);
                CueItem(playlistItem);
                ExecuteTakeAndRespond(sendResponse);
            });
        }

        /// <summary>
        /// Executes take and sends response. Must be called on UI thread.
        /// </summary>
        private void ExecuteTakeAndRespond(Action<string> sendResponse)
        {
            ExecuteTake();

            var currentItem = _playlist.CurrentItem;
            if (currentItem != null)
            {
                sendResponse($"OK:PLAYING:{currentItem.Name}");
                Logger.Info($"Mosart PLAY: Playing '{currentItem.Name}'");
            }
            else
            {
                sendResponse("OK:PLAYING");
            }
        }

        private void HandleMosartStop(string itemId, Action<string> sendResponse)
        {
            // All operations are UI-bound and fast - no background thread needed
            ExecuteClear();

            // Remove item from playlist after stop
            var item = _playlist.Items.FirstOrDefault(i => i.Id == itemId);
            if (item != null)
            {
                _playlist.RemoveItem(item);
            }

            _lastMosartItemId = null;
            UpdateUI();

            sendResponse("OK:STOPPED");

            Logger.Info($"Mosart STOP: Cleared playback, removed item '{item?.Name}' from playlist");
        }

        private void HandleMosartPause(Action<string> sendResponse)
        {
            // All operations are UI-bound and fast - no background thread needed
            _engineWindow?.Stop();
            _mosartServer?.BroadcastState("PAUSED");
            sendResponse("OK:PAUSED");
        }

        private void OnMosartStatusChanged(object sender, string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateMosartStatusUI();
            }));
        }

        private void OnMosartLog(object sender, string message)
        {
            Logger.Info(message);
        }

        private void UpdateMosartStatusUI()
        {
            if (_mosartServer != null)
            {
                var status = _mosartServer.IsRunning
                    ? $"Mosart: Port {_mosartServer.Port} ({_mosartServer.ClientCount} client)"
                    : "Mosart: Stopped";

                TxtMosartStatus.Text = status;
            }
        }

        #endregion

        #region Mode Switching

        private void ModeDesigner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Close engine window first
            _engineWindow?.Close();
            _engineWindow = null;

            // Open Designer and close Playout
            var designerWindow = new MainWindow();
            designerWindow.Show();
            this.Close();
        }

        private void ModeTemplate_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Open Template window (modal)
            var templateWindow = new TemplateWindow();
            templateWindow.Owner = this;
            templateWindow.ShowDialog();
        }

        #endregion

        #region Middleware Management

        private async void Middleware_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                switch (_middlewareState)
                {
                    case MiddlewareState.Stopped:
                        await StartMiddlewareAsync();
                        break;
                    case MiddlewareState.Running:
                        await StopMiddlewareAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Middleware button error", ex);
                SetMiddlewareState(MiddlewareState.Stopped);
                TxtStatus.Text = $"Middleware error: {ex.Message}";
            }
        }

        private async Task StartMiddlewareAsync()
        {
            SetMiddlewareState(MiddlewareState.Starting);

            // Search for middleware exe: first in Middleware/ subdir, then next to app, then in repo structure
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var searchPaths = new[]
            {
                Path.Combine(appDir, AppConstants.MiddlewareSubDir, AppConstants.MiddlewareExeName),
                Path.Combine(appDir, "..", "..", "..", "..", "GraphicsMiddleware", "bin", "Release", "net9.0", "win-x64", AppConstants.MiddlewareExeName),
                Path.Combine(appDir, "..", "..", "..", "..", "GraphicsMiddleware", "bin", "Debug", "net9.0", AppConstants.MiddlewareExeName),
            };
            var middlewarePath = searchPaths.FirstOrDefault(File.Exists);

            if (middlewarePath == null)
            {
                MessageBox.Show(
                    $"GraphicsMiddleware.exe not found.\nSearched:\n{string.Join("\n", searchPaths.Select(Path.GetFullPath))}",
                    "Middleware Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetMiddlewareState(MiddlewareState.Stopped);
                return;
            }
            middlewarePath = Path.GetFullPath(middlewarePath);

            // Check if port is already in use
            try
            {
                var response = await _healthClient.GetAsync(AppConstants.MiddlewareHealthUrl);
                if (response.IsSuccessStatusCode)
                {
                    MessageBox.Show(
                        "Port 5000 is already in use. Another instance of middleware may be running.",
                        "Port In Use", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetMiddlewareState(MiddlewareState.Stopped);
                    return;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }

            Logger.Info($"Starting middleware from: {middlewarePath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = middlewarePath,
                WorkingDirectory = Path.GetDirectoryName(middlewarePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

            try
            {
                _middlewareProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _middlewareProcess.OutputDataReceived += (s, args) => { if (args.Data != null) Logger.Info($"[Middleware] {args.Data}"); };
                _middlewareProcess.ErrorDataReceived += (s, args) => { if (args.Data != null) Logger.Warn($"[Middleware] {args.Data}"); };
                _middlewareProcess.Exited += OnMiddlewareProcessExited;

                if (!_middlewareProcess.Start())
                {
                    SetMiddlewareState(MiddlewareState.Stopped);
                    TxtStatus.Text = "Failed to start middleware process";
                    return;
                }

                _middlewareProcess.BeginOutputReadLine();
                _middlewareProcess.BeginErrorReadLine();
                Logger.Info($"Middleware process started, PID: {_middlewareProcess.Id}");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to start middleware process", ex);
                SetMiddlewareState(MiddlewareState.Stopped);
                TxtStatus.Text = $"Middleware start failed: {ex.Message}";
                return;
            }

            _middlewareHealthCts = new CancellationTokenSource();
            var healthOk = await PollHealthUntilReadyAsync(_middlewareHealthCts.Token);

            if (!healthOk)
            {
                Logger.Warn("Middleware health check timed out, killing process");
                await StopMiddlewareAsync();
                TxtStatus.Text = "Middleware failed to start (health check timeout)";
                return;
            }

            SetMiddlewareState(MiddlewareState.Running);
            TxtStatus.Text = "Middleware running on port 5000";

            // Open default browser
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppConstants.MiddlewareBrowseUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to open browser: {ex.Message}");
            }
        }

        private async Task<bool> PollHealthUntilReadyAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(AppConstants.MiddlewareHealthTimeoutMs);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var process = _middlewareProcess;
                if (process == null || process.HasExited)
                    return false;

                try
                {
                    var response = await _healthClient.GetAsync(AppConstants.MiddlewareHealthUrl, ct);
                    if (response.IsSuccessStatusCode)
                        return true;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }

                await Task.Delay(AppConstants.MiddlewareHealthPollMs, ct);
            }

            return false;
        }

        private async Task StopMiddlewareAsync()
        {
            SetMiddlewareState(MiddlewareState.Stopping);

            _middlewareHealthCts?.Cancel();
            _middlewareHealthCts?.Dispose();
            _middlewareHealthCts = null;

            var process = _middlewareProcess;
            if (process != null && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await Task.Run(() => process.WaitForExit(AppConstants.MiddlewareShutdownTimeoutMs));
                    Logger.Info("Middleware process stopped");
                }
                catch (InvalidOperationException) { }
                catch (Exception ex)
                {
                    Logger.Error("Error stopping middleware process", ex);
                }
            }

            CleanupMiddlewareProcess();
            SetMiddlewareState(MiddlewareState.Stopped);
            TxtStatus.Text = "Middleware stopped";
        }

        private void OnMiddlewareProcessExited(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_middlewareState == MiddlewareState.Stopping)
                    return;

                Logger.Warn($"Middleware process exited unexpectedly, exit code: {_middlewareProcess?.ExitCode}");
                CleanupMiddlewareProcess();
                SetMiddlewareState(MiddlewareState.Stopped);
                TxtStatus.Text = "Middleware stopped unexpectedly";
            }));
        }

        private void CleanupMiddlewareProcess()
        {
            if (_middlewareProcess != null)
            {
                _middlewareProcess.Exited -= OnMiddlewareProcessExited;
                _middlewareProcess.Dispose();
                _middlewareProcess = null;
            }
        }

        private void SetMiddlewareState(MiddlewareState newState)
        {
            _middlewareState = newState;
            switch (newState)
            {
                case MiddlewareState.Stopped:
                    BtnMiddleware.Content = "Start Middleware";
                    BtnMiddleware.IsEnabled = true;
                    break;
                case MiddlewareState.Starting:
                    BtnMiddleware.Content = "Starting...";
                    BtnMiddleware.IsEnabled = false;
                    break;
                case MiddlewareState.Running:
                    BtnMiddleware.Content = "Stop Middleware";
                    BtnMiddleware.IsEnabled = true;
                    break;
                case MiddlewareState.Stopping:
                    BtnMiddleware.Content = "Stopping...";
                    BtnMiddleware.IsEnabled = false;
                    break;
            }
        }

        #endregion

        #region Window

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closing) return;
            _closing = true;

            // Unsubscribe event handlers
            Loaded -= OnLoaded;
            UnsubscribeFormFieldEvents();

            if (_statusTimer != null)
            {
                _statusTimer.Stop();
                _statusTimer.Tick -= OnStatusTick;
            }

            // Stop Mosart server
            if (_mosartServer != null)
            {
                _mosartServer.CommandReceived -= OnMosartCommand;
                _mosartServer.StatusChanged -= OnMosartStatusChanged;
                _mosartServer.LogMessage -= OnMosartLog;
                _mosartServer.Stop();
                _mosartServer.Dispose();
                _mosartServer = null;
            }

            // Release database reference (no longer IDisposable)
            _playlistDatabase = null;

            // Dispose Mosart command lock
            _mosartCommandLock?.Dispose();

            // Stop middleware process
            if (_middlewareProcess != null && !_middlewareProcess.HasExited)
            {
                _middlewareHealthCts?.Cancel();
                try
                {
                    _middlewareProcess.Kill(entireProcessTree: true);
                    _middlewareProcess.WaitForExit(AppConstants.MiddlewareShutdownTimeoutMs);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Error killing middleware on close: {ex.Message}");
                }
                CleanupMiddlewareProcess();
            }

            _engineWindow?.Close();
        }

        #endregion
    }
}
