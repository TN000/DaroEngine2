// Designer/Models/TransfunctionerBindingModel.cs
using System;
using System.Globalization;

namespace DaroDesigner.Models
{
    /// <summary>
    /// Type of value that the transfunctioner binds.
    /// </summary>
    public enum TransfunctionerType
    {
        String = 0,  // Text properties
        Float = 1,   // Numeric properties
        Color = 2    // RGBA color
    }

    /// <summary>
    /// Represents a binding between a template element and a scene layer property.
    /// When the template value is filled in during playout, it controls the scene property.
    /// </summary>
    public class TransfunctionerBindingModel : ViewModelBase
    {
        private string _id;
        private string _name;
        private string _templateElementId;
        private string _templateElementName;
        private int _targetLayerId;
        private string _targetLayerName;
        private string _targetPropertyId;
        private TransfunctionerType _bindingType;
        private float _stepValue = 1f;

        /// <summary>
        /// Unique identifier for this binding.
        /// </summary>
        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        /// <summary>
        /// User-friendly name for the binding (e.g., "Title.Text").
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>
        /// ID of the template element that provides the value (if linked from template).
        /// </summary>
        public string TemplateElementId
        {
            get => _templateElementId;
            set => SetProperty(ref _templateElementId, value);
        }

        /// <summary>
        /// Display name of the template element.
        /// </summary>
        public string TemplateElementName
        {
            get => _templateElementName;
            set => SetProperty(ref _templateElementName, value);
        }

        /// <summary>
        /// ID of the target layer in the scene.
        /// </summary>
        public int TargetLayerId
        {
            get => _targetLayerId;
            set => SetProperty(ref _targetLayerId, value);
        }

        /// <summary>
        /// Display name of the target layer.
        /// </summary>
        public string TargetLayerName
        {
            get => _targetLayerName;
            set => SetProperty(ref _targetLayerName, value);
        }

        /// <summary>
        /// The property ID on the target layer (e.g., "TextContent", "PosX", "Opacity").
        /// </summary>
        public string TargetPropertyId
        {
            get => _targetPropertyId;
            set => SetProperty(ref _targetPropertyId, value);
        }

        /// <summary>
        /// Type of the binding (String, Float, or Color).
        /// </summary>
        public TransfunctionerType BindingType
        {
            get => _bindingType;
            set => SetProperty(ref _bindingType, value);
        }

        /// <summary>
        /// Step value for incrementing/decrementing this property (default 1).
        /// </summary>
        public float StepValue
        {
            get => _stepValue;
            set => SetProperty(ref _stepValue, value > 0 ? value : 0.01f);
        }

        /// <summary>
        /// Creates a new transfunctioner binding with a unique ID.
        /// </summary>
        public TransfunctionerBindingModel()
        {
            _id = Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Gets a display string for this binding.
        /// </summary>
        public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : $"{TargetLayerName}.{TargetPropertyId}";

        /// <summary>
        /// Reference to the parent layer (set at runtime, not serialized).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public LayerModel ParentLayer { get; set; }

        /// <summary>
        /// Gets or sets the current value of the bound property.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string CurrentValue
        {
            get
            {
                if (ParentLayer == null) return "";
                return GetPropertyValue(ParentLayer, TargetPropertyId);
            }
            set
            {
                if (ParentLayer == null) return;
                SetPropertyValue(ParentLayer, TargetPropertyId, value);
                OnPropertyChanged();
            }
        }

        private static bool IsColorProperty(string propertyId)
            => propertyId is "ColorR" or "ColorG" or "ColorB";

        private string GetPropertyValue(LayerModel layer, string propertyId)
        {
            if (propertyId == "TextContent")
                return layer.TextContent ?? "";

            float val = layer.GetPropertyValue(propertyId);

            // Color properties displayed as 0-255 in transfunctioner UI
            if (IsColorProperty(propertyId))
                return ((int)(val * 255)).ToString();

            return val.ToString("F2", CultureInfo.InvariantCulture);
        }

        private void SetPropertyValue(LayerModel layer, string propertyId, string value)
        {
            if (propertyId == "TextContent")
            {
                layer.TextContent = value;
                return;
            }

            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatVal))
                return;

            // Color properties: transfunctioner UI uses 0-255, layer uses 0-1
            if (IsColorProperty(propertyId))
                floatVal /= 255f;

            layer.SetPropertyValue(propertyId, floatVal);
        }
    }
}
