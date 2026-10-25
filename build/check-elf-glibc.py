#!/usr/bin/env python3
"""Lists the newest GLIBC symbol version every ELF file under the given paths needs, and fails when one needs a
newer glibc than --max allows.

A Linux package runs only on systems whose glibc is at least as new as the newest GLIBC_x.y version any of its
programs or libraries asks for (the .gnu.version_r section, what `objdump -T` prints). The portable package is
checked against the oldest Ubuntu LTS Filee supports. Standard library only, so it runs on any CI image.

Usage: check-elf-glibc.py [--max 2.35] [--needed] PATH [PATH ...]
Exit codes: 0 every file is within the limit, 1 a file needs a newer glibc, 2 no ELF files or a file can't be read.
"""

import argparse
import os
import struct
import sys

SHT_GNU_VERNEED = 0x6FFFFFFE
PT_LOAD = 1
PT_DYNAMIC = 2
DT_NULL = 0
DT_NEEDED = 1
DT_STRTAB = 5
DT_STRSZ = 10
DT_VERNEED = 0x6FFFFFFE
DT_VERNEEDNUM = 0x6FFFFFFF


class ElfError(Exception):
    pass


class Elf:
    """The parts of an ELF file needed to read its version requirements and DT_NEEDED entries."""

    def __init__(self, data):
        if data[:4] != b"\x7fELF":
            raise ElfError("not an ELF file")
        if data[4] not in (1, 2) or data[5] not in (1, 2):
            raise ElfError("unknown ELF class or byte order")
        self.data = data
        self.is64 = data[4] == 2
        self.endian = "<" if data[5] == 1 else ">"
        if self.is64:
            (self.phoff, self.shoff) = self._unpack("QQ", 0x20)
            (self.phentsize, self.phnum, self.shentsize, self.shnum, self.shstrndx) = self._unpack("HHHHH", 0x36)
        else:
            (self.phoff, self.shoff) = self._unpack("II", 0x1C)
            (self.phentsize, self.phnum, self.shentsize, self.shnum, self.shstrndx) = self._unpack("HHHHH", 0x2A)

    def _unpack(self, fmt, offset):
        fmt = self.endian + fmt
        end = offset + struct.calcsize(fmt)
        if offset < 0 or end > len(self.data):
            raise ElfError("truncated file")
        return struct.unpack_from(fmt, self.data, offset)

    def _string(self, offset):
        if offset < 0 or offset >= len(self.data):
            raise ElfError("string outside the file")
        end = self.data.find(b"\0", offset)
        if end < 0:
            raise ElfError("unterminated string")
        return self.data[offset:end].decode("ascii", "replace")

    def _sections(self):
        for index in range(self.shnum if self.shoff else 0):
            offset = self.shoff + index * self.shentsize
            if self.is64:
                (_, sh_type, _, _, sh_offset, sh_size, sh_link, sh_info) = self._unpack("IIQQQQII", offset)
            else:
                (_, sh_type, _, _, sh_offset, sh_size, sh_link, sh_info) = self._unpack("IIIIIIII", offset)
            yield sh_type, sh_offset, sh_size, sh_link, sh_info

    def _segments(self):
        for index in range(self.phnum if self.phoff else 0):
            offset = self.phoff + index * self.phentsize
            if self.is64:
                (p_type, _, p_offset, p_vaddr, _, p_filesz, _, _) = self._unpack("IIQQQQQQ", offset)
            else:
                (p_type, p_offset, p_vaddr, _, p_filesz, _, _, _) = self._unpack("IIIIIIII", offset)
            yield p_type, p_offset, p_vaddr, p_filesz

    def _file_offset(self, address):
        for p_type, p_offset, p_vaddr, p_filesz in self._segments():
            if p_type == PT_LOAD and p_vaddr <= address < p_vaddr + p_filesz:
                return address - p_vaddr + p_offset
        raise ElfError(f"address {address:#x} is not in a loaded segment")

    def _dynamic(self):
        """DT_* entries of the PT_DYNAMIC segment as (tag, value) pairs."""
        for p_type, p_offset, _, p_filesz in self._segments():
            if p_type != PT_DYNAMIC:
                continue
            size = 16 if self.is64 else 8
            for offset in range(p_offset, p_offset + p_filesz - size + 1, size):
                tag, value = self._unpack("qQ" if self.is64 else "iI", offset)
                if tag == DT_NULL:
                    return
                yield tag, value
            return

    def _read_verneed(self, offset, count, strtab):
        """Walks Elf_Verneed records and their Elf_Vernaux entries: {library: [version, ...]}."""
        result = {}
        for _ in range(count):
            (vn_version, vn_cnt, vn_file, vn_aux, vn_next) = self._unpack("HHIII", offset)
            if vn_version != 1:
                raise ElfError(f"unknown verneed version {vn_version}")
            library = self._string(strtab + vn_file)
            names = result.setdefault(library, [])
            aux = offset + vn_aux
            for _ in range(vn_cnt):
                (_, _, _, vna_name, vna_next) = self._unpack("IHHII", aux)
                names.append(self._string(strtab + vna_name))
                if vna_next == 0:
                    break
                aux += vna_next
            if vn_next == 0:
                break
            offset += vn_next
        return result

    def version_needs(self):
        """{library: [version names]} from .gnu.version_r, or from the dynamic segment when sections are stripped."""
        sections = list(self._sections())
        for sh_type, sh_offset, _, sh_link, sh_info in sections:
            if sh_type == SHT_GNU_VERNEED:
                if sh_link >= len(sections):
                    raise ElfError("bad string table link")
                return self._read_verneed(sh_offset, sh_info, sections[sh_link][1])
        dynamic = dict(self._dynamic())
        if DT_VERNEED in dynamic and DT_STRTAB in dynamic:
            return self._read_verneed(self._file_offset(dynamic[DT_VERNEED]), dynamic.get(DT_VERNEEDNUM, 0),
                                      self._file_offset(dynamic[DT_STRTAB]))
        return {}

    def needed_libraries(self):
        entries = list(self._dynamic())
        strtab = next((value for tag, value in entries if tag == DT_STRTAB), None)
        if strtab is None:
            return []
        base = self._file_offset(strtab)
        return [self._string(base + value) for tag, value in entries if tag == DT_NEEDED]


def parse_version(text):
    try:
        return tuple(int(part) for part in text.split("."))
    except ValueError:
        return None


def glibc_versions(needs):
    """The numeric GLIBC_x.y versions in a version_needs() result (GLIBC_PRIVATE is reported separately)."""
    versions = []
    for names in needs.values():
        for name in names:
            if name.startswith("GLIBC_"):
                version = parse_version(name[len("GLIBC_"):])
                if version is not None:
                    versions.append(version)
    return versions


def is_elf(path):
    try:
        with open(path, "rb") as stream:
            return stream.read(4) == b"\x7fELF"
    except OSError:
        return False


def elf_files(paths):
    for path in paths:
        if os.path.isfile(path) and not os.path.islink(path):
            if is_elf(path):
                yield path, os.path.basename(path)
            continue
        for directory, folders, files in os.walk(path):
            folders.sort()
            for name in sorted(files):
                full = os.path.join(directory, name)
                if not os.path.islink(full) and is_elf(full):
                    yield full, os.path.relpath(full, path)


def format_version(version):
    return "GLIBC_" + ".".join(str(part) for part in version) if version else "-"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--max", default="2.35", help="newest GLIBC version allowed (default 2.35, Ubuntu 22.04)")
    parser.add_argument("--needed", action="store_true", help="also list each file's DT_NEEDED libraries")
    parser.add_argument("paths", nargs="+")
    options = parser.parse_args(argv)
    limit = parse_version(options.max)
    if limit is None:
        parser.error(f"--max must look like 2.35, not {options.max}")

    rows = []
    unreadable = []
    for full, shown in elf_files(options.paths):
        try:
            with open(full, "rb") as stream:
                elf = Elf(stream.read())
            needs = elf.version_needs()
            needed = elf.needed_libraries() if options.needed else []
        except (ElfError, OSError, struct.error) as error:
            unreadable.append((shown, str(error)))
            continue
        versions = glibc_versions(needs)
        newest = max(versions) if versions else None
        private = any("GLIBC_PRIVATE" in names for names in needs.values())
        rows.append((shown, newest, private, needed))

    if not rows and not unreadable:
        print("No ELF files found in: " + ", ".join(options.paths), file=sys.stderr)
        return 2

    width = max([len(shown) for shown, *_ in rows] + [len(shown) for shown, _ in unreadable] + [4])
    print(f"{'File':<{width}}  {'Needs':<12}  Result")
    print(f"{'-' * width}  {'-' * 12}  ------")
    failed = 0
    for shown, newest, private, needed in rows:
        too_new = newest is not None and newest > limit
        failed += too_new
        result = f"needs a newer glibc than {options.max}" if too_new else "ok"
        if private:
            result += " (also GLIBC_PRIVATE: tied to one exact glibc build)"
        print(f"{shown:<{width}}  {format_version(newest):<12}  {result}")
        if needed:
            print(f"{'':<{width}}  {'':<12}  needs {', '.join(needed)}")
    for shown, error in unreadable:
        print(f"{shown:<{width}}  {'?':<12}  cannot read: {error}")

    newest_overall = max((newest for _, newest, _, _ in rows if newest), default=None)
    print()
    print(f"{len(rows)} ELF files, newest requirement {format_version(newest_overall)}, limit GLIBC_{options.max}.")
    sys.stdout.flush()
    if failed:
        print(f"{failed} file(s) need a glibc newer than {options.max}: they would not start on the oldest "
              "supported distribution.", file=sys.stderr)
        return 1
    if unreadable:
        print(f"{len(unreadable)} ELF file(s) could not be read.", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
