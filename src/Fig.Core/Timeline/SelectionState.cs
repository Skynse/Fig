using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    public class SelectionState
    {
        private readonly HashSet<string> _ids = new();
        private string? _activeTrackId;

        public IReadOnlyList<string> SelectedClipIds => _ids.ToList();

        public string? ActiveTrackId
        {
            get => _activeTrackId;
            set
            {
                if (_activeTrackId == value)
                    return;
                _activeTrackId = value;
                Changed?.Invoke();
            }
        }

        /// <summary>Raised after any selection mutation (clips or active track).</summary>
        public event Action? Changed;

        public bool IsSelected(string clipId) => _ids.Contains(clipId);

        public int Count => _ids.Count;

        public void Select(string clipId)
        {
            if (!_ids.Add(clipId))
                return;
            Changed?.Invoke();
        }

        public void Deselect(string clipId)
        {
            if (!_ids.Remove(clipId))
                return;
            Changed?.Invoke();
        }

        public void SelectOnly(string clipId)
        {
            if (_ids.Count == 1 && _ids.Contains(clipId) && _activeTrackId is null)
                return;
            _ids.Clear();
            _ids.Add(clipId);
            _activeTrackId = null;
            Changed?.Invoke();
        }

        /// <summary>
        /// Replaces the selection with <paramref name="clipIds"/> in one notification
        /// (clears the active track). Used after split so the right halves stay selected.
        /// </summary>
        public void SelectClips(IEnumerable<string> clipIds)
        {
            var next = new HashSet<string>(clipIds.Where(id => !string.IsNullOrEmpty(id)));
            if (next.SetEquals(_ids) && _activeTrackId is null)
                return;
            _ids.Clear();
            foreach (var id in next)
                _ids.Add(id);
            _activeTrackId = null;
            Changed?.Invoke();
        }

        public void Clear()
        {
            if (_ids.Count == 0 && _activeTrackId is null)
                return;
            _ids.Clear();
            _activeTrackId = null;
            Changed?.Invoke();
        }
    }
}
