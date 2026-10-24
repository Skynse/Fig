using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fig.App.ViewModels;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views;

public partial class GraphEditorView : UserControl
{
    private readonly Popup _search = new() { IsLightDismissEnabled = true, Placement = PlacementMode.Pointer };
    private readonly TextBox _query = new() { PlaceholderText = "Search nodes…", MinWidth = 260 };
    private readonly ListBox _results = new() { MaxHeight = 320, MinWidth = 260 };
    private Point _searchWorld;
    private PendingWire? _searchWire;
    private List<SearchEntry> _entries = new();

    private sealed record SearchEntry(string Label, string Category, string PrimitiveId, string? EffectTypeId)
    {
        public override string ToString() => $"{Label}   ·   {Category}";
    }

    public GraphEditorView()
    {
        InitializeComponent();

        _search.Child = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#26272b")),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3a3c42")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = new StackPanel { Spacing = 8, Children = { _query, _results } },
        };
        ((Panel)Content!).Children.Add(_search);
        _query.TextChanged += (_, _) => FillResults();
        _query.KeyDown += OnQueryKey;
        _results.DoubleTapped += (_, _) => PickSelected();

        Canvas.QuickSearchRequested += OpenSearch;
        Canvas.Refused += message => (DataContext as GraphEditorViewModel)?.NotifyUser(message);

        AddMenuButton.Click += (_, _) => ShowAddMenu();
        EditMenuButton.Click += (_, _) => ShowEditMenu();
        ViewMenuButton.Click += (_, _) => ShowViewMenu();
    }

    private GraphEditorViewModel? Vm => DataContext as GraphEditorViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Vm is not { } vm)
            return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.S && ctrl)
        {
            vm.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.ConfirmClose)
        {
            vm.KeepEditingCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- menus -------------------------------------------------------------------------------

    private void ShowAddMenu()
    {
        if (Vm is not { } vm)
            return;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Quick search…", InputGesture = KeyGesture.Parse("Shift+A") }.With(item =>
            item.Click += (_, _) => OpenSearch(PointToScreenInView(), Canvas.ViewCenterWorld, null)));
        menu.Items.Add(new Separator());
        var center = Canvas.ViewCenterWorld;
        foreach (var item in GraphCanvas.BuildAddItems((id, effect) => vm.AddNode(id, center.X - 88, center.Y - 20, effect)))
            menu.Items.Add(item);
        menu.Open(AddMenuButton);
    }

    private void ShowEditMenu()
    {
        if (Vm is not { } vm)
            return;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Undo", InputGesture = KeyGesture.Parse("Ctrl+Z"), IsEnabled = vm.CanUndo }
            .With(i => i.Click += (_, _) => vm.UndoCommand.Execute(null)));
        menu.Items.Add(new MenuItem { Header = "Redo", InputGesture = KeyGesture.Parse("Ctrl+Y"), IsEnabled = vm.CanRedo }
            .With(i => i.Click += (_, _) => vm.RedoCommand.Execute(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Copy", InputGesture = KeyGesture.Parse("Ctrl+C") }.With(i => i.Click += (_, _) => vm.Copy()));
        menu.Items.Add(new MenuItem { Header = "Paste", InputGesture = KeyGesture.Parse("Ctrl+V"), IsEnabled = vm.HasClipboard }
            .With(i => i.Click += (_, _) => { var c = Canvas.ViewCenterWorld; vm.Paste(c.X, c.Y); }));
        menu.Items.Add(new MenuItem { Header = "Duplicate", InputGesture = KeyGesture.Parse("Ctrl+D") }.With(i => i.Click += (_, _) => vm.Duplicate()));
        menu.Items.Add(new MenuItem { Header = "Delete", InputGesture = KeyGesture.Parse("Delete") }.With(i => i.Click += (_, _) => vm.DeleteSelection()));
        menu.Open(EditMenuButton);
    }

    private void ShowViewMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Frame all", InputGesture = KeyGesture.Parse("F") }.With(i => i.Click += (_, _) => Canvas.FrameAll()));
        menu.Open(ViewMenuButton);
    }

    private Point PointToScreenInView()
        => new(Canvas.Bounds.Width / 2, Canvas.Bounds.Height / 3);

    // ---- quick search ------------------------------------------------------------------------

    private void OpenSearch(Point screen, Point world, PendingWire? wire)
    {
        _searchWorld = world;
        _searchWire = wire;

        _entries = PrimitiveCatalog.All
            .Where(d => d.Id is not PrimitiveCatalog.Output and not PrimitiveCatalog.StockEffect)
            .Select(d => new SearchEntry(d.DisplayName, d.Category, d.Id, null))
            .Concat(EffectCatalog.All.Where(e => !e.IsCustom && e.Kind != EffectKind.Audio)
                .Select(e => new SearchEntry(e.DisplayName, "Stock effect", PrimitiveCatalog.StockEffect, e.TypeId)))
            .ToList();

        // a wire from an output offers nodes that have an input of that type, and the reverse
        if (wire is not null)
            _entries = _entries.Where(e => Accepts(e, wire)).ToList();

        _query.Text = "";
        FillResults();
        _search.PlacementTarget = Canvas;
        _search.IsOpen = true;
        _query.Focus();
    }

    private static bool Accepts(SearchEntry entry, PendingWire wire)
    {
        var d = PrimitiveCatalog.Find(entry.PrimitiveId);
        if (d is null)
            return false;
        return wire.FromOutput ? d.Inputs.Any(p => p.Type == wire.Type) : d.Outputs.Any(p => p.Type == wire.Type);
    }

    private void FillResults()
    {
        var q = (_query.Text ?? "").Trim();
        var matches = _entries
            .Where(e => q.Length == 0 || e.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || e.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => q.Length > 0 && e.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Label)
            .Take(40)
            .ToList();
        _results.ItemsSource = matches;
        _results.SelectedIndex = matches.Count > 0 ? 0 : -1;
    }

    private void OnQueryKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _results.SelectedIndex = Math.Min(_results.ItemCount - 1, _results.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up:
                _results.SelectedIndex = Math.Max(0, _results.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                PickSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                _search.IsOpen = false;
                Canvas.Focus();
                e.Handled = true;
                break;
        }
    }

    private void PickSelected()
    {
        if (_results.SelectedItem is not SearchEntry entry || Vm is not { } vm)
            return;
        _search.IsOpen = false;
        var node = vm.AddNode(entry.PrimitiveId, _searchWorld.X - 88, _searchWorld.Y - 13, entry.EffectTypeId);
        if (_searchWire is { } wire)
        {
            var d = PrimitiveCatalog.Find(entry.PrimitiveId)!;
            if (wire.FromOutput)
            {
                var input = d.Inputs.FirstOrDefault(p => p.Type == wire.Type);
                if (input is not null)
                    vm.Connect(wire.NodeId, wire.Port, node.Id, input.Id, out _);
            }
            else
            {
                var output = d.Outputs.FirstOrDefault(p => p.Type == wire.Type);
                if (output is not null)
                    vm.Connect(node.Id, output.Id, wire.NodeId, wire.Port, out _);
            }
        }
        _searchWire = null;
        Canvas.Focus();
    }
}

internal static class MenuItemExtensions
{
    public static T With<T>(this T item, Action<T> configure)
    {
        configure(item);
        return item;
    }
}
