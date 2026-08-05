using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    public class SelectionState
    {
        private readonly HashSet<string> _ids = new();
        public IReadOnlyList<string> SelectedClipIds => _ids.ToList();

        public string? ActiveTrackId { get; set; }

        public bool IsSelected(string clipId) => _ids.Contains(clipId);

        public int Count => _ids.Count;

        public void Select(string clipId)
        {
            _ids.Add(clipId);
        }

        public void Deselect(string clipId)
        {
            _ids.Remove(clipId);
        }

        public void SelectOnly(string clipId)
        {
            _ids.Clear();
            _ids.Add(clipId);
        }

        public void Clear()
        {
            _ids.Clear();
            ActiveTrackId = null;
        }
    }
}
