using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views;

/// <summary>A wire being dragged from a port that has not landed yet.</summary>
public sealed record PendingWire(string NodeId, string Port, bool FromOutput, GraphPortType Type);

/// <summary>
/// The node canvas: pan/zoom surface, nodes with ports and pinnable parameters, wires, marquee selection,
/// and the right-click menus. All edits go through <see cref="GraphEditorViewModel"/>.
/// </summary>
public sealed class GraphCanvas : Control
{
    // ---- geometry (world units) --------------------------------------------------------------
    private const double NodeW = 176;
    private const double HeaderH = 26;
    private const double RowH = 22;
    private const double PadTop = 8;
    private const double ParamRowH = 22;
    private const double ParamGap = 6;
    private const double PortRadius = 5;
    private const double PortHit = 9;

    public static readonly StyledProperty<GraphEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<GraphCanvas, GraphEditorViewModel?>(nameof(Editor));

    public GraphEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>Raised when the user wants a node picker: at a screen point, the world point to place it, and any wire to connect.</summary>
    public event Action<Point, Point, PendingWire?>? QuickSearchRequested;

    /// <summary>Raised with a short reason when something is refused (a wire that cannot connect).</summary>
    public event Action<string>? Refused;

    private double _zoom = 1;
    private Point _pan = new(60, 40);
    private bool _needsFrame = true;

    private enum Drag { None, Pan, Nodes, Wire, Marquee }
    private Drag _drag = Drag.None;
    private Point _dragStart;           // screen
    private Point _last;                // screen
    private bool _moved;
    private PendingWire? _wire;
    private Point _wireEnd;             // world
    private Rect _marquee;              // screen
    private bool _marqueeAdditive;
    private string? _hoverNode;
    private (string NodeId, string Port, bool Output)? _hoverPort;
    private Point _pointerWorld;
    private bool _spaceDown;

    static GraphCanvas()
    {
        AffectsRender<GraphCanvas>(EditorProperty);
        FocusableProperty.OverrideDefaultValue<GraphCanvas>(true);
    }

    public GraphCanvas()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    // ---- palette -----------------------------------------------------------------------------

    private static readonly IBrush BgBrush = new SolidColorBrush(Color.Parse("#141517"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1b1c1f")), 1);
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#232427"));
    private static readonly IBrush RowBrush = new SolidColorBrush(Color.Parse("#1b1c1f"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#f0f0f0"));
    private static readonly IBrush Text2Brush = new SolidColorBrush(Color.Parse("#c8c8c8"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#8a8a8a"));
    private static readonly IBrush FaintBrush = new SolidColorBrush(Color.Parse("#666668"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#5f9cf8"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#ff6b6b"));
    private static readonly IBrush MaskBrush = new SolidColorBrush(Color.Parse("#eab308"));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.Parse("#3a3c42")), 1);
    private static readonly IPen SelectPen = new Pen(Brushes.White, 1.6);
    private static readonly IPen ErrorPen = new Pen(DangerBrush, 1.6);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.Parse("#91baff")), 1.5);

    private static Color PortColor(GraphPortType t) => t == GraphPortType.Mask ? Color.Parse("#eab308") : Color.Parse("#5f9cf8");

    private static (Color Header, Color Edge) Palette(PrimitiveDescriptor? d)
    {
        if (d is null) return (Color.Parse("#3a1f22"), Color.Parse("#ff6b6b"));
        if (d.Id == PrimitiveCatalog.Output) return (Color.Parse("#2d674a"), Color.Parse("#589b74"));
        if (d.Outputs.Any(o => o.Type == GraphPortType.Mask) && d.Inputs.Count == 0) return (Color.Parse("#4a3f12"), Color.Parse("#eab308"));
        return d.Category switch
        {
            "Sources" => (Color.Parse("#2f7484"), Color.Parse("#55a2b2")),
            "Stock effects" => (Color.Parse("#6f5526"), Color.Parse("#c79a3d")),
            _ => (Color.Parse("#29466e"), Color.Parse("#5f9cf8")),
        };
    }

    // ---- node layout -------------------------------------------------------------------------

    private sealed class Box
    {
        public GraphNode Node = null!;
        public PrimitiveDescriptor? D;
        public Rect Rect;
        public string Title = "";
        public List<(PortDef Def, Point Pos)> In = new();
        public List<(PortDef Def, Point Pos)> Out = new();
        public List<(ParamDef Def, Rect Row, Point Pin)> Params = new();
    }

    private static Box Layout(GraphNode node)
    {
        var d = PrimitiveCatalog.Find(node.PrimitiveId);
        var box = new Box { Node = node, D = d, Title = GraphEditorViewModel.NodeTitle(node) };
        var schema = PrimitiveCatalog.ParamSchemaFor(node);
        var inCount = d?.Inputs.Count ?? 0;
        var outCount = d?.Outputs.Count ?? 0;
        var rows = Math.Max(1, Math.Max(inCount, outCount));
        var h = HeaderH + PadTop + rows * RowH + (schema.Count > 0 ? ParamGap + schema.Count * ParamRowH : 0) + 8;
        box.Rect = new Rect(node.X, node.Y, NodeW, h);
        if (d is not null)
        {
            for (var i = 0; i < d.Inputs.Count; i++)
                box.In.Add((d.Inputs[i], new Point(node.X, node.Y + HeaderH + PadTop + i * RowH + RowH / 2)));
            for (var i = 0; i < d.Outputs.Count; i++)
                box.Out.Add((d.Outputs[i], new Point(node.X + NodeW, node.Y + HeaderH + PadTop + i * RowH + RowH / 2)));
        }
        var y0 = node.Y + HeaderH + PadTop + rows * RowH + ParamGap;
        for (var i = 0; i < schema.Count; i++)
        {
            var row = new Rect(node.X + 6, y0 + i * ParamRowH, NodeW - 12, 20);
            box.Params.Add((schema[i], row, new Point(node.X + NodeW - 18, row.Y + 10)));
        }
        return box;
    }

    private IEnumerable<Box> Boxes() => Editor is null ? Enumerable.Empty<Box>() : Editor.Graph.Nodes.Select(Layout);

    // ---- coordinates -------------------------------------------------------------------------

    public Point ToWorld(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);
    public Point ToScreen(Point world) => new(world.X * _zoom + _pan.X, world.Y * _zoom + _pan.Y);

    public Point ViewCenterWorld => ToWorld(new Point(Bounds.Width / 2, Bounds.Height / 2));

    public void FrameAll()
    {
        if (Editor is null || Editor.Graph.Nodes.Count == 0 || Bounds.Width < 10 || Bounds.Height < 10)
            return;
        var boxes = Boxes().ToList();
        var left = boxes.Min(b => b.Rect.Left);
        var top = boxes.Min(b => b.Rect.Top);
        var right = boxes.Max(b => b.Rect.Right);
        var bottom = boxes.Max(b => b.Rect.Bottom);
        const double margin = 48;
        var zx = (Bounds.Width - margin * 2) / Math.Max(1, right - left);
        var zy = (Bounds.Height - margin * 2) / Math.Max(1, bottom - top);
        _zoom = Math.Clamp(Math.Min(zx, zy), 0.35, 1.2);
        _pan = new Point(
            (Bounds.Width - (right - left) * _zoom) / 2 - left * _zoom,
            (Bounds.Height - (bottom - top) * _zoom) / 2 - top * _zoom);
        _needsFrame = false;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_needsFrame)
            FrameAll();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty)
            return;
        if (change.OldValue is GraphEditorViewModel old)
            old.GraphChanged -= OnGraphChanged;
        if (change.NewValue is GraphEditorViewModel vm)
        {
            vm.GraphChanged += OnGraphChanged;
            _needsFrame = true;
            FrameAll();
        }
    }

    private void OnGraphChanged() => InvalidateVisual();

    // ---- rendering ---------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(BgBrush, null, new Rect(Bounds.Size));
        if (Editor is null)
            return;

        DrawGrid(context);
        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y)))
            DrawWorld(context);

        if (_drag == Drag.Marquee)
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 95, 156, 248)), new Pen(AccentBrush, 1), _marquee);
    }

    private void DrawWorld(DrawingContext context)
    {
        var boxes = Editor!.Graph.Nodes.Select(Layout).ToDictionary(b => b.Node.Id);

        // wires first, so nodes sit on top of them
        foreach (var edge in Editor.Graph.Edges)
        {
            if (!boxes.TryGetValue(edge.FromNodeId, out var from) || !boxes.TryGetValue(edge.ToNodeId, out var to))
                continue;
            var a = from.Out.FirstOrDefault(p => p.Def.Id == edge.FromPort);
            var b = to.In.FirstOrDefault(p => p.Def.Id == edge.ToPort);
            if (a.Def is null || b.Def is null)
                continue;
            var selected = ReferenceEquals(edge, Editor.SelectedEdge);
            var color = PortColor(a.Def.Type);
            var pen = new Pen(selected ? Brushes.White : new SolidColorBrush(color), selected ? 3 : 2);
            context.DrawGeometry(null, pen, Wire(a.Pos, b.Pos));
        }

        if (_wire is not null && boxes.TryGetValue(_wire.NodeId, out var src))
        {
            var anchor = _wire.FromOutput
                ? src.Out.FirstOrDefault(p => p.Def.Id == _wire.Port).Pos
                : src.In.FirstOrDefault(p => p.Def.Id == _wire.Port).Pos;
            var pen = new Pen(Brushes.White, 1.6, new DashStyle(new[] { 4.0, 4.0 }, 0));
            context.DrawGeometry(null, pen, _wire.FromOutput ? Wire(anchor, _wireEnd) : Wire(_wireEnd, anchor));
        }

        foreach (var box in boxes.Values)
            DrawNode(context, box);
    }

    private void DrawGrid(DrawingContext context)
    {
        var step = 48 * _zoom;
        if (step < 10)
            return;
        var ox = ((_pan.X % step) + step) % step;
        var oy = ((_pan.Y % step) + step) % step;
        for (var x = ox; x < Bounds.Width; x += step)
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, Bounds.Height));
        for (var y = oy; y < Bounds.Height; y += step)
            context.DrawLine(GridPen, new Point(0, y), new Point(Bounds.Width, y));
    }

    private static StreamGeometry Wire(Point a, Point b)
    {
        var dx = Math.Max(40, Math.Abs(b.X - a.X) * 0.5);
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(a, false);
        c.CubicBezierTo(new Point(a.X + dx, a.Y), new Point(b.X - dx, b.Y), b);
        c.EndFigure(false);
        return g;
    }

    private static FormattedText Text(string s, double size, IBrush brush, bool bold = false, double maxWidth = double.PositiveInfinity)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            size, brush)
        { MaxTextWidth = double.IsInfinity(maxWidth) ? 1000 : Math.Max(1, maxWidth), MaxTextHeight = size * 1.5, Trimming = TextTrimming.CharacterEllipsis };

    private void DrawNode(DrawingContext context, Box box)
    {
        var vm = Editor!;
        var node = box.Node;
        var selected = vm.SelectedNodeIds.Contains(node.Id);
        var error = vm.ErrorNodeIds.Contains(node.Id);
        var dim = vm.UnusedNodeIds.Contains(node.Id) || node.Bypass;
        var (header, edge) = Palette(box.D);
        var r = box.Rect;

        using var opacity = dim ? context.PushOpacity(0.5) : default;
        var border = selected ? SelectPen : error ? ErrorPen : node.Id == _hoverNode ? HoverPen : BorderPen;
        context.DrawRectangle(CardBrush, border, r, 6, 6);
        context.DrawRectangle(new SolidColorBrush(header), null, new Rect(r.X, r.Y, r.Width, HeaderH), 6, 6);
        context.DrawRectangle(new SolidColorBrush(header), null, new Rect(r.X, r.Y + HeaderH - 8, r.Width, 8));
        context.DrawRectangle(new SolidColorBrush(edge), null, new Rect(r.X, r.Y + HeaderH, r.Width, 1));
        context.DrawText(Text(box.Title, 11, TextBrush, true, r.Width - 20), new Point(r.X + 10, r.Y + 6));
        if (node.Bypass)
            context.DrawText(Text("bypassed", 9, MutedBrush), new Point(r.Right - 52, r.Y + 8));

        foreach (var (def, pos) in box.In)
        {
            DrawPort(context, node.Id, def, pos, false);
            context.DrawText(Text(def.Label, 10, Text2Brush), new Point(pos.X + 12, pos.Y - 7));
        }
        foreach (var (def, pos) in box.Out)
        {
            DrawPort(context, node.Id, def, pos, true);
            var t = Text(def.Label, 10, Text2Brush);
            context.DrawText(t, new Point(pos.X - 12 - t.Width, pos.Y - 7));
        }

        foreach (var (def, row, pin) in box.Params)
        {
            context.DrawRectangle(RowBrush, null, row, 3, 3);
            context.DrawText(Text(def.Label, 10, MutedBrush, false, row.Width - 74), new Point(row.X + 6, row.Y + 4));
            var value = Text(FormatValue(node, def), 10, TextBrush, false, 60);
            context.DrawText(value, new Point(row.Right - 26 - value.Width, row.Y + 4));
            var promoted = vm.IsPromoted(node.Id, def.Key);
            if (promoted)
                context.DrawEllipse(AccentBrush, null, pin, 4.5, 4.5);
            else
                context.DrawEllipse(null, new Pen(FaintBrush, 1.2), pin, 4.5, 4.5);
        }
    }

    private void DrawPort(DrawingContext context, string nodeId, PortDef def, Point pos, bool output)
    {
        var vm = Editor!;
        var hot = _hoverPort is { } h && h.NodeId == nodeId && h.Port == def.Id && h.Output == output;
        var wired = vm.Graph.Edges.Any(e => output ? e.FromNodeId == nodeId && e.FromPort == def.Id : e.ToNodeId == nodeId && e.ToPort == def.Id);
        var brush = new SolidColorBrush(PortColor(def.Type));
        // while a wire is being dragged, ports it could land on glow
        if (_wire is not null && _wire.FromOutput != output && _wire.Type == def.Type && _wire.NodeId != nodeId)
            context.DrawEllipse(null, new Pen(brush, 2), pos, PortRadius + 3, PortRadius + 3);
        context.DrawEllipse(wired || hot ? brush : CardBrush, new Pen(brush, 2), pos, PortRadius, PortRadius);
    }

    private static string FormatValue(GraphNode node, ParamDef def)
    {
        var value = node.Params.TryGetValue(def.Key, out var v) ? v : def.DefaultValue();
        return value.Kind switch
        {
            ParamKind.Bool => value.AsBool ? "On" : "Off",
            ParamKind.Color => "#" + (value.AsColor & 0xFFFFFF).ToString("X6"),
            ParamKind.List => value.AsChoice >= 0 && value.AsChoice < def.Choices.Count ? def.Choices[value.AsChoice] : value.AsChoice.ToString(),
            ParamKind.Int => value.AsInt.ToString(CultureInfo.InvariantCulture),
            _ => value.AsDouble.ToString("0.###", CultureInfo.InvariantCulture),
        };
    }

    // ---- hit testing -------------------------------------------------------------------------

    private enum HitKind { None, Node, InPort, OutPort, Pin, Wire }

    private readonly record struct Hit(HitKind Kind, Box? Box = null, string? Port = null, GraphEdge? Edge = null, PortDef? Def = null);

    private Hit HitTest(Point world)
    {
        if (Editor is null)
            return default;
        var boxes = Boxes().ToList();
        var radius = PortHit / Math.Max(_zoom, 0.5);
        // ports and pins sit on top of everything
        foreach (var box in Enumerable.Reverse(boxes))
        {
            foreach (var (def, pos) in box.Out)
                if (Distance(world, pos) <= radius)
                    return new Hit(HitKind.OutPort, box, def.Id, null, def);
            foreach (var (def, pos) in box.In)
                if (Distance(world, pos) <= radius)
                    return new Hit(HitKind.InPort, box, def.Id, null, def);
            foreach (var (def, _, pin) in box.Params)
                if (Distance(world, pin) <= radius)
                    return new Hit(HitKind.Pin, box, def.Key);
        }
        foreach (var box in Enumerable.Reverse(boxes))
            if (box.Rect.Contains(world))
                return new Hit(HitKind.Node, box);

        var byId = boxes.ToDictionary(b => b.Node.Id);
        var tolerance = 6 / _zoom;
        foreach (var edge in Editor.Graph.Edges)
        {
            if (!byId.TryGetValue(edge.FromNodeId, out var from) || !byId.TryGetValue(edge.ToNodeId, out var to))
                continue;
            var a = from.Out.FirstOrDefault(p => p.Def.Id == edge.FromPort);
            var b = to.In.FirstOrDefault(p => p.Def.Id == edge.ToPort);
            if (a.Def is null || b.Def is null)
                continue;
            if (DistanceToCurve(world, a.Pos, b.Pos) <= tolerance)
                return new Hit(HitKind.Wire, null, null, edge);
        }
        return default;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double DistanceToCurve(Point p, Point a, Point b)
    {

        // optimization algorithm to find shortest distance between pointA and pointB with cubic bezier
        var dx = Math.Max(40, Math.Abs(b.X - a.X) * 0.5);
        var c1 = new Point(a.X + dx, a.Y);
        var c2 = new Point(b.X - dx, b.Y);
        var best = double.MaxValue;
        var prev = a;
        for (var i = 1; i <= 24; i++)
        {
            var t = i / 24.0;
            var u = 1 - t;
            var q = new Point(
                u * u * u * a.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * b.X,
                u * u * u * a.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * b.Y);
            best = Math.Min(best, DistanceToSegment(p, prev, q));
            prev = q;
        }
        return best;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        var len2 = vx * vx + vy * vy;
        var t = len2 <= 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1);
        return Distance(p, new Point(a.X + vx * t, a.Y + vy * t));
    }

    // ---- test hooks --------------------------------------------------------------------------

    // ---- pointer -----------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is null)
            return;
        Focus();
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        var props = e.GetCurrentPoint(this).Properties;
        _dragStart = _last = screen;
        _moved = false;

        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && _spaceDown))
        {
            _drag = Drag.Pan;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (props.IsRightButtonPressed)
        {
            OpenContextMenu(screen, world);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;

        var hit = HitTest(world);
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (hit.Kind)
        {
            case HitKind.OutPort:
                _wire = new PendingWire(hit.Box!.Node.Id, hit.Port!, true, hit.Def!.Type);
                _wireEnd = world;
                _drag = Drag.Wire;
                break;
            case HitKind.InPort:
            {
                // pulling on a connected input picks its wire up; an empty one starts a wire backwards
                var existing = Editor.Graph.Edges.FirstOrDefault(x => x.ToNodeId == hit.Box!.Node.Id && x.ToPort == hit.Port);
                if (existing is not null && Editor.Graph.FindNode(existing.FromNodeId) is not null)
                {
                    var type = hit.Def!.Type;
                    Editor.Disconnect(existing);
                    _wire = new PendingWire(existing.FromNodeId, existing.FromPort, true, type);
                }
                else
                    _wire = new PendingWire(hit.Box!.Node.Id, hit.Port!, false, hit.Def!.Type);
                _wireEnd = world;
                _drag = Drag.Wire;
                break;
            }
            case HitKind.Pin:
                Editor.TogglePromote(hit.Box!.Node.Id, hit.Port!);
                e.Handled = true;
                return;
            case HitKind.Node:
            {
                var id = hit.Box!.Node.Id;
                if (additive)
                    Editor.ToggleSelect(id);
                else if (!Editor.SelectedNodeIds.Contains(id))
                    Editor.Select(new[] { id });
                _drag = Drag.Nodes;
                break;
            }
            case HitKind.Wire:
                Editor.SelectEdge(hit.Edge);
                e.Handled = true;
                return;
            default:
                _drag = Drag.Marquee;
                _marqueeAdditive = additive;
                if (!additive)
                    Editor.ClearSelection();
                _marquee = new Rect(screen, new Size(0, 0));
                break;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Editor is null)
            return;
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        _pointerWorld = world;

        if (_drag == Drag.None)
        {
            var hit = HitTest(world);
            var node = hit.Box?.Node.Id;
            (string, string, bool)? port = hit.Kind is HitKind.InPort or HitKind.OutPort
                ? (hit.Box!.Node.Id, hit.Port!, hit.Kind == HitKind.OutPort) : null;
            if (node != _hoverNode || port != _hoverPort)
            {
                _hoverNode = node;
                _hoverPort = port;
                InvalidateVisual();
            }
            Cursor = new Cursor(hit.Kind is HitKind.Pin or HitKind.InPort or HitKind.OutPort ? StandardCursorType.Hand : StandardCursorType.Arrow);
            return;
        }

        var dx = screen.X - _last.X;
        var dy = screen.Y - _last.Y;
        _last = screen;
        if (Distance(screen, _dragStart) > 3)
            _moved = true;

        switch (_drag)
        {
            case Drag.Pan:
                _pan = new Point(_pan.X + dx, _pan.Y + dy);
                break;
            case Drag.Nodes:
                if (_moved)
                {
                    if (!_dragBegun)
                    {
                        Editor.BeginDrag();
                        _dragBegun = true;
                    }
                    Editor.MoveNodes(Editor.SelectedNodeIds.ToList(), dx / _zoom, dy / _zoom);
                }
                break;
            case Drag.Wire:
                _wireEnd = world;
                break;
            case Drag.Marquee:
                _marquee = new Rect(
                    new Point(Math.Min(_dragStart.X, screen.X), Math.Min(_dragStart.Y, screen.Y)),
                    new Point(Math.Max(_dragStart.X, screen.X), Math.Max(_dragStart.Y, screen.Y)));
                var worldRect = new Rect(ToWorld(_marquee.TopLeft), ToWorld(_marquee.BottomRight));
                var ids = Boxes().Where(b => b.Rect.Intersects(worldRect)).Select(b => b.Node.Id).ToList();
                Editor.Select(ids, _marqueeAdditive);
                break;
        }
        InvalidateVisual();
    }

    private bool _dragBegun;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Editor is null || _drag == Drag.None)
            return;
        e.Pointer.Capture(null);
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);

        if (_drag == Drag.Wire && _wire is { } wire)
        {
            var hit = HitTest(world);
            if (hit.Kind is HitKind.InPort or HitKind.OutPort && hit.Box is not null)
            {
                var targetIsInput = hit.Kind == HitKind.InPort;
                if (targetIsInput == wire.FromOutput)
                {
                    var (fromNode, fromPort, toNode, toPort) = wire.FromOutput
                        ? (wire.NodeId, wire.Port, hit.Box.Node.Id, hit.Port!)
                        : (hit.Box.Node.Id, hit.Port!, wire.NodeId, wire.Port);
                    if (!Editor.Connect(fromNode, fromPort, toNode, toPort, out var reason) && reason is not null)
                        Refused?.Invoke(reason);
                }
            }
            else if (hit.Kind == HitKind.None)
            {
                // dropped on empty canvas: offer a node that connects to the wire
                QuickSearchRequested?.Invoke(screen, world, wire);
            }
        }
        else if (_drag == Drag.Nodes && !_moved)
        {
            // a plain click on one of several selected nodes narrows the selection to it
            var hit = HitTest(world);
            if (hit.Kind == HitKind.Node && Editor.SelectedNodeIds.Count > 1
                && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
                Editor.Select(new[] { hit.Box!.Node.Id });
        }

        _drag = Drag.None;
        _wire = null;
        _dragBegun = false;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var screen = e.GetPosition(this);
        var before = ToWorld(screen);
        var factor = Math.Pow(1.12, e.Delta.Y);
        _zoom = Math.Clamp(_zoom * factor, 0.35, 2.2);
        _pan = new Point(screen.X - before.X * _zoom, screen.Y - before.Y * _zoom);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverNode is not null || _hoverPort is not null)
        {
            _hoverNode = null;
            _hoverPort = null;
            InvalidateVisual();
        }
    }

    // ---- keyboard ----------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Editor is null)
            return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Space:
                _spaceDown = true;
                e.Handled = true;
                return;
            case Key.Delete:
            case Key.Back:
                Editor.DeleteSelection();
                break;
            case Key.B when !ctrl:
                foreach (var id in Editor.SelectedNodeIds.ToList())
                    Editor.ToggleBypass(id);
                break;
            case Key.F when !ctrl:
                FrameAll();
                break;
            case Key.A when ctrl:
                Editor.Select(Editor.Graph.Nodes.Select(n => n.Id));
                break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                QuickSearchRequested?.Invoke(ToScreen(_pointerWorld), _pointerWorld, null);
                break;
            case Key.C when ctrl:
                Editor.Copy();
                break;
            case Key.V when ctrl:
                Editor.Paste(_pointerWorld.X, _pointerWorld.Y);
                break;
            case Key.D when ctrl:
                Editor.Duplicate();
                break;
            case Key.Z when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Y when ctrl:
                Editor.RedoCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                Editor.UndoCommand.Execute(null);
                break;
            case Key.Escape:
                Editor.ClearSelection();
                break;
            default:
                return;
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
            _spaceDown = false;
    }

    // ---- context menus -----------------------------------------------------------------------

    private static MenuItem Item(string header, Action run, bool enabled = true, string? gesture = null, bool danger = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (gesture is not null)
            item.InputGesture = KeyGesture.Parse(gesture);
        if (danger)
            item.Foreground = DangerBrush;
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>The "Add node" categories as menu items; shared by the right-click menu and the top bar.</summary>
    public static IEnumerable<MenuItem> BuildAddItems(Action<string, string?> pick)
    {
        foreach (var group in PrimitiveCatalog.All
                     .Where(d => d.Id != PrimitiveCatalog.Output && d.Id != PrimitiveCatalog.StockEffect)
                     .GroupBy(d => d.Category).OrderBy(g => g.Key))
        {
            var category = new MenuItem { Header = group.Key };
            foreach (var d in group.OrderBy(d => d.DisplayName))
            {
                var id = d.Id;
                category.Items.Add(Item(d.DisplayName, () => pick(id, null)));
            }
            yield return category;
        }

        var stock = new MenuItem { Header = "Stock effects" };
        foreach (var group in EffectCatalog.All.Where(e => !e.IsCustom && e.Kind != EffectKind.Audio)
                     .GroupBy(e => e.Category).OrderBy(g => g.Key))
        {
            var category = new MenuItem { Header = group.Key };
            foreach (var fx in group.OrderBy(e => e.DisplayName))
            {
                var typeId = fx.TypeId;
                category.Items.Add(Item(fx.DisplayName, () => pick(PrimitiveCatalog.StockEffect, typeId)));
            }
            stock.Items.Add(category);
        }
        yield return stock;
    }

    private void OpenContextMenu(Point screen, Point world)
    {
        if (Editor is null)
            return;
        var vm = Editor;
        var hit = HitTest(world);
        var menu = new ContextMenu();

        void Add(string primitive, string? effect) => vm.AddNode(primitive, world.X - NodeW / 2, world.Y - HeaderH / 2, effect);

        if (hit.Kind is HitKind.Node or HitKind.Pin or HitKind.InPort or HitKind.OutPort && hit.Box is not null)
        {
            var id = hit.Box.Node.Id;
            if (!vm.SelectedNodeIds.Contains(id))
                vm.Select(new[] { id });
            var isOutput = hit.Box.Node.PrimitiveId == PrimitiveCatalog.Output;
            var d = hit.Box.D;
            var canBypass = d is not null && d.Inputs.Count == 1 && d.Outputs.Count == 1 && d.Inputs[0].Type == d.Outputs[0].Type;
            menu.Items.Add(Item(hit.Box.Node.Bypass ? "Turn bypass off" : "Bypass", () => vm.ToggleBypass(id), canBypass, "B"));
            menu.Items.Add(Item("Duplicate", vm.Duplicate, !isOutput, "Ctrl+D"));
            menu.Items.Add(Item("Pin all parameters", () => vm.PromoteAll(id), hit.Box.Params.Count > 0));
            menu.Items.Add(Item("Disconnect all wires", () => vm.DisconnectNode(id)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Copy", vm.Copy, !isOutput, "Ctrl+C"));
            menu.Items.Add(Item("Delete", vm.DeleteSelection, !isOutput, "Delete", danger: true));
        }
        else if (hit.Kind == HitKind.Wire && hit.Edge is { } edge)
        {
            vm.SelectEdge(edge);
            var insert = new MenuItem { Header = "Insert node" };
            var fromType = PrimitiveCatalog.Find(vm.Graph.FindNode(edge.FromNodeId)?.PrimitiveId ?? "")?.Output(edge.FromPort)?.Type;
            foreach (var group in PrimitiveCatalog.All
                         .Where(d => d.Id is not PrimitiveCatalog.Output and not PrimitiveCatalog.StockEffect && d.Id != PrimitiveCatalog.Input)
                         .Where(d => d.Inputs.Any(p => p.Type == fromType) && d.Outputs.Any(p => p.Type == fromType))
                         .GroupBy(d => d.Category).OrderBy(g => g.Key))
            {
                var sub = new MenuItem { Header = group.Key };
                foreach (var d in group.OrderBy(d => d.DisplayName))
                {
                    var pid = d.Id;
                    sub.Items.Add(Item(d.DisplayName, () => vm.InsertOnEdge(edge, pid)));
                }
                insert.Items.Add(sub);
            }
            menu.Items.Add(insert);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete wire", () => vm.Disconnect(edge), true, "Delete", danger: true));
        }
        else
        {
            var add = new MenuItem { Header = "Add node" };
            foreach (var item in BuildAddItems(Add))
                add.Items.Add(item);
            menu.Items.Add(add);
            menu.Items.Add(Item("Quick search…", () => QuickSearchRequested?.Invoke(screen, world, null), true, "Shift+A"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Paste", () => vm.Paste(world.X, world.Y), vm.HasClipboard, "Ctrl+V"));
            menu.Items.Add(Item("Select all", () => vm.Select(vm.Graph.Nodes.Select(n => n.Id)), true, "Ctrl+A"));
            menu.Items.Add(Item("Frame all", FrameAll, true, "F"));
        }
        menu.Open(this);
    }
}
