import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';

const DONUT_TITLE = 'Filee Donut';
// Filee reads the protocol version from each line and says on its Triggers page when it doesn't match, so the socket
// name stays the same across versions.
const PROTOCOL_VERSION = 1;
const SOCKET_NAME = 'pointer.sock';
const encoder = new TextEncoder();

// This bridge reads only pointer coordinates, buttons, modifier state and the app under the pointer.
// It never grabs input, changes Nautilus's copy action, reads typed keys, or accesses /dev/input.
export default class FileeDonutExtension extends Extension {
    enable() {
        this._enabled = true;
        this._processes = new WeakMap();
        this._pendingActor = null;
        this._connection = null;
        this._peerPid = 0;
        this._writing = false;
        this._lastState = '';
        this._pointerSource = 0;
        this._placeSource = 0;
        const directory = GLib.build_filenamev([GLib.get_user_runtime_dir(), 'filee-gnome']);
        GLib.mkdir_with_parents(directory, 0o700);
        this._socketFile = Gio.File.new_for_path(`${directory}/${SOCKET_NAME}`);
        if (this._socketFile.query_exists(null))
            this._socketFile.delete(null); // a stale socket from a previous Shell session
        this._service = new Gio.SocketService();
        this._service.add_address(new Gio.UnixSocketAddress({path: this._socketFile.get_path()}),
            Gio.SocketType.STREAM, Gio.SocketProtocol.DEFAULT, null);
        this._socketFile.set_attribute_uint32('unix::mode', 0o600, Gio.FileQueryInfoFlags.NONE, null);
        this._service.connect('incoming', (_service, connection) => {
            // Filesystem permissions and peer credentials restrict the bridge to the signed-in user.
            const credentials = connection.get_socket().get_credentials();
            if (credentials.get_unix_user() !== new Gio.Credentials().get_unix_user()) {
                connection.close(null);
                return true;
            }
            this._closeConnection();
            this._connection = connection;
            // The donut is Filee's window: with native Wayland it has no WM class or app id, so match it by process.
            try { this._peerPid = credentials.get_unix_pid(); } catch (_error) { this._peerPid = 0; }
            this._lastState = '';
            this._lastSent = 0;
            // Poll the pointer only while Filee is connected; _closeConnection stops it.
            this._pointerSource = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 16, () => {
                this._sendPointer();
                return GLib.SOURCE_CONTINUE;
            });
            return true;
        });
        this._service.start();
        this._mapSignal = global.window_manager.connect('map', (_wm, actor) => {
            if (!this._isDonut(actor.meta_window))
                return;
            this._pendingActor = actor;
            this._placeAttempts = 0;
            if (!this._placeSource) {
                // Retry for a few frames until the window has a size, then stop.
                this._placeSource = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 16, () => {
                    if (this._pendingActor && !this._placeDonut(this._pendingActor) && ++this._placeAttempts < 20)
                        return GLib.SOURCE_CONTINUE;
                    this._pendingActor = null;
                    this._placeSource = 0;
                    return GLib.SOURCE_REMOVE;
                });
            }
        });
    }

    disable() {
        this._enabled = false;
        if (this._placeSource)
            GLib.Source.remove(this._placeSource);
        this._placeSource = 0;
        if (this._mapSignal)
            global.window_manager.disconnect(this._mapSignal);
        this._mapSignal = 0;
        this._pendingActor = null;
        this._closeConnection();
        this._service?.stop();
        this._service?.close();
        this._service = null;
        if (this._socketFile?.query_exists(null))
            this._socketFile.delete(null);
        this._socketFile = null;
        this._processes = null;
    }

    _closeConnection() {
        const connection = this._connection;
        this._connection = null;
        this._peerPid = 0;
        this._writing = false;
        if (this._pointerSource)
            GLib.Source.remove(this._pointerSource);
        this._pointerSource = 0;
        if (connection) {
            try { connection.close(null); } catch (_error) { /* already disconnected */ }
        }
    }

    _isDonut(window) {
        if (window?.get_title() !== DONUT_TITLE)
            return false;
        // X11/XWayland: WM class. Native Wayland (FILEE_USE_WAYLAND=1): the app id, which Mutter reports as the WM
        // class, or, when Filee sets none, the connected Filee process or the process name.
        return [window.get_wm_class(), window.get_wm_class_instance(), window.get_gtk_application_id()]
            .some(value => /^(filee|com\.filee\.app)$/i.test(value ?? '')) ||
            (this._peerPid > 0 && window.get_pid() === this._peerPid) ||
            /^filee$/i.test(this._processName(window) ?? '');
    }

    _pointerWindow(x, y) {
        const windows = global.get_window_actors().filter(actor => actor.is_visible())
            .map(actor => actor.meta_window).filter(window => window?.showing_on_its_workspace());
        return global.display.sort_windows_by_stacking(windows).reverse().find(window => {
            const rect = window.get_frame_rect();
            return x >= rect.x && x < rect.x + rect.width && y >= rect.y && y < rect.y + rect.height;
        });
    }

    _processName(window) {
        if (!window)
            return null;
        if (!this._processes.has(window)) {
            let process = null;
            try {
                const [ok, bytes] = GLib.file_get_contents(`/proc/${window.get_pid()}/comm`);
                if (ok)
                    process = new TextDecoder().decode(bytes).trim();
            } catch (_error) { /* a window can disappear between snapshots */ }
            this._processes.set(window, process);
        }
        return this._processes.get(window);
    }

    _sendPointer() {
        // One async write at a time: a slow/dead Filee must never block the desktop or build an unbounded queue.
        if (this._writing || !this._connection)
            return;
        const [x, y, mask] = global.get_pointer();
        const blocked = Main.sessionMode.isLocked || !Main.sessionMode.hasWindows || Main.overview.visible || Main.modalCount > 0;
        const now = GLib.get_monotonic_time();
        const pointerKey = `${x}:${y}:${mask}:${blocked}`;
        // Idle desktops need only a heartbeat. Look up window geometry when input changes or at most four times/s.
        if (pointerKey === this._lastPointerKey && now - this._lastSent < 250000)
            return;
        const window = blocked ? null : this._pointerWindow(x, y);
        const modifiers = (mask & Clutter.ModifierType.CONTROL_MASK ? 1 : 0) |
            (mask & Clutter.ModifierType.MOD1_MASK ? 2 : 0) |
            (mask & Clutter.ModifierType.SHIFT_MASK ? 4 : 0) |
            (mask & Clutter.ModifierType.SUPER_MASK ? 8 : 0);
        // Clutter numbers middle before right; Filee numbers right before middle.
        const buttons = (mask & Clutter.ModifierType.BUTTON1_MASK ? 1 : 0) |
            (mask & Clutter.ModifierType.BUTTON3_MASK ? 2 : 0) |
            (mask & Clutter.ModifierType.BUTTON2_MASK ? 4 : 0) |
            (mask & Clutter.ModifierType.BUTTON4_MASK ? 8 : 0) |
            (mask & Clutter.ModifierType.BUTTON5_MASK ? 16 : 0);
        const state = JSON.stringify({Version: PROTOCOL_VERSION, X: x, Y: y, Modifiers: blocked ? 0 : modifiers,
            Buttons: blocked ? 0 : buttons, Process: this._processName(window),
            Desktop: window?.get_window_type() === Meta.WindowType.DESKTOP, Blocked: blocked});
        if (state === this._lastState && now - this._lastSent < 250000)
            return;
        this._writing = true;
        const connection = this._connection;
        connection.get_output_stream().write_all_async(encoder.encode(`${state}\n`), GLib.PRIORITY_DEFAULT, null,
            (stream, result) => {
                try {
                    stream.write_all_finish(result);
                    if (this._enabled && this._connection === connection) {
                        this._lastState = state;
                        this._lastSent = now;
                        this._lastPointerKey = pointerKey;
                    }
                } catch (_error) {
                    if (this._connection === connection)
                        this._closeConnection();
                } finally {
                    if (this._connection === connection)
                        this._writing = false;
                }
            });
    }

    _placeDonut(actor) {
        const window = actor.meta_window;
        if (!this._isDonut(window))
            return true;
        const rect = window.get_frame_rect();
        if (rect.width <= 0 || rect.height <= 0 || !actor.is_visible())
            return false;
        const [pointerX, pointerY] = global.get_pointer();
        let x = Math.round(pointerX - rect.width / 2);
        let y = Math.round(pointerY - rect.height / 2);
        const monitor = Main.layoutManager.monitors.find(area =>
            pointerX >= area.x && pointerX < area.x + area.width &&
            pointerY >= area.y && pointerY < area.y + area.height);
        if (monitor) {
            x = Math.max(monitor.x, Math.min(x, monitor.x + monitor.width - rect.width));
            y = Math.max(monitor.y, Math.min(y, monitor.y + monitor.height - rect.height));
        }
        window.make_above();
        window.move_frame(true, x, y);
        window.raise();
        // Keep it here so moving outward can reach a format; recenter only on the next map/open.
        return true;
    }
}
