// A frame loop for custom-drawn animations: calls back on every rendered frame (at most ~60 fps) while the control is
// in a visible, not minimized window and animations are on; stops otherwise and starts again when that changes.

using Avalonia;
using Avalonia.Controls;

namespace Filee.Setup.Controls;

internal sealed class FrameLoop
{
    private readonly Control _owner;
    private readonly Func<TimeSpan, bool> _onFrame;
    private TopLevel? _top;
    private bool _running;
    private TimeSpan? _start;

    /// <param name="onFrame">Gets the time since the loop started; returns false to stop.</param>
    public FrameLoop(Control owner, Func<TimeSpan, bool> onFrame)
    {
        _owner = owner;
        _onFrame = onFrame;
        owner.AttachedToVisualTree += (_, _) => Attach();
        owner.DetachedFromVisualTree += (_, _) => Detach();
    }

    /// <summary>Starts (again) from time zero.</summary>
    public void Restart()
    {
        _start = null;
        Kick();
    }

    public void Kick()
    {
        if (_running || _top is null || !SetupMotion.Enabled)
            return;
        if (_top is Window { WindowState: WindowState.Minimized })
            return;
        _running = true;
        _top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _running = false;
        if (_top is null || !_owner.IsEffectivelyVisible || _top is Window { WindowState: WindowState.Minimized })
            return;
        _start ??= now;
        if (_onFrame(now - _start.Value))
        {
            _running = true;
            _top.RequestAnimationFrame(OnFrame);
        }
    }

    private void Attach()
    {
        _top = TopLevel.GetTopLevel(_owner);
        if (_top is Window window)
            window.PropertyChanged += OnWindowChanged;
        Kick();
    }

    private void Detach()
    {
        if (_top is Window window)
            window.PropertyChanged -= OnWindowChanged;
        _top = null;
        _running = false;
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
            Kick();
    }
}
