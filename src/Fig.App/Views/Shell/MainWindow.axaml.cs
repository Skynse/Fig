using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Fig.App.Services;
using Fig.App.ViewModels;

namespace Fig.App.Views;

public partial class MainWindow : Window
{
    private readonly Dictionary<KeyGesture, ICommand> _typable = new();
    private AppViewModel? _app;

    public MainWindow()
    {
        InitializeComponent();
        // Shortcuts that are also typed characters (J, K, L, Shift+S) cannot be KeyBindings: a binding marks the
        // key handled, and a handled key never becomes text, so typing "shake" in a search box lost its "k".
        // They are routed here instead, after the focused control has had its chance, and never while typing.
        AddHandler(KeyDownEvent, OnTypableShortcut, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnTimelineNavigationKey, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Attach(DataContext as AppViewModel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // a maximized window can't be resized, so its edges shouldn't offer to
        if (change.Property == WindowStateProperty)
            ResizeGrips.IsVisible = WindowState == WindowState.Normal;
    }

    private void Attach(AppViewModel? app)
    {
        if (_app is not null)
        {
            _app.Preferences.Changed -= ApplyShortcuts;
            _app.PropertyChanged -= OnAppPropertyChanged;
        }
        _app = app;
        if (_app is not null)
        {
            _app.Preferences.Changed += ApplyShortcuts;
            _app.PropertyChanged += OnAppPropertyChanged;
        }
        ApplyShortcuts();
    }

    private void OnAppPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.ShortcutsSuspended))
            ApplyShortcuts();
    }

    /// <summary>Rebuilds every keyboard shortcut from the shortcut table and the user's changes to it.</summary>
    private void ApplyShortcuts()
    {
        KeyBindings.Clear();
        _typable.Clear();
        if (_app is null || _app.ShortcutsSuspended)
            return;
        foreach (var action in ShortcutCatalog.All)
        {
            if (ShortcutCatalog.Parse(ShortcutCatalog.GestureFor(_app.Preferences.Current, action)) is not { } gesture
                || action.Command(_app) is not { } command)
                continue;
            if (ShortcutCatalog.IsTypable(gesture))
                _typable[gesture] = command;
            else
                KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new TextAwareCommand(command, () => TextBoxWants(gesture)) });
        }
    }

    /// <summary>
    /// True when a text box has focus and <paramref name="gesture"/> is one it uses itself (paste, undo, moving
    /// the caret…). Avalonia tries the window's key bindings before the focused control sees the key, so without
    /// this Ctrl+V in a text box pasted clips on the timeline instead of the text, and Left stepped a frame.
    /// </summary>
    private bool TextBoxWants(KeyGesture gesture)
    {
        if (FocusManager?.GetFocusedElement() is not Visual focused)
            return false;
        // a focused slider or number field steps with the arrows (and Home / End)
        if (focused is Fig.App.Controls.FigSlider or Fig.App.Controls.NumberField
            && gesture.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            return true;
        return focused.GetSelfAndVisualAncestors().OfType<TextBox>().Any() && TextEditing().Contains(gesture);
    }

    private HashSet<KeyGesture>? _textEditing;

    /// <summary>The keys a text box handles: the platform's editing hotkeys, plus moving and deleting by character and by word.</summary>
    private HashSet<KeyGesture> TextEditing()
    {
        if (_textEditing is not null)
            return _textEditing;
        var gestures = new HashSet<KeyGesture>();
        if (Application.Current?.PlatformSettings?.HotkeyConfiguration is { } hotkeys)
            foreach (var list in new[]
                     {
                         hotkeys.Copy, hotkeys.Cut, hotkeys.Paste, hotkeys.Undo, hotkeys.Redo, hotkeys.SelectAll,
                         hotkeys.MoveCursorToTheStartOfLine, hotkeys.MoveCursorToTheEndOfLine,
                         hotkeys.MoveCursorToTheStartOfDocument, hotkeys.MoveCursorToTheEndOfDocument,
                         hotkeys.MoveCursorToTheStartOfLineWithSelection, hotkeys.MoveCursorToTheEndOfLineWithSelection,
                         hotkeys.MoveCursorToTheStartOfDocumentWithSelection, hotkeys.MoveCursorToTheEndOfDocumentWithSelection,
                     })
                gestures.UnionWith(list);
        var word = Application.Current?.PlatformSettings?.HotkeyConfiguration.WholeWordTextActionModifiers ?? KeyModifiers.Control;
        foreach (var key in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.Back, Key.Delete })
            foreach (var modifiers in new[] { KeyModifiers.None, KeyModifiers.Shift, word, word | KeyModifiers.Shift })
                gestures.Add(new KeyGesture(key, modifiers));
        return _textEditing = gestures;
    }

    /// <summary>A window shortcut that steps aside (cannot run) while a text box wants its keys.</summary>
    private sealed class TextAwareCommand(ICommand inner, Func<bool> textBoxWants) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => inner.CanExecuteChanged += value;
            remove => inner.CanExecuteChanged -= value;
        }

        public bool CanExecute(object? parameter) => !textBoxWants() && inner.CanExecute(parameter);

        public void Execute(object? parameter) => inner.Execute(parameter);
    }

    /// <summary>
    /// With the timeline focused, Up, Down, Home, End and Page Up / Down are shortcuts (jump between cuts…). They are
    /// taken on the way in, before the scroll viewer the timeline sits in uses them to scroll.
    /// </summary>
    private void OnTimelineNavigationKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            || e.Source is not Visual source || !source.GetSelfAndVisualAncestors().OfType<TimelineView>().Any())
            return;
        OnTypableShortcut(sender, e);
    }

    private void OnTypableShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _app is null)
            return;
        if (e.Source is Visual source)
        {
            var chain = source.GetSelfAndVisualAncestors().ToList();
            if (chain.OfType<TextBox>().Any())
                return;
            // full-screen tools such as the effect graph editor own their keys
            if (chain.OfType<Control>().Any(c => c.Classes.Contains("no-transport")))
                return;
        }

        if (_typable.TryGetValue(new KeyGesture(e.Key, e.KeyModifiers), out var command) && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
