"""Zips a .app bundle with Unix permissions (the executable bit), for packing a macOS build on Windows / Linux.

Usage: python build/macos/zip_app.py <path/to/NextCommander.app> <out.zip>

A zip written by Windows tools stores no Unix modes; unpacked on macOS the program is then not executable and the app
does not start. Here every entry is marked as made on Unix: folders 755, Contents/MacOS/* 755, the rest 644.
"""
import os
import sys
import time
import zipfile


def main(app: str, destination: str) -> None:
    app = os.path.abspath(app)
    parent = os.path.dirname(app)
    temporary = destination + ".tmp"
    write(app, parent, temporary)
    # the old zip may be open for a moment (virus scanner, a copy in progress): retry the swap
    for attempt in range(10):
        try:
            os.replace(temporary, destination)
            return
        except PermissionError:
            if attempt == 9:
                raise
            time.sleep(2)


def write(app: str, parent: str, destination: str) -> None:
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for folder, folders, files in os.walk(app):
            folders.sort()
            relative = os.path.relpath(folder, parent).replace(os.sep, "/")
            info = zipfile.ZipInfo(relative + "/")
            info.create_system = 3
            info.external_attr = (0o40755 << 16) | 0x10
            archive.writestr(info, b"")
            executable = relative.endswith("/Contents/MacOS")
            for name in sorted(files):
                path = os.path.join(folder, name)
                info = zipfile.ZipInfo.from_file(path, relative + "/" + name)
                info.create_system = 3
                info.external_attr = (0o100755 if executable else 0o100644) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                with open(path, "rb") as source:
                    archive.writestr(info, source.read())


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
