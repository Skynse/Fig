using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>One selected layer as the gizmo draws it: where it is, and its size before scaling.</summary>
public sealed record GizmoItem(string ClipId, LayerTransform Layout, LayerSize Size, bool IsPrimary);

/// <summary>
/// The gizmo over the preview for drawn layers (text, images): picks a layer by clicking it, and moves, scales
/// and turns every selected layer together. Each drag is one undo step.
/// </summary>
public sealed partial class LayerGizmoViewModel : ViewModelBase, Fig.App.Controls.IPreviewOverlayModel
{
    /// <summary>Layers are measured at this frame height; sizes come back as shares of it, so any height would do.</summary>
    private const int MeasureHeight = 1080;

    /// <summary>How close (in frame units) the centre must come to the middle of the frame to snap onto it.</summary>
    private const double SnapDistance = 0.012;

    private readonly PropertiesViewModel _host;
    private readonly EditorViewModel _editor;
    private Dictionary<string, LayerTransform> _start = new();

    [ObservableProperty] private IReadOnlyList<GizmoItem> _items = Array.Empty<GizmoItem>();
    [ObservableProperty] private double _frameAspect = 16.0 / 9.0;

    /// <summary>The primary layer's centre is on the frame's vertical / horizontal middle line (draw a guide).</summary>
    [ObservableProperty] private bool _snappedX;
    [ObservableProperty] private bool _snappedY;

    public LayerGizmoViewModel(PropertiesViewModel host, EditorViewModel editor)
    {
        _host = host;
        _editor = editor;
    }

    /// <summary>The gizmo is there whenever preview controls are on: with nothing selected it still picks layers.</summary>
    public bool ShowOverlay => _host.ShowPreviewControls;

    public void NotifyControlsVisibilityChanged() => OnPropertyChanged(nameof(ShowOverlay));

    public GizmoItem? Primary => Items.LastOrDefault();

    /// <summary>Re-reads the selected layers showing at the playhead (after any edit, selection change or seek).</summary>
    public void Refresh()
    {
        FrameAspect = _editor.Preview.CanvasAspect;
        var t = _editor.PlayheadTimeSec;
        var layers = _editor.Editor.Selected.OfType<Clip>()
            .Where(c => c is ILayerClip && c.Enabled && t >= c.StartSec && t < c.StartSec + c.DurSec)
            .ToList();
        var items = new List<GizmoItem>(layers.Count);
        for (var i = 0; i < layers.Count; i++)
            if (Measure(layers[i]) is { } size)
                items.Add(new GizmoItem(layers[i].Id, ClipProperties.LayoutAt(layers[i], t - layers[i].StartSec), size, i == layers.Count - 1));
        if (!items.SequenceEqual(Items))
        {
            Items = items;
            OnPropertyChanged(nameof(Primary));
        }
    }

    private LayerSize? Measure(Clip clip)
        => _editor.Layers.Measure(clip, (int)Math.Round(MeasureHeight * FrameAspect), MeasureHeight,
            _editor.PlayheadTimeSec - clip.StartSec);

    /// <summary>The layer showing under a frame point, topmost first: a selected one as it is, any other measured afresh.</summary>
    public GizmoItem? ItemAt(double x, double y)
    {
        if (Items.LastOrDefault(item => LayerGeometry.Contains(item.Layout, item.Size, x, y, FrameAspect)) is { } selected)
            return selected;
        return _editor.Editor.LayerAt(_editor.PlayheadTimeSec, x, y, FrameAspect, Measure) is { } clip && Measure(clip) is { } size
            ? new GizmoItem(clip.Id, ClipProperties.LayoutAt(clip, _editor.PlayheadTimeSec - clip.StartSec), size, IsPrimary: false)
            : null;
    }

    /// <summary>
    /// A press on the picture at a frame point. On a selected layer it keeps the selection; on another layer it
    /// selects that one (<paramref name="additive"/>: adds or removes it). True when a layer is now under the pointer to drag.
    /// </summary>
    public bool Pick(double x, double y, bool additive)
    {
        if (!additive && Items.Any(item => LayerGeometry.Contains(item.Layout, item.Size, x, y, FrameAspect)))
            return true;
        if (_editor.Editor.LayerAt(_editor.PlayheadTimeSec, x, y, FrameAspect, Measure) is not { } clip)
            return false;
        var key = clip.Key;
        if (additive)
            _editor.Editor.Selection.Toggle(key);
        else
            _editor.Editor.Selection.Replace(key);
        Refresh();
        return Items.Any(item => item.ClipId == clip.Id);
    }

    /// <summary>Starts a drag: remembers where every selected layer was, and opens a new undo step.</summary>
    public void Begin()
    {
        _start = Items.ToDictionary(item => item.ClipId, item => item.Layout);
        // moving a caption moves every caption on its track, each from where it is
        foreach (var id in _editor.StyleTargets(_start.Keys.ToList()))
            if (!_start.ContainsKey(id) && _editor.Editor.FindClipForView(id) is ILayerClip and Clip clip)
                _start[id] = ClipProperties.LayoutAt(clip, Math.Clamp(_editor.PlayheadTimeSec - clip.StartSec, 0, clip.DurSec));
        _editor.Editor.History.BreakCoalescing();
    }

    public void End() => SnappedX = SnappedY = false;

    /// <summary>Moves every selected layer by the drag so far (frame units); the primary's centre snaps to the middle.</summary>
    public void Move(double dx, double dy, bool snap)
    {
        if (Primary is not { } primary || !_start.TryGetValue(primary.ClipId, out var anchor))
            return;
        var (x, y) = (anchor.CenterX + dx, anchor.CenterY + dy);
        SnappedX = snap && Math.Abs(x - 0.5) < SnapDistance;
        SnappedY = snap && Math.Abs(y - 0.5) < SnapDistance;
        if (SnappedX) dx = 0.5 - anchor.CenterX;
        if (SnappedY) dy = 0.5 - anchor.CenterY;
        Apply(l => l with { CenterX = l.CenterX + dx, CenterY = l.CenterY + dy });
    }

    /// <summary>Scales every selected layer by <paramref name="factor"/> of its size when the drag began.</summary>
    public void Scale(double factor)
        => Apply(l => l with { Scale = Math.Clamp(l.Scale * factor, 0.05, 20) });

    /// <summary>Turns every selected layer by <paramref name="degrees"/> from where it was when the drag began.</summary>
    public void Rotate(double degrees)
        => Apply(l => l with { Rotation = Normalize(l.Rotation + degrees) });

    /// <summary>The primary layer's turn when the drag began (the gizmo snaps the result, not the change).</summary>
    public double StartRotation => Primary is { } p && _start.TryGetValue(p.ClipId, out var l) ? l.Rotation : 0;

    private void Apply(Func<LayerTransform, LayerTransform> change)
    {
        if (_start.Count == 0)
            return;
        _editor.Editor.SetLayouts(_start.ToDictionary(kv => kv.Key, kv => change(kv.Value)), _editor.PlayheadTimeSec);
    }

    private static double Normalize(double degrees)
    {
        degrees %= 360;
        return degrees > 180 ? degrees - 360 : degrees <= -180 ? degrees + 360 : degrees;
    }
}
