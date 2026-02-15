// Designer/Models/TemplateModel.cs
using System;
using System.Collections.ObjectModel;

namespace DaroDesigner.Models
{
    public class TemplateModel : ViewModelBase
    {
        private string _id;
        private string _name;
        private string _folderPath;
        private double _canvasWidth;
        private double _canvasHeight;
        private string _backgroundColor;
        private ObservableCollection<TemplateElementModel> _elements;
        private ObservableCollection<TemplateTakeModel> _takes;
        private string _linkedScenePath;
        private bool _isDirty;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                    IsDirty = true;
            }
        }

        public string FolderPath
        {
            get => _folderPath;
            set => SetProperty(ref _folderPath, value);
        }

        public double CanvasWidth
        {
            get => _canvasWidth;
            set
            {
                if (SetProperty(ref _canvasWidth, value))
                    IsDirty = true;
            }
        }

        public double CanvasHeight
        {
            get => _canvasHeight;
            set
            {
                if (SetProperty(ref _canvasHeight, value))
                    IsDirty = true;
            }
        }

        public string BackgroundColor
        {
            get => _backgroundColor;
            set
            {
                if (SetProperty(ref _backgroundColor, value))
                    IsDirty = true;
            }
        }

        public ObservableCollection<TemplateElementModel> Elements
        {
            get => _elements;
            set => SetProperty(ref _elements, value);
        }

        public ObservableCollection<TemplateTakeModel> Takes
        {
            get => _takes;
            set => SetProperty(ref _takes, value);
        }

        public string LinkedScenePath
        {
            get => _linkedScenePath;
            set => SetProperty(ref _linkedScenePath, value);
        }

        /// <summary>
        /// Transfunctioners extracted from the linked scene (runtime only, not serialized).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public ObservableCollection<TransfunctionerBindingModel> ExtractedTransfunctioners { get; }
            = new ObservableCollection<TransfunctionerBindingModel>();

        /// <summary>
        /// Animation names extracted from the linked scene (runtime only, not serialized).
        /// Used for Take action configuration.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public ObservableCollection<string> ExtractedAnimations { get; }
            = new ObservableCollection<string>();

        public bool IsDirty
        {
            get => _isDirty;
            set => SetProperty(ref _isDirty, value);
        }

        public TemplateModel()
        {
            Id = Guid.NewGuid().ToString();
            Name = "New Template";
            CanvasWidth = 400;
            CanvasHeight = 200;
            BackgroundColor = "#FF1E1E1E";
            Elements = new ObservableCollection<TemplateElementModel>();
            Takes = new ObservableCollection<TemplateTakeModel>();
        }
    }

    public class TemplateFolderModel : ViewModelBase
    {
        private string _name;
        private string _path;
        private ObservableCollection<TemplateFolderModel> _subFolders;
        private ObservableCollection<TemplateItemModel> _templates;
        private bool _isExpanded;
        private bool _isSelected;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }

        public ObservableCollection<TemplateFolderModel> SubFolders
        {
            get => _subFolders;
            set => SetProperty(ref _subFolders, value);
        }

        public ObservableCollection<TemplateItemModel> Templates
        {
            get => _templates;
            set => SetProperty(ref _templates, value);
        }

        // Combined children for TreeView - folders first, then templates
        public System.Collections.IEnumerable Children
        {
            get
            {
                if (SubFolders != null)
                    foreach (var folder in SubFolders)
                        yield return folder;
                if (Templates != null)
                    foreach (var template in Templates)
                        yield return template;
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public TemplateFolderModel()
        {
            SubFolders = new ObservableCollection<TemplateFolderModel>();
            Templates = new ObservableCollection<TemplateItemModel>();
            IsExpanded = true;
        }

        public void NotifyChildrenChanged()
        {
            OnPropertyChanged(nameof(Children));
        }
    }

    public class TemplateItemModel : ViewModelBase
    {
        private string _id;
        private string _name;
        private string _filePath;
        private bool _isSelected;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public TemplateItemModel()
        {
            Id = Guid.NewGuid().ToString();
        }
    }
}
