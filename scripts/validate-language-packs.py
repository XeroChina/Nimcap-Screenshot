"""Check language packs without network access or launching the application."""

from collections import Counter
from pathlib import Path
import json
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
RESOURCES = ROOT / "Libraries/NimCap.Screenshot.Core/Resources"
EXPECTED = "zh-CN".split()
FIELDS = re.compile(r"\{[^{}]+\}")


def load(path):
    entries = ET.parse(path).getroot().findall("data")
    values = {entry.attrib["name"]: entry.findtext("value", "") for entry in entries}
    if len(values) != len(entries):
        raise ValueError(f"Duplicate keys: {path.name}")
    return values


def main():
    source = load(RESOURCES / "Strings.resx")
    errors = []
    for name in ("capture-guide-translations.json", "desktop-ruler-translations.json"):
        guides = json.loads((ROOT / "scripts" / name).read_text(encoding="utf-8"))
        for culture, language in guides["cultures"].items():
            suffix = "" if culture == "en" else "." + culture
            path = RESOURCES / f"Strings{suffix}.resx"
            if not path.exists():
                continue
            actual = load(path)
            for key, value in zip(guides["keys"], guides["translations"][language], strict=True):
                if actual.get(key) != value:
                    errors.append(f"{culture}/{key}: guide translation differs from reviewed UTF-8 text")
                if "\ufffd" in value or re.search(r"\?{2,}", value):
                    errors.append(f"{culture}/{key}: damaged guide translation")
    for code in EXPECTED:
        path = RESOURCES / f"Strings.{code}.resx"
        if not path.exists():
            errors.append(f"Missing language pack: {code}")
            continue
        translated = load(path)
        if source.keys() != translated.keys():
            errors.append(f"{code}: missing or extra resource keys")
        for key in source.keys() & translated.keys():
            value, original = translated[key], source[key]
            if not value.strip():
                errors.append(f"{code}/{key}: empty translation")
            if Counter(FIELDS.findall(value)) != Counter(FIELDS.findall(original)):
                errors.append(f"{code}/{key}: changed format parameters or template fields")
            if "|*" in original and original.split("|")[1::2] != value.split("|")[1::2]:
                errors.append(f"{code}/{key}: changed file-dialog mask")
            if re.search(r"ZXQ\s*\d+\s*QXZ|[\ue000\ue001]", value, re.I):
                errors.append(f"{code}/{key}: leaked translation marker")
        changed = sum(translated[key] != original for key, original in source.items() if key in translated)
        if changed < len(source) // 2:
            errors.append(f"{code}: most strings are untranslated")
        print(f"{code}: {len(translated)} keys, {changed} translated values")
    if errors:
        print("\n".join(errors))
        return 1
    print(f"PASS: {len(EXPECTED)} language packs, {len(source)} keys per pack; fields, masks and markers checked.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
