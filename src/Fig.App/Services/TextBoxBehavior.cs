using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Fig.App.Services;

/// <summary>
/// How every one-line text box behaves. Clicking or tabbing in selects what is there, so typing replaces it. Enter
/// applies it (focus moves away, which commits bindings that wait for that); Escape puts back what was there when
/// the box was entered and leaves. Boxes that take several lines are left alone, as are ones with their own keys.
/// </summary>
public static class TextBoxBehavior
{
    /// <summary>Text boxes with this class handle their own keys (a number field's editor).</summary>
    public const string OwnKeys = "number-field-editor";

    // what each box held when it was entered, for Escape
    private static readonly ConditionalWeakTable<TextBox, string> Entered = new();

    public static void Install()
    {
        InputElement.GotFocusEvent.AddClassHandler<TextBox>(OnGotFocus);
        InputElement.KeyDownEvent.AddClassHandler<TextBox>(OnKeyDown, RoutingStrategies.Tunnel);
    }

    private static bool Applies(TextBox box) => !box.AcceptsReturn && !box.Classes.Contains(OwnKeys) && !box.IsReadOnly;

    private static void OnGotFocus(TextBox box, FocusChangedEventArgs e)
    {
        if (!Applies(box))
            return;
        Entered.AddOrUpdate(box, box.Text ?? "");
        // after the click has placed the caret
        Dispatcher.UIThread.Post(box.SelectAll, DispatcherPriority.Input);
    }

    private static void OnKeyDown(TextBox box, KeyEventArgs e)
    {
        if (!Applies(box) || e.KeyModifiers != KeyModifiers.None)
            return;
        if (e.Key == Key.Escape)
        {
            if (Entered.TryGetValue(box, out var before))
                box.Text = before;
            Leave(box);
        }
        else if (e.Key == Key.Enter)
        {
            Leave(box);
        }
        // the key goes on, so a dialog's OK (Enter) and Cancel (Escape) buttons still answer it
    }

    /// <summary>Moves focus off the box to its window, which commits a binding that updates when focus leaves.</summary>
    private static void Leave(TextBox box) => TopLevel.GetTopLevel(box)?.Focus();
}
