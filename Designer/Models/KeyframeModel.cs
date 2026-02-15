// Designer/Models/KeyframeModel.cs
using System;

namespace DaroDesigner.Models
{
    public class KeyframeModel : ViewModelBase
    {
        private int _frame;
        private float _value;
        private float _easeIn;
        private float _easeOut;
        private bool _isSelected;

        public int Frame
        {
            get => _frame;
            set => SetProperty(ref _frame, Math.Max(0, value));
        }

        public float Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        /// <summary>
        /// Deceleration factor (0-1). Higher value = slower approach to this keyframe.
        /// This is applied when interpolating TO this keyframe.
        /// </summary>
        public float EaseIn
        {
            get => _easeIn;
            set => SetProperty(ref _easeIn, Math.Clamp(value, 0f, 1f));
        }

        /// <summary>
        /// Acceleration factor (0-1). Higher value = faster departure from this keyframe.
        /// This is applied when interpolating FROM this keyframe.
        /// </summary>
        public float EaseOut
        {
            get => _easeOut;
            set => SetProperty(ref _easeOut, Math.Clamp(value, 0f, 1f));
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
