# GNOME Wayland

Filee supports drag gestures on GNOME Wayland through the **Filee donut at the cursor** GNOME Shell extension.

The extension provides the pointer state required by Filee without access to `/dev/input`, membership in the `input` group, or running Filee as root.

> This integration is experimental and has been tested on Fedora 44 with GNOME Shell 50.

## Setup

Install Filee normally:

```sh
./install.sh
```

On a GNOME session, `install.sh` puts the extension in `~/.local/share/gnome-shell/extensions/filee-donut@filee.sh` and asks GNOME to enable it. Then:

1. Open the **Extensions** application.
2. Enable **Filee donut at the cursor**.
3. Log out and back in if the extension does not appear.
4. Start Filee and make sure gesture detection is enabled under **Triggers**.

Do not run Filee with `sudo`.

## Usage

With the default gesture:

1. Open **Files / Nautilus**.
2. Hold **Ctrl** and drag a file.
3. Move the file over the desired format in the Filee wheel.
4. Release it to start the conversion.

The wheel stays in place while selecting a format.

Pausing Filee, disabling the gesture or extension, excluding the application, or dropping outside the wheel leaves the normal file-manager behavior unchanged.

Custom modifiers and excluded applications continue to work through Filee's existing gesture settings.

## Updates

Updating Filee, in the app or with `./install.sh --upgrade`, also updates the extension when Filee installed it. GNOME Shell keeps running the version it loaded at login, so log out and back in after an update. If the running extension and Filee don't match, the **Triggers** page says so.

`install.sh` replaces only an extension folder that Filee installed. If the folder came from somewhere else, a first install stops and an upgrade leaves it as it is. `uninstall.sh` removes the extension when Filee installed it.

## Limitations

- GNOME integration is currently experimental.
- GNOME Shell 45–50 are declared as supported, but not every version has been tested.
- Global Tab and mouse-wheel state are not available for switching wheel faces during a drag.
- Global selection shortcuts and hold gestures are not available.
- Filee runs through XWayland by default. With the experimental native Wayland backend (`FILEE_USE_WAYLAND=1`), the extension recognizes the donut by Filee's process; this combination has had less testing.
- Multi-monitor setups and fractional scaling may require additional testing.

KDE Plasma and Hyprland use a different setup; see `INSTALL.txt`.

## Troubleshooting

If the wheel does not appear:

- Make sure Filee is not paused.
- Check that the drag gesture is enabled under **Triggers**.
- Make sure **Filee donut at the cursor** is enabled.
- Make sure Files / Nautilus is not excluded.
- Do not run Filee with `sudo`.
- If the extension was installed or updated recently, log out and back in.

To check the extension:

```sh
gnome-extensions info filee-donut@filee.sh
```

To check the communication socket:

```sh
ls -l "$XDG_RUNTIME_DIR/filee-gnome/pointer.sock"
```

When connected successfully, Filee's log (`~/.config/Filee/logs/`) contains:

```text
GNOME pointer extension connected
```

If the **Triggers** page says the extension belongs to another version of Filee, log out and back in so GNOME Shell loads the installed extension. If the message stays, reinstall Filee.
