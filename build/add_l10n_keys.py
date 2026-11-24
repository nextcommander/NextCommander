"""Adds (or replaces) keys in the language files of a folder, every language at once.

python build/add_l10n_keys.py <keys.json> <folder>
    folder: src/NextCommander.App/Localization, or a plugin's languages folder
    keys.json: {"Form.Key": {"en": "...", "tr": "...", "de": "...", "es": "...", "fr": "...", "zh": "..."}, ...}

A new key goes after the last key of its group (the part before the first dot) that sorts before it, so the files keep
their order; an existing key gets the new text. The files are written as UTF-8 with LF line ends and two-space indent,
as they are kept. Write keys.json with an editor (not a shell heredoc: it can mangle \\n and quotes).
"""
import json
import sys

LANGUAGES = ["en", "tr", "de", "es", "fr", "zh"]


def main() -> None:
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    keys = json.load(open(sys.argv[1], encoding="utf-8"))
    folder = sys.argv[2]
    for key, texts in keys.items():
        missing = [lang for lang in LANGUAGES if lang not in texts]
        if missing:
            sys.exit(f"{key}: no text for {', '.join(missing)}")
    for lang in LANGUAGES:
        path = f"{folder}/{lang}.json"
        data = json.load(open(path, encoding="utf-8"))
        items = list(data.items())
        for key, texts in keys.items():
            text = texts[lang]
            if key in data:
                items = [(k, text if k == key else v) for k, v in items]
                data[key] = text
                continue
            group = key.split(".")[0] + "."
            at = None
            for i, (k, _) in enumerate(items):
                if k.startswith(group):
                    if k < key:
                        at = i + 1
                    elif at is None:
                        at = i
            if at is None:
                at = len(items)
            items.insert(at, (key, text))
            data[key] = text
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(json.dumps(dict(items), ensure_ascii=False, indent=2) + "\n")
    print(f"{len(keys)} keys in {len(LANGUAGES)} files of {folder}")


if __name__ == "__main__":
    main()
