namespace Fig.Core.Input
{
    /// <summary>
    /// A named, parameterized user action on the timeline (zoom, pan, move-clip, ...).
    /// The view does not decide gestures itself; it asks the registry what a gesture
    /// maps to and executes that action.
    /// </summary>
    public enum TimelineGesture
    {
        None,
        ZoomIn,
        ZoomOut,
        ScrollHorizontal,
        ScrollVertical,
        Pan,
        MoveClip,
        ResizeClipStart,
        ResizeClipEnd,
        DragPlayhead,
    }
}
