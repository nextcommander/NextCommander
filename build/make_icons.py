"""Puts the application icon in place from the designer's files in build/icons.

python build/make_icons.py

Sources (build/icons):
  nextcommander.ico      Windows: 16, 24, 32, 48, 64, 128, 256 px
  nextcommander.icns     macOS: 16 ... 1024 px (Retina sizes too)
  png/<size>.png         16, 24, 32, 48, 64, 96, 128, 256, 512, 1024 px (Linux hicolor theme and the window icon)
  nextcommander.svg      Linux: the scalable icon (hicolor/scalable)

Written:
  src/NextCommander.App/Assets/NextCommander.ico / .icns / .png (256 px window icon)
build/publish.ps1 copies png/<size>.png and the svg into the Linux packages as hicolor icons (tar.gz, deb, rpm).
"""
import os
import shutil

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "icons")
ASSETS = os.path.join(HERE, "..", "src", "NextCommander.App", "Assets")


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    shutil.copyfile(os.path.join(SOURCE, "nextcommander.ico"), os.path.join(ASSETS, "NextCommander.ico"))
    shutil.copyfile(os.path.join(SOURCE, "nextcommander.icns"), os.path.join(ASSETS, "NextCommander.icns"))
    shutil.copyfile(os.path.join(SOURCE, "png", "256.png"), os.path.join(ASSETS, "NextCommander.png"))
    print("icons written to", os.path.normpath(ASSETS))
