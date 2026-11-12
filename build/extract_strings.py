"""Collects the (English) user interface texts of the application that are not yet in the language files.

python build/extract_strings.py [out.json]
Prints/writes {"suggested key": {"en": "English text", "files": [...]}} for texts missing from en.json (key format:
see l10n_keys.py). Add each to en.json (key: English text) and tr.json (key: Turkish translation). Interpolated strings
become patterns: $"Delete {n} files?" -> "Delete {0} files?". Texts that still contain Turkish letters are listed under
"_turkish" (the code is English; Turkish belongs in tr.json).
"""
import html
import json
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
SRC = os.path.join(ROOT, "src")
TURKISH = re.compile(r"[çğıöşüÇĞİÖŞÜ]")
WORDS = re.compile(r"\b(ve|bir|için|ile|dosya|dosyalar|klasör|Tamam|İptal|Kopyala|Taşı|Sil|Yeni|Ara|Aç|Kapat|Bul|Sonraki|Önceki|Seç|Göster|"
                   r"Gizle|Metin|Resim|Bilgi|Hata|Uyarı|Evet|Hayır|Durdur|Başlat|Kaydet|Yükle|Ekle|Kaldır|Düzenle|Ayarlar|Sekme|"
                   r"Panel|Sol|Sağ|Üst|Alt|Ad|Boyut|Tarih|Saat|Uzantı|Öznitelik|Bağlantı|Parola|Kullanıcı|Sunucu|Port|Hız|Sınır|"
                   r"Hepsi|Tümü|Hiçbiri|Atla|Yeniden|Dene|Arka|plan|kuyrukta|Bitti|tarandı|sonuç|bayt|öğe|satır|sütun|"
                   r"Biçim|Sıkıştırma|Paketle|Görüntüle|Medya|Önizleme|Eklenti|Komut|Komutlar|Gezinme|Görünüm|Ağ|Dosya|İşaretle|"
                   r"Sekmeler|Diğer|Yazdır|Çıkış|Kodla|Kodu|Hotlist|Geri|İleri|Kök|Yol|Yolu|Filtre|Sıralama|Sırala|Tema|Dil|"
                   r"Sistem|Açık|Koyu|Kısa|Ayrıntılı|Küçük|Büyük|harf|kelime|Tam|Regex|Hex|İkili|Kodlama|Kaydırma|Liste|Ters|"
                   r"Örnek|yok|var|mi|mı|değil|olarak|olan|gibi|kadar|sonra|önce|şimdi|tümünü|hepsini|seçili|seçim|Seçim)\b")
SKIP_LINE = re.compile(r"(Log\.(Debug|Information|Warning|Error|Verbose|Fatal)|\bLog(Debug|Information|Warning|Error)\b|GeneratedRegex|"
                       r"\[InlineData|Assert\.|nameof\(|DllImport|LibraryImport|Guid\(|EnumeratorCancellation|ExcludeFromCodeCoverage|"
                       r"GetManifestResourceStream|Environment\.GetEnvironmentVariable)")


def decode_cs(body, verbatim):
    if verbatim:
        return body.replace('""', '"')
    out = []
    i = 0
    while i < len(body):
        c = body[i]
        if c == "\\" and i + 1 < len(body):
            n = body[i + 1]
            mapping = {"n": "\n", "r": "\r", "t": "\t", '"': '"', "\\": "\\", "'": "'", "0": "\0"}
            if n in mapping:
                out.append(mapping[n])
                i += 2
                continue
            if n == "u" and i + 5 < len(body):
                out.append(chr(int(body[i + 2:i + 6], 16)))
                i += 6
                continue
            out.append(n)
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


def interpolation_to_pattern(body):
    """Replaces {expr} holes of an interpolated string body with {0}, {1}...; '{{' and '}}' stay literal braces."""
    out = []
    i = 0
    index = 0
    while i < len(body):
        c = body[i]
        if c == "{" and body[i:i + 2] == "{{":
            out.append("{")
            i += 2
            continue
        if c == "}" and body[i:i + 2] == "}}":
            out.append("}")
            i += 2
            continue
        if c == "{":
            depth = 1
            j = i + 1
            in_str = False
            while j < len(body) and depth > 0:
                ch = body[j]
                if ch == '"' and body[j - 1] != "\\":
                    in_str = not in_str
                elif not in_str:
                    if ch == "{":
                        depth += 1
                    elif ch == "}":
                        depth -= 1
                j += 1
            out.append("{" + str(index) + "}")
            index += 1
            i = j
            continue
        out.append(c)
        i += 1
    return "".join(out)


# $@"..", @$"..", $"..", @"..", ".." (no raw/multiline strings in UI code)
CS_STRING = re.compile(r'(\$@|@\$|\$|@)?"((?:[^"\\\n]|\\.|"")*)"')


def is_ui_text(text):
    """A text a user reads: words with spaces, or one capitalized word ("Copy"); not identifiers, keys, paths or code."""
    if text.startswith("_") and len(text) > 1:
        text = text[1:]  # menu access key
    if not re.search(r"[A-Za-zÇĞİÖŞÜçğıöşü]", text):
        return False
    if TURKISH.search(text):
        return True
    if text.startswith(("nc_", "uc_", "avares://", "http://", "https://", "#", "Fm", "Mi", "Ov", "/", ".", "-")) and " " not in text:
        return False
    if re.search(r"(\\\\|\*\.|::|=>|\(\)|</?[a-z]+>|\{x:|\bSystem\.|\.(json|exe|dll|txt|cs)\b)", text):
        return False
    if " " not in text.strip():
        # one word: "Copy", "Hidden" yes; "FileList", "ColorThemeFile", "text.lines", "utf-8" no
        word = text.strip().rstrip(":.…")
        return bool(re.fullmatch(r"[A-Z][a-z]+(\.\.\.)?", word))
    return bool(re.search(r"[A-Za-z]{2,} [A-Za-z{]", text))


def scan_cs(path, found):
    rel = os.path.relpath(path, ROOT)
    for line in open(path, encoding="utf-8"):
        stripped = line.strip()
        if stripped.startswith("//") or stripped.startswith("///") or SKIP_LINE.search(line):
            continue
        for m in CS_STRING.finditer(line):
            prefix, body = m.group(1) or "", m.group(2)
            verbatim = "@" in prefix
            text = decode_cs(body, verbatim)
            if "$" in prefix:
                text = interpolation_to_pattern(text)
            if is_ui_text(text):
                found.setdefault(text, set()).add(rel)


ATTR = re.compile(r'\b(Header|Content|Text|Title|Watermark|ToolTip\.Tip|InputGesture)="([^"]*)"')


def scan_axaml(path, found):
    rel = os.path.relpath(path, ROOT)
    for line in open(path, encoding="utf-8"):
        for m in ATTR.finditer(line):
            if m.group(1) == "InputGesture":
                continue
            text = html.unescape(m.group(2))
            if text.startswith("{"):
                continue
            if is_ui_text(text):
                found.setdefault(text, set()).add(rel)


def main():
    found = {}
    for folder, _, files in os.walk(SRC):
        if os.sep + "obj" in folder or os.sep + "bin" in folder:
            continue
        for name in files:
            path = os.path.join(folder, name)
            if name.endswith(".cs"):
                scan_cs(path, found)
            elif name.endswith(".axaml"):
                scan_axaml(path, found)
    import l10n_keys
    loc = os.path.join(SRC, "NextCommander.App", "Localization")
    en = {}
    if os.path.exists(os.path.join(loc, "en.json")):
        en = json.load(open(os.path.join(loc, "en.json"), encoding="utf-8"))
    known = set(en.values())
    texts = [k for k in found if k not in known and k.strip() not in known and not TURKISH.search(k)]
    turkish = {t: sorted(found[t]) for t in found if TURKISH.search(t)}
    occurrences = l10n_keys.scan_occurrences()
    keys = l10n_keys.assign_keys(texts, {t: t for t in texts}, occurrences, taken={k: v for k, v in en.items()})
    missing = {keys[t]: {"en": t, "files": sorted(found[t])} for t in sorted(texts, key=lambda t: keys[t])}
    if turkish:
        missing["_turkish"] = turkish
    out = json.dumps(missing, ensure_ascii=False, indent=1)
    if len(sys.argv) > 1:
        open(sys.argv[1], "w", encoding="utf-8").write(out)
        print(f"{len(found)} texts, {len(texts)} not in en.json, {len(turkish)} with Turkish letters -> {sys.argv[1]}")
    else:
        print(out)


if __name__ == "__main__":
    main()
