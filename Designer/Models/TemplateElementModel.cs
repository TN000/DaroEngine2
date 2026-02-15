// Designer/Models/TemplateElementModel.cs
using System;

namespace DaroDesigner.Models
{
    public enum TemplateElementType
    {
        Label,
        TextBox,
        MultilineTextBox
    }

    public class TemplateElementModel : ViewModelBase
    {
        private string _id;
        private string _name;
        private TemplateElementType _elementType;
        private double _x;
        private double _y;
        private double _width;
        private double _height;
        private string _defaultText;
        private string _fontFamily;
        private double _fontSize;
        private string _foregroundColor;
        private string _backgroundColor;
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

        public TemplateElementType ElementType
        {
            get => _elementType;
            set => SetProperty(ref _elementType, value);
        }

        public double X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        public double Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }

        public double Width
        {
            get => _width;
            set => SetProperty(ref _width, value);
        }

        public double Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }

        public string DefaultText
        {
            get => _defaultText;
            set => SetProperty(ref _defaultText, value);
        }

        public string FontFamily
        {
            get => _fontFamily;
            set => SetProperty(ref _fontFamily, value);
        }

        public double FontSize
        {
            get => _fontSize;
            set => SetProperty(ref _fontSize, value);
        }

        public string ForegroundColor
        {
            get => _foregroundColor;
            set => SetProperty(ref _foregroundColor, value);
        }

        public string BackgroundColor
        {
            get => _backgroundColor;
            set => SetProperty(ref _backgroundColor, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// ID of the linked transfunctioner binding (if this element was created from a transfunctioner drag).
        /// </summary>
        public string LinkedTransfunctionerId { get; set; }

        // Validation properties
        /// <summary>Whether this field is required to have a non-empty value.</summary>
        public bool IsRequired { get; set; }

        /// <summary>Maximum allowed length for the field value. 0 = no limit.</summary>
        public int MaxLength { get; set; }

        /// <summary>Placeholder text shown when the field is empty.</summary>
        public string Placeholder { get; set; }

        /// <summary>
        /// Validates the given value against this element's rules.
        /// Returns error message or null if valid.
        /// </summary>
        public string Validate(string value)
        {
            if (IsRequired && string.IsNullOrWhiteSpace(value))
                return $"{Name} is required";

            if (MaxLength > 0 && value != null && value.Length > MaxLength)
                return $"{Name} exceeds maximum length of {MaxLength} characters";

            return null;  // Valid
        }

        public TemplateElementModel()
        {
            Id = Guid.NewGuid().ToString();
            Name = "Element";
            FontFamily = "Segoe UI";
            FontSize = 14;
            ForegroundColor = "#FFFFFF";
            BackgroundColor = "#00000000";
            Width = 150;
            Height = 30;
        }

        public TemplateElementModel Clone()
        {
            return new TemplateElementModel
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name + " Copy",
                ElementType = ElementType,
                X = X + 20,
                Y = Y + 20,
                Width = Width,
                Height = Height,
                DefaultText = DefaultText,
                FontFamily = FontFamily,
                FontSize = FontSize,
                ForegroundColor = ForegroundColor,
                BackgroundColor = BackgroundColor,
                IsRequired = IsRequired,
                MaxLength = MaxLength,
                Placeholder = Placeholder,
                LinkedTransfunctionerId = LinkedTransfunctionerId
            };
        }
    }
}
