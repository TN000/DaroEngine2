// Designer/TemplateWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using DaroDesigner.Models;
using DaroDesigner.Services;

namespace DaroDesigner
{
    public partial class TemplateWindow : Window
    {
        #region Fields

        private TemplateModel _currentTemplate;
        private TemplateElementModel _selectedElement;
        private ObservableCollection<TemplateFolderModel> _templateFolders;
        private string _currentFilePath;

        // Linked scene for transfunctioners
        private ProjectModel _linkedProject;

        // Drag & drop state
        private bool _isDraggingElement;
        private Point _dragStartPoint;
        private FrameworkElement _draggedVisual;
        private string _draggedToolboxItem;

        // Resize state
        private bool _isResizingElement;
        private string _resizeHandle;
        private Point _resizeStartPoint;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private double _resizeStartX;
        private double _resizeStartY;

        // UI update flag
        private bool _isUpdatingUI;

        // Selected action for editing
        private TakeActionModel _selectedAction;

        // Action dragging state
        private bool _isDraggingAction;
        private Point _actionDragStartPoint;
        private int _actionDragStartFrame;

        // Cached brushes for performance (frozen for better WPF performance)
        private static readonly SolidColorBrush BrushElementBorder = new SolidColorBrush(Color.FromRgb(80, 80, 80));
        private static readonly SolidColorBrush BrushElementSelected = Brushes.Cyan;
        private static readonly SolidColorBrush BrushGridLine = new SolidColorBrush(Color.FromRgb(50, 50, 50));
        private static readonly SolidColorBrush BrushGridText = new SolidColorBrush(Color.FromRgb(100, 100, 100));
        private static readonly SolidColorBrush BrushActionPlay = new SolidColorBrush(Color.FromRgb(46, 125, 50));
        private static readonly SolidColorBrush BrushActionStop = new SolidColorBrush(Color.FromRgb(198, 40, 40));
        private static readonly SolidColorBrush BrushActionCue = new SolidColorBrush(Color.FromRgb(21, 101, 192));

        // Static constructor to freeze brushes
        static TemplateWindow()
        {
            BrushElementBorder.Freeze();
            BrushGridLine.Freeze();
            BrushGridText.Freeze();
            BrushActionPlay.Freeze();
            BrushActionStop.Freeze();
            BrushActionCue.Freeze();
        }

        // Cached font families list (expensive to enumerate)
        private static List<FontFamily> _cachedFontFamilies;

        #endregion

        #region Constructor

        public TemplateWindow()
        {
            InitializeComponent();

            _templateFolders = new ObservableCollection<TemplateFolderModel>();
            _currentTemplate = new TemplateModel();

            InitializeFontList();
            LoadTemplatesList();
            UpdateCanvasFromTemplate();
        }

        private void InitializeFontList()
        {
            // Cache font families list (expensive to enumerate)
            if (_cachedFontFamilies == null)
            {
                _cachedFontFamilies = Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
            }

            CmbElementFont.ItemsSource = _cachedFontFamilies;
            CmbElementFont.DisplayMemberPath = "Source";

            var segoeUI = _cachedFontFamilies.FirstOrDefault(f => f.Source == "Segoe UI");
            if (segoeUI != null)
                CmbElementFont.SelectedItem = segoeUI;
        }

        #endregion

        #region Template List Management

        private void LoadTemplatesList()
        {
            var rootFolder = new TemplateFolderModel
            {
                Name = "Templates",
                Path = GetTemplatesDirectory()
            };

            LoadFolderContents(rootFolder);
            _templateFolders.Clear();
            _templateFolders.Add(rootFolder);

            TemplatesTree.ItemsSource = _templateFolders;
        }

        private string GetTemplatesDirectory()
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DaroEngine", "Templates");

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return dir;
        }

        private void LoadFolderContents(TemplateFolderModel folder)
        {
            if (!Directory.Exists(folder.Path)) return;

            // Load subfolders
            foreach (var subDir in Directory.GetDirectories(folder.Path))
            {
                var subFolder = new TemplateFolderModel
                {
                    Name = System.IO.Path.GetFileName(subDir),
                    Path = subDir
                };
                LoadFolderContents(subFolder);
                folder.SubFolders.Add(subFolder);
            }

            // Load template files
            foreach (var file in Directory.GetFiles(folder.Path, "*.dtemplate"))
            {
                folder.Templates.Add(new TemplateItemModel
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(file),
                    FilePath = file
                });
            }
        }

        private async void TemplatesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            try
            {
                if (e.NewValue is TemplateItemModel templateItem)
                {
                    await LoadTemplateAsync(templateItem.FilePath);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error loading template on selection change", ex);
            }
        }

        private void TemplatesTree_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
        }

        private void TemplatesTree_Drop(object sender, DragEventArgs e)
        {
            // Future: implement template reordering
        }

        private void NewTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmUnsavedChanges()) return;

            _currentTemplate = new TemplateModel { Name = "New Template" };
            _currentFilePath = null;
            UpdateCanvasFromTemplate();
            UpdatePropertiesUI();
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new InputDialog("New Folder", "Enter folder name:", "New Folder");
            if (dialog.ShowDialog() == true)
            {
                var selectedFolder = GetSelectedFolder();
                var basePath = selectedFolder?.Path ?? GetTemplatesDirectory();
                var newPath = System.IO.Path.Combine(basePath, dialog.ResponseText);

                if (!Directory.Exists(newPath))
                {
                    Directory.CreateDirectory(newPath);
                    LoadTemplatesList();
                }
            }
        }

        private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
        {
            var selected = TemplatesTree.SelectedItem;

            if (selected is TemplateItemModel templateItem)
            {
                if (MessageBox.Show($"Delete template '{templateItem.Name}'?",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    if (File.Exists(templateItem.FilePath))
                        File.Delete(templateItem.FilePath);
                    LoadTemplatesList();
                }
            }
            else if (selected is TemplateFolderModel folder && folder.Path != GetTemplatesDirectory())
            {
                if (MessageBox.Show($"Delete folder '{folder.Name}' and all its contents?",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    if (Directory.Exists(folder.Path))
                        Directory.Delete(folder.Path, true);
                    LoadTemplatesList();
                }
            }
        }

        private TemplateFolderModel GetSelectedFolder()
        {
            var selected = TemplatesTree.SelectedItem;
            if (selected is TemplateFolderModel folder) return folder;
            return null;
        }

        #endregion

        #region Template Save/Load

        private async Task LoadTemplateAsync(string filePath)
        {
            if (!ConfirmUnsavedChanges()) return;

            // Security: Validate path before file access
            if (!PathValidator.IsPathAllowed(filePath))
            {
                MessageBox.Show("Invalid template path", "Security Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var template = JsonSerializer.Deserialize<TemplateModel>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    MaxDepth = AppConstants.MaxJsonDepth
                });
                if (template != null)
                {
                    _currentTemplate = template;
                    _currentFilePath = filePath;
                    _currentTemplate.IsDirty = false;

                    // Reload linked scene if available (with path validation)
                    if (!string.IsNullOrEmpty(_currentTemplate.LinkedScenePath) &&
                        PathValidator.IsPathAllowed(_currentTemplate.LinkedScenePath) &&
                        File.Exists(_currentTemplate.LinkedScenePath))
                    {
                        await LoadSceneFileAsync(_currentTemplate.LinkedScenePath);
                        _currentTemplate.IsDirty = false; // Reset after scene load
                    }
                    else
                    {
                        TxtLoadedScene.Text = string.IsNullOrEmpty(_currentTemplate.LinkedScenePath)
                            ? "No scene loaded"
                            : "Scene file not found";
                    }

                    UpdateCanvasFromTemplate();
                    UpdateCanvasSizeUI();
                    _selectedAction = null;
                    UpdateActionAnimationsUI();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load template: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SaveTemplateAsync(string filePath)
        {
            // Security: Validate path before file write
            if (!PathValidator.IsPathAllowed(filePath))
            {
                MessageBox.Show("Invalid save path", "Security Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_currentTemplate, options);
                await File.WriteAllTextAsync(filePath, json);
                _currentFilePath = filePath;
                _currentTemplate.IsDirty = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save template: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Synchronous save for ConfirmUnsavedChanges dialog
        private void SaveTemplateSync(string filePath)
        {
            // Security: Validate path before file write
            if (!PathValidator.IsPathAllowed(filePath))
            {
                MessageBox.Show("Invalid save path", "Security Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_currentTemplate, options);
                File.WriteAllText(filePath, json);
                _currentFilePath = filePath;
                _currentTemplate.IsDirty = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save template: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool ConfirmUnsavedChanges()
        {
            if (_currentTemplate?.IsDirty != true) return true;

            var result = MessageBox.Show(
                "You have unsaved changes. Do you want to save them?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes)
            {
                if (string.IsNullOrEmpty(_currentFilePath))
                    Menu_SaveTemplateAs(null, null);
                else
                    SaveTemplateSync(_currentFilePath);
            }

            return true;
        }

        #endregion

        #region Scene Loading

        private async void LoadScene_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Daro Project (*.daro)|*.daro|All Files (*.*)|*.*",
                    Title = "Load Scene"
                };

                if (dialog.ShowDialog() == true)
                {
                    await LoadSceneFileAsync(dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error in LoadScene", ex);
                MessageBox.Show($"Error loading scene:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadSceneFileAsync(string filePath)
        {
            // Security: Validate path before file access
            if (!PathValidator.IsPathAllowed(filePath))
            {
                MessageBox.Show("Invalid scene path", "Security Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var projectData = JsonSerializer.Deserialize<ProjectData>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    MaxDepth = AppConstants.MaxJsonDepth
                });
                if (projectData == null) return;

                _linkedProject = ProjectModel.FromSerializable(projectData);
                _currentTemplate.LinkedScenePath = filePath;

                // Extract all transfunctioners from all layers in all animations
                _currentTemplate.ExtractedTransfunctioners.Clear();
                _currentTemplate.ExtractedAnimations.Clear();

                foreach (var anim in _linkedProject.Animations)
                {
                    // Extract animation names for Take actions
                    _currentTemplate.ExtractedAnimations.Add(anim.Name);

                    foreach (var layer in anim.Layers)
                    {
                        foreach (var tf in layer.Transfunctioners)
                        {
                            _currentTemplate.ExtractedTransfunctioners.Add(tf);
                        }
                    }
                }

                TransfunctionersList.ItemsSource = _currentTemplate.ExtractedTransfunctioners;
                TxtLoadedScene.Text = System.IO.Path.GetFileName(filePath);
                _currentTemplate.IsDirty = true;

                // Update animation selector if action is selected
                UpdateActionAnimationsUI();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load scene: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TransfunctionerItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is TransfunctionerBindingModel tf)
            {
                DragDrop.DoDragDrop(element, $"Transfunctioner:{tf.Id}", DragDropEffects.Copy);
            }
        }

        #endregion

        #region Canvas Management

        private void CleanupCanvasEventHandlers()
        {
            foreach (UIElement child in TemplateCanvas.Children)
            {
                if (child is Border border)
                {
                    border.MouseLeftButtonDown -= Element_MouseDown;
                    border.MouseMove -= Element_MouseMove;
                    border.MouseLeftButtonUp -= Element_MouseUp;
                }
                else if (child is Rectangle rect)
                {
                    rect.MouseLeftButtonDown -= ResizeHandle_MouseDown;
                    rect.MouseMove -= ResizeHandle_MouseMove;
                    rect.MouseLeftButtonUp -= ResizeHandle_MouseUp;
                }
            }
        }

        private void UpdateCanvasFromTemplate()
        {
            CleanupCanvasEventHandlers();
            TemplateCanvas.Children.Clear();
            if (_currentTemplate == null) return;
            TemplateCanvas.Width = _currentTemplate.CanvasWidth;
            TemplateCanvas.Height = _currentTemplate.CanvasHeight;
            CanvasBackground.Width = _currentTemplate.CanvasWidth;
            CanvasBackground.Height = _currentTemplate.CanvasHeight;

            foreach (var element in _currentTemplate.Elements)
            {
                AddElementToCanvas(element);
            }

            UpdateTakesUI();
        }

        private void UpdateCanvasSizeUI()
        {
            _isUpdatingUI = true;
            TxtCanvasWidth.Text = _currentTemplate.CanvasWidth.ToString();
            TxtCanvasHeight.Text = _currentTemplate.CanvasHeight.ToString();
            _isUpdatingUI = false;
        }

        private void AddElementToCanvas(TemplateElementModel element)
        {
            var container = CreateElementVisual(element);
            Canvas.SetLeft(container, element.X);
            Canvas.SetTop(container, element.Y);
            TemplateCanvas.Children.Add(container);
        }

        private Border CreateElementVisual(TemplateElementModel element)
        {
            var border = new Border
            {
                Width = element.Width,
                Height = element.Height,
                Background = ParseBrush(element.BackgroundColor),
                BorderBrush = element.IsSelected ? BrushElementSelected : BrushElementBorder,
                BorderThickness = new Thickness(element.IsSelected ? 2 : 1),
                Tag = element,
                Cursor = Cursors.SizeAll
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
                    break;

                case TemplateElementType.TextBox:
                    content = new TextBox
                    {
                        Text = element.DefaultText ?? "",
                        FontFamily = new FontFamily(element.FontFamily ?? "Segoe UI"),
                        FontSize = element.FontSize > 0 ? element.FontSize : 14,
                        Foreground = ParseBrush(element.ForegroundColor),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(4, 2, 4, 2),
                        IsHitTestVisible = false
                    };
                    break;

                case TemplateElementType.MultilineTextBox:
                    content = new TextBox
                    {
                        Text = element.DefaultText ?? "",
                        FontFamily = new FontFamily(element.FontFamily ?? "Segoe UI"),
                        FontSize = element.FontSize > 0 ? element.FontSize : 14,
                        Foreground = ParseBrush(element.ForegroundColor),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Padding = new Thickness(4, 2, 4, 2),
                        IsHitTestVisible = false
                    };
                    break;

                default:
                    content = new TextBlock { Text = "?" };
                    break;
            }

            border.Child = content;

            // Mouse events for selection and dragging
            border.MouseLeftButtonDown += Element_MouseDown;
            border.MouseMove += Element_MouseMove;
            border.MouseLeftButtonUp += Element_MouseUp;

            // Add resize handles if selected
            if (element.IsSelected)
            {
                AddResizeHandles(border);
            }

            return border;
        }

        private void AddResizeHandles(Border border)
        {
            var element = border.Tag as TemplateElementModel;
            if (element == null) return;

            var handles = new string[] { "NW", "N", "NE", "E", "SE", "S", "SW", "W" };
            var positions = new (double x, double y)[]
            {
                (0, 0), (0.5, 0), (1, 0), (1, 0.5),
                (1, 1), (0.5, 1), (0, 1), (0, 0.5)
            };

            for (int i = 0; i < handles.Length; i++)
            {
                var handle = new Rectangle
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brushes.White,
                    Stroke = Brushes.DarkCyan,
                    StrokeThickness = 1,
                    Tag = handles[i],
                    Cursor = GetResizeCursor(handles[i])
                };

                double hx = element.X + (element.Width * positions[i].x) - 4;
                double hy = element.Y + (element.Height * positions[i].y) - 4;

                Canvas.SetLeft(handle, hx);
                Canvas.SetTop(handle, hy);

                handle.MouseLeftButtonDown += ResizeHandle_MouseDown;
                handle.MouseMove += ResizeHandle_MouseMove;
                handle.MouseLeftButtonUp += ResizeHandle_MouseUp;

                TemplateCanvas.Children.Add(handle);
            }
        }

        private Cursor GetResizeCursor(string handle)
        {
            return handle switch
            {
                "NW" or "SE" => Cursors.SizeNWSE,
                "NE" or "SW" => Cursors.SizeNESW,
                "N" or "S" => Cursors.SizeNS,
                "E" or "W" => Cursors.SizeWE,
                _ => Cursors.Arrow
            };
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

        #endregion

        #region Element Selection & Manipulation

        private void SelectElement(TemplateElementModel element)
        {
            // Deselect all
            foreach (var el in _currentTemplate.Elements)
                el.IsSelected = false;

            if (element != null)
            {
                element.IsSelected = true;
                _selectedElement = element;
                PropertiesPanel.IsEnabled = true;
            }
            else
            {
                _selectedElement = null;
                PropertiesPanel.IsEnabled = false;
            }

            UpdateCanvasFromTemplate();
            UpdatePropertiesUI();
        }

        private void UpdatePropertiesUI()
        {
            _isUpdatingUI = true;

            if (_selectedElement != null)
            {
                TxtElementName.Text = _selectedElement.Name;
                TxtElementX.Text = _selectedElement.X.ToString("F0");
                TxtElementY.Text = _selectedElement.Y.ToString("F0");
                TxtElementWidth.Text = _selectedElement.Width.ToString("F0");
                TxtElementHeight.Text = _selectedElement.Height.ToString("F0");
                TxtElementText.Text = _selectedElement.DefaultText;
                TxtElementFontSize.Text = _selectedElement.FontSize.ToString("F0");
                TxtElementForeground.Text = _selectedElement.ForegroundColor;
                TxtElementBackground.Text = _selectedElement.BackgroundColor;

                ForegroundPreview.Fill = ParseBrush(_selectedElement.ForegroundColor);
                BackgroundPreview.Fill = ParseBrush(_selectedElement.BackgroundColor);

                // Use cached font list instead of re-enumerating
                if (_cachedFontFamilies != null)
                {
                    var font = _cachedFontFamilies.FirstOrDefault(f => f.Source == _selectedElement.FontFamily);
                    CmbElementFont.SelectedItem = font;
                }
            }
            else
            {
                TxtElementName.Text = "";
                TxtElementX.Text = "";
                TxtElementY.Text = "";
                TxtElementWidth.Text = "";
                TxtElementHeight.Text = "";
                TxtElementText.Text = "";
                TxtElementFontSize.Text = "";
                TxtElementForeground.Text = "";
                TxtElementBackground.Text = "";
            }

            _isUpdatingUI = false;
        }

        #endregion

        #region Mouse Events - Element Dragging

        private void Element_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is TemplateElementModel element)
            {
                SelectElement(element);

                _isDraggingElement = true;
                _dragStartPoint = e.GetPosition(TemplateCanvas);
                _draggedVisual = border;
                border.CaptureMouse();
                e.Handled = true;
            }
        }

        private void Element_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingElement && _selectedElement != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(TemplateCanvas);
                var dx = pos.X - _dragStartPoint.X;
                var dy = pos.Y - _dragStartPoint.Y;

                _selectedElement.X = Math.Max(0, Math.Min(_currentTemplate.CanvasWidth - _selectedElement.Width,
                    _selectedElement.X + dx));
                _selectedElement.Y = Math.Max(0, Math.Min(_currentTemplate.CanvasHeight - _selectedElement.Height,
                    _selectedElement.Y + dy));

                _dragStartPoint = pos;
                _currentTemplate.IsDirty = true;

                UpdateCanvasFromTemplate();
                UpdatePropertiesUI();
            }
        }

        private void Element_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingElement = false;
            if (sender is Border border)
                border.ReleaseMouseCapture();
        }

        #endregion

        #region Mouse Events - Resize Handles

        private void ResizeHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle handle && _selectedElement != null)
            {
                _isResizingElement = true;
                _resizeHandle = handle.Tag as string;
                _resizeStartPoint = e.GetPosition(TemplateCanvas);
                _resizeStartWidth = _selectedElement.Width;
                _resizeStartHeight = _selectedElement.Height;
                _resizeStartX = _selectedElement.X;
                _resizeStartY = _selectedElement.Y;
                handle.CaptureMouse();
                e.Handled = true;
            }
        }

        private void ResizeHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isResizingElement && _selectedElement != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(TemplateCanvas);
                var dx = pos.X - _resizeStartPoint.X;
                var dy = pos.Y - _resizeStartPoint.Y;

                double newX = _resizeStartX;
                double newY = _resizeStartY;
                double newW = _resizeStartWidth;
                double newH = _resizeStartHeight;

                switch (_resizeHandle)
                {
                    case "E":
                        newW = Math.Max(10, _resizeStartWidth + dx);
                        break;
                    case "W":
                        newW = Math.Max(10, _resizeStartWidth - dx);
                        newX = _resizeStartX + (_resizeStartWidth - newW);
                        break;
                    case "S":
                        newH = Math.Max(10, _resizeStartHeight + dy);
                        break;
                    case "N":
                        newH = Math.Max(10, _resizeStartHeight - dy);
                        newY = _resizeStartY + (_resizeStartHeight - newH);
                        break;
                    case "SE":
                        newW = Math.Max(10, _resizeStartWidth + dx);
                        newH = Math.Max(10, _resizeStartHeight + dy);
                        break;
                    case "SW":
                        newW = Math.Max(10, _resizeStartWidth - dx);
                        newH = Math.Max(10, _resizeStartHeight + dy);
                        newX = _resizeStartX + (_resizeStartWidth - newW);
                        break;
                    case "NE":
                        newW = Math.Max(10, _resizeStartWidth + dx);
                        newH = Math.Max(10, _resizeStartHeight - dy);
                        newY = _resizeStartY + (_resizeStartHeight - newH);
                        break;
                    case "NW":
                        newW = Math.Max(10, _resizeStartWidth - dx);
                        newH = Math.Max(10, _resizeStartHeight - dy);
                        newX = _resizeStartX + (_resizeStartWidth - newW);
                        newY = _resizeStartY + (_resizeStartHeight - newH);
                        break;
                }

                _selectedElement.X = newX;
                _selectedElement.Y = newY;
                _selectedElement.Width = newW;
                _selectedElement.Height = newH;
                _currentTemplate.IsDirty = true;

                UpdateCanvasFromTemplate();
                UpdatePropertiesUI();
            }
        }

        private void ResizeHandle_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isResizingElement = false;
            if (sender is Rectangle handle)
                handle.ReleaseMouseCapture();
        }

        #endregion

        #region Mouse Events - Canvas

        private void TemplateCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Source == TemplateCanvas)
            {
                SelectElement(null);
            }
        }

        private void TemplateCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Future: multi-select box
        }

        private void TemplateCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingElement = false;
        }

        #endregion

        #region Drag & Drop - Toolbox to Canvas

        private void ToolboxItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                _draggedToolboxItem = border.Tag as string;
                DragDrop.DoDragDrop(border, _draggedToolboxItem, DragDropEffects.Copy);
            }
        }

        private void TemplateCanvas_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
        }

        private void TemplateCanvas_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
        }

        private void TemplateCanvas_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
            {
                var data = e.Data.GetData(DataFormats.StringFormat) as string;
                var pos = e.GetPosition(TemplateCanvas);

                // Handle transfunctioner drop
                if (data?.StartsWith("Transfunctioner:") == true && data.Length > 16)
                {
                    var tfId = data.Substring(16);
                    var tf = _currentTemplate.ExtractedTransfunctioners.FirstOrDefault(t => t.Id == tfId);
                    if (tf != null)
                    {
                        var element = new TemplateElementModel
                        {
                            X = pos.X - 75,
                            Y = pos.Y - 15,
                            Width = 150,
                            Height = 30,
                            ElementType = TemplateElementType.TextBox,
                            Name = tf.DisplayName,
                            DefaultText = "",
                            BackgroundColor = "#FF2D2D30",
                            LinkedTransfunctionerId = tf.Id
                        };

                        _currentTemplate.Elements.Add(element);
                        _currentTemplate.IsDirty = true;
                        SelectElement(element);
                        return;
                    }
                }

                // Handle regular toolbox items
                var element2 = new TemplateElementModel
                {
                    X = pos.X - 75,
                    Y = pos.Y - 15,
                    Width = 150,
                    Height = 30
                };

                switch (data)
                {
                    case "Label":
                        element2.ElementType = TemplateElementType.Label;
                        element2.Name = "Label";
                        element2.DefaultText = "Label";
                        break;

                    case "TextBox":
                        element2.ElementType = TemplateElementType.TextBox;
                        element2.Name = "TextBox";
                        element2.DefaultText = "";
                        element2.BackgroundColor = "#FF2D2D30";
                        break;

                    case "MultilineTextBox":
                        element2.ElementType = TemplateElementType.MultilineTextBox;
                        element2.Name = "TextArea";
                        element2.DefaultText = "";
                        element2.Height = 80;
                        element2.BackgroundColor = "#FF2D2D30";
                        break;

                    default:
                        return; // Unknown item type
                }

                _currentTemplate.Elements.Add(element2);
                _currentTemplate.IsDirty = true;
                SelectElement(element2);
            }
        }

        #endregion

        #region Takes Management

        private void UpdateTakesUI()
        {
            TakesList.ItemsSource = _currentTemplate.Takes;

            if (_currentTemplate.Takes.Any())
            {
                var selected = _currentTemplate.Takes.FirstOrDefault(t => t.IsSelected)
                    ?? _currentTemplate.Takes.First();
                TakesList.SelectedItem = selected;
            }

            UpdateTakeTimeline();
        }

        private void AddTake_Click(object sender, RoutedEventArgs e)
        {
            var take = new TemplateTakeModel
            {
                Name = $"Take {_currentTemplate.Takes.Count + 1}"
            };
            _currentTemplate.Takes.Add(take);
            _currentTemplate.IsDirty = true;
            UpdateTakesUI();
            TakesList.SelectedItem = take;
        }

        private void RemoveTake_Click(object sender, RoutedEventArgs e)
        {
            if (TakesList.SelectedItem is TemplateTakeModel take)
            {
                _currentTemplate.Takes.Remove(take);
                _currentTemplate.IsDirty = true;
                UpdateTakesUI();
            }
        }

        private void TakesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (var take in _currentTemplate.Takes)
                take.IsSelected = false;

            if (TakesList.SelectedItem is TemplateTakeModel selected)
            {
                selected.IsSelected = true;
                TxtSelectedTake.Text = selected.Name;

                // Update Take name editor
                _isUpdatingUI = true;
                TxtTakeName.Text = selected.Name;
                TxtTakeName.IsEnabled = true;
                _isUpdatingUI = false;
            }
            else
            {
                TxtSelectedTake.Text = "(no take selected)";
                TxtTakeName.Text = "";
                TxtTakeName.IsEnabled = false;
            }

            // Deselect action when switching takes
            SelectAction(null);
            UpdateTakeTimeline();
        }

        private void TakeName_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI) return;

            if (TakesList.SelectedItem is TemplateTakeModel selected)
            {
                selected.Name = TxtTakeName.Text;
                TxtSelectedTake.Text = selected.Name;
                _currentTemplate.IsDirty = true;

                // Refresh the list to show updated name
                var index = TakesList.SelectedIndex;
                TakesList.ItemsSource = null;
                TakesList.ItemsSource = _currentTemplate.Takes;
                TakesList.SelectedIndex = index;
            }
        }

        private void UpdateTakeTimeline()
        {
            TakeTimelineCanvas.Children.Clear();

            var selectedTake = TakesList.SelectedItem as TemplateTakeModel;
            if (selectedTake == null) return;

            // Draw grid lines
            for (int i = 0; i <= 800; i += 50)
            {
                var line = new Line
                {
                    X1 = i, Y1 = 0, X2 = i, Y2 = 120,
                    Stroke = BrushGridLine,
                    StrokeThickness = 1
                };
                TakeTimelineCanvas.Children.Add(line);

                var text = new TextBlock
                {
                    Text = (i / 50).ToString(),
                    FontSize = 9,
                    Foreground = BrushGridText
                };
                Canvas.SetLeft(text, i + 2);
                Canvas.SetTop(text, 2);
                TakeTimelineCanvas.Children.Add(text);
            }

            // Draw actions
            int row = 0;
            foreach (var action in selectedTake.Actions)
            {
                var actionVisual = CreateActionVisual(action);
                Canvas.SetLeft(actionVisual, action.StartFrame);
                Canvas.SetTop(actionVisual, 20 + row * 25);
                TakeTimelineCanvas.Children.Add(actionVisual);
                row++;
            }
        }

        private Border CreateActionVisual(TakeActionModel action)
        {
            var brush = action.ActionType switch
            {
                TakeActionType.Play => BrushActionPlay,
                TakeActionType.Stop => BrushActionStop,
                TakeActionType.Cue => BrushActionCue,
                TakeActionType.Pause => Brushes.Orange,
                TakeActionType.Continue => Brushes.Teal,
                _ => Brushes.Gray
            };

            var icon = action.ActionType switch
            {
                TakeActionType.Play => "▶",
                TakeActionType.Stop => "■",
                TakeActionType.Cue => "◆",
                TakeActionType.Pause => "❚❚",
                TakeActionType.Continue => "▶▶",
                _ => "?"
            };

            var displayText = $"{icon} {action.ActionType}";
            if (action.TargetAnimationNames.Count > 0)
            {
                displayText += $": {action.TargetAnimationsDisplay}";
            }

            var border = new Border
            {
                Width = Math.Max(80, action.Duration),
                Height = 22,
                Background = brush,
                BorderBrush = action.IsSelected ? Brushes.Yellow : Brushes.Transparent,
                BorderThickness = new Thickness(action.IsSelected ? 2 : 0),
                CornerRadius = new CornerRadius(3),
                Tag = action,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = displayText,
                    Foreground = Brushes.White,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };

            border.MouseLeftButtonDown += TimelineAction_MouseDown;
            border.MouseMove += TimelineAction_MouseMove;
            border.MouseLeftButtonUp += TimelineAction_MouseUp;

            return border;
        }

        private void TimelineAction_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingAction && _selectedAction != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(TakeTimelineCanvas);
                var dx = pos.X - _actionDragStartPoint.X;

                // Calculate new start frame (minimum 0)
                int newFrame = Math.Max(0, _actionDragStartFrame + (int)dx);

                _selectedAction.StartFrame = newFrame;
                _currentTemplate.IsDirty = true;
                UpdateTakeTimeline();
            }
        }

        private void TimelineAction_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingAction = false;
            if (sender is Border border)
                border.ReleaseMouseCapture();
        }

        private void TimelineAction_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is TakeActionModel action)
            {
                SelectAction(action);

                // Start dragging
                _isDraggingAction = true;
                _actionDragStartPoint = e.GetPosition(TakeTimelineCanvas);
                _actionDragStartFrame = action.StartFrame;
                border.CaptureMouse();

                e.Handled = true;
            }
        }

        private void SelectAction(TakeActionModel action)
        {
            // Deselect all actions in current take
            var selectedTake = TakesList.SelectedItem as TemplateTakeModel;
            if (selectedTake != null)
            {
                foreach (var a in selectedTake.Actions)
                    a.IsSelected = false;
            }

            _selectedAction = action;
            if (action != null)
            {
                action.IsSelected = true;
            }

            UpdateTakeTimeline();
            UpdateActionAnimationsUI();
        }

        private void UpdateActionAnimationsUI()
        {
            if (ActionAnimationsPanel == null) return;

            // Unsubscribe handlers before clearing to prevent memory leaks
            foreach (var child in ActionAnimationsList.Children)
            {
                if (child is CheckBox cb)
                {
                    cb.Checked -= ActionAnimation_CheckChanged;
                    cb.Unchecked -= ActionAnimation_CheckChanged;
                }
            }
            ActionAnimationsList.Children.Clear();

            if (_selectedAction == null)
            {
                ActionAnimationsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // Only show for Play/Stop/Cue actions that target animations
            if (_selectedAction.ActionType != TakeActionType.Play &&
                _selectedAction.ActionType != TakeActionType.Stop &&
                _selectedAction.ActionType != TakeActionType.Cue)
            {
                ActionAnimationsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            ActionAnimationsPanel.Visibility = Visibility.Visible;
            TxtActionType.Text = $"{_selectedAction.ActionType} Action";

            // Show available animations from linked scene
            foreach (var animName in _currentTemplate.ExtractedAnimations)
            {
                var cb = new CheckBox
                {
                    Content = animName,
                    Foreground = Brushes.White,
                    IsChecked = _selectedAction.TargetAnimationNames.Contains(animName),
                    Tag = animName,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                cb.Checked += ActionAnimation_CheckChanged;
                cb.Unchecked += ActionAnimation_CheckChanged;
                ActionAnimationsList.Children.Add(cb);
            }

            if (_currentTemplate.ExtractedAnimations.Count == 0)
            {
                ActionAnimationsList.Children.Add(new TextBlock
                {
                    Text = "No scene loaded",
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                    FontStyle = FontStyles.Italic
                });
            }
        }

        private void ActionAnimation_CheckChanged(object sender, RoutedEventArgs e)
        {
            if (_selectedAction == null || sender is not CheckBox cb) return;

            var animName = cb.Tag as string;
            if (string.IsNullOrEmpty(animName)) return;

            if (cb.IsChecked == true)
            {
                if (!_selectedAction.TargetAnimationNames.Contains(animName))
                    _selectedAction.TargetAnimationNames.Add(animName);
            }
            else
            {
                _selectedAction.TargetAnimationNames.Remove(animName);
            }

            _selectedAction.NotifyTargetAnimationsChanged();
            _currentTemplate.IsDirty = true;
            UpdateTakeTimeline();
        }

        private void DeleteAction_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAction == null) return;

            var selectedTake = TakesList.SelectedItem as TemplateTakeModel;
            if (selectedTake != null)
            {
                selectedTake.Actions.Remove(_selectedAction);
                _currentTemplate.IsDirty = true;
                SelectAction(null);
            }
        }

        #endregion

        #region Drag & Drop - Actions to Timeline

        private void ActionItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                var actionType = border.Tag as string;
                DragDrop.DoDragDrop(border, $"Action:{actionType}", DragDropEffects.Copy);
            }
        }

        private void TakeTimeline_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
            {
                var data = e.Data.GetData(DataFormats.StringFormat) as string;
                if (data?.StartsWith("Action:") == true)
                    e.Effects = DragDropEffects.Copy;
                else
                    e.Effects = DragDropEffects.None;
            }
        }

        private void TakeTimeline_DragOver(object sender, DragEventArgs e)
        {
            TakeTimeline_DragEnter(sender, e);
        }

        private void TakeTimeline_Drop(object sender, DragEventArgs e)
        {
            if (TakesList.SelectedItem is not TemplateTakeModel selectedTake) return;

            if (e.Data.GetDataPresent(DataFormats.StringFormat))
            {
                var data = e.Data.GetData(DataFormats.StringFormat) as string;
                if (data?.StartsWith("Action:") == true && data.Length > 7)
                {
                    var actionTypeStr = data.Substring(7);
                    var pos = e.GetPosition(TakeTimelineCanvas);

                    var action = new TakeActionModel
                    {
                        ActionType = actionTypeStr switch
                        {
                            "Play" => TakeActionType.Play,
                            "Stop" => TakeActionType.Stop,
                            "Cue" => TakeActionType.Cue,
                            _ => TakeActionType.Play
                        },
                        StartFrame = (int)pos.X,
                        Duration = 80
                    };

                    selectedTake.Actions.Add(action);
                    _currentTemplate.IsDirty = true;
                    UpdateTakeTimeline();
                }
            }
        }

        private void TakeTimeline_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Deselect action when clicking on empty timeline space
            if (e.Source == TakeTimelineCanvas)
            {
                SelectAction(null);
            }
        }

        #endregion

        #region Property Changes

        private void CanvasSize_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _currentTemplate == null) return;

            if (double.TryParse(TxtCanvasWidth.Text, out double w) && w > 0)
                _currentTemplate.CanvasWidth = w;

            if (double.TryParse(TxtCanvasHeight.Text, out double h) && h > 0)
                _currentTemplate.CanvasHeight = h;

            _currentTemplate.IsDirty = true;
            UpdateCanvasFromTemplate();
        }

        private void ElementName_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedElement == null) return;
            _selectedElement.Name = TxtElementName.Text;
            _currentTemplate.IsDirty = true;
        }

        private void ElementPosition_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedElement == null) return;

            if (sender is TextBox txt)
            {
                if (double.TryParse(txt.Text, out double val))
                {
                    if (txt.Tag as string == "X")
                        _selectedElement.X = val;
                    else if (txt.Tag as string == "Y")
                        _selectedElement.Y = val;

                    _currentTemplate.IsDirty = true;
                    UpdateCanvasFromTemplate();
                }
            }
        }

        private void ElementSize_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedElement == null) return;

            if (sender is TextBox txt)
            {
                if (double.TryParse(txt.Text, out double val) && val > 0)
                {
                    if (txt.Tag as string == "Width")
                        _selectedElement.Width = val;
                    else if (txt.Tag as string == "Height")
                        _selectedElement.Height = val;

                    _currentTemplate.IsDirty = true;
                    UpdateCanvasFromTemplate();
                }
            }
        }

        private void ElementText_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedElement == null) return;
            _selectedElement.DefaultText = TxtElementText.Text;
            _currentTemplate.IsDirty = true;
            UpdateCanvasFromTemplate();
        }

        private void ElementFont_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedElement == null) return;
            if (CmbElementFont.SelectedItem is FontFamily font)
            {
                _selectedElement.FontFamily = font.Source;
                _currentTemplate.IsDirty = true;
                UpdateCanvasFromTemplate();
            }
        }

        private void ElementFontSize_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedElement == null) return;
            if (double.TryParse(TxtElementFontSize.Text, out double size) && size > 0)
            {
                _selectedElement.FontSize = size;
                _currentTemplate.IsDirty = true;
                UpdateCanvasFromTemplate();
            }
        }

        private void ElementForeground_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedElement == null) return;
            _selectedElement.ForegroundColor = TxtElementForeground.Text;
            _currentTemplate.IsDirty = true;
            ForegroundPreview.Fill = ParseBrush(_selectedElement.ForegroundColor);
            UpdateCanvasFromTemplate();
        }

        private void ElementBackground_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _selectedElement == null) return;
            _selectedElement.BackgroundColor = TxtElementBackground.Text;
            _currentTemplate.IsDirty = true;
            BackgroundPreview.Fill = ParseBrush(_selectedElement.BackgroundColor);
            UpdateCanvasFromTemplate();
        }

        #endregion

        #region Menu Handlers

        private void Menu_NewTemplate(object sender, RoutedEventArgs e)
        {
            NewTemplate_Click(sender, e);
        }

        private async void Menu_OpenTemplate(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ConfirmUnsavedChanges()) return;

                var dialog = new OpenFileDialog
                {
                    Filter = "Daro Template (*.dtemplate)|*.dtemplate|All Files (*.*)|*.*",
                    InitialDirectory = GetTemplatesDirectory()
                };

                if (dialog.ShowDialog() == true)
                {
                    await LoadTemplateAsync(dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error opening template", ex);
                MessageBox.Show($"Error opening template:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Menu_SaveTemplate(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_currentFilePath))
                    await SaveTemplateAsAsync();
                else
                    await SaveTemplateAsync(_currentFilePath);
            }
            catch (Exception ex)
            {
                Logger.Error("Error saving template", ex);
                MessageBox.Show($"Error saving template:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Menu_SaveTemplateAs(object sender, RoutedEventArgs e)
        {
            try
            {
                await SaveTemplateAsAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("Error saving template as", ex);
                MessageBox.Show($"Error saving template:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SaveTemplateAsAsync()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Daro Template (*.dtemplate)|*.dtemplate",
                FileName = _currentTemplate.Name,
                InitialDirectory = GetTemplatesDirectory()
            };

            if (dialog.ShowDialog() == true)
            {
                _currentTemplate.Name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                await SaveTemplateAsync(dialog.FileName);
                LoadTemplatesList();
            }
        }

        private void Menu_Close(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Menu_DeleteElement(object sender, RoutedEventArgs e)
        {
            if (_selectedElement != null)
            {
                _currentTemplate.Elements.Remove(_selectedElement);
                _currentTemplate.IsDirty = true;
                SelectElement(null);
            }
        }

        private void Menu_DuplicateElement(object sender, RoutedEventArgs e)
        {
            if (_selectedElement != null)
            {
                var clone = _selectedElement.Clone();
                _currentTemplate.Elements.Add(clone);
                _currentTemplate.IsDirty = true;
                SelectElement(clone);
            }
        }

        private void Menu_ResetCanvasSize(object sender, RoutedEventArgs e)
        {
            _currentTemplate.CanvasWidth = 400;
            _currentTemplate.CanvasHeight = 200;
            _currentTemplate.IsDirty = true;
            UpdateCanvasSizeUI();
            UpdateCanvasFromTemplate();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmUnsavedChanges())
            {
                e.Cancel = true;
                return;
            }

            // Cleanup canvas event handlers to prevent memory leaks
            CleanupCanvasEventHandlers();

            // Cleanup animation checkbox handlers
            foreach (var child in ActionAnimationsList.Children)
            {
                if (child is CheckBox cb)
                {
                    cb.Checked -= ActionAnimation_CheckChanged;
                    cb.Unchecked -= ActionAnimation_CheckChanged;
                }
            }
        }

        #endregion
    }

    // Simple input dialog for folder creation
    public class InputDialog : Window
    {
        private TextBox _textBox;
        public string ResponseText => _textBox.Text;

        public InputDialog(string title, string question, string defaultValue = "")
        {
            Title = title;
            Width = 350;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 48));

            var grid = new Grid { Margin = new Thickness(15) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = question,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            _textBox = new TextBox
            {
                Text = defaultValue,
                Margin = new Thickness(0, 0, 0, 15)
            };
            Grid.SetRow(_textBox, 1);
            grid.Children.Add(_textBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(buttonPanel, 2);

            var okButton = new Button
            {
                Content = "OK",
                Width = 75,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };
            okButton.Click += (s, e) => { DialogResult = true; };
            buttonPanel.Children.Add(okButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 75,
                IsCancel = true
            };
            buttonPanel.Children.Add(cancelButton);

            grid.Children.Add(buttonPanel);
            Content = grid;
        }
    }
}
