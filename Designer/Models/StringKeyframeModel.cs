// Designer/Models/StringKeyframeModel.cs
using System.Text.Json.Serialization;

namespace DaroDesigner.Models
{
    /// <summary>
    /// Represents a keyframe for string/text properties.
    /// Uses "jump" interpolation - value changes instantly at keyframe, no interpolation.
    /// </summary>
    public class StringKeyframeModel : ViewModelBase
    {
        private int _frame;
        private string _value = "";
        private bool _isSelected;

        /// <summary>
        /// Frame number where this keyframe exists.
        /// </summary>
        public int Frame
        {
            get => _frame;
            set => SetProperty(ref _frame, value);
        }

        /// <summary>
        /// The string value at this keyframe.
        /// </summary>
        public string Value
        {
            get => _value;
            set => SetProperty(ref _value, value ?? "");
        }

        /// <summary>
        /// Whether this keyframe is currently selected in the UI.
        /// </summary>
        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
