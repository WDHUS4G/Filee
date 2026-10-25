using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Filee.App.ViewModels.Pages;

namespace Filee.App.Views.Pages;

public partial class TriggersPage : UserControl
{
    /// <summary>The key a recorder just took: its release is kept from the button too.</summary>
    private Key? _recordedKey;

    public TriggersPage()
    {
        InitializeComponent();
        // Tunnel, so the recorder sees the key before the focused button does (#450): a button takes Space and Enter
        // for itself, and on Space's release it clicked, which switched recording off and kept the old shortcut.
        AddHandler(KeyDownEvent, OnRecorderKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnRecorderKeyUp, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// While "Record" is on, the next key combination pressed on its button becomes the shortcut; on the flip
    /// recorder, the next key becomes the key that flips the donut.
    /// </summary>
    private void OnRecorderKeyDown(object? sender, KeyEventArgs e)
    {
        var recorder = (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true);
        var recorded = recorder switch
        {
            { DataContext: TriggerGestureViewModel { IsRecording: true } vm } when recorder.Classes.Contains("recorder") =>
                vm.Record(e.Key, e.KeyModifiers),
            { Name: "FlipRecorder", DataContext: FlipOptionsViewModel { IsRecording: true } flip } => flip.Record(e.Key),
            _ => false,
        };
        if (!recorded)
            return;
        e.Handled = true;
        _recordedKey = e.Key;
    }

    private void OnRecorderKeyUp(object? sender, KeyEventArgs e)
    {
        if (_recordedKey != e.Key)
            return;
        _recordedKey = null;
        e.Handled = true;
    }
}
