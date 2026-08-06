using System;
using System.Collections.Generic;

namespace Fig.Core.Media
{
    /// <summary>
    /// Small LRU cache of preview BGRA frames keyed by source path + quantized time.
    /// Makes scrubbing long clips hit recently decoded frames instead of re-seeking.
    ///
    /// The cache OWNS a pooled copy of every stored frame's pixels: <see cref="Put"/> copies
    /// into a cache-owned pooled buffer and that buffer is released back to the pool on
    /// eviction/clear. This keeps cached frames fully decoupled from the caller's mutable
    /// scratch (e.g. a <see cref="VideoFrameSource"/>'s reused output buffer), so a later
    /// decode can never corrupt a cached frame.
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

            // Copy into a cache-owned pooled buffer so the caller's mutable scratch (and any
            // pooled effect buffer) can be reused without corrupting the cached frame. The
            // pool buffer is released when this entry is evicted.
            var copy = FramePool.Rent(frame.Pixels.Length);
            Buffer.BlockCopy(frame.Pixels, 0, copy, 0, frame.Pixels.Length);
            var owned = new DecodedFrame { Width = frame.Width, Height = frame.Height, Pixels = copy };

            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _lru.Remove(existing.Node);
                    _lru.AddFirst(existing.Node);
                    _map[key] = (existing.Node, owned);
                    FramePool.Return(existing.Frame.Pixels);
                    return;
                }

                var node = _lru.AddFirst(key);
                _map[key] = (node, owned);
                while (_map.Count > _capacity && _lru.Last is not null)
                {
                    var evict = _lru.Last.Value;
                    _lru.RemoveLast();
                    if (_map.Remove(evict, out var evicted))
                        FramePool.Return(evicted.Frame.Pixels);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                foreach (var entry in _map.Values)
                    FramePool.Return(entry.Frame.Pixels);
                _map.Clear();
                _lru.Clear();
            }
        }
    }
}
