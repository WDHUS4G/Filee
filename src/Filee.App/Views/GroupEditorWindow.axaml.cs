using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

/// <summary>Modal group editor. Returns true from ShowDialog when the user saved or deleted the group.</summary>
public partial class GroupEditorWindow : Window
{
    public GroupEditorWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
        Opened += (_, _) => NameBox.Focus();
        // Before the ComboBox, which would take Enter to open its list again.
        AddBox.AddHandler(KeyDownEvent, OnAddKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Asks before deleting, in a dialog over this window (set by WindowService; without it the group is deleted at
    /// once). Over this window, not its owner: this one stays blocked meanwhile, and on top of the donut stays visible.
    /// </summary>
    public Func<Window, string, Task<bool>>? Confirm { get; init; }

    /// <summary>True after the user saved or deleted (also available when the window was not shown as a dialog).</summary>
    public bool Changed { get; private set; }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GroupEditorViewModel vm || !vm.Apply())
            return;
        Changed = true;
        Close(true);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GroupEditorViewModel vm)
            return;
        if (Confirm is not null && !await Confirm(this, vm.DeleteConfirmation))
            return;
        vm.Delete();
        Changed = true;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Enter on the closed preset list adds the chosen preset; without this it would save and close.</summary>
    private void OnAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is ComboBox { IsDropDownOpen: true } || DataContext is not GroupEditorViewModel vm ||
            !vm.AddCommand.CanExecute(null))
            return;
        vm.AddCommand.Execute(null);
        e.Handled = true;
    }
}
