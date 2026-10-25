// Keeps combo boxes and single-selection lists from losing their selection to the toggle modifier. Avalonia treats
// Ctrl+Space, Ctrl+Enter and Ctrl+click (Cmd on macOS) on the selected item as "toggle", which leaves a combo box
// empty and pushes null into a setting that always has a value (#107). Multi-selection lists keep the toggle.
//
// A selection cleared some other way is refused by the view models (Choices.Restore); the control then shows the
// bound value again.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Filee.App.Controls;

public static class SingleSelectionGuard
{
    private static bool _registered;

    /// <summary>Installs the class handlers once for every combo box and list box of the application.</summary>
    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        // Tunnel: the item's own handlers (which toggle) run in the bubble phase.
        InputElement.KeyDownEvent.AddClassHandler<ListBoxItem>(OnKeyDown, RoutingStrategies.Tunnel);
        InputElement.PointerPressedEvent.AddClassHandler<ListBoxItem>(OnPointer, RoutingStrategies.Tunnel);
        InputElement.PointerReleasedEvent.AddClassHandler<ListBoxItem>(OnPointer, RoutingStrategies.Tunnel);
        SelectingItemsControl.SelectedItemProperty.Changed.AddClassHandler<SelectingItemsControl>(OnSelectedItemChanged);
    }

    /// <summary>
    /// The view model puts its value back while the control is still clearing its selection, which the control misses:
    /// once it is done, it reads the bound value again (still null when the view model took the null).
    /// </summary>
    private static void OnSelectedItemChanged(SelectingItemsControl control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is null && e.OldValue is not null && control is ComboBox or ListBox
            && BindingOperations.GetBindingExpressionBase(control, SelectingItemsControl.SelectedItemProperty) is { } binding)
            Dispatcher.UIThread.Post(binding.UpdateTarget);
    }

    private static void OnKeyDown(ListBoxItem item, KeyEventArgs e)
    {
        if (ItemSelectionEventTriggers.ShouldTriggerSelection(item, e) && WouldDeselect(item, e))
            Keep(item, e);
    }

    private static void OnPointer(ListBoxItem item, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(item).Properties.PointerUpdateKind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased
            && WouldDeselect(item, e))
            Keep(item, e);
    }

    /// <summary>The selected item of a single-selection control, picked again with the toggle modifier held.</summary>
    private static bool WouldDeselect(ListBoxItem item, RoutedEventArgs e) =>
        !e.Handled && item.IsSelected && ItemSelectionEventTriggers.HasToggleSelectionModifier(item, e)
        && ItemsControl.ItemsControlFromItemContainer(item) switch
        {
            ComboBox => true,
            ListBox list => (list.SelectionMode & (SelectionMode.Multiple | SelectionMode.Toggle)) == 0,
            _ => false,
        };

    /// <summary>Picking the item that is already selected: nothing changes, an open combo box closes as after any pick.</summary>
    private static void Keep(ListBoxItem item, RoutedEventArgs e)
    {
        e.Handled = true;
        if (e.RoutedEvent != InputElement.PointerPressedEvent && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox combo)
            combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
    }
}
