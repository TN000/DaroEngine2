// Designer/Models/PlaylistModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DaroDesigner.Models
{
    /// <summary>
    /// Status of a playlist item in the playout workflow.
    /// </summary>
    public enum PlaylistItemStatus
    {
        /// <summary>Item is ready to be played.</summary>
        Ready,
        /// <summary>Item is currently cued (prepared for immediate playback).</summary>
        Cued,
        /// <summary>Item is currently on air (playing).</summary>
        OnAir,
        /// <summary>Item has finished playing.</summary>
        Done
    }

    /// <summary>
    /// Represents a single filled-in template instance in the playlist.
    /// Contains the template definition and the user-provided data values.
    /// </summary>
    public class PlaylistItemModel : ViewModelBase
    {
        private string _id;
        private string _name;
        private string _templateId;
        private string _templateName;
        private string _templateFilePath;
        private string _linkedScenePath;
        private PlaylistItemStatus _status;
        private bool _isSelected;
        private int _order;
        private DateTime _createdAt;
        private DateTime? _lastPlayedAt;

        // Filled data: ElementId -> Value
        private Dictionary<string, string> _filledData = new Dictionary<string, string>();

        // Takes from the template
        private ObservableCollection<TemplateTakeModel> _takes = new ObservableCollection<TemplateTakeModel>();

        // Runtime reference to loaded project (not serialized)
        [System.Text.Json.Serialization.JsonIgnore]
        public ProjectModel LoadedProject { get; set; }

        /// <summary>
        /// Takes from the source template, available for execution.
        /// </summary>
        public ObservableCollection<TemplateTakeModel> Takes
        {
            get => _takes;
            set => SetProperty(ref _takes, value);
        }

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        /// <summary>
        /// Display name for this playlist item (can be auto-generated from filled data).
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string TemplateId
        {
            get => _templateId;
            set => SetProperty(ref _templateId, value);
        }

        public string TemplateName
        {
            get => _templateName;
            set => SetProperty(ref _templateName, value);
        }

        public string TemplateFilePath
        {
            get => _templateFilePath;
            set => SetProperty(ref _templateFilePath, value);
        }

        public string LinkedScenePath
        {
            get => _linkedScenePath;
            set => SetProperty(ref _linkedScenePath, value);
        }

        public PlaylistItemStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public int Order
        {
            get => _order;
            set => SetProperty(ref _order, value);
        }

        public DateTime CreatedAt
        {
            get => _createdAt;
            set => SetProperty(ref _createdAt, value);
        }

        public DateTime? LastPlayedAt
        {
            get => _lastPlayedAt;
            set => SetProperty(ref _lastPlayedAt, value);
        }

        /// <summary>
        /// Dictionary mapping template element IDs to their filled-in values.
        /// </summary>
        public Dictionary<string, string> FilledData
        {
            get => _filledData;
            set => SetProperty(ref _filledData, value);
        }

        public PlaylistItemModel()
        {
            Id = Guid.NewGuid().ToString();
            Status = PlaylistItemStatus.Ready;
            CreatedAt = DateTime.Now;
            FilledData = new Dictionary<string, string>();
        }

        /// <summary>
        /// Creates a playlist item from a template with filled data.
        /// </summary>
        public static PlaylistItemModel FromTemplate(
            TemplateModel template,
            Dictionary<string, string> filledData,
            string customName = null)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));

            var item = new PlaylistItemModel
            {
                TemplateId = template.Id,
                TemplateName = template.Name,
                TemplateFilePath = template.FolderPath,
                LinkedScenePath = template.LinkedScenePath,
                FilledData = new Dictionary<string, string>(filledData ?? new Dictionary<string, string>())
            };

            // Copy Takes from template
            if (template.Takes != null)
            {
                foreach (var take in template.Takes)
                {
                    item.Takes.Add(take);
                }
            }

            // Auto-generate name from first text field or use template name
            if (string.IsNullOrEmpty(customName))
            {
                var firstValue = filledData.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                item.Name = !string.IsNullOrEmpty(firstValue)
                    ? $"{template.Name}: {TruncateString(firstValue, 30)}"
                    : template.Name;
            }
            else
            {
                item.Name = customName;
            }

            return item;
        }

        private static string TruncateString(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }

        /// <summary>
        /// Gets the filled value for a specific element, or empty string if not set.
        /// </summary>
        public string GetFilledValue(string elementId)
        {
            return FilledData.TryGetValue(elementId, out var value) ? value : string.Empty;
        }

        /// <summary>
        /// Creates a deep copy of this playlist item.
        /// </summary>
        public PlaylistItemModel Clone()
        {
            var clone = new PlaylistItemModel
            {
                Name = Name + " (Copy)",
                TemplateId = TemplateId,
                TemplateName = TemplateName,
                TemplateFilePath = TemplateFilePath,
                LinkedScenePath = LinkedScenePath,
                Status = PlaylistItemStatus.Ready,
                FilledData = new Dictionary<string, string>(FilledData)
            };

            // Copy Takes from original
            foreach (var take in Takes)
            {
                clone.Takes.Add(take);
            }

            return clone;
        }
    }

    /// <summary>
    /// Manages the playout playlist - a list of filled template instances ready for playback.
    /// </summary>
    public class PlaylistModel : ViewModelBase
    {
        private string _id;
        private string _name;
        private ObservableCollection<PlaylistItemModel> _items;
        private PlaylistItemModel _currentItem;
        private PlaylistItemModel _nextItem;
        private bool _autoAdvance;
        private bool _loop;

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

        public ObservableCollection<PlaylistItemModel> Items
        {
            get => _items;
            set => SetProperty(ref _items, value);
        }

        /// <summary>
        /// Currently playing (OnAir) item, or null if nothing is playing.
        /// </summary>
        public PlaylistItemModel CurrentItem
        {
            get => _currentItem;
            set => SetProperty(ref _currentItem, value);
        }

        /// <summary>
        /// Next item to play (cued), or null.
        /// </summary>
        public PlaylistItemModel NextItem
        {
            get => _nextItem;
            set => SetProperty(ref _nextItem, value);
        }

        /// <summary>
        /// If true, automatically advances to next item when current finishes.
        /// </summary>
        public bool AutoAdvance
        {
            get => _autoAdvance;
            set => SetProperty(ref _autoAdvance, value);
        }

        /// <summary>
        /// If true, loops back to first item after last item finishes.
        /// </summary>
        public bool Loop
        {
            get => _loop;
            set => SetProperty(ref _loop, value);
        }

        public PlaylistModel()
        {
            Id = Guid.NewGuid().ToString();
            Name = "Playlist";
            Items = new ObservableCollection<PlaylistItemModel>();
            AutoAdvance = false;
            Loop = false;
        }

        /// <summary>
        /// Adds a new item to the playlist.
        /// </summary>
        public void AddItem(PlaylistItemModel item)
        {
            item.Order = Items.Count;
            Items.Add(item);
            UpdateNextItem();
        }

        /// <summary>
        /// Removes an item from the playlist.
        /// </summary>
        public void RemoveItem(PlaylistItemModel item)
        {
            // Clear references if the removed item is current or next
            if (CurrentItem == item)
            {
                CurrentItem = null;
            }
            if (NextItem == item)
            {
                NextItem = null;
            }

            Items.Remove(item);
            ReorderItems();
            UpdateNextItem();
        }

        /// <summary>
        /// Moves an item up in the playlist order.
        /// </summary>
        public void MoveItemUp(PlaylistItemModel item)
        {
            int index = Items.IndexOf(item);
            if (index > 0)
            {
                Items.Move(index, index - 1);
                ReorderItems();
            }
        }

        /// <summary>
        /// Moves an item down in the playlist order.
        /// </summary>
        public void MoveItemDown(PlaylistItemModel item)
        {
            int index = Items.IndexOf(item);
            if (index >= 0 && index < Items.Count - 1)
            {
                Items.Move(index, index + 1);
                ReorderItems();
            }
        }

        /// <summary>
        /// Cues an item for playback (marks it as next).
        /// </summary>
        public void CueItem(PlaylistItemModel item)
        {
            // Clear previous cued item
            if (NextItem != null && NextItem != item)
            {
                NextItem.Status = PlaylistItemStatus.Ready;
            }

            item.Status = PlaylistItemStatus.Cued;
            NextItem = item;
        }

        /// <summary>
        /// Takes the cued item on air.
        /// </summary>
        public void TakeOnAir()
        {
            if (NextItem == null) return;

            // Mark current as done
            if (CurrentItem != null)
            {
                CurrentItem.Status = PlaylistItemStatus.Done;
            }

            // Take next on air
            CurrentItem = NextItem;
            CurrentItem.Status = PlaylistItemStatus.OnAir;
            CurrentItem.LastPlayedAt = DateTime.Now;

            // Auto-cue next item if auto-advance is enabled
            if (AutoAdvance)
            {
                var currentIndex = Items.IndexOf(CurrentItem);
                var nextIndex = currentIndex + 1;
                if (currentIndex >= 0 && nextIndex < Items.Count)
                {
                    CueItem(Items[nextIndex]);
                }
                else if (Loop && Items.Count > 0)
                {
                    CueItem(Items[0]);
                }
                else
                {
                    NextItem = null;
                }
            }
            else
            {
                NextItem = null;
            }
        }

        /// <summary>
        /// Clears the current on-air item (fade out / cut).
        /// </summary>
        public void ClearOnAir()
        {
            if (CurrentItem != null)
            {
                CurrentItem.Status = PlaylistItemStatus.Done;
                CurrentItem = null;
            }
        }

        /// <summary>
        /// Resets all items to Ready status.
        /// </summary>
        public void ResetAll()
        {
            foreach (var item in Items)
            {
                item.Status = PlaylistItemStatus.Ready;
            }
            CurrentItem = null;
            NextItem = null;
        }

        /// <summary>
        /// Clears all items from the playlist.
        /// </summary>
        public void Clear()
        {
            Items.Clear();
            CurrentItem = null;
            NextItem = null;
        }

        private void ReorderItems()
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Order = i;
            }
        }

        private void UpdateNextItem()
        {
            // Intentionally does not auto-cue. Called after item removal to maintain state.
            // Auto-cueing is handled explicitly by CueItem() and TakeOnAir().
        }
    }
}
