"""Apply reviewed capture-guide translations locally, preserving resource formatting."""
import json
from pathlib import Path
import re
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]


def apply_catalog(catalog):
    for culture, language in catalog["cultures"].items():
        suffix = "" if culture == "en" else "." + culture
        path = ROOT / f"Libraries/NimCap.Screenshot.Core/Resources/Strings{suffix}.resx"
        if not path.exists():
            continue
        content = path.read_text(encoding="utf-8-sig")
        for key, value in zip(catalog["keys"], catalog["translations"][language], strict=True):
            pattern = r'(<data name="' + re.escape(key) + r'"[^>]*>\s*<value>).*?(</value>\s*</data>)'
            content, count = re.subn(pattern, lambda match: match[1] + escape(value) + match[2], content, flags=re.S)
            if not count:
                entry = f'  <data name="{key}" xml:space="preserve"><value>{escape(value)}</value></data>\n'
                content = content.replace("</root>", entry + "</root>")
        path.write_text(content, encoding="utf-8")
    print(f'Applied {len(catalog["keys"])} reviewed translations to {len(catalog["cultures"])} resource packs.')


def main():
    for name in ("capture-guide-translations.json", "desktop-ruler-translations.json"):
        apply_catalog(json.loads((ROOT / "scripts" / name).read_text(encoding="utf-8")))


if __name__ == "__main__":
    main()
