// Designer/Models/LayerModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using DaroDesigner.Engine;

namespace DaroDesigner.Models
{
    public enum LayerType
    {
        Rectangle = 0,
        Circle = 1,
        Text = 2,
        Image = 3,
        Video = 4,
        Mask = 5,
        Group = 6
    }

    public enum TextureSourceType
    {
        SolidColor = 0,
        SpoutInput = 1,
        ImageFile = 2,
        VideoFile = 3
    }

    public enum TextAlignment
    {
        Left = 0,
        Center = 1,
        Right = 2
    }

    public enum MaskMode
    {
        Inner = 0,  // Show content inside mask
        Outer = 1   // Show content outside mask
    }

    public enum TextAntialiasMode
    {
        Smooth = 0,      // Grayscale antialiasing (best for transparent backgrounds)
        Sharp = 1        // No antialiasing (pixel-perfect, aliased edges)
    }

    public class LayerModel : ViewModelBase
    {
        private static int _nextId = 1;

        /// <summary>
        /// Ensures _nextId is above the given id to prevent collisions after project load.
        /// </summary>
        public static void SyncNextId(int loadedId)
        {
            // Thread-safe: keep incrementing if loadedId is higher
            int current;
            do
            {
                current = Volatile.Read(ref _nextId);
                if (current > loadedId) return;
            }
            while (Interlocked.CompareExchange(ref _nextId, loadedId + 1, current) != current);
        }

        private int _id;
        private string _name;
        private LayerType _layerType = LayerType.Rectangle;
        private bool _isVisible = true;
        private bool _isSelected;
        private bool _isExpanded = true;
        private int _parentId = -1;  // For group hierarchy

        // Transform
        private float _posX = AppConstants.FrameWidth / 2f;
        private float _posY = AppConstants.FrameHeight / 2f;
        private float _sizeX = 400;
        private float _sizeY = 300;
        private float _rotX = 0;
        private float _rotY = 0;
        private float _rotZ = 0;
        private float _anchorX = 0.5f;
        private float _anchorY = 0.5f;
        private bool _lockAspectRatio = false;
        private float _aspectRatio = 400f / 300f;

        // Appearance
        private float _opacity = 1.0f;
        private float _colorR = 1.0f;
        private float _colorG = 1.0f;
        private float _colorB = 1.0f;
        private float _colorA = 1.0f;

        // Texture
        private TextureSourceType _textureSource = TextureSourceType.SolidColor;
        private float _texX = 0;
        private float _texY = 0;
        private float _texW = AppConstants.FrameWidth;
        private float _texH = AppConstants.FrameHeight;
        private float _texRot = 0;
        private bool _lockTexAspectRatio = false;
        private string _texturePath = "";
        private string _spoutSenderName = "";

        // Text properties
        private string _textContent = "Text";
        private string _fontFamily = "Arial";
        private float _fontSize = 48;
        private bool _fontBold = false;
        private bool _fontItalic = false;
        private TextAlignment _textAlignment = TextAlignment.Center;
        private float _lineHeight = 1.2f;
        private float _letterSpacing = 0;
        private TextAntialiasMode _textAntialiasMode = TextAntialiasMode.Smooth;

        // Mask properties
        private MaskMode _maskMode = MaskMode.Inner;
        private ObservableCollection<int> _maskedLayerIds = new ObservableCollection<int>();

        // Group children (runtime reference)
        private ObservableCollection<LayerModel> _children = new ObservableCollection<LayerModel>();

        // Video properties
        private bool _videoAlpha = false;  // Whether video has alpha/transparency mask

        // Engine IDs (runtime only, not serialized)
        private int _textureId = -1;
        private int _spoutReceiverId = -1;
        private int _videoId = -1;

        // Cached arrays for ToNative() to avoid per-frame allocations (GC pressure optimization)
        private readonly byte[] _cachedTexturePath = new byte[260];
        private readonly int[] _cachedMaskedLayerIds = new int[DaroConstants.MAX_LAYERS];

        public LayerModel()
        {
            _id = Interlocked.Increment(ref _nextId);
            _name = $"Layer_{_id}";
            InitializeTracks();
        }

        #region Properties

        public int Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public LayerType LayerType
        {
            get => _layerType;
            set => SetProperty(ref _layerType, value);
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public int ParentId
        {
            get => _parentId;
            set => SetProperty(ref _parentId, value);
        }

        // Transform
        public float PosX
        {
            get => _posX;
            set => SetProperty(ref _posX, value);
        }

        public float PosY
        {
            get => _posY;
            set => SetProperty(ref _posY, value);
        }

        public float SizeX
        {
            get => _sizeX;
            set
            {
                value = Math.Max(0.1f, value); // Minimum size to prevent zero/negative
                float oldSize = _sizeX;
                if (SetProperty(ref _sizeX, value))
                {
                    // Adjust position to keep anchor point fixed
                    float delta = oldSize - value;
                    if (delta != 0 && AnchorX != 0.5f)
                    {
                        _posX += (AnchorX - 0.5f) * delta;
                        OnPropertyChanged(nameof(PosX));
                    }

                    if (_lockAspectRatio && _aspectRatio > 0)
                    {
                        float oldSizeY = _sizeY;
                        _sizeY = value / _aspectRatio;
                        // Also adjust Y position for aspect ratio change
                        float deltaY = oldSizeY - _sizeY;
                        if (deltaY != 0 && AnchorY != 0.5f)
                        {
                            _posY += (AnchorY - 0.5f) * deltaY;
                            OnPropertyChanged(nameof(PosY));
                        }
                        OnPropertyChanged(nameof(SizeY));
                    }
                }
            }
        }

        public float SizeY
        {
            get => _sizeY;
            set
            {
                value = Math.Max(0.1f, value); // Minimum size to prevent zero/negative
                float oldSize = _sizeY;
                if (SetProperty(ref _sizeY, value))
                {
                    // Adjust position to keep anchor point fixed
                    float delta = oldSize - value;
                    if (delta != 0 && AnchorY != 0.5f)
                    {
                        _posY += (AnchorY - 0.5f) * delta;
                        OnPropertyChanged(nameof(PosY));
                    }

                    if (_lockAspectRatio && value > 0)
                    {
                        float oldSizeX = _sizeX;
                        _sizeX = value * _aspectRatio;
                        // Also adjust X position for aspect ratio change
                        float deltaX = oldSizeX - _sizeX;
                        if (deltaX != 0 && AnchorX != 0.5f)
                        {
                            _posX += (AnchorX - 0.5f) * deltaX;
                            OnPropertyChanged(nameof(PosX));
                        }
                        OnPropertyChanged(nameof(SizeX));
                    }
                }
            }
        }

        public float RotX
        {
            get => _rotX;
            set => SetProperty(ref _rotX, value);
        }

        public float RotY
        {
            get => _rotY;
            set => SetProperty(ref _rotY, value);
        }

        public float RotZ
        {
            get => _rotZ;
            set => SetProperty(ref _rotZ, value);
        }

        public float AnchorX
        {
            get => _anchorX;
            set => SetProperty(ref _anchorX, Math.Clamp(value, 0f, 1f));
        }

        public float AnchorY
        {
            get => _anchorY;
            set => SetProperty(ref _anchorY, Math.Clamp(value, 0f, 1f));
        }

        public bool LockAspectRatio
        {
            get => _lockAspectRatio;
            set
            {
                if (SetProperty(ref _lockAspectRatio, value) && value && _sizeY > 0)
                {
                    _aspectRatio = _sizeX / _sizeY;
                }
            }
        }

        // Appearance
        public float Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, Math.Clamp(value, 0f, 1f));
        }

        public float ColorR
        {
            get => _colorR;
            set => SetProperty(ref _colorR, Math.Clamp(value, 0f, 1f));
        }

        public float ColorG
        {
            get => _colorG;
            set => SetProperty(ref _colorG, Math.Clamp(value, 0f, 1f));
        }

        public float ColorB
        {
            get => _colorB;
            set => SetProperty(ref _colorB, Math.Clamp(value, 0f, 1f));
        }

        public float ColorA
        {
            get => _colorA;
            set => SetProperty(ref _colorA, Math.Clamp(value, 0f, 1f));
        }

        // Texture
        public TextureSourceType TextureSource
        {
            get => _textureSource;
            set => SetProperty(ref _textureSource, value);
        }

        public float TexX
        {
            get => _texX;
            set => SetProperty(ref _texX, value);
        }

        public float TexY
        {
            get => _texY;
            set => SetProperty(ref _texY, value);
        }

        public float TexW
        {
            get => _texW;
            set => SetProperty(ref _texW, Math.Max(0.001f, value)); // Prevent zero/negative
        }

        public float TexH
        {
            get => _texH;
            set => SetProperty(ref _texH, Math.Max(0.001f, value)); // Prevent zero/negative
        }

        public float TexRot
        {
            get => _texRot;
            set => SetProperty(ref _texRot, value);
        }

        public bool LockTexAspectRatio
        {
            get => _lockTexAspectRatio;
            set => SetProperty(ref _lockTexAspectRatio, value);
        }

        public string TexturePath
        {
            get => _texturePath;
            set => SetProperty(ref _texturePath, value);
        }

        public string SpoutSenderName
        {
            get => _spoutSenderName;
            set => SetProperty(ref _spoutSenderName, value);
        }

        public int TextureId
        {
            get => _textureId;
            set => SetProperty(ref _textureId, value);
        }

        public int SpoutReceiverId
        {
            get => _spoutReceiverId;
            set => SetProperty(ref _spoutReceiverId, value);
        }

        public int VideoId
        {
            get => _videoId;
            set => SetProperty(ref _videoId, value);
        }

        public bool VideoAlpha
        {
            get => _videoAlpha;
            set => SetProperty(ref _videoAlpha, value);
        }

        // Text Properties
        public string TextContent
        {
            get => _textContent;
            set => SetProperty(ref _textContent, value?.Length > 1023 ? value.Substring(0, 1023) : value); // Max 1023 chars + null
        }

        public string FontFamily
        {
            get => _fontFamily;
            set => SetProperty(ref _fontFamily, string.IsNullOrEmpty(value) ? "Arial" : value);
        }

        public float FontSize
        {
            get => _fontSize;
            set => SetProperty(ref _fontSize, Math.Max(1, value));
        }

        public bool FontBold
        {
            get => _fontBold;
            set => SetProperty(ref _fontBold, value);
        }

        public bool FontItalic
        {
            get => _fontItalic;
            set => SetProperty(ref _fontItalic, value);
        }

        public TextAlignment TextAlignment
        {
            get => _textAlignment;
            set => SetProperty(ref _textAlignment, value);
        }

        public float LineHeight
        {
            get => _lineHeight;
            set => SetProperty(ref _lineHeight, Math.Max(0.5f, value));
        }

        public float LetterSpacing
        {
            get => _letterSpacing;
            set => SetProperty(ref _letterSpacing, value);
        }

        public TextAntialiasMode TextAntialiasMode
        {
            get => _textAntialiasMode;
            set => SetProperty(ref _textAntialiasMode, value);
        }

        // Mask Properties
        public MaskMode MaskMode
        {
            get => _maskMode;
            set => SetProperty(ref _maskMode, value);
        }

        public ObservableCollection<int> MaskedLayerIds
        {
            get => _maskedLayerIds;
            set => SetProperty(ref _maskedLayerIds, value);
        }

        // Group Children
        public ObservableCollection<LayerModel> Children
        {
            get => _children;
            set => SetProperty(ref _children, value);
        }

        #endregion

        #region Tracks

        public ObservableCollection<PropertyTrackModel> Tracks { get; } = new ObservableCollection<PropertyTrackModel>();

        /// <summary>
        /// Track for text content keyframes with jump interpolation.
        /// </summary>
        public StringPropertyTrackModel TextTrack { get; private set; }

        private void InitializeTracks()
        {
            // Initialize string/text track
            TextTrack = new StringPropertyTrackModel { PropertyName = "Text Content", PropertyId = "TextContent" };

            // Initialize numeric tracks
            Tracks.Add(new PropertyTrackModel { PropertyName = "Pos X", PropertyId = "PosX" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Pos Y", PropertyId = "PosY" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Size X", PropertyId = "SizeX" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Size Y", PropertyId = "SizeY" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Rot X", PropertyId = "RotX" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Rot Y", PropertyId = "RotY" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Rot Z", PropertyId = "RotZ" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Opacity", PropertyId = "Opacity" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Color R", PropertyId = "ColorR" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Color G", PropertyId = "ColorG" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Color B", PropertyId = "ColorB" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Visible", PropertyId = "Visible" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Font Size", PropertyId = "FontSize" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Line Height", PropertyId = "LineHeight" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Letter Spacing", PropertyId = "LetterSpacing" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Tex X", PropertyId = "TexX" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Tex Y", PropertyId = "TexY" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Tex W", PropertyId = "TexW" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Tex H", PropertyId = "TexH" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Tex Rot", PropertyId = "TexRot" });
            Tracks.Add(new PropertyTrackModel { PropertyName = "Video State", PropertyId = "VideoState" });
        }

        public PropertyTrackModel GetTrack(string propertyId)
        {
            return Tracks.FirstOrDefault(t => t.PropertyId == propertyId);
        }

        public void SetKeyframe(string propertyId, int frame, float value)
        {
            GetTrack(propertyId)?.SetKeyframe(frame, value);
        }

        public void DeleteKeyframe(string propertyId, int frame)
        {
            GetTrack(propertyId)?.DeleteKeyframe(frame);
        }

        public bool HasKeyframeAtFrame(string propertyId, int frame)
        {
            return GetTrack(propertyId)?.HasKeyframeAtFrame(frame) ?? false;
        }

        public bool HasAnyKeyframes()
        {
            return Tracks.Any(t => t.Keyframes.Count > 0) ||
                   (TextTrack != null && TextTrack.Keyframes.Count > 0);
        }

        // Text keyframe helpers
        public void SetTextKeyframe(int frame, string value)
        {
            TextTrack?.SetKeyframe(frame, value);
        }

        public void DeleteTextKeyframe(int frame)
        {
            TextTrack?.DeleteKeyframe(frame);
        }

        public bool HasTextKeyframeAtFrame(int frame)
        {
            return TextTrack?.HasKeyframeAtFrame(frame) ?? false;
        }

        public void ApplyAnimationAtFrame(int frame)
        {
            // Apply numeric track keyframes with value clamping for broadcast safety
            foreach (var track in Tracks)
            {
                if (track.Keyframes.Count == 0) continue;

                float value = track.GetValueAtFrame(frame);

                switch (track.PropertyId)
                {
                    case "PosX": _posX = value; OnPropertyChanged(nameof(PosX)); break;
                    case "PosY": _posY = value; OnPropertyChanged(nameof(PosY)); break;
                    case "SizeX": _sizeX = value; OnPropertyChanged(nameof(SizeX)); break;
                    case "SizeY": _sizeY = value; OnPropertyChanged(nameof(SizeY)); break;
                    case "RotX": _rotX = value; OnPropertyChanged(nameof(RotX)); break;
                    case "RotY": _rotY = value; OnPropertyChanged(nameof(RotY)); break;
                    case "RotZ": _rotZ = value; OnPropertyChanged(nameof(RotZ)); break;
                    case "Opacity": _opacity = Math.Clamp(value, 0f, 1f); OnPropertyChanged(nameof(Opacity)); break;
                    case "ColorR": _colorR = Math.Clamp(value, 0f, 1f); OnPropertyChanged(nameof(ColorR)); break;
                    case "ColorG": _colorG = Math.Clamp(value, 0f, 1f); OnPropertyChanged(nameof(ColorG)); break;
                    case "ColorB": _colorB = Math.Clamp(value, 0f, 1f); OnPropertyChanged(nameof(ColorB)); break;
                    case "Visible": _isVisible = value > 0.5f; OnPropertyChanged(nameof(IsVisible)); break;
                    case "FontSize": _fontSize = Math.Max(value, 1f); OnPropertyChanged(nameof(FontSize)); break;
                    case "LineHeight": _lineHeight = Math.Max(value, 0.1f); OnPropertyChanged(nameof(LineHeight)); break;
                    case "LetterSpacing": _letterSpacing = value; OnPropertyChanged(nameof(LetterSpacing)); break;
                    case "TexX": _texX = value; OnPropertyChanged(nameof(TexX)); break;
                    case "TexY": _texY = value; OnPropertyChanged(nameof(TexY)); break;
                    case "TexW": _texW = value; OnPropertyChanged(nameof(TexW)); break;
                    case "TexH": _texH = value; OnPropertyChanged(nameof(TexH)); break;
                    case "TexRot": _texRot = value; OnPropertyChanged(nameof(TexRot)); break;
                    case "VideoState": break; // Handled externally by playback engine (discrete, not interpolated)
                }
            }

            // Apply text track keyframes (jump interpolation)
            if (TextTrack != null && TextTrack.Keyframes.Count > 0)
            {
                var textValue = TextTrack.GetValueAtFrame(frame);
                if (textValue != null)
                {
                    _textContent = textValue;
                    OnPropertyChanged(nameof(TextContent));
                }
            }
        }

        public float GetPropertyValue(string propertyId)
        {
            return propertyId switch
            {
                "PosX" => PosX,
                "PosY" => PosY,
                "SizeX" => SizeX,
                "SizeY" => SizeY,
                "RotX" => RotX,
                "RotY" => RotY,
                "RotZ" => RotZ,
                "Opacity" => Opacity,
                "ColorR" => ColorR,
                "ColorG" => ColorG,
                "ColorB" => ColorB,
                "Visible" => IsVisible ? 1f : 0f,
                "FontSize" => FontSize,
                "LineHeight" => LineHeight,
                "LetterSpacing" => LetterSpacing,
                "TexX" => TexX,
                "TexY" => TexY,
                "TexW" => TexW,
                "TexH" => TexH,
                "TexRot" => TexRot,
                "VideoState" => 1f, // Default: Play (0=Stop, 1=Play, 2=Pause)
                _ => 0
            };
        }

        public void SetPropertyValue(string propertyId, float value)
        {
            switch (propertyId)
            {
                case "PosX": PosX = value; break;
                case "PosY": PosY = value; break;
                case "SizeX": SizeX = value; break;
                case "SizeY": SizeY = value; break;
                case "RotX": _rotX = value; OnPropertyChanged(nameof(RotX)); break;
                case "RotY": _rotY = value; OnPropertyChanged(nameof(RotY)); break;
                case "RotZ": _rotZ = value; OnPropertyChanged(nameof(RotZ)); break;
                case "Opacity": _opacity = Math.Clamp(value, 0f, 1f); OnPropertyChanged(nameof(Opacity)); break;
                case "ColorR": _colorR = Math.Clamp(value, 0f, 1f); OnPropertyChanged(nameof(ColorR)); break;
                case "ColorG": _colorG = Math.Clamp(value, 0f, 1f); OnPropertyChanged(nameof(ColorG)); break;
                case "ColorB": _colorB = Math.Clamp(value, 0f, 1f); OnPropertyChanged(nameof(ColorB)); break;
                case "Visible": _isVisible = value > 0.5f; OnPropertyChanged(nameof(IsVisible)); break;
                case "FontSize": _fontSize = Math.Max(value, 1f); OnPropertyChanged(nameof(FontSize)); break;
                case "LineHeight": _lineHeight = Math.Max(value, 0.1f); OnPropertyChanged(nameof(LineHeight)); break;
                case "LetterSpacing": _letterSpacing = value; OnPropertyChanged(nameof(LetterSpacing)); break;
                case "TexX": _texX = value; OnPropertyChanged(nameof(TexX)); break;
                case "TexY": _texY = value; OnPropertyChanged(nameof(TexY)); break;
                case "TexW": _texW = value; OnPropertyChanged(nameof(TexW)); break;
                case "TexH": _texH = value; OnPropertyChanged(nameof(TexH)); break;
                case "TexRot": _texRot = value; OnPropertyChanged(nameof(TexRot)); break;
            }
        }

        #endregion

        #region Transfunctioners

        /// <summary>
        /// Collection of transfunctioner bindings for this layer's properties.
        /// Links template values to scene layer properties for playout control.
        /// </summary>
        public ObservableCollection<TransfunctionerBindingModel> Transfunctioners { get; } = new ObservableCollection<TransfunctionerBindingModel>();

        /// <summary>
        /// Adds a new transfunctioner binding for the specified property.
        /// </summary>
        public TransfunctionerBindingModel AddTransfunctioner(string propertyId)
        {
            var binding = new TransfunctionerBindingModel
            {
                Name = $"{Name}.{propertyId}",
                TargetLayerId = Id,
                TargetLayerName = Name,
                TargetPropertyId = propertyId,
                BindingType = GetTransfunctionerType(propertyId),
                ParentLayer = this
            };
            Transfunctioners.Add(binding);
            return binding;
        }

        /// <summary>
        /// Removes a transfunctioner binding by ID.
        /// </summary>
        public void RemoveTransfunctioner(string bindingId)
        {
            var binding = Transfunctioners.FirstOrDefault(t => t.Id == bindingId);
            if (binding != null)
                Transfunctioners.Remove(binding);
        }

        /// <summary>
        /// Determines the transfunctioner type based on property ID.
        /// </summary>
        private static TransfunctionerType GetTransfunctionerType(string propertyId)
        {
            return propertyId switch
            {
                "TextContent" => TransfunctionerType.String,
                "ColorR" or "ColorG" or "ColorB" or "ColorA" => TransfunctionerType.Color,
                _ => TransfunctionerType.Float
            };
        }

        #endregion

        #region Conversion

        public DaroLayerNative ToNative()
        {
            // Clear cached arrays (reuse to avoid GC pressure at 50 FPS)
            Array.Clear(_cachedTexturePath, 0, _cachedTexturePath.Length);
            Array.Clear(_cachedMaskedLayerIds, 0, _cachedMaskedLayerIds.Length);

            // Copy texture path as UTF-8 bytes with null termination
            if (!string.IsNullOrEmpty(TexturePath))
            {
                var pathBytes = System.Text.Encoding.UTF8.GetBytes(TexturePath);
                int copyLen = Math.Min(pathBytes.Length, 258); // Leave room for null terminator
                Array.Copy(pathBytes, _cachedTexturePath, copyLen);
                _cachedTexturePath[copyLen] = 0; // Ensure null termination
            }

            // Copy masked layer IDs
            int maskedCount = Math.Min(MaskedLayerIds.Count, DaroConstants.MAX_LAYERS);
            for (int i = 0; i < maskedCount; i++)
            {
                _cachedMaskedLayerIds[i] = MaskedLayerIds[i];
            }

            var native = new DaroLayerNative
            {
                id = Id,
                active = IsVisible ? 1 : 0,
                layerType = (int)LayerType,
                posX = PosX,
                posY = PosY,
                sizeX = SizeX,
                sizeY = SizeY,
                rotX = RotX,
                rotY = RotY,
                rotZ = RotZ,
                anchorX = AnchorX,
                anchorY = AnchorY,
                opacity = Opacity,
                colorR = ColorR,
                colorG = ColorG,
                colorB = ColorB,
                colorA = ColorA,
                sourceType = (int)TextureSource,
                textureId = (TextureSource == TextureSourceType.VideoFile) ? VideoId : TextureId,
                spoutReceiverId = SpoutReceiverId,
                texX = TexX,
                texY = TexY,
                texW = TexW,
                texH = TexH,
                texRot = TexRot,
                textureLocked = LockTexAspectRatio ? 1 : 0,

                // Text properties
                textContent = TextContent ?? "",
                fontFamily = FontFamily ?? "Arial",
                fontSize = FontSize,
                fontBold = FontBold ? 1 : 0,
                fontItalic = FontItalic ? 1 : 0,
                textAlignment = (int)TextAlignment,
                lineHeight = LineHeight,
                letterSpacing = LetterSpacing,
                textAntialiasMode = (int)TextAntialiasMode,

                // Use cached arrays (no allocation)
                texturePath = _cachedTexturePath,

                // Mask properties
                maskMode = (int)MaskMode,
                maskedLayerCount = Math.Min(MaskedLayerIds.Count, DaroConstants.MAX_LAYERS),
                maskedLayerIds = _cachedMaskedLayerIds
            };

            return native;
        }

        #endregion

        #region Serialization

        public LayerData ToSerializable()
        {
            var data = new LayerData
            {
                Id = Id,
                Name = Name,
                LayerType = (int)LayerType,
                IsVisible = IsVisible,
                ParentId = ParentId,
                PosX = PosX,
                PosY = PosY,
                SizeX = SizeX,
                SizeY = SizeY,
                RotX = RotX,
                RotY = RotY,
                RotZ = RotZ,
                AnchorX = AnchorX,
                AnchorY = AnchorY,
                LockAspectRatio = LockAspectRatio,
                Opacity = Opacity,
                ColorR = ColorR,
                ColorG = ColorG,
                ColorB = ColorB,
                ColorA = ColorA,
                TextureSource = (int)TextureSource,
                TexX = TexX,
                TexY = TexY,
                TexW = TexW,
                TexH = TexH,
                TexRot = TexRot,
                TexturePath = TexturePath,
                SpoutSenderName = SpoutSenderName,
                VideoAlpha = VideoAlpha,
                // Text
                TextContent = TextContent,
                FontFamily = FontFamily,
                FontSize = FontSize,
                FontBold = FontBold,
                FontItalic = FontItalic,
                TextAlignment = (int)TextAlignment,
                LineHeight = LineHeight,
                LetterSpacing = LetterSpacing,
                TextAntialiasMode = (int)TextAntialiasMode,
                // Mask
                MaskMode = (int)MaskMode,
                MaskedLayerIds = MaskedLayerIds.ToList(),
                // Tracks
                Tracks = Tracks.Where(t => t.Keyframes.Count > 0)
                              .Select(t => t.ToSerializable()).ToList(),
                // String tracks (text content with jump interpolation)
                StringTracks = TextTrack != null && TextTrack.Keyframes.Count > 0
                    ? new List<StringTrackData>
                    {
                        new StringTrackData
                        {
                            PropertyId = TextTrack.PropertyId,
                            Keyframes = TextTrack.Keyframes.Select(k => new StringKeyframeData
                            {
                                Frame = k.Frame,
                                Value = k.Value
                            }).ToList()
                        }
                    }
                    : new List<StringTrackData>(),
                // Transfunctioner bindings
                Transfunctioners = Transfunctioners.Select(t => new TransfunctionerData
                {
                    Id = t.Id,
                    Name = t.Name,
                    TemplateElementId = t.TemplateElementId,
                    TemplateElementName = t.TemplateElementName,
                    TargetLayerId = t.TargetLayerId,
                    TargetLayerName = t.TargetLayerName,
                    TargetPropertyId = t.TargetPropertyId,
                    BindingType = (int)t.BindingType
                }).ToList()
            };

            return data;
        }

        public static LayerModel FromSerializable(LayerData data)
        {
            // Ensure _nextId stays above loaded IDs to prevent collisions
            SyncNextId(data.Id);

            var layer = new LayerModel
            {
                Id = data.Id,
                Name = data.Name,
                LayerType = (LayerType)data.LayerType,
                IsVisible = data.IsVisible,
                ParentId = data.ParentId,
                PosX = data.PosX,
                PosY = data.PosY,
                SizeX = data.SizeX,
                SizeY = data.SizeY,
                RotX = data.RotX,
                RotY = data.RotY,
                RotZ = data.RotZ,
                AnchorX = data.AnchorX,
                AnchorY = data.AnchorY,
                LockAspectRatio = data.LockAspectRatio,
                Opacity = data.Opacity,
                ColorR = data.ColorR,
                ColorG = data.ColorG,
                ColorB = data.ColorB,
                ColorA = data.ColorA,
                TextureSource = (TextureSourceType)data.TextureSource,
                TexX = data.TexX,
                TexY = data.TexY,
                TexW = data.TexW,
                TexH = data.TexH,
                TexRot = data.TexRot,
                TexturePath = data.TexturePath ?? "",
                SpoutSenderName = data.SpoutSenderName ?? "",
                VideoAlpha = data.VideoAlpha,
                // Text
                TextContent = data.TextContent ?? "Text",
                FontFamily = data.FontFamily ?? "Arial",
                FontSize = data.FontSize > 0 ? data.FontSize : 48,
                FontBold = data.FontBold,
                FontItalic = data.FontItalic,
                TextAlignment = (TextAlignment)data.TextAlignment,
                LineHeight = data.LineHeight > 0 ? data.LineHeight : 1.2f,
                LetterSpacing = data.LetterSpacing,
                TextAntialiasMode = (TextAntialiasMode)Math.Clamp(data.TextAntialiasMode, 0, 1),
                // Mask
                MaskMode = (MaskMode)data.MaskMode
            };

            // Load masked layer IDs
            if (data.MaskedLayerIds != null)
            {
                foreach (var id in data.MaskedLayerIds)
                    layer.MaskedLayerIds.Add(id);
            }

            // Load numeric keyframes
            if (data.Tracks != null)
            {
                foreach (var trackData in data.Tracks)
                {
                    var track = layer.GetTrack(trackData.PropertyId);
                    if (track != null && trackData.Keyframes != null)
                    {
                        foreach (var kfData in trackData.Keyframes)
                        {
                            var kf = new KeyframeModel
                            {
                                Frame = kfData.Frame,
                                Value = kfData.Value,
                                EaseIn = kfData.EaseIn,
                                EaseOut = kfData.EaseOut
                            };
                            track.Keyframes.Add(kf);
                        }
                    }
                }
            }

            // Load string keyframes (text content with jump interpolation)
            if (data.StringTracks != null)
            {
                foreach (var trackData in data.StringTracks)
                {
                    if (trackData.PropertyId == "TextContent" && layer.TextTrack != null && trackData.Keyframes != null)
                    {
                        foreach (var kfData in trackData.Keyframes)
                        {
                            layer.TextTrack.Keyframes.Add(new StringKeyframeModel
                            {
                                Frame = kfData.Frame,
                                Value = kfData.Value ?? ""
                            });
                        }
                    }
                }
            }

            // Load transfunctioner bindings
            if (data.Transfunctioners != null)
            {
                foreach (var tfData in data.Transfunctioners)
                {
                    layer.Transfunctioners.Add(new TransfunctionerBindingModel
                    {
                        Id = tfData.Id ?? Guid.NewGuid().ToString(),
                        Name = tfData.Name ?? "",
                        TemplateElementId = tfData.TemplateElementId,
                        TemplateElementName = tfData.TemplateElementName,
                        TargetLayerId = tfData.TargetLayerId,
                        TargetLayerName = tfData.TargetLayerName,
                        TargetPropertyId = tfData.TargetPropertyId,
                        BindingType = (TransfunctionerType)tfData.BindingType
                    });
                }
            }

            return layer;
        }

        #endregion
    }
}
