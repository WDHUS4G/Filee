#!/usr/bin/env python3
"""Builds the Linux native pieces of the portable package that upstream builds on a newer glibc than Filee supports.

Filee's Linux packages must start on Ubuntu 22.04 (glibc 2.35). The rhwp CLI (HWP/HWPX -> PDF) release binary is linked
on Ubuntu 24.04 (GLIBC_2.39), so CI builds it from its pinned source on Ubuntu 22.04 and build/build-portable.ps1 puts it
into the package. SharpHook's native libraries (libuiohook) come from its NuGet package: from 8.0.1 on they need glibc
2.34 at most.

Subcommands:
  check                     verify the rhwp pin below still matches engines.json
  rhwp --output DIR         build the rhwp CLI into DIR (rhwp, LICENSE, README files, like its release archive)

Needs git, a C toolchain and rustup. Standard library only.
"""

import argparse
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))

# The rhwp release pinned in engines.json for Linux (`check` compares the versions). RUST_TOOLCHAIN is the one in the
# tag's rust-toolchain.toml. Cargo.lock in the tag pins every dependency (--locked).
RHWP_VERSION = "0.8.7"
RHWP_REPOSITORY = "https://github.com/edwardkim/rhwp"
RHWP_COMMIT = "1a76570e833917d15817415a53c09ad61ab3203f"
RUST_TOOLCHAIN = "1.93.1"


class BuildError(Exception):
    pass


def run(command, cwd=None, env=None, capture=False):
    print("+ " + " ".join(command), flush=True)
    result = subprocess.run(command, cwd=cwd, env=env, check=False, text=True,
                            stdout=subprocess.PIPE if capture else None)
    if result.returncode != 0:
        raise BuildError(f"{command[0]} failed with exit code {result.returncode}")
    return result.stdout if capture else None


def linux_rhwp_versions():
    path = os.path.join(ROOT, "src", "Filee.Engines", "Infrastructure", "engines.json")
    with open(path, encoding="utf-8") as stream:
        manifest = json.load(stream)
    return {rid: manifest["platforms"][rid]["components"]["rhwp"]["version"] for rid in ("linux-x64", "linux-arm64")}


def check():
    problems = []
    for rid, rhwp in linux_rhwp_versions().items():
        if rhwp != RHWP_VERSION:
            problems.append(
                f"engines.json pins rhwp {rhwp} for {rid}, but build/build-linux-native.py builds rhwp {RHWP_VERSION}. "
                "Update RHWP_VERSION, RHWP_COMMIT and RUST_TOOLCHAIN (from the tag's rust-toolchain.toml).")
    if problems:
        raise BuildError("\n".join(problems))
    print(f"Pins match: rhwp {RHWP_VERSION} -> {RHWP_COMMIT} (Rust {RUST_TOOLCHAIN}).")


def fetch(repository, commit, destination):
    """A shallow checkout of exactly one commit (GitHub serves any reachable commit by its hash)."""
    os.makedirs(destination)
    run(["git", "init", "-q", destination])
    run(["git", "-C", destination, "fetch", "-q", "--depth", "1", repository, commit])
    run(["git", "-C", destination, "checkout", "-q", "--detach", "FETCH_HEAD"])
    head = run(["git", "-C", destination, "rev-parse", "HEAD"], capture=True).strip()
    if head != commit:
        raise BuildError(f"{repository}: expected commit {commit}, got {head}")


def verify_rhwp_tag():
    """Warns when the tag no longer points at the pinned commit (the pinned commit is built either way)."""
    lines = run(["git", "ls-remote", RHWP_REPOSITORY, f"refs/tags/v{RHWP_VERSION}", f"refs/tags/v{RHWP_VERSION}^{{}}"],
                capture=True).splitlines()
    refs = dict(reversed(line.split("\t", 1)) for line in lines if "\t" in line)
    tagged = refs.get(f"refs/tags/v{RHWP_VERSION}^{{}}") or refs.get(f"refs/tags/v{RHWP_VERSION}")
    if tagged != RHWP_COMMIT:
        print(f"::warning::rhwp tag v{RHWP_VERSION} points at {tagged}, the build uses the pinned {RHWP_COMMIT}.")


def build_rhwp(output, work):
    if shutil.which("rustup") is None:
        raise BuildError("rhwp needs rustup (https://rustup.rs).")
    verify_rhwp_tag()
    source = os.path.join(work, "rhwp")
    fetch(RHWP_REPOSITORY, RHWP_COMMIT, source)
    run(["rustup", "toolchain", "install", RUST_TOOLCHAIN, "--profile", "minimal", "--no-self-update"])
    # "+toolchain" takes precedence over the repository's rust-toolchain.toml (whose wasm target the CLI doesn't need).
    env = dict(os.environ, CARGO_TARGET_DIR=os.path.join(work, "rhwp-target"))
    run(["cargo", f"+{RUST_TOOLCHAIN}", "build", "--release", "--locked", "--bin", "rhwp"], cwd=source, env=env)
    program = os.path.join(env["CARGO_TARGET_DIR"], "release", "rhwp")
    version = run([program, "--version"], capture=True)
    print(version.strip())
    if RHWP_VERSION not in version:
        raise BuildError(f"rhwp --version printed {version.strip()!r}, expected {RHWP_VERSION}.")
    # The same layout as rhwp's release archive, which build/fetch-portable-engines.ps1 unpacks for local builds.
    os.makedirs(output, exist_ok=True)
    shutil.copy2(program, os.path.join(output, "rhwp"))
    for name in ("LICENSE", "README.md", "README_EN.md"):
        if os.path.isfile(os.path.join(source, name)):
            shutil.copy2(os.path.join(source, name), os.path.join(output, name))
    write_source_note(output, "rhwp", RHWP_REPOSITORY, RHWP_COMMIT,
                      f"v{RHWP_VERSION}; cargo +{RUST_TOOLCHAIN} build --release --locked --bin rhwp; {platform.machine()}")


def write_source_note(output, name, repository, commit, details):
    with open(os.path.join(output, "SOURCE.txt"), "w", encoding="utf-8") as stream:
        stream.write(f"{name} built from source by Filee's CI on {distribution()}.\n"
                     f"Source: {repository}/tree/{commit}\n{details}\n")


def distribution():
    try:
        with open("/etc/os-release", encoding="utf-8") as stream:
            for line in stream:
                if line.startswith("PRETTY_NAME="):
                    return line.split("=", 1)[1].strip().strip('"')
    except OSError:
        pass
    return platform.platform()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("component", choices=["check", "rhwp"])
    parser.add_argument("--output", help="folder for the built files (must not exist yet)")
    parser.add_argument("--work", help="scratch folder (default: a new temporary folder)")
    options = parser.parse_args(argv)
    try:
        check()
        if options.component == "check":
            return 0
        if not sys.platform.startswith("linux"):
            raise BuildError("Build the Linux native pieces on Linux.")
        if not options.output:
            raise BuildError("--output is required.")
        output = os.path.abspath(options.output)
        if os.path.exists(output):
            raise BuildError(f"{output} already exists. Use a fresh output folder.")
        work = os.path.abspath(options.work) if options.work else tempfile.mkdtemp(prefix=f"filee-{options.component}-")
        os.makedirs(work, exist_ok=True)
        build_rhwp(output, work)
        print(f"{options.component} ready in {output}: {', '.join(sorted(os.listdir(output)))}")
        return 0
    except BuildError as error:
        print(f"::error::{error}" if os.environ.get("GITHUB_ACTIONS") else f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
