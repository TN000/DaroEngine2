// Designer/Models/ProjectModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DaroDesigner.Models
{
    public class ProjectModel : ViewModelBase
    {
        private int _currentFrame;
        private bool _isPlaying;
        private double _timelineZoom = 1.0;
        private AnimationModel _selectedAnimation;
        private LayerModel _selectedLayer;
        private bool _isDirty;
        private string _filePath;

        public ObservableCollection<AnimationModel> Animations { get; } = new ObservableCollection<AnimationModel>();

        public int CurrentFrame
        {
            get => _currentFrame;
            set => SetProperty(ref _currentFrame, value);
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        public double TimelineZoom
        {
            get => _timelineZoom;
            set => SetProperty(ref _timelineZoom, Math.Clamp(value, 0.1, 10.0));
        }

        public AnimationModel SelectedAnimation
        {
            get => _selectedAnimation;
            set => SetProperty(ref _selectedAnimation, value);
        }

        public LayerModel SelectedLayer
        {
            get => _selectedLayer;
            set => SetProperty(ref _selectedLayer, value);
        }

        public bool IsDirty
        {
            get => _isDirty;
            set => SetProperty(ref _isDirty, value);
        }

        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        public void MarkDirty()
        {
            IsDirty = true;
        }

        #region Serialization

        public ProjectData ToSerializable()
        {
            return new ProjectData
            {
                Version = 1,
                TimelineZoom = TimelineZoom,
                Animations = Animations.Select(a => a.ToSerializable()).ToList()
            };
        }

        public static ProjectModel FromSerializable(ProjectData data)
        {
            var project = new ProjectModel
            {
                TimelineZoom = data.TimelineZoom > 0 ? data.TimelineZoom : 1.0
            };

            if (data.Animations != null)
            {
                foreach (var animData in data.Animations)
                {
                    project.Animations.Add(AnimationModel.FromSerializable(animData));
                }
            }

            return project;
        }

        #endregion
    }

    #region Serialization Classes

    public class ProjectData
    {
        public int Version { get; set; }
        public double TimelineZoom { get; set; }
        public List<AnimationData> Animations { get; set; } = new List<AnimationData>();
    }

    public class AnimationData
    {
        public string Name { get; set; }
        public int LengthFrames { get; set; }
        public List<LayerData> Layers { get; set; } = new List<LayerData>();
    }

    public class LayerData
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int LayerType { get; set; }
        public bool IsVisible { get; set; } = true;  // Default to visible
        public int ParentId { get; set; } = -1;

        // Transform
        public float PosX { get; set; } = AppConstants.FrameWidth / 2f;
        public float PosY { get; set; } = AppConstants.FrameHeight / 2f;
        public float SizeX { get; set; } = 400;     // Default size
        public float SizeY { get; set; } = 300;
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float AnchorX { get; set; } = 0.5f;  // Center anchor
        public float AnchorY { get; set; } = 0.5f;
        public bool LockAspectRatio { get; set; }

        // Appearance
        public float Opacity { get; set; } = 1.0f;  // Default to fully opaque
        public float ColorR { get; set; } = 1.0f;   // Default to white
        public float ColorG { get; set; } = 1.0f;
        public float ColorB { get; set; } = 1.0f;
        public float ColorA { get; set; } = 1.0f;

        // Texture
        public int TextureSource { get; set; }
        public float TexX { get; set; }
        public float TexY { get; set; }
        public float TexW { get; set; }
        public float TexH { get; set; }
        public float TexRot { get; set; }
        public string TexturePath { get; set; }
        public string SpoutSenderName { get; set; }
        public bool VideoAlpha { get; set; }

        // Text
        public string TextContent { get; set; }
        public string FontFamily { get; set; }
        public float FontSize { get; set; }
        public bool FontBold { get; set; }
        public bool FontItalic { get; set; }
        public int TextAlignment { get; set; }
        public float LineHeight { get; set; }
        public float LetterSpacing { get; set; }
        public int TextAntialiasMode { get; set; }

        // Mask
        public int MaskMode { get; set; }
        public List<int> MaskedLayerIds { get; set; } = new List<int>();

        // Tracks
        public List<TrackData> Tracks { get; set; } = new List<TrackData>();

        // String tracks (for text content with jump interpolation)
        public List<StringTrackData> StringTracks { get; set; } = new List<StringTrackData>();

        // Transfunctioner bindings
        public List<TransfunctionerData> Transfunctioners { get; set; } = new List<TransfunctionerData>();
    }

    public class TrackData
    {
        public string PropertyId { get; set; }
        public List<KeyframeData> Keyframes { get; set; } = new List<KeyframeData>();
    }

    public class KeyframeData
    {
        public int Frame { get; set; }
        public float Value { get; set; }
        public float EaseIn { get; set; }
        public float EaseOut { get; set; }
    }

    /// <summary>
    /// Serialization data for string property tracks (e.g., text content).
    /// </summary>
    public class StringTrackData
    {
        public string PropertyId { get; set; }
        public List<StringKeyframeData> Keyframes { get; set; } = new List<StringKeyframeData>();
    }

    /// <summary>
    /// Serialization data for string keyframes.
    /// </summary>
    public class StringKeyframeData
    {
        public int Frame { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// Serialization data for transfunctioner bindings.
    /// </summary>
    public class TransfunctionerData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string TemplateElementId { get; set; }
        public string TemplateElementName { get; set; }
        public int TargetLayerId { get; set; }
        public string TargetLayerName { get; set; }
        public string TargetPropertyId { get; set; }
        public int BindingType { get; set; }
    }

    #endregion
}
