// Designer/Models/PropertyTrackModel.cs
using System.Collections.ObjectModel;
using System.Linq;

namespace DaroDesigner.Models
{
    public class PropertyTrackModel : ViewModelBase
    {
        private string _propertyName;
        private string _propertyId;
        private bool _isExpanded;
        private float _defaultValue;

        public string PropertyName
        {
            get => _propertyName;
            set => SetProperty(ref _propertyName, value);
        }

        public string PropertyId
        {
            get => _propertyId;
            set => SetProperty(ref _propertyId, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        /// <summary>
        /// Default value to return when there are no keyframes.
        /// Typically set based on the property type (e.g., 1.0 for opacity, 0 for position).
        /// </summary>
        public float DefaultValue
        {
            get => _defaultValue;
            set => SetProperty(ref _defaultValue, value);
        }

        public ObservableCollection<KeyframeModel> Keyframes { get; } = new ObservableCollection<KeyframeModel>();

        public void SetKeyframe(int frame, float value, float easeIn = 0, float easeOut = 0)
        {
            int index = BinarySearchKeyframe(frame);
            if (index >= 0)
            {
                // Update existing keyframe
                Keyframes[index].Value = value;
            }
            else
            {
                var kf = new KeyframeModel
                {
                    Frame = frame,
                    Value = value,
                    EaseIn = easeIn,
                    EaseOut = easeOut
                };

                // Binary search for insert position
                int insertIndex = BinarySearchInsertPosition(frame);
                Keyframes.Insert(insertIndex, kf);
            }
        }

        public void DeleteKeyframe(int frame)
        {
            int index = BinarySearchKeyframe(frame);
            if (index >= 0)
            {
                Keyframes.RemoveAt(index);
            }
        }

        public bool HasKeyframeAtFrame(int frame)
        {
            return BinarySearchKeyframe(frame) >= 0;
        }

        /// <summary>
        /// Binary search for exact frame match. Returns index or -1 if not found.
        /// </summary>
        private int BinarySearchKeyframe(int frame)
        {
            if (Keyframes.Count == 0) return -1;

            int left = 0;
            int right = Keyframes.Count - 1;

            while (left <= right)
            {
                int mid = (left + right) / 2;
                int midFrame = Keyframes[mid].Frame;

                if (midFrame == frame)
                    return mid;
                else if (midFrame < frame)
                    left = mid + 1;
                else
                    right = mid - 1;
            }

            return -1;
        }

        /// <summary>
        /// Binary search to find the insert position for a new keyframe.
        /// </summary>
        private int BinarySearchInsertPosition(int frame)
        {
            if (Keyframes.Count == 0) return 0;

            int left = 0;
            int right = Keyframes.Count;

            while (left < right)
            {
                int mid = (left + right) / 2;
                if (Keyframes[mid].Frame < frame)
                    left = mid + 1;
                else
                    right = mid;
            }

            return left;
        }

        public float GetValueAtFrame(int frame)
        {
            if (Keyframes.Count == 0) return _defaultValue;
            if (Keyframes.Count == 1) return Keyframes[0].Value;

            // Binary search to find surrounding keyframes (O(log n) instead of O(n))
            int left = 0;
            int right = Keyframes.Count - 1;

            // If frame is before first keyframe
            if (frame <= Keyframes[0].Frame) return Keyframes[0].Value;
            // If frame is after last keyframe
            if (frame >= Keyframes[right].Frame) return Keyframes[right].Value;

            // Binary search for the keyframe just before or at the frame
            while (left < right - 1)
            {
                int mid = (left + right) / 2;
                if (Keyframes[mid].Frame <= frame)
                    left = mid;
                else
                    right = mid;
            }

            var prev = Keyframes[left];
            var next = Keyframes[right];

            // Guard against division by zero (duplicate keyframes at same frame)
            int frameDelta = next.Frame - prev.Frame;
            if (frameDelta == 0)
            {
                return next.Value; // Use the later keyframe's value
            }

            // Interpolate
            float t = (float)(frame - prev.Frame) / frameDelta;

            // Apply easing: prev.EaseOut = acceleration, next.EaseIn = deceleration
            t = ApplyEasing(t, prev.EaseOut, next.EaseIn);

            return prev.Value + (next.Value - prev.Value) * t;
        }

        /// <summary>
        /// Gets the value at the most recent keyframe at or before the given frame (no interpolation).
        /// Useful for discrete/step properties like VideoState.
        /// </summary>
        public float GetStepValueAtFrame(int frame)
        {
            if (Keyframes.Count == 0) return _defaultValue;
            if (frame <= Keyframes[0].Frame) return Keyframes[0].Value;
            if (frame >= Keyframes[Keyframes.Count - 1].Frame) return Keyframes[Keyframes.Count - 1].Value;

            // Binary search for the keyframe at or just before the frame
            int left = 0;
            int right = Keyframes.Count - 1;
            while (left < right - 1)
            {
                int mid = (left + right) / 2;
                if (Keyframes[mid].Frame <= frame)
                    left = mid;
                else
                    right = mid;
            }
            return Keyframes[left].Value;
        }

        /// <summary>
        /// Apply easing curve to interpolation parameter t.
        /// Uses keyframe-centric naming (After Effects convention):
        ///   easeOut = outgoing keyframe's acceleration (0-1) - starts slow, speeds up
        ///   easeIn  = incoming keyframe's deceleration (0-1) - starts fast, slows down
        /// Both 0 = linear motion.
        /// </summary>
        private float ApplyEasing(float t, float easeOut, float easeIn)
        {
            if (easeOut == 0 && easeIn == 0)
                return t; // Linear

            // Normalize to percentages (0-1)
            float accel = easeOut; // acceleration factor
            float decel = easeIn;  // deceleration factor

            // Use bezier-like cubic curve
            // When accel is high, object accelerates more at the start
            // When decel is high, object decelerates more at the end
            
            float result;
            
            if (accel > 0 && decel > 0)
            {
                // Both acceleration and deceleration - S-curve with weighted blend
                // blend determines where the inflection point is (0.5 = symmetric S-curve)
                float blend = accel / (accel + decel);

                // Guard against edge cases (blend too close to 0 or 1)
                blend = System.Math.Clamp(blend, 0.01f, 0.99f);

                // Weighted smooth step: shift the S-curve based on accel/decel ratio
                // blend < 0.5: more acceleration, inflection point later
                // blend > 0.5: more deceleration, inflection point earlier
                float adjustedT;
                if (t < blend)
                {
                    float normalized = t / blend;
                    adjustedT = 0.5f * normalized * normalized;  // Acceleration phase
                }
                else
                {
                    float normalized = (1 - t) / (1 - blend);
                    adjustedT = 1 - 0.5f * normalized * normalized;  // Deceleration phase
                }

                // Blend between linear and eased based on total easing amount
                float easingStrength = System.Math.Min((accel + decel) / 2, 1.0f);
                result = t * (1 - easingStrength) + adjustedT * easingStrength;
            }
            else if (accel > 0)
            {
                // Only acceleration - starts slow, speeds up (quadratic)
                result = t * t * accel + t * (1 - accel);
            }
            else if (decel > 0)
            {
                // Only deceleration - starts fast, slows down (quadratic)
                float invT = 1 - t;
                result = 1 - (invT * invT * decel + invT * (1 - decel));
            }
            else
            {
                result = t;
            }

            return System.Math.Clamp(result, 0f, 1f);
        }

        #region Serialization

        public TrackData ToSerializable()
        {
            return new TrackData
            {
                PropertyId = PropertyId,
                Keyframes = Keyframes.Select(kf => new KeyframeData
                {
                    Frame = kf.Frame,
                    Value = kf.Value,
                    EaseIn = kf.EaseIn,
                    EaseOut = kf.EaseOut
                }).ToList()
            };
        }

        #endregion
    }
}
