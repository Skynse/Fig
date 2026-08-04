using System;
using System.Collections.Generic;

namespace Fig.Core.Timeline
{
    public class SelectionState
    {
        public readonly List<String> _ids = new();
        public IReadOnlyList<string> SelectedClipIds => _ids;

        public string? ActiveTrackId { get; set; }

        public void Clear()
        {
            _ids.Clear();
        }

        public void Select(string clipId)
        {
            _ids.Add(clipId);
        }
    }
}
