"""Keys of the language files (src/NextCommander.App/Localization/*.json).

The code keeps plain English texts; a language file maps readable keys to texts:
  en.json  {"Menu.nc_Copy": "Copy...",    "Settings.Display.ShowHiddenFiles": "Show hidden files", ...}
  tr.json  {"Menu.nc_Copy": "Kopyala...", "Settings.Display.ShowHiddenFiles": "Gizli dosyaları göster", ...}
At run time the English text of en.json links a text in the code to its translation through the key.

Key = <Form>.<Item>[.<Option>]:
  Menu.<command>                 menu items of the main window (Menu.File for the top menus)
  Command.<command>              descriptions of the commands (command browser, button tips)
  Settings.<Page>.<Label>        a setting; .Description its help text; Settings.<Page>.Group.<Group> a group title
  <Form>.<Item>                  any other text: Form = the file it is used in (CopyDialog, Lister, MainWindow ...),
                                 Item = the English text in PascalCase (at most five words)
"""
import html
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import extract_strings as ex  # noqa: E402

ROOT = ex.ROOT
SRC = ex.SRC

REGISTER = re.compile(r'\.Register\("((?:cm|em)_\w+)"')
COMMAND_PARAM = re.compile(r'CommandParameter="([^"]+)"')
SETTING = re.compile(r'\b(\w+)\((\w+), "((?:[^"\\]|\\.)*)", "((?:[^"\\]|\\.)*)"(?:,\s*(?:"((?:[^"\\]|\\.)*)"|null))?')
FORM_SUFFIXES = ("ViewModel",)


def scan_occurrences():
    """{text: [(relative file, line), ...]} for every UI text of the sources."""
    found = {}

    def add(text, rel, line):
        found.setdefault(text, []).append((rel, line))

    for folder, _, files in os.walk(SRC):
        if os.sep + "obj" in folder or os.sep + "bin" in folder:
            continue
        for name in sorted(files):
            path = os.path.join(folder, name)
            rel = os.path.relpath(path, ROOT).replace("\\", "/")
            if name.endswith(".cs"):
                for line in open(path, encoding="utf-8"):
                    stripped = line.strip()
                    if stripped.startswith("//") or ex.SKIP_LINE.search(line):
                        continue
                    for m in ex.CS_STRING.finditer(line):
                        prefix, body = m.group(1) or "", m.group(2)
                        text = ex.decode_cs(body, "@" in prefix)
                        if "$" in prefix:
                            text = ex.interpolation_to_pattern(text)
                        if ex.is_ui_text(text):
                            add(text, rel, line)
            elif name.endswith(".axaml"):
                for line in open(path, encoding="utf-8"):
                    for m in ex.ATTR.finditer(line):
                        if m.group(1) == "InputGesture":
                            continue
                        text = html.unescape(m.group(2))
                        if not text.startswith("{") and ex.is_ui_text(text):
                            add(text, rel, line)
    return found


TR_ASCII = str.maketrans("çğıöşüÇĞİÖŞÜ", "cgiosuCGIOSU")


def pascal(text, words=5):
    """'Show hidden files' -> 'ShowHiddenFiles' (letters and digits only)."""
    parts = re.findall(r"[A-Za-z0-9]+", text.translate(TR_ASCII).replace("{0}", " ").replace("{1}", " "))
    parts = [p for p in parts if not re.fullmatch(r"\d+", p)] or parts
    return "".join(p[:1].upper() + p[1:] for p in parts[:words]) or "Text"


def form_of(rel):
    """src/NextCommander.App/Views/Dialogs/CopyDialog.cs -> CopyDialog; MainWindowViewModel.Menus.cs -> MainWindow."""
    name = os.path.basename(rel)
    name = name.split(".")[0]
    for suffix in FORM_SUFFIXES:
        if name.endswith(suffix) and len(name) > len(suffix):
            name = name[: -len(suffix)]
    return name


def unescape_cs(text):
    return ex.decode_cs(text, False)


def key_candidates(text, english, occurrences):
    """Keys for one text, most specific first (setting, menu item, command, form of the first use, Common)."""
    settings, menus, commands, forms = [], [], [], []
    item = pascal(english or text)
    for rel, line in occurrences:
        if REGISTER.search(line):
            commands.append("Command." + REGISTER.search(line).group(1))
        if rel.endswith(".axaml") and "MenuItem" in line:
            param = COMMAND_PARAM.search(line)
            menus.append("Menu." + (re.sub(r"[^A-Za-z0-9_]+", "_", param.group(1)).strip("_") if param else item))
        if rel.endswith("SettingsWindow.cs"):
            m = SETTING.search(line)
            if m:
                page, group, label, desc = m.group(2), unescape_cs(m.group(3)), unescape_cs(m.group(4)), m.group(5)
                desc = unescape_cs(desc) if desc else None
                page_key = page[:1].upper() + page[1:]
                label_key = pascal(english_of(label) or label)
                if text == label:
                    settings.append(f"Settings.{page_key}.{label_key}")
                elif desc is not None and text == desc:
                    settings.append(f"Settings.{page_key}.{label_key}.Description")
                elif text == group:
                    settings.append(f"Settings.{page_key}.Group.{item}")
        forms.append(f"{form_of(rel)}.{item}")
    return settings + menus + commands + forms + ["Common." + item]


_english = {}


def english_of(text):
    return _english.get(text)


def assign_keys(texts, english, occurrences, taken=None):
    """{text: key} for the texts, keys unique (a number is added on a clash)."""
    _english.clear()
    _english.update(english)
    used = dict(taken or {})  # key -> text
    result = {}
    for text in sorted(texts, key=lambda t: (len(occurrences.get(t, [])) == 0, t)):
        candidates = key_candidates(text, english.get(text), occurrences.get(text, []))
        chosen = next((k for k in candidates if k not in used), None)
        if chosen is None:
            base = candidates[0]
            n = 2
            while f"{base}{n}" in used:
                n += 1
            chosen = f"{base}{n}"
        used[chosen] = text
        result[text] = chosen
    return result
