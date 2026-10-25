#!/bin/sh
set -eu

# Filee Linux standalone installer.
# Supports first-time installation and in-place upgrade (--upgrade).
# Preserves downloaded conversion engines (~/.local/share/Filee/engines) and user settings (~/.config/Filee).

upgrade=0

for arg do
    case "$arg" in
        --upgrade|-u)
            upgrade=1
            ;;
        --help|-h)
            printf '%s\n' "Usage: $0 [--upgrade|-u]"
            exit 0
            ;;
        *)
            printf '%s\n' "Unknown option: $arg" >&2
            printf '%s\n' "Usage: $0 [--upgrade|-u]" >&2
            exit 1
            ;;
    esac
done

source_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
data_dir=${XDG_DATA_HOME:-"$HOME/.local/share"}
install_dir="$data_dir/filee/application"
bin_dir="$HOME/.local/bin"
desktop_file="$data_dir/applications/com.filee.app.desktop"
gnome_extension_id="filee-donut@filee.sh"
gnome_extension_dir="$data_dir/gnome-shell/extensions/$gnome_extension_id"
is_gnome=false
if printf '%s' "${XDG_CURRENT_DESKTOP:-}" | tr ':' '\n' | grep -qi '^GNOME$'; then
    is_gnome=true
fi

# The GNOME Shell extension folder is ours when it is a real folder whose .filee-package marker says com.filee.app.
gnome_extension_is_ours() {
    [ -d "$gnome_extension_dir" ] && [ ! -L "$gnome_extension_dir" ] \
        && [ -f "$gnome_extension_dir/.filee-package" ] && [ ! -L "$gnome_extension_dir/.filee-package" ] \
        && [ "$(tr -d '\r\n' < "$gnome_extension_dir/.filee-package" 2>/dev/null || true)" = 'com.filee.app' ]
}

# Puts the extension bundled with the installed app in place, replacing only a folder that carries our marker. The
# new copy is staged first, so a failed copy leaves the previous extension as it was.
copy_gnome_extension() {
    work=$(mktemp -d "$data_dir/filee/.gnome-extension.XXXXXX") || return 1
    if ! cp -R "$install_dir/gnome/$gnome_extension_id" "$work/new" \
        || ! printf '%s\n' 'com.filee.app' > "$work/new/.filee-package" \
        || ! mkdir -p "$(dirname -- "$gnome_extension_dir")"; then
        rm -rf "$work"
        return 1
    fi
    if [ -e "$gnome_extension_dir" ] || [ -L "$gnome_extension_dir" ]; then
        if ! gnome_extension_is_ours || ! mv "$gnome_extension_dir" "$work/old"; then
            rm -rf "$work"
            return 1
        fi
    fi
    if ! mv "$work/new" "$gnome_extension_dir"; then
        if [ -d "$work/old" ]; then
            mv "$work/old" "$gnome_extension_dir" || true
        fi
        rm -rf "$work"
        return 1
    fi
    rm -rf "$work"
}

# Asks GNOME Shell to enable the extension. Only a first install does this; an upgrade leaves the user's choice alone.
enable_gnome_extension() {
    command -v gsettings >/dev/null 2>&1 || return 0
    enabled=$(gsettings get org.gnome.shell enabled-extensions 2>/dev/null || true)
    [ -n "$enabled" ] || return 0
    updated=$(python3 - "$enabled" "$gnome_extension_id" <<'PY'
import ast
import sys

try:
    extensions = ast.literal_eval(sys.argv[1].removeprefix("@as "))
except (SyntaxError, ValueError):
    raise SystemExit(0)
if not isinstance(extensions, list) or not all(isinstance(item, str) for item in extensions):
    raise SystemExit(0)
if sys.argv[2] not in extensions:
    extensions.append(sys.argv[2])
print("[" + ", ".join("'" + item.replace("'", "\\'") + "'" for item in extensions) + "]")
PY
    ) || return 0
    if [ -n "$updated" ]; then
        gsettings set org.gnome.shell enabled-extensions "$updated" >/dev/null 2>&1 || true
    fi
}

# Validate required tools and incoming payload files before doing anything.
command -v python3 >/dev/null 2>&1 || {
    printf '%s\n' "Python 3 is required to install Filee." >&2
    exit 1
}


if [ ! -f "$source_dir/Filee" ] || [ ! -x "$source_dir/Filee" ]; then
    printf '%s\n' "Payload validation failed: $source_dir/Filee is missing or not executable." >&2
    exit 1
fi

if [ ! -f "$source_dir/filee-cli" ] || [ ! -x "$source_dir/filee-cli" ]; then
    printf '%s\n' "Payload validation failed: $source_dir/filee-cli is missing or not executable." >&2
    exit 1
fi

# A first install on GNOME replaces an extension folder only when it carries our marker.
if [ "$upgrade" -eq 0 ] && [ "$is_gnome" = true ] && [ -d "$source_dir/gnome/$gnome_extension_id" ] \
    && { [ -e "$gnome_extension_dir" ] || [ -L "$gnome_extension_dir" ]; } && ! gnome_extension_is_ours; then
    printf '%s\n' "A GNOME extension that Filee didn't install exists at $gnome_extension_dir. Remove or rename it before installing Filee."
    exit 1
fi

if [ "$upgrade" -eq 0 ]; then
    # First-time installation: refuse to overwrite an existing installation or files.
    if [ -e "$install_dir" ] || [ -L "$install_dir" ]; then
        printf '%s\n' "An installation already exists at $install_dir. Run its uninstall.sh first."
        exit 1
    fi
    if [ -e "$bin_dir/filee" ] || [ -L "$bin_dir/filee" ]; then
        printf '%s\n' "$bin_dir/filee already exists. Remove or rename it before installing Filee."
        exit 1
    fi
    if [ -e "$desktop_file" ] || [ -L "$desktop_file" ]; then
        printf '%s\n' "$desktop_file already exists. Remove or rename it before installing Filee."
        exit 1
    fi

    mkdir -p "$data_dir/filee" "$bin_dir" "$data_dir/applications"
    cp -R "$source_dir" "$install_dir"
    printf '%s\n' 'com.filee.app' > "$install_dir/.filee-package"
    rm -f "$install_dir/.filee-upgrade-token"
    ln -s "$install_dir/filee-cli" "$bin_dir/filee"
else
    # Upgrade existing installation: verify it is genuine and safely swap in place.
    if [ ! -d "$install_dir" ] || [ -L "$install_dir" ]; then
        printf '%s\n' "No existing installation found at $install_dir to upgrade." >&2
        exit 1
    fi

    marker_file="$install_dir/.filee-package"
    if [ ! -f "$marker_file" ]; then
        printf '%s\n' "Cannot upgrade: $install_dir is missing the .filee-package marker." >&2
        exit 1
    fi

    marker=$(tr -d '\r\n' < "$marker_file" 2>/dev/null || true)
    if [ "$marker" != "com.filee.app" ]; then
        printf '%s\n' "Cannot upgrade: $install_dir was not installed by Filee." >&2
        exit 1
    fi

    # Tell running instance to save and exit cleanly before touching files.
    if [ -x "$install_dir/Filee" ]; then
        if ! "$install_dir/Filee" --quit; then
            printf '%s\n' "Could not gracefully shut down the running Filee instance. Please exit Filee and try again." >&2
            exit 1
        fi
    fi

    # Generate a unique transaction ID for this upgrade session using PID and random bytes.
    # This prevents collisions with any concurrent installer or pre-existing backup artifacts.
    token_rand=$(od -vN 8 -An -tx1 /dev/urandom 2>/dev/null | tr -d ' \n' || true)
    tx_id="$$-${token_rand:-$(date +%s)}"
    staging_dir="$data_dir/filee/.staging-$tx_id"
    backup_dir="$data_dir/filee/.backup-$tx_id"
    failed_dir="$data_dir/filee/.failed-$tx_id"
    upgrade_token="filee-upgrade-$tx_id"
    activated=0

    # Ensure transaction paths are completely distinct and do not collide with pre-existing files.
    if [ -e "$staging_dir" ] || [ -e "$backup_dir" ] || [ -e "$failed_dir" ]; then
        printf '%s\n' "Transaction path conflict detected at $data_dir/filee. Please retry." >&2
        exit 1
    fi

    mkdir -p "$data_dir/filee" "$bin_dir" "$data_dir/applications"
    mkdir "$staging_dir"

    # Copy incoming payload into staging (preserves source_dir without moving/deleting it).
    cp -R "$source_dir/." "$staging_dir/"
    printf '%s\n' 'com.filee.app' > "$staging_dir/.filee-package"
    printf '%s\n' "$upgrade_token" > "$staging_dir/.filee-upgrade-token"
    chmod +x "$staging_dir/Filee" "$staging_dir/filee-cli"

    # Validate the staged files before moving the existing installation aside.
    if [ ! -x "$staging_dir/Filee" ] || [ ! -x "$staging_dir/filee-cli" ]; then
        rm -rf "$staging_dir"
        printf '%s\n' "Staged payload validation failed." >&2
        exit 1
    fi

    # Rollback trap: handles catchable signals and script aborts.
    # Ensures $backup_dir is restored if anything fails during replacement.
    rollback_and_cleanup() {
        exit_code=$?
        if [ -d "$backup_dir" ]; then
            # If new files were activated into $install_dir, verify that $install_dir
            # actually contains this run's upgrade token before moving it aside.
            # This prevents another process or symlink swap from causing cleanup
            # to remove anything other than this process's failed payload.
            if [ "$activated" -eq 1 ] && [ -d "$install_dir" ] && [ ! -L "$install_dir" ]; then
                token_file="$install_dir/.filee-upgrade-token"
                if [ -f "$token_file" ] && [ ! -L "$token_file" ]; then
                    token=$(tr -d '\r\n' < "$token_file" 2>/dev/null || true)
                    if [ "$token" = "$upgrade_token" ]; then
                        # Token verified: safely move the failed payload aside to quarantine.
                        # Ensure $failed_dir does not already exist so mv performs a clean rename,
                        # and never fall back to recursive deletion if quarantine move fails.
                        if [ ! -e "$failed_dir" ] && mv "$install_dir" "$failed_dir" 2>/dev/null; then
                            # Re-verify token inside quarantined directory before deletion (TOCTOU mitigation).
                            # If ownership, symlink checks, or token match fail, keep $failed_dir intact.
                            q_token_file="$failed_dir/.filee-upgrade-token"
                            if [ -d "$failed_dir" ] && [ ! -L "$failed_dir" ] && [ -f "$q_token_file" ] && [ ! -L "$q_token_file" ]; then
                                q_token=$(tr -d '\r\n' < "$q_token_file" 2>/dev/null || true)
                                if [ "$q_token" = "$upgrade_token" ]; then
                                    rm -rf "$failed_dir" 2>/dev/null || true
                                fi
                            fi
                        fi
                    fi
                fi
            fi

            if [ ! -d "$install_dir" ] && mv "$backup_dir" "$install_dir" 2>/dev/null; then
                printf '%s\n' "Update failed. Restored the previous Filee installation." >&2
                if [ -d "$failed_dir" ]; then
                    printf '%s\n' "Quarantined failed update files preserved at: $failed_dir" >&2
                fi
            else
                printf '%s\n' "CRITICAL: Update failed and automatic rollback could not complete." >&2
                printf '%s\n' "Your previous installation is preserved at: $backup_dir" >&2
                if [ -d "$install_dir" ]; then
                    printf '%s\n' "A partial or invalid directory exists at: $install_dir" >&2
                    printf '%s\n' "To restore manually, inspect and remove or rename '$install_dir', then run:" >&2
                    printf '%s\n' "  mv '$backup_dir' '$install_dir'" >&2
                else
                    printf '%s\n' "To restore manually, run: mv '$backup_dir' '$install_dir'" >&2
                fi
                if [ -d "$failed_dir" ]; then
                    printf '%s\n' "Quarantined failed update files preserved at: $failed_dir" >&2
                fi
            fi
        fi
        rm -rf "$staging_dir" 2>/dev/null || true
        exit "$exit_code"
    }
    trap rollback_and_cleanup EXIT INT TERM HUP

    # Note on atomicity and crash recovery:
    # Individual filesystem renames (rename syscall) are atomic in POSIX.
    # However, replacing a non-empty directory on Linux requires two renames:
    #   1. mv "$install_dir" "$backup_dir"
    #   2. mv "$staging_dir" "$install_dir"
    # The two-step sequence as a whole is not a single atomic transaction.
    # There is a brief window between step 1 and step 2 where $install_dir does
    # not exist. Catchable signals (SIGINT, SIGTERM, SIGHUP) and script errors
    # are handled by the rollback trap above, which immediately restores $backup_dir.
    # If uncatchable termination (SIGKILL) or sudden hardware power loss occurs
    # during this brief window, automatic traps cannot run; the previous installation
    # remains safe in $backup_dir and can be restored manually.

    # Move active installation to backup.
    mv "$install_dir" "$backup_dir"

    # Activate new installation from staging.
    if ! mv "$staging_dir" "$install_dir"; then
        exit 1
    fi
    activated=1

    # Post-swap validation: ensure new installation is active and healthy.
    if [ ! -x "$install_dir/Filee" ] || [ ! -x "$install_dir/filee-cli" ] || [ ! -f "$install_dir/.filee-package" ]; then
        printf '%s\n' "Activated installation failed post-swap validation. Rolling back..." >&2
        exit 1
    fi

    # Success: disarm the rollback trap before removing the old version backup.
    trap - EXIT INT TERM HUP
    rm -rf "$backup_dir"
    rm -f "$install_dir/.filee-upgrade-token"

    # Refresh launcher symlink (using -f to overwrite existing link atomically).
    ln -sf "$install_dir/filee-cli" "$bin_dir/filee"
fi

# GNOME Shell extension. A first install puts it in place on GNOME and asks GNOME to enable it. An upgrade refreshes
# a copy that Filee installed, and on GNOME adds a missing one without enabling it (the Triggers page says how).
gnome_extension_note=''
if [ -d "$install_dir/gnome/$gnome_extension_id" ]; then
    if gnome_extension_is_ours; then
        gnome_extension_note=updated
    elif [ "$is_gnome" = true ] && [ ! -e "$gnome_extension_dir" ] && [ ! -L "$gnome_extension_dir" ]; then
        gnome_extension_note=added
    elif [ "$is_gnome" = true ]; then
        printf '%s\n' "Left $gnome_extension_dir as it is: Filee didn't install it." >&2
    fi
    if [ -n "$gnome_extension_note" ]; then
        if copy_gnome_extension; then
            if [ "$upgrade" -eq 0 ] && [ "$is_gnome" = true ]; then
                enable_gnome_extension
            fi
        else
            gnome_extension_note=''
            printf '%s\n' "Could not install the GNOME extension at $gnome_extension_dir." >&2
        fi
    fi
fi

# Update desktop launcher.
python3 - "$install_dir" "$desktop_file" <<'PY'
import pathlib
import sys

app, desktop = map(pathlib.Path, sys.argv[1:])
def quoted_exec(value):
    value = value.replace('%', '%%')
    for character in ('\\', '"', '`', '$'):
        value = value.replace(character, '\\' + character)
    return '"' + value.replace('\\', '\\\\').replace('\n', '\\n').replace('\r', '\\r') + '"'

def desktop_value(value):
    return str(value).replace('\\', '\\\\').replace('\n', '\\n').replace('\r', '\\r')

desktop.write_text(
    '[Desktop Entry]\nType=Application\nName=Filee\n'
    'Comment=Convert files with Filee\n'
    f'Exec={quoted_exec(str(app / "Filee"))} --convert %F\n'
    f'Icon={desktop_value(app / "filee.png")}\n'
    'Terminal=false\nCategories=Utility;\nX-Filee-Managed=true\n', encoding='utf-8')
PY

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$data_dir/applications" 2>/dev/null || true
fi

if [ "$upgrade" -eq 1 ]; then
    printf '%s\n' "Upgraded Filee at $install_dir."
else
    printf '%s\n' "Installed Filee at $install_dir" "CLI: $bin_dir/filee (add $bin_dir to PATH if needed)."
fi

if [ -n "$gnome_extension_note" ] && [ "$upgrade" -eq 0 ] && [ "$is_gnome" = true ]; then
    printf '%s\n' 'GNOME Wayland: enable "Filee donut at the cursor" in Extensions.' \
        'Sign out and sign in if the extension is not listed yet. Then hold Ctrl and drag a file in Files.' \
        'No administrator access or input group is required. See GNOME-WAYLAND.md.'
elif [ "$gnome_extension_note" = added ]; then
    printf '%s\n' 'GNOME Wayland: added the "Filee donut at the cursor" extension. Sign out and sign in, then enable it in Extensions.'
elif [ "$gnome_extension_note" = updated ]; then
    printf '%s\n' 'GNOME: updated the "Filee donut at the cursor" extension. Sign out and sign in so GNOME Shell loads it.'
fi
