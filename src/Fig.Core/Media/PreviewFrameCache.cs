using System;
using System.Collections.Generic;

namespace Fig.Core.Media
{
    /// <summary>
    /// Small LRU cache of preview BGRA frames keyed by source path + quantized time.
    /// Makes scrubbing long clips hit recently decoded frames instead of re-seeking.
    /// </summary>
    public sealed class PreviewFrameCache
    {
        private readonly int _capacity;
        private readonly double _bucketSec;
        private readonly LinkedList<string> _lru = new();
        private readonly Dictionary<string, (LinkedListNode<string> Node, DecodedFrame Frame)> _map = new();
        private readonly object _gate = new();

        public PreviewFrameCache(int capacity = 48, double bucketSec = 1.0 / 30.0)
        {
            _capacity = Math.Max(1, capacity);
            _bucketSec = Math.Max(1.0 / 120.0, bucketSec);
        }

        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        public static string MakeKey(string path, double timeSec, int width, int height, double bucketSec)
        {
            var bucket = (long)Math.Round(Math.Max(0, timeSec) / bucketSec);
            return $"{path}|{width}x{height}|{bucket}";
        }

        public bool TryGet(string path, double timeSec, int width, int height, out DecodedFrame? frame)
        {
            var key = MakeKey(path, timeSec, width, height, _bucketSec);
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var entry))
                {
                    _lru.Remove(entry.Node);
                    _lru.AddFirst(entry.Node);
                    frame = entry.Frame;
                    return true;
                }
            }
            frame = null;
            return false;
        }

        public void Put(string path, double timeSec, int width, int height, DecodedFrame frame)
        {
            if (frame.Pixels is null || frame.Pixels.Length == 0)
                return;
            var key = MakeKey(path, timeSec, width, height, _bucketSec);
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _lru.Remove(existing.Node);
                    _lru.AddFirst(existing.Node);
                    _map[key] = (existing.Node, frame);
                    return;
                }

                var node = _lru.AddFirst(key);
                _map[key] = (node, frame);
                while (_map.Count > _capacity && _lru.Last is not null)
                {
                    var evict = _lru.Last.Value;
                    _lru.RemoveLast();
                    _map.Remove(evict);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
                _lru.Clear();
            }
        }
    }
}
