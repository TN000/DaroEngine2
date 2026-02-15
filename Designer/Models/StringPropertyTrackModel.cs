// Designer/Models/StringPropertyTrackModel.cs
using System.Collections.ObjectModel;

namespace DaroDesigner.Models
{
    /// <summary>
    /// Manages keyframes for string/text properties with "jump" interpolation.
    /// Unlike numeric tracks, string values change instantly at keyframes with no interpolation.
    /// </summary>
    public class StringPropertyTrackModel : ViewModelBase
    {
        private string _propertyName = "";
        private string _propertyId = "";

        /// <summary>
        /// Display name of the property (e.g., "Text Content").
        /// </summary>
        public string PropertyName
        {
            get => _propertyName;
            set => SetProperty(ref _propertyName, value);
        }

        /// <summary>
        /// Identifier used for serialization and lookup (e.g., "TextContent").
        /// </summary>
        public string PropertyId
        {
            get => _propertyId;
            set => SetProperty(ref _propertyId, value);
        }

        /// <summary>
        /// Collection of keyframes for this track, sorted by frame number.
        /// </summary>
        public ObservableCollection<StringKeyframeModel> Keyframes { get; } = new ObservableCollection<StringKeyframeModel>();

        /// <summary>
        /// Sets or updates a keyframe at the specified frame.
        /// </summary>
        public void SetKeyframe(int frame, string value)
        {
            int index = BinarySearchKeyframe(frame);
            if (index >= 0)
            {
                Keyframes[index].Value = value;
            }
            else
            {
                var newKf = new StringKeyframeModel { Frame = frame, Value = value };
                int insertIndex = BinarySearchInsertPosition(frame);
                Keyframes.Insert(insertIndex, newKf);
            }
        }

        /// <summary>
        /// Removes a keyframe at the specified frame if it exists.
        /// </summary>
        public void DeleteKeyframe(int frame)
        {
            int index = BinarySearchKeyframe(frame);
            if (index >= 0)
            {
                Keyframes.RemoveAt(index);
            }
        }

        /// <summary>
        /// Checks if a keyframe exists at the specified frame.
        /// Uses binary search for O(log n) performance.
        /// </summary>
        public bool HasKeyframeAtFrame(int frame)
        {
            return BinarySearchKeyframe(frame) >= 0;
        }

        /// <summary>
        /// Gets the keyframe at the specified frame, or null if none exists.
        /// Uses binary search for O(log n) performance.
        /// </summary>
        public StringKeyframeModel GetKeyframeAtFrame(int frame)
        {
            int index = BinarySearchKeyframe(frame);
            return index >= 0 ? Keyframes[index] : null;
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
        /// Gets the string value at the specified frame using "jump" interpolation.
        /// Returns the value from the keyframe at or immediately before the given frame.
        /// Returns null if no keyframe exists at or before the frame.
        /// Uses binary search for O(log n) performance.
        /// </summary>
        public string GetValueAtFrame(int frame)
        {
            if (Keyframes.Count == 0)
                return null;

            // Binary search to find keyframe at or just before this frame
            int left = 0;
            int right = Keyframes.Count - 1;

            // If frame is before first keyframe, no value
            if (frame < Keyframes[0].Frame)
                return null;

            // If frame is at or after last keyframe, return last value
            if (frame >= Keyframes[right].Frame)
                return Keyframes[right].Value;

            // Binary search for the keyframe at or just before the frame
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
        /// Clears all keyframes from this track.
        /// </summary>
        public void ClearKeyframes()
        {
            Keyframes.Clear();
        }
    }
}
