// System tray icon: open settings, pause gestures, install a new version, quit.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Services;

public sealed class TrayService(UserDataStore store, ILocalizer loc, WindowService windows, UpdateService updates)
{
    private TrayIcon? _tray;
    private NativeMenuItem? _open, _pause, _quit, _update;

    public void Create(Application app)
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Filee/Assets/Icons/filee.ico"));
        _open = new NativeMenuItem();
        _open.Click += (_, _) => windows.ShowMain();
        _pause = new NativeMenuItem();
        _pause.Click += (_, _) =>
        {
            store.Settings.Paused = !store.Settings.Paused;
            store.SaveSettings();
        };
        _quit = new NativeMenuItem();
        _quit.Click += (_, _) => (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        _update = new NativeMenuItem();
        _update.Click += async (_, _) => await updates.UpdateAsync();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            // Set before the icon is attached: on macOS Avalonia passes the tooltip to native code, which can't take null.
            ToolTipText = "Filee",
            Menu = new NativeMenu { Items = { _open, _pause, new NativeMenuItemSeparator(), _quit } },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => windows.ShowMain();
        TrayIcon.SetIcons(app, [_tray]);

        Refresh();
        loc.LanguageChanged += (_, _) => Refresh();
        store.SettingsChanged += (_, _) => Refresh();
        updates.PropertyChanged += (_, e) =>
        {
            if (UpdateTexts.Affects(e.PropertyName))
                Refresh();
        };
    }

    private void Refresh()
    {
        if (_tray?.Menu is not { } menu)
            return;
        // "Update to x.y.z" sits at the top of the menu while a newer release is out ("Downloading… 42%" meanwhile).
        if (updates.LatestVersion is { } latest)
        {
            _update!.Header = updates.Step is UpdateStep.Downloading or UpdateStep.Installing
                ? UpdateTexts.Short(updates, loc)
                : loc.Format("tray.update", latest);
            if (!menu.Items.Contains(_update))
                menu.Items.Insert(0, _update);
        }
        else
        {
            menu.Items.Remove(_update!);
        }
        _open!.Header = loc["tray.open"];
        _pause!.Header = store.Settings.Paused ? loc["tray.resume"] : loc["tray.pause"];
        _quit!.Header = loc["tray.quit"];
        var drag = store.Settings.Triggers.FirstOrDefault(t => t.Enabled && t.Kind == TriggerKind.Drag);
        _tray.ToolTipText = store.Settings.Paused
            ? loc["triggers.paused"]
            : loc.Format("tray.tooltip", drag is null ? "—" : GestureText.Modifiers(drag.Modifiers));
    }

    public void Dispose() => _tray?.Dispose();
}
