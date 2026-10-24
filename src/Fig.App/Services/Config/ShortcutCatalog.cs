using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Input;
using Fig.App.ViewModels;

namespace Fig.App.Services
{
    /// <summary>One thing a keyboard shortcut can do, with the shortcut it ships with.</summary>
    public sealed record ShortcutAction(
        string Id,
        string Section,
        string Label,
        string DefaultGesture,
        Func<AppViewModel, ICommand?> Command);

    /// <summary>
    /// Every rebindable keyboard shortcut. The window builds its key bindings from this list (with the user's
    /// changes from <see cref="KeyboardPreferences"/>), and the Keyboard page edits it.
    /// </summary>
    public static class ShortcutCatalog
    {
        public static IReadOnlyList<ShortcutAction> All { get; } =
        [
            new("project.save", "Project", "Save", "Ctrl+S", a => a.Editor.SaveCommand),
            new("project.close", "Project", "Close project", "Ctrl+W", a => a.CloseProjectCommand),
            new("app.preferences", "Project", "Preferences", "Ctrl+OemComma", a => a.OpenPreferencesCommand),

            new("edit.undo", "Editing", "Undo", "Ctrl+Z", a => a.Editor.UndoCommand),
            new("edit.redo", "Editing", "Redo", "Ctrl+Y", a => a.Editor.RedoCommand),
            new("edit.split", "Editing", "Split at playhead", "Ctrl+B", a => a.Editor.SplitAtPlayheadCommand),
            new("edit.splitAll", "Editing", "Split every track at the playhead", "Ctrl+Shift+B", a => a.Editor.SplitAllAtPlayheadCommand),
            new("edit.trimStart", "Editing", "Delete before the playhead", "Q", a => a.Editor.TrimStartToPlayheadCommand),
            new("edit.trimEnd", "Editing", "Delete after the playhead", "W", a => a.Editor.TrimEndToPlayheadCommand),
            new("edit.freeze", "Editing", "Freeze frame", "", a => a.Editor.FreezeFrameCommand),
            new("edit.group", "Editing", "Group clips (move and cut together)", "Ctrl+G", a => a.Editor.GroupSelectedCommand),
            new("edit.ungroup", "Editing", "Ungroup clips", "Ctrl+Shift+G", a => a.Editor.UngroupSelectedCommand),
            new("edit.magnet", "Editing", "Main track magnet on or off", "", a => a.Editor.ToggleMainTrackMagnetCommand),
            new("tool.select", "Editing", "Select tool", "A", a => a.Editor.SelectToolCommand),
            new("tool.blade", "Editing", "Blade tool (click a clip to cut it)", "B", a => a.Editor.ToggleBladeToolCommand),
            new("edit.copy", "Editing", "Copy", "Ctrl+C", a => a.Editor.CopySelectedCommand),
            new("edit.cut", "Editing", "Cut", "Ctrl+X", a => a.Editor.CutSelectedCommand),
            new("edit.paste", "Editing", "Paste", "Ctrl+V", a => a.Editor.PasteCommand),
            new("edit.duplicate", "Editing", "Duplicate", "Ctrl+D", a => a.Editor.DuplicateSelectedCommand),

            new("play.loop", "Playback", "Loop playback", "Ctrl+L", a => a.Editor.ToggleLoopCommand),
            new("play.frameBack", "Playback", "Previous frame", "Left", a => a.Editor.StepBackFrameCommand),
            new("play.frameForward", "Playback", "Next frame", "Right", a => a.Editor.StepForwardFrameCommand),
            new("play.secondBack", "Playback", "Back one second", "Shift+Left", a => a.Editor.StepBackSecondCommand),
            new("play.secondForward", "Playback", "Forward one second", "Shift+Right", a => a.Editor.StepForwardSecondCommand),
            new("play.shuttleForward", "Playback", "Play forward (faster each press)", "L", a => a.Editor.ShuttleForwardCommand),
            new("play.shuttleBack", "Playback", "Play backward (faster each press)", "J", a => a.Editor.ShuttleBackCommand),
            new("play.shuttleStop", "Playback", "Stop", "K", a => a.Editor.ShuttleStopCommand),
            new("play.audioScrub", "Playback", "Audio scrubbing on or off", "Shift+S", a => a.Editor.ToggleAudioScrubCommand),
            new("play.previousEdit", "Playback", "Previous cut", "Up", a => a.Editor.PreviousEditPointCommand),
            new("play.nextEdit", "Playback", "Next cut", "Down", a => a.Editor.NextEditPointCommand),
            new("play.start", "Playback", "Go to the start", "Home", a => a.Editor.JumpToStartCommand),
            new("play.end", "Playback", "Go to the end", "End", a => a.Editor.JumpToEndCommand),

            new("view.mediaPanel", "View", "Show or hide the media panel", "Ctrl+D1", a => a.Editor.Layout.ToggleLeftCommand),
            new("view.inspector", "View", "Show or hide the inspector", "Ctrl+D2", a => a.Editor.Layout.ToggleRightCommand),
            new("view.zoomIn", "View", "Zoom the timeline in", "Ctrl+OemPlus", a => a.Editor.ZoomTimelineInCommand),
            new("view.zoomOut", "View", "Zoom the timeline out", "Ctrl+OemMinus", a => a.Editor.ZoomTimelineOutCommand),
            new("view.zoomFit", "View", "Zoom to fit the sequence", "Shift+Z", a => a.Editor.ZoomTimelineToFitCommand),
        ];

        public static ShortcutAction? Find(string id) => All.FirstOrDefault(a => a.Id == id);

        /// <summary>The gesture an action has now: the user's choice if they made one, else the default. "" means none.</summary>
        public static string GestureFor(Preferences preferences, ShortcutAction action)
            => preferences.Keyboard.Shortcuts.TryGetValue(action.Id, out var chosen) ? chosen : action.DefaultGesture;

        /// <summary>The gesture as a key gesture, or null when the action has none (or the text doesn't parse).</summary>
        public static KeyGesture? Parse(string gesture)
        {
            if (string.IsNullOrWhiteSpace(gesture))
                return null;
            try { return KeyGesture.Parse(gesture); }
            catch (ArgumentException) { return null; }
        }

        /// <summary>
        /// True for a shortcut that is also something you type (a letter, digit or symbol, alone or with Shift), or a
        /// key a focused list or slider moves with (Up, Down, Home, End, Page Up / Down). Those cannot be window key
        /// bindings: a binding would swallow the character in every text box, and the arrows in every list. They run
        /// only when nothing focused used the key.
        /// </summary>
        public static bool IsTypable(KeyGesture gesture)
        {
            var modifiers = gesture.KeyModifiers & ~KeyModifiers.Shift;
            if (modifiers != KeyModifiers.None)
                return false;
            var key = gesture.Key;
            return key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Space
                       or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
                   || key.ToString().StartsWith("Oem", StringComparison.Ordinal);
        }

        /// <summary>How a gesture is written for people: "Ctrl+," rather than "Ctrl+OemComma".</summary>
        public static string Display(string gesture)
        {
            if (Parse(gesture) is not { } parsed)
                return "None";
            var keyName = parsed.Key switch
            {
                Key.OemComma => ",",
                Key.OemPeriod => ".",
                Key.OemPlus => "+",
                Key.OemMinus => "-",
                Key.OemQuestion => "/",
                Key.OemSemicolon => ";",
                Key.OemQuotes => "'",
                Key.OemOpenBrackets => "[",
                Key.OemCloseBrackets => "]",
                Key.OemPipe => "\\",
                Key.OemTilde => "`",
                >= Key.D0 and <= Key.D9 => ((int)(parsed.Key - Key.D0)).ToString(),
                _ => parsed.Key.ToString(),
            };
            var parts = new List<string>();
            if (parsed.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (parsed.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (parsed.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (parsed.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
            parts.Add(keyName);
            return string.Join("+", parts);
        }
    }
}
