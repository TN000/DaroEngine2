// Designer/Models/TemplateTakeModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DaroDesigner.Models
{
    public enum TakeActionType
    {
        Play,
        Stop,
        Cue,
        Pause,
        Continue
    }

    /// <summary>
    /// Represents an animation reference within a Take action.
    /// </summary>
    public class AnimationTargetModel : ViewModelBase
    {
        private string _animationName = "";
        private bool _isSelected;

        /// <summary>
        /// Name of the animation in the linked scene.
        /// </summary>
        public string AnimationName
        {
            get => _animationName;
            set => SetProperty(ref _animationName, value);
        }

        /// <summary>
        /// Whether this animation is selected for this action.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    public class TakeActionModel : ViewModelBase
    {
        private string _id = "";
        private TakeActionType _actionType;
        private int _startFrame;
        private int _duration;
        private bool _isSelected;
        private ObservableCollection<string> _targetAnimationNames = new();

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public TakeActionType ActionType
        {
            get => _actionType;
            set => SetProperty(ref _actionType, value);
        }

        /// <summary>
        /// List of animation names that this action targets.
        /// </summary>
        public ObservableCollection<string> TargetAnimationNames
        {
            get => _targetAnimationNames;
            set => SetProperty(ref _targetAnimationNames, value);
        }

        /// <summary>
        /// Display text showing selected animations.
        /// </summary>
        public string TargetAnimationsDisplay
        {
            get
            {
                if (TargetAnimationNames == null || TargetAnimationNames.Count == 0)
                    return "(none)";
                if (TargetAnimationNames.Count == 1)
                    return TargetAnimationNames[0];
                return $"{TargetAnimationNames.Count} animations";
            }
        }

        public int StartFrame
        {
            get => _startFrame;
            set => SetProperty(ref _startFrame, value);
        }

        public int Duration
        {
            get => _duration;
            set => SetProperty(ref _duration, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public TakeActionModel()
        {
            Id = Guid.NewGuid().ToString();
            Duration = 80;
            TargetAnimationNames = new ObservableCollection<string>();
        }

        public void NotifyTargetAnimationsChanged()
        {
            OnPropertyChanged(nameof(TargetAnimationsDisplay));
        }
    }

    public class TemplateTakeModel : ViewModelBase
    {
        private string _id = "";
        private string _name = "";
        private ObservableCollection<TakeActionModel> _actions = new();
        private bool _isSelected;
        private int _timelineDurationFrames = 250;

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

        public ObservableCollection<TakeActionModel> Actions
        {
            get => _actions;
            set => SetProperty(ref _actions, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// Duration of the take timeline in frames.
        /// </summary>
        public int TimelineDurationFrames
        {
            get => _timelineDurationFrames;
            set => SetProperty(ref _timelineDurationFrames, Math.Max(50, value));
        }

        public TemplateTakeModel()
        {
            Id = Guid.NewGuid().ToString();
            Name = "Take 1";
            Actions = new ObservableCollection<TakeActionModel>();
        }

        /// <summary>
        /// Gets all selected actions (for execution).
        /// </summary>
        public IEnumerable<TakeActionModel> GetActionsByFrame()
        {
            return Actions.OrderBy(a => a.StartFrame);
        }
    }
}
