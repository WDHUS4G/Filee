#!/bin/sh
set -eu

data_dir=${XDG_DATA_HOME:-"$HOME/.local/share"}
install_dir="$data_dir/filee/application"
desktop_file="$data_dir/applications/com.filee.app.desktop"
gnome_extension_id="filee-donut@filee.sh"
gnome_extension_dir="$data_dir/gnome-shell/extensions/$gnome_extension_id"
python3 - "$data_dir" "$install_dir" <<'PY'
import pathlib
import sys

root, target = map(pathlib.Path, sys.argv[1:])
expected = root.resolve() / 'filee' / 'application'
if target.is_symlink() or target.resolve() != expected:
    raise SystemExit('Refusing to remove an installation outside its expected directory.')
if (target / '.filee-package').read_text(encoding='utf-8').strip() != 'com.filee.app':
    raise SystemExit('The directory is not an installation created by Filee.')
PY
"$install_dir/Filee" --uninstall-cleanup
if [ -f "$gnome_extension_dir/.filee-package" ] && [ "$(cat "$gnome_extension_dir/.filee-package")" = 'com.filee.app' ]; then
    if command -v gnome-extensions >/dev/null 2>&1; then
        gnome-extensions disable "$gnome_extension_id" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$gnome_extension_dir"
fi
if [ -L "$HOME/.local/bin/filee" ] && [ "$(readlink "$HOME/.local/bin/filee")" = "$install_dir/filee-cli" ]; then
    rm -- "$HOME/.local/bin/filee"
fi
if [ -f "$desktop_file" ] && grep -q '^X-Filee-Managed=true$' "$desktop_file"; then
    rm -- "$desktop_file"
fi
rm -rf -- "$install_dir"
printf '%s\n' 'Filee was removed. Your settings and conversion results were kept.'
