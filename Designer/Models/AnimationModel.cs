// Designer/Models/AnimationModel.cs
using System.Collections.ObjectModel;
using System.Linq;

namespace DaroDesigner.Models
{
    public class AnimationModel : ViewModelBase
    {
        private string _name = "Animation";
        private int _lengthFrames = 250;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public int LengthFrames
        {
            get => _lengthFrames;
            set => SetProperty(ref _lengthFrames, value > 0 ? value : 1);
        }

        public ObservableCollection<LayerModel> Layers { get; } = new ObservableCollection<LayerModel>();

        #region Serialization

        public AnimationData ToSerializable()
        {
            return new AnimationData
            {
                Name = Name,
                LengthFrames = LengthFrames,
                Layers = Layers.Select(l => l.ToSerializable()).ToList()
            };
        }

        public static AnimationModel FromSerializable(AnimationData data)
        {
            var anim = new AnimationModel
            {
                Name = string.IsNullOrWhiteSpace(data.Name) ? "Animation" : data.Name,
                LengthFrames = data.LengthFrames > 0 ? data.LengthFrames : AppConstants.DefaultAnimationLength
            };

            if (data.Layers != null)
            {
                foreach (var layerData in data.Layers)
                {
                    anim.Layers.Add(LayerModel.FromSerializable(layerData));
                }
            }

            return anim;
        }

        #endregion
    }
}