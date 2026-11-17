"""Puts libmtp and the libraries it needs into a macOS .app, from Homebrew bottles, on any system.

Usage:
  python bundle_libmtp.py --bottles <folder of *.tar.gz bottles> --out <App.app/Contents/MacOS> [--root libmtp.9.dylib]
  python bundle_libmtp.py --selftest <a signed .dylib>   (re-hashing an unchanged file must give the same bytes)

A Homebrew bottle keeps its paths as placeholders (@@HOMEBREW_PREFIX@@/opt/libusb/lib/libusb-1.0.0.dylib):
Homebrew writes the real ones when it installs. Here they become @loader_path/<file> (the copies side by side in
Contents/MacOS), starting from the root library and following what it loads. Changing a load command makes the
code signature's page hashes wrong, and macOS on Apple silicon loads no code with a wrong signature; codesign is
only on a Mac, so the hashes of the existing ad-hoc signature (Homebrew's / the linker's) are computed again here.
A codesign of the whole bundle afterwards (on a Mac) replaces them anyway.
"""
import argparse
import hashlib
import os
import re
import struct
import sys
import tarfile

MH_MAGIC_64 = 0xFEEDFACF
FAT_MAGIC = 0xCAFEBABE
FAT_MAGIC_64 = 0xCAFEBABF
LC_CODE_SIGNATURE = 0x1D
LC_ID_DYLIB = 0xD
DYLIB_COMMANDS = {0xC, 0x80000018, 0x8000001F, 0x20, 0x80000023}  # load, weak, reexport, lazy, upward
CSMAGIC_EMBEDDED_SIGNATURE = 0xFADE0CC0
CSMAGIC_CODEDIRECTORY = 0xFADE0C02
HASHES = {1: hashlib.sha1, 2: hashlib.sha256, 3: hashlib.sha256, 4: hashlib.sha384}
PLACEHOLDER = re.compile(r'^@@HOMEBREW_(?:PREFIX@@/opt/([^/]+)|CELLAR@@/([^/]+)/[^/]+)/lib/([^/]+\.dylib)$')


def slices(data):
    """(offset, size) of each Mach-O in the file (one, or one per architecture of a universal file)."""
    magic = struct.unpack_from('>I', data, 0)[0]
    if magic in (FAT_MAGIC, FAT_MAGIC_64):
        count = struct.unpack_from('>I', data, 4)[0]
        result = []
        for i in range(count):
            if magic == FAT_MAGIC:
                _, _, offset, size, _ = struct.unpack_from('>iiIII', data, 8 + i * 20)
            else:
                _, _, offset, size, _, _ = struct.unpack_from('>iiQQII', data, 8 + i * 32)
            result.append((offset, size))
        return result
    return [(0, len(data))]


def load_commands(data, base):
    magic, _, _, _, ncmds, _, _, _ = struct.unpack_from('<IiiIIIII', data, base)
    if magic != MH_MAGIC_64:
        raise ValueError('not a 64-bit Mach-O')
    offset = base + 32
    for _ in range(ncmds):
        cmd, size = struct.unpack_from('<II', data, offset)
        yield cmd, offset, size
        offset += size


def dylib_name(data, offset, size):
    name_offset = struct.unpack_from('<I', data, offset + 8)[0]
    raw = bytes(data[offset + name_offset:offset + size])
    return raw.split(b'\0', 1)[0].decode('utf-8'), offset + name_offset, size - name_offset


def dependencies(data):
    """The libraries a Mach-O loads (of its first slice)."""
    base = slices(data)[0][0]
    return [dylib_name(data, o, s)[0] for c, o, s in load_commands(data, base) if c in DYLIB_COMMANDS]


def rename(data, rewrite):
    """Rewrites the dylib names (and the library's own id) for which rewrite(name) gives a new one."""
    for base, _ in slices(data):
        for cmd, offset, size in list(load_commands(data, base)):
            if cmd in DYLIB_COMMANDS or cmd == LC_ID_DYLIB:
                name, start, room = dylib_name(data, offset, size)
                new = rewrite(name)
                if new is None or new == name:
                    continue
                encoded = new.encode('utf-8')
                if len(encoded) + 1 > room:
                    raise ValueError(f'no room for {new} in place of {name}')
                data[start:start + room] = encoded + b'\0' * (room - len(encoded))


def rehash(data):
    """Computes the code page hashes of every code directory of the existing signature again."""
    for base, size in slices(data):
        signature = [(o, s) for c, o, s in load_commands(data, base) if c == LC_CODE_SIGNATURE]
        if not signature:
            raise ValueError('no code signature to update (macOS on Apple silicon needs one)')
        dataoff, _ = struct.unpack_from('<II', data, signature[0][0] + 8)
        blob = base + dataoff
        magic, _, count = struct.unpack_from('>III', data, blob)
        if magic != CSMAGIC_EMBEDDED_SIGNATURE:
            raise ValueError('unknown code signature')
        for i in range(count):
            _, offset = struct.unpack_from('>II', data, blob + 12 + i * 8)
            directory = blob + offset
            if struct.unpack_from('>I', data, directory)[0] != CSMAGIC_CODEDIRECTORY:
                continue  # requirements, entitlements, CMS: not hashes of the code
            (_, _, version, _, hash_offset, _, _, code_slots, code_limit, hash_size, hash_type, _, page_log) = \
                struct.unpack_from('>IIIIIIIIIBBBB', data, directory)
            if version >= 0x20300:
                limit64 = struct.unpack_from('>Q', data, directory + 56)[0]
                code_limit = limit64 or code_limit
            page = 1 << page_log if page_log else code_limit
            algorithm = HASHES[hash_type]
            for slot in range(code_slots):
                start = base + slot * page
                end = base + min((slot + 1) * page, code_limit)
                digest = algorithm(bytes(data[start:end])).digest()[:hash_size]
                at = directory + hash_offset + slot * hash_size
                data[at:at + hash_size] = digest


def bottle_libraries(folder):
    """(formula, file name) -> bytes of every library in the bottles (links followed)."""
    libraries = {}
    for name in sorted(os.listdir(folder)):
        if not name.endswith('.tar.gz'):
            continue
        with tarfile.open(os.path.join(folder, name)) as archive:
            members = {m.name: m for m in archive.getmembers()}
            for member in members.values():
                parts = member.name.split('/')
                if len(parts) != 4 or parts[2] != 'lib' or not parts[3].endswith('.dylib'):
                    continue
                target = member
                while target is not None and target.issym():
                    target = members.get('/'.join(parts[:3] + [target.linkname]))
                if target is not None and target.isfile():
                    libraries[(parts[0], parts[3])] = archive.extractfile(target).read()
    return libraries


def bundle(bottles, out, root):
    libraries = bottle_libraries(bottles)
    start = [key for key in libraries if key[1] == root]
    if not start:
        raise SystemExit(f'{root} is not in the bottles of {bottles}')
    pending, done = [start[0]], {}
    while pending:
        key = pending.pop()
        if key[1] in done:
            continue
        data = bytearray(libraries[key])
        for name in dependencies(data):
            match = PLACEHOLDER.match(name)
            if match:
                formula = match.group(1) or match.group(2)
                if (formula, match.group(3)) not in libraries:
                    raise SystemExit(f'{key[1]} needs {name}: that bottle is missing')
                pending.append((formula, match.group(3)))

        def rewrite(name):
            match = PLACEHOLDER.match(name)
            return '@loader_path/' + match.group(3) if match else None

        rename(data, rewrite)
        rehash(data)
        done[key[1]] = data
    os.makedirs(out, exist_ok=True)
    for name, data in done.items():
        with open(os.path.join(out, name), 'wb') as file:
            file.write(data)
        print('bundled', name)
    for name, data in done.items():
        left = [d for d in dependencies(data) if d.startswith('@@HOMEBREW')]
        if left:
            raise SystemExit(f'{name} still loads {left}')


def selftest(path):
    original = open(path, 'rb').read()
    data = bytearray(original)
    rehash(data)
    if bytes(data) != original:
        raise SystemExit(f'FAILED: the hashes of {path} came out different')
    print(f'ok: {path} ({len(slices(original))} slice(s))')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--bottles')
    parser.add_argument('--out')
    parser.add_argument('--root', default='libmtp.9.dylib')
    parser.add_argument('--selftest', nargs='*')
    args = parser.parse_args()
    if args.selftest:
        for path in args.selftest:
            selftest(path)
        return
    if not args.bottles or not args.out:
        parser.error('--bottles and --out are needed')
    bundle(args.bottles, args.out, args.root)


if __name__ == '__main__':
    sys.exit(main())
