using System;

namespace Fig.Core.Input
{
    public static class GesturePatternParser
    {
        /// <summary>
        /// Parses a pattern string like "Ctrl+Wheel", "Middle+Move", "Left+Move" into a GesturePattern.
        /// Grammar: [+modifiers]* + pointer (+ movement is implied by "Move" segment for pointer gestures).
        /// </summary>
        public static GesturePattern Parse(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return new GesturePattern();

            var parts = pattern.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var gp = new GesturePattern();

            foreach (var raw in parts)
            {
                var part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": gp = gp with { Ctrl = true }; break;
                    case "shift": gp = gp with { Shift = true }; break;
                    case "alt": gp = gp with { Alt = true }; break;
                    case "wheel": gp = gp with { Wheel = true }; break;
                    case "wheelup": gp = gp with { Wheel = true, WheelDir = WheelDirection.Up }; break;
                    case "wheeldown": gp = gp with { Wheel = true, WheelDir = WheelDirection.Down }; break;
                    case "left": gp = gp with { Button = MouseButton.Left }; break;
                    case "middle": gp = gp with { Button = MouseButton.Middle }; break;
                    case "right": gp = gp with { Button = MouseButton.Right }; break;
                    case "move": gp = gp with { }; break;   // move is implied; kept for readability
                    default:
                        throw new FormatException($"Unknown gesture token: '{part}'");
                }
            }

            return gp;
        }

        public static string Serialize(GesturePattern gp)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (gp.Ctrl) parts.Add("Ctrl");
            if (gp.Shift) parts.Add("Shift");
            if (gp.Alt) parts.Add("Alt");
            if (gp.Wheel)
            {
                parts.Add(gp.WheelDir switch
                {
                    WheelDirection.Up => "WheelUp",
                    WheelDirection.Down => "WheelDown",
                    _ => "Wheel",
                });
            }
            if (gp.Button == MouseButton.Left) parts.Add("Left");
            if (gp.Button == MouseButton.Middle) parts.Add("Middle");
            if (gp.Button == MouseButton.Right) parts.Add("Right");
            if (parts.Count == 0) parts.Add("None");
            return string.Join("+", parts);
        }
    }
}
