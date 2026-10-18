#!/bin/sh
set -eu

source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
install_dir="$HOME/Applications/Filee.app"
bin_dir="$HOME/.local/bin"
if [ -e "$install_dir" ] || [ -L "$install_dir" ]; then
    printf '%s\n' "An installation already exists at $install_dir. Run uninstall.sh first."
    exit 1
fi
if [ -e "$bin_dir/filee" ] || [ -L "$bin_dir/filee" ]; then
    printf '%s\n' "$bin_dir/filee already exists. Remove or rename it before installing Filee."
    exit 1
fi
test -x "$source_dir/Filee.app/Contents/MacOS/Filee"
mkdir -p "$HOME/Applications" "$bin_dir"
/usr/bin/ditto "$source_dir/Filee.app" "$install_dir"
ln -s "$install_dir/Contents/MacOS/filee-cli" "$bin_dir/filee"
printf '%s\n' "Installed $install_dir" "CLI: $bin_dir/filee (add $bin_dir to PATH if needed)."
printf '%s\n' 'Open Filee from Applications. Grant Accessibility and Finder Automation access when requested.'
