using System.Text.Json.Serialization;

namespace Fig.Core.Input
{
    /// <summary>A pointer+modifier pattern that maps to a <see cref="TimelineGesture"/>.</summary>
    public sealed record GesturePattern
    {
        public MouseButton Button { get; init; } = MouseButton.None;
        public bool Ctrl { get; init; }
        public bool Shift { get; init; }
        public bool Alt { get; init; }
        public bool Wheel { get; init; }
        public WheelDirection WheelDir { get; init; } = WheelDirection.None;
    }

    public enum WheelDirection
    {
        None,
        Up,
        Down,
    }

    public enum MouseButton
    {
        None,
        Left,
        Middle,
        Right,
    }

    /// <summary>Serialized binding: a pattern string ("Ctrl+Wheel") -> a gesture name ("ZoomIn").</summary>
    public sealed record GestureBinding
    {
        public string Pattern { get; init; } = "";
        public string Gesture { get; init; } = "";
    }
}
