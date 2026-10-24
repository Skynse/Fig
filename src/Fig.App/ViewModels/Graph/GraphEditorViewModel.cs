using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.App.Services;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>One parameter of the selected node: its editor plus the pin that exposes it as an effect control.</summary>
public sealed partial class NodeParamRow : ViewModelBase
{
    private readonly Action<string> _toggle;

    public NodeParamRow(string key, string label, ParamEditorViewModel editor, bool isPromoted, Action<string> toggle)
    {
        Key = key;
        Label = label;
        Editor = editor;
        _isPromoted = isPromoted;
        _toggle = toggle;
    }

    public string Key { get; }
    public string Label { get; }
    public ParamEditorViewModel Editor { get; }

    [ObservableProperty]
    private bool _isPromoted;

    [RelayCommand]
    private void TogglePin() => _toggle(Key);
}

/// <summary>One control the saved effect will show on a clip.</summary>
public sealed partial class ExposedControlRow : ViewModelBase
{
    private readonly Action<string, string> _rename;
    private readonly Action<string, int> _move;
    private readonly Action<string> _remove;
    private bool _loading = true;

    public ExposedControlRow(string key, string label, string source, string range, string defaultText,
        Action<string, string> rename, Action<string, int> move, Action<string> remove)
    {
        Key = key;
        _label = label;
        Source = source;
        Range = range;
        DefaultText = defaultText;
        _rename = rename;
        _move = move;
        _remove = remove;
        _loading = false;
    }

    public string Key { get; }
    public string Source { get; }
    public string Range { get; }
    public string DefaultText { get; }

    [ObservableProperty]
    private string _label;

    partial void OnLabelChanged(string value)
    {
        if (!_loading)
            _rename(Key, value);
    }

    [RelayCommand]
    private void MoveUp() => _move(Key, -1);

    [RelayCommand]
    private void MoveDown() => _move(Key, 1);

    [RelayCommand]
    private void Remove() => _remove(Key);
}

public sealed record GraphProblem(string Message, string? NodeId);

/// <summary>
/// Edits one effect graph: add, wire, move and delete nodes; pin parameters as controls; validate and
/// preview live; save into the project's effect library. Edits are snapshot-based undo steps.
/// </summary>
public sealed partial class GraphEditorViewModel : ViewModelBase
{
    private readonly GraphEffectLibrary _library;
    private readonly Action<string>? _notify;
    private readonly List<EffectGraph> _undo = new();
    private readonly List<EffectGraph> _redo = new();
    private EffectGraph _baseline;
    private string? _coalesceKey;
    private DateTime _coalesceAt;
    private bool _rebuildingEditors;
    private List<GraphNode> _clipboard = new();
    private List<GraphEdge> _clipboardEdges = new();

    public GraphEditorViewModel(GraphEffectLibrary library, EffectGraph graph, Action<string>? notify = null)
    {
        _library = library;
        _notify = notify;
        Graph = graph;
        _baseline = graph.Clone();
        _name = graph.Name;
        Refresh();
    }

    public EffectGraph Graph { get; }

    /// <summary>Raised for every change the canvas must redraw (structure, positions, selection, errors).</summary>
    public event Action? GraphChanged;

    /// <summary>Raised when the editor wants to close (back, or after discarding).</summary>
    public event Action? CloseRequested;

    // ---- selection ---------------------------------------------------------------------------

    public HashSet<string> SelectedNodeIds { get; } = new();
    public GraphEdge? SelectedEdge { get; private set; }

    public GraphNode? SelectedNode => SelectedNodeIds.Count == 1 ? Graph.FindNode(SelectedNodeIds.First()) : null;

    public void Select(IEnumerable<string> nodeIds, bool additive = false)
    {
        if (!additive)
            SelectedNodeIds.Clear();
        foreach (var id in nodeIds)
            SelectedNodeIds.Add(id);
        SelectedEdge = null;
        RebuildInspector();
        GraphChanged?.Invoke();
    }

    public void SelectEdge(GraphEdge? edge)
    {
        SelectedNodeIds.Clear();
        SelectedEdge = edge;
        RebuildInspector();
        GraphChanged?.Invoke();
    }

    public void ToggleSelect(string nodeId)
    {
        if (!SelectedNodeIds.Remove(nodeId))
            SelectedNodeIds.Add(nodeId);
        SelectedEdge = null;
        RebuildInspector();
        GraphChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (SelectedNodeIds.Count == 0 && SelectedEdge is null)
            return;
        SelectedNodeIds.Clear();
        SelectedEdge = null;
        RebuildInspector();
        GraphChanged?.Invoke();
    }

    // ---- observable state --------------------------------------------------------------------

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isValid = true;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _validityTip = "";

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _canRedo;

    [ObservableProperty]
    private bool _confirmClose;

    [ObservableProperty]
    private string _selectedTitle = "";

    [ObservableProperty]
    private bool _hasSelectedNode;

    [ObservableProperty]
    private Avalonia.Media.Imaging.WriteableBitmap? _beforeImage;

    [ObservableProperty]
    private Avalonia.Media.Imaging.WriteableBitmap? _afterImage;

    public ObservableCollection<NodeParamRow> NodeParams { get; } = new();
    public ObservableCollection<ExposedControlRow> Exposed { get; } = new();
    public ObservableCollection<GraphProblem> Problems { get; } = new();

    public HashSet<string> ErrorNodeIds { get; } = new();
    public HashSet<string> UnusedNodeIds { get; } = new();

    public bool HasExposed => Exposed.Count > 0;
    public bool HasNoExposed => Exposed.Count == 0;
    public string ExposedCount => Exposed.Count.ToString();

    partial void OnNameChanged(string value)
    {
        if (Graph.Name == value)
            return;
        Checkpoint("name");
        Graph.Name = value;
        IsDirty = true;
    }

    // ---- editing -----------------------------------------------------------------------------

    private void Checkpoint(string? coalesce = null)
    {
        var now = DateTime.UtcNow;
        if (coalesce is not null && coalesce == _coalesceKey && (now - _coalesceAt).TotalMilliseconds < 700)
        {
            _coalesceAt = now;
            return;
        }
        _coalesceKey = coalesce;
        _coalesceAt = now;
        _undo.Add(Graph.Clone());
        if (_undo.Count > 100)
            _undo.RemoveAt(0);
        _redo.Clear();
        CanUndo = true;
        CanRedo = false;
    }

    /// <summary>Starts a drag so every move in it is one undo step.</summary>
    public void BeginDrag() => Checkpoint();

    private void Restore(EffectGraph snapshot)
    {
        Graph.Nodes = snapshot.Nodes.Select(n => n.Clone()).ToList();
        Graph.Edges = snapshot.Edges.Select(e => e.Clone()).ToList();
        Graph.Promoted = snapshot.Promoted.Select(p => p.Clone()).ToList();
        Graph.Name = snapshot.Name;
        // the graph already holds the name, so OnNameChanged sees no change and takes no checkpoint
        Name = snapshot.Name;
        SelectedNodeIds.RemoveWhere(id => Graph.FindNode(id) is null);
        SelectedEdge = null;
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Add(Graph.Clone());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        _coalesceKey = null;
        AfterEdit();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Add(Graph.Clone());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        _coalesceKey = null;
        AfterEdit();
    }

    /// <summary>Goes back to the last saved version.</summary>
    [RelayCommand]
    private void Revert()
    {
        Checkpoint();
        Restore(_baseline);
        IsDirty = false;
        AfterEdit(markDirty: false);
    }

    private void AfterEdit(bool markDirty = true, bool rebuildInspector = true)
    {
        CanUndo = _undo.Count > 0;
        CanRedo = _redo.Count > 0;
        if (markDirty)
            IsDirty = true;
        Refresh(rebuildInspector);
    }

    public GraphNode AddNode(string primitiveId, double x, double y, string? effectTypeId = null)
    {
        Checkpoint();
        var node = new GraphNode { Id = NewId(), PrimitiveId = primitiveId, EffectTypeId = effectTypeId, X = x, Y = y };
        foreach (var def in PrimitiveCatalog.ParamSchemaFor(node))
            node.Params[def.Key] = def.DefaultValue();
        Graph.Nodes.Add(node);
        SelectedNodeIds.Clear();
        SelectedNodeIds.Add(node.Id);
        SelectedEdge = null;
        AfterEdit();
        return node;
    }

    /// <summary>Adds a node in the middle of a wire: wire source → new node → wire target.</summary>
    public GraphNode? InsertOnEdge(GraphEdge edge, string primitiveId, string? effectTypeId = null)
    {
        var descriptor = PrimitiveCatalog.Find(primitiveId);
        var fromNode = Graph.FindNode(edge.FromNodeId);
        var toNode = Graph.FindNode(edge.ToNodeId);
        if (descriptor is null || fromNode is null || toNode is null)
            return null;
        var fromType = PrimitiveCatalog.Find(fromNode.PrimitiveId)?.Output(edge.FromPort)?.Type;
        var input = descriptor.Inputs.FirstOrDefault(p => p.Type == fromType);
        var output = descriptor.Outputs.FirstOrDefault(p => p.Type == fromType);
        if (fromType is null || input is null || output is null)
            return null;

        Checkpoint();
        var node = new GraphNode
        {
            Id = NewId(), PrimitiveId = primitiveId, EffectTypeId = effectTypeId,
            X = (fromNode.X + toNode.X) / 2, Y = (fromNode.Y + toNode.Y) / 2 + 40,
        };
        foreach (var def in PrimitiveCatalog.ParamSchemaFor(node))
            node.Params[def.Key] = def.DefaultValue();
        Graph.Nodes.Add(node);
        Graph.Edges.Remove(edge);
        Graph.Edges.Add(new GraphEdge { FromNodeId = edge.FromNodeId, FromPort = edge.FromPort, ToNodeId = node.Id, ToPort = input.Id });
        Graph.Edges.Add(new GraphEdge { FromNodeId = node.Id, FromPort = output.Id, ToNodeId = edge.ToNodeId, ToPort = edge.ToPort });
        SelectedNodeIds.Clear();
        SelectedNodeIds.Add(node.Id);
        SelectedEdge = null;
        AfterEdit();
        return node;
    }

    /// <summary>
    /// Checks whether an output can feed an input without breaking a rule. A reason is returned when it cannot,
    /// so the canvas can say why a drop was refused. Dropping on an occupied input replaces its wire.
    /// </summary>
    public string? WhyCannotConnect(string fromNodeId, string fromPort, string toNodeId, string toPort)
    {
        if (fromNodeId == toNodeId)
            return "A node cannot feed itself.";
        var from = Graph.FindNode(fromNodeId);
        var to = Graph.FindNode(toNodeId);
        if (from is null || to is null)
            return "That node is gone.";
        var outPort = PrimitiveCatalog.Find(from.PrimitiveId)?.Output(fromPort);
        var inPort = PrimitiveCatalog.Find(to.PrimitiveId)?.Input(toPort);
        if (outPort is null || inPort is null)
            return "That port does not exist.";
        if (outPort.Type != inPort.Type)
            return $"A {outPort.Type.ToString().ToLowerInvariant()} cannot go into a {inPort.Type.ToString().ToLowerInvariant()} input.";
        if (Reaches(toNodeId, fromNodeId))
            return "That wire would make a loop.";
        return null;
    }

    private bool Reaches(string start, string target)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id == target)
                return true;
            if (!seen.Add(id))
                continue;
            foreach (var e in Graph.Edges.Where(e => e.FromNodeId == id))
                stack.Push(e.ToNodeId);
        }
        return false;
    }

    public bool Connect(string fromNodeId, string fromPort, string toNodeId, string toPort, out string? reason)
    {
        reason = WhyCannotConnect(fromNodeId, fromPort, toNodeId, toPort);
        if (reason is not null)
            return false;
        Checkpoint();
        Graph.Edges.RemoveAll(e => e.ToNodeId == toNodeId && e.ToPort == toPort);
        Graph.Edges.Add(new GraphEdge { FromNodeId = fromNodeId, FromPort = fromPort, ToNodeId = toNodeId, ToPort = toPort });
        SelectedEdge = null;
        AfterEdit();
        return true;
    }

    public void Disconnect(GraphEdge edge)
    {
        Checkpoint();
        Graph.Edges.Remove(edge);
        if (ReferenceEquals(SelectedEdge, edge))
            SelectedEdge = null;
        AfterEdit();
    }

    public void DisconnectNode(string nodeId)
    {
        if (!Graph.Edges.Any(e => e.FromNodeId == nodeId || e.ToNodeId == nodeId))
            return;
        Checkpoint();
        Graph.Edges.RemoveAll(e => e.FromNodeId == nodeId || e.ToNodeId == nodeId);
        SelectedEdge = null;
        AfterEdit();
    }

    public void MoveNodes(IEnumerable<string> ids, double dx, double dy)
    {
        foreach (var id in ids)
            if (Graph.FindNode(id) is { } n)
            {
                n.X += dx;
                n.Y += dy;
            }
        IsDirty = true;
        GraphChanged?.Invoke();
    }

    public void DeleteSelection()
    {
        if (SelectedEdge is { } edge)
        {
            Disconnect(edge);
            return;
        }
        var ids = SelectedNodeIds.Where(id => Graph.FindNode(id)?.PrimitiveId != PrimitiveCatalog.Output).ToList();
        if (ids.Count == 0)
        {
            if (SelectedNodeIds.Count > 0)
                _notify?.Invoke("Every effect needs its Output node.");
            return;
        }
        Checkpoint();
        Graph.Nodes.RemoveAll(n => ids.Contains(n.Id));
        Graph.Edges.RemoveAll(e => ids.Contains(e.FromNodeId) || ids.Contains(e.ToNodeId));
        foreach (var promoted in Graph.Promoted)
            promoted.Targets.RemoveAll(t => ids.Contains(t.NodeId));
        Graph.Promoted.RemoveAll(p => p.Targets.Count == 0);
        SelectedNodeIds.Clear();
        AfterEdit();
    }

    public void ToggleBypass(string nodeId)
    {
        if (Graph.FindNode(nodeId) is not { } node)
            return;
        var d = PrimitiveCatalog.Find(node.PrimitiveId);
        if (d is null || d.Inputs.Count != 1 || d.Outputs.Count != 1 || d.Inputs[0].Type != d.Outputs[0].Type)
            return;
        Checkpoint();
        node.Bypass = !node.Bypass;
        AfterEdit();
    }

    public void Copy()
    {
        var ids = SelectedNodeIds.Where(id => Graph.FindNode(id)?.PrimitiveId != PrimitiveCatalog.Output).ToHashSet();
        _clipboard = Graph.Nodes.Where(n => ids.Contains(n.Id)).Select(n => n.Clone()).ToList();
        _clipboardEdges = Graph.Edges.Where(e => ids.Contains(e.FromNodeId) && ids.Contains(e.ToNodeId)).Select(e => e.Clone()).ToList();
    }

    public void Paste(double atX, double atY)
    {
        if (_clipboard.Count == 0)
            return;
        Checkpoint();
        var minX = _clipboard.Min(n => n.X);
        var minY = _clipboard.Min(n => n.Y);
        var map = new Dictionary<string, string>();
        SelectedNodeIds.Clear();
        foreach (var source in _clipboard)
        {
            var copy = source.Clone();
            copy.Id = NewId();
            copy.X = atX + (source.X - minX);
            copy.Y = atY + (source.Y - minY);
            map[source.Id] = copy.Id;
            Graph.Nodes.Add(copy);
            SelectedNodeIds.Add(copy.Id);
        }
        foreach (var e in _clipboardEdges)
            Graph.Edges.Add(new GraphEdge { FromNodeId = map[e.FromNodeId], FromPort = e.FromPort, ToNodeId = map[e.ToNodeId], ToPort = e.ToPort });
        SelectedEdge = null;
        AfterEdit();
    }

    public void Duplicate()
    {
        Copy();
        var minX = _clipboard.Count == 0 ? 0 : _clipboard.Min(n => n.X);
        var minY = _clipboard.Count == 0 ? 0 : _clipboard.Min(n => n.Y);
        Paste(minX + 32, minY + 32);
    }

    public bool HasClipboard => _clipboard.Count > 0;

    /// <summary>Shows a short message to the user (a refused wire, a failed save).</summary>
    public void NotifyUser(string message) => _notify?.Invoke(message);

    private string NewId()
    {
        string id;
        do { id = Guid.NewGuid().ToString("N")[..8]; } while (Graph.FindNode(id) is not null);
        return id;
    }

    // ---- parameters and promoted controls ------------------------------------------------------

    public void SetNodeParam(string nodeId, string key, ParamValue value)
    {
        if (Graph.FindNode(nodeId) is not { } node)
            return;
        Checkpoint($"param:{nodeId}:{key}");
        node.Params[key] = value;
        // the inspector row that wrote this keeps its own value, so it must not be rebuilt under the slider
        AfterEdit(rebuildInspector: false);
    }

    public PromotedParam? PromotedFor(string nodeId, string param)
        => Graph.Promoted.FirstOrDefault(p => p.Targets.Any(t => t.NodeId == nodeId && t.Param == param));

    public bool IsPromoted(string nodeId, string param) => PromotedFor(nodeId, param) is not null;

    public void TogglePromote(string nodeId, string param)
    {
        if (Graph.FindNode(nodeId) is not { } node)
            return;
        var existing = PromotedFor(nodeId, param);
        Checkpoint();
        if (existing is not null)
        {
            existing.Targets.RemoveAll(t => t.NodeId == nodeId && t.Param == param);
            if (existing.Targets.Count == 0)
                Graph.Promoted.Remove(existing);
        }
        else
        {
            var def = PrimitiveCatalog.ParamSchemaFor(node).FirstOrDefault(p => p.Key == param);
            if (def is null)
                return;
            Graph.Promoted.Add(new PromotedParam
            {
                Key = UniqueControlKey(def.Key),
                Label = def.Label,
                Targets = { new PromotedTarget { NodeId = nodeId, Param = param } },
            });
        }
        AfterEdit();
    }

    public void PromoteAll(string nodeId)
    {
        if (Graph.FindNode(nodeId) is not { } node)
            return;
        var missing = PrimitiveCatalog.ParamSchemaFor(node).Where(d => !IsPromoted(nodeId, d.Key)).ToList();
        if (missing.Count == 0)
            return;
        Checkpoint();
        foreach (var def in missing)
            Graph.Promoted.Add(new PromotedParam
            {
                Key = UniqueControlKey(def.Key),
                Label = def.Label,
                Targets = { new PromotedTarget { NodeId = nodeId, Param = def.Key } },
            });
        AfterEdit();
    }

    private string UniqueControlKey(string preferred)
    {
        var key = preferred;
        for (var i = 2; Graph.Promoted.Any(p => p.Key == key); i++)
            key = preferred + i;
        return key;
    }

    private void RenameControl(string key, string label)
    {
        if (Graph.Promoted.FirstOrDefault(p => p.Key == key) is not { } promoted || promoted.Label == label)
            return;
        Checkpoint($"label:{key}");
        promoted.Label = label;
        IsDirty = true;
        GraphChanged?.Invoke();
    }

    private void MoveControl(string key, int delta)
    {
        var index = Graph.Promoted.FindIndex(p => p.Key == key);
        var to = index + delta;
        if (index < 0 || to < 0 || to >= Graph.Promoted.Count)
            return;
        Checkpoint();
        (Graph.Promoted[index], Graph.Promoted[to]) = (Graph.Promoted[to], Graph.Promoted[index]);
        AfterEdit();
    }

    private void RemoveControl(string key)
    {
        if (Graph.Promoted.FirstOrDefault(p => p.Key == key) is not { } promoted)
            return;
        Checkpoint();
        Graph.Promoted.Remove(promoted);
        AfterEdit();
    }

    // ---- save / close ------------------------------------------------------------------------

    [RelayCommand]
    private void Save()
    {
        var result = _library.Save(Graph);
        if (!result.IsValid)
        {
            _notify?.Invoke("Fix the problems before saving.");
            return;
        }
        _baseline = Graph.Clone();
        IsDirty = false;
        ConfirmClose = false;
        _notify?.Invoke($"Saved \"{Graph.Name}\"");
    }

    [RelayCommand]
    private void Back()
    {
        if (IsDirty)
        {
            ConfirmClose = true;
            return;
        }
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void SaveAndClose()
    {
        Save();
        if (!IsDirty)
            CloseRequested?.Invoke();
        else
            ConfirmClose = false;
    }

    [RelayCommand]
    private void DiscardAndClose()
    {
        ConfirmClose = false;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void KeepEditing() => ConfirmClose = false;

    // ---- derived state -----------------------------------------------------------------------

    /// <summary>Recompiles, re-renders the preview and rebuilds the inspector. Cheap enough for every edit.</summary>
    private void Refresh(bool rebuildInspector = true)
    {
        var processor = new GraphEffectProcessor(Graph);
        var result = processor.Compile;
        IsValid = result.IsValid;

        ErrorNodeIds.Clear();
        UnusedNodeIds.Clear();
        Problems.Clear();
        foreach (var error in result.Errors)
        {
            Problems.Add(new GraphProblem(DescribeError(error), error.NodeId));
            if (error.NodeId is not null)
                ErrorNodeIds.Add(error.NodeId);
        }
        foreach (var id in result.UnusedNodeIds)
            UnusedNodeIds.Add(id);

        var wires = Graph.Edges.Count;
        StatusText = result.IsValid
            ? $"{Graph.Nodes.Count} nodes  ·  {wires} wires" + (result.UnusedNodeIds.Count > 0 ? $"  ·  {result.UnusedNodeIds.Count} not connected to the output" : "  ·  no problems")
            : $"{Graph.Nodes.Count} nodes  ·  {wires} wires  ·  {Problems.Count} problem{(Problems.Count == 1 ? "" : "s")}";
        ValidityTip = result.IsValid ? "Valid" : string.Join("\n", Problems.Select(p => p.Message));

        RenderPreview(processor);
        RebuildExposed();
        if (rebuildInspector)
            RebuildInspector();
        GraphChanged?.Invoke();
    }

    private static string DescribeError(GraphError error) => error.Message;

    private DecodedFrame? _source;

    private void RenderPreview(GraphEffectProcessor processor)
    {
        _source ??= GraphPreviewSource.Create(320, 180);
        BeforeImage ??= GraphPreviewSource.ToBitmap(_source);
        var after = processor.Apply(_source, new Dictionary<string, ParamValue>(), 0.5);
        try
        {
            if (AfterImage is null || AfterImage.PixelSize.Width != after.Width || AfterImage.PixelSize.Height != after.Height)
                AfterImage = GraphPreviewSource.ToBitmap(after);
            else
                GraphPreviewSource.CopyInto(AfterImage, after);
            OnPropertyChanged(nameof(AfterImage));
        }
        finally
        {
            if (!ReferenceEquals(after.Pixels, _source.Pixels))
                FramePool.Return(after.Pixels);
        }
    }

    private void RebuildExposed()
    {
        Exposed.Clear();
        foreach (var promoted in Graph.Promoted)
        {
            var target = promoted.Targets.FirstOrDefault();
            var node = target is null ? null : Graph.FindNode(target.NodeId);
            var def = node is null ? null : PrimitiveCatalog.ParamSchemaFor(node).FirstOrDefault(p => p.Key == target!.Param);
            var source = node is null || def is null ? "Missing parameter" : $"{NodeTitle(node)}  ·  {def.Label}"
                + (promoted.Targets.Count > 1 ? $" (+{promoted.Targets.Count - 1})" : "");
            var min = promoted.Min ?? def?.Min ?? 0;
            var max = promoted.Max ?? def?.Max ?? 0;
            var current = node is not null && def is not null && node.Params.TryGetValue(def.Key, out var v) ? v.ToString() : def?.DefaultValue().ToString() ?? "";
            Exposed.Add(new ExposedControlRow(promoted.Key, promoted.Label, source,
                def?.Kind is ParamKind.Double or ParamKind.Int ? $"{min:0.##} – {max:0.##}" : "",
                $"default {promoted.Default?.ToString("0.###") ?? current}", RenameControl, MoveControl, RemoveControl));
        }
        OnPropertyChanged(nameof(HasExposed));
        OnPropertyChanged(nameof(HasNoExposed));
        OnPropertyChanged(nameof(ExposedCount));
    }

    public static string NodeTitle(GraphNode node)
    {
        if (node.PrimitiveId == PrimitiveCatalog.StockEffect)
            return node.EffectTypeId is { } id && EffectCatalog.Find(id) is { } fx ? fx.DisplayName : "Effect";
        return PrimitiveCatalog.Find(node.PrimitiveId)?.DisplayName ?? node.PrimitiveId;
    }

    private void RebuildInspector()
    {
        if (_rebuildingEditors)
            return;
        _rebuildingEditors = true;
        try
        {
            NodeParams.Clear();
            var node = SelectedNode;
            HasSelectedNode = node is not null;
            SelectedTitle = node is null ? "" : NodeTitle(node);
            if (node is null)
                return;
            foreach (var def in PrimitiveCatalog.ParamSchemaFor(node))
                NodeParams.Add(new NodeParamRow(def.Key, def.Label, MakeEditor(node, def), IsPromoted(node.Id, def.Key),
                    key => TogglePromote(node.Id, key)));
        }
        finally { _rebuildingEditors = false; }
    }

    private ParamEditorViewModel MakeEditor(GraphNode node, ParamDef def)
    {
        // a node's setting: a plain value (graphs are animated through the effect that uses them)
        ParamValue Current()
            => node.Params.TryGetValue(def.Key, out var v) && Fits(v, def) ? v : def.DefaultValue();
        var editor = ParamEditorViewModel.For(def, new PlainParamTarget(Current, v => SetNodeParam(node.Id, def.Key, v)));
        editor.RefreshValue();
        return editor;
    }

    /// <summary>A stored value of the kind the parameter is (numbers fit either number kind).</summary>
    private static bool Fits(ParamValue value, ParamDef def) => def.Kind switch
    {
        ParamKind.Double or ParamKind.Int => value.Kind is ParamKind.Double or ParamKind.Int,
        _ => value.Kind == def.Kind,
    };
}
