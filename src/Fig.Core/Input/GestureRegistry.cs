using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Fig.Core.Input
{
    public class GestureRegistry
    {
        private readonly Dictionary<GesturePattern, TimelineGesture> _bindings = new();
        public string? SourcePath { get; }

        public GestureRegistry()
        {
            RegisterDefaults();
        }

        public GestureRegistry(string configPath)
            : this()
        {
            SourcePath = configPath;
            if (File.Exists(configPath))
                Load(configPath);
        }

        public void Load(string path)
        {
            var bindings = JsonSerializer.Deserialize<List<GestureBinding>>(File.ReadAllText(path));
            if (bindings is null)
                return;

            // Overlay user bindings on top of defaults so newly added defaults still apply.
            foreach (var b in bindings)
            {
                if (string.IsNullOrEmpty(b.Pattern))
                    continue;
                var pattern = GesturePatternParser.Parse(b.Pattern);
                var gesture = Enum.TryParse<TimelineGesture>(b.Gesture, ignoreCase: true, out var g)
                    ? g
                    : TimelineGesture.None;
                _bindings[pattern] = gesture;
            }

            MigrateNavigationDefaults();
        }

        /// <summary>
        /// Plain wheel scrolls tracks; middle-drag pans time. Older configs mapped wheel to
        /// horizontal time scroll — rewrite those so navigation stays consistent.
        /// </summary>
        private void MigrateNavigationDefaults()
        {
            foreach (var dir in new[] { WheelDirection.Up, WheelDirection.Down })
            {
                var pattern = new GesturePattern { Wheel = true, WheelDir = dir };
                if (_bindings.TryGetValue(pattern, out var gesture)
                    && gesture == TimelineGesture.ScrollHorizontal)
                {
                    _bindings[pattern] = TimelineGesture.ScrollVertical;
                }
            }

            _bindings[new GesturePattern { Button = MouseButton.Middle }] = TimelineGesture.Pan;
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var bindings = _bindings
                .Select(kv => new GestureBinding
                {
                    Pattern = GesturePatternParser.Serialize(kv.Key),
                    Gesture = kv.Value.ToString(),
                })
                .ToList();

            File.WriteAllText(path, JsonSerializer.Serialize(bindings, new JsonSerializerOptions { WriteIndented = true }));
        }

        public TimelineGesture Resolve(GesturePattern pattern)
        {
            return _bindings.TryGetValue(pattern, out var gesture) ? gesture : TimelineGesture.None;
        }

        public void Bind(GesturePattern pattern, TimelineGesture gesture)
        {
            _bindings[pattern] = gesture;
        }

        private void RegisterDefaults()
        {
            Bind(GesturePatternParser.Parse("Ctrl+WheelUp"), TimelineGesture.ZoomIn);
            Bind(GesturePatternParser.Parse("Ctrl+WheelDown"), TimelineGesture.ZoomOut);
            Bind(GesturePatternParser.Parse("Shift+WheelUp"), TimelineGesture.ScrollHorizontal);
            Bind(GesturePatternParser.Parse("Shift+WheelDown"), TimelineGesture.ScrollHorizontal);
            Bind(GesturePatternParser.Parse("Alt+WheelUp"), TimelineGesture.TallerTracks);
            Bind(GesturePatternParser.Parse("Alt+WheelDown"), TimelineGesture.ShorterTracks);
            // Plain wheel → vertical track scroll (ScrollViewer). Middle-drag → pan time.
            Bind(GesturePatternParser.Parse("WheelUp"), TimelineGesture.ScrollVertical);
            Bind(GesturePatternParser.Parse("WheelDown"), TimelineGesture.ScrollVertical);
            Bind(GesturePatternParser.Parse("Middle"), TimelineGesture.Pan);
            Bind(GesturePatternParser.Parse("Middle+Move"), TimelineGesture.Pan);
            Bind(GesturePatternParser.Parse("Left+Move"), TimelineGesture.MoveClip);
        }
    }
}
