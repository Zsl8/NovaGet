#!/usr/bin/env python3
"""Maintain NovaGet .resx string tables.

  tools/resx.py set <file.resx> KEY "value" [KEY "value" ...]   add or replace entries
  tools/resx.py load <file.resx> <entries.txt>                   KEY=value lines (# comments, \\n escapes)
  tools/resx.py keys <file.resx>                                  list keys

Entries are kept sorted by key so diffs stay small.
"""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""


def read(path: Path) -> dict:
    if not path.exists():
        return {}
    root = ET.parse(path).getroot()
    return {d.get("name"): (d.findtext("value") or "") for d in root.findall("data")}


def write(path: Path, entries: dict) -> None:
    def esc(text: str) -> str:
        return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

    lines = [HEADER]
    for key in sorted(entries, key=str.lower):
        lines.append(f'  <data name="{esc(key)}" xml:space="preserve"><value>{esc(entries[key])}</value></data>\n')
    lines.append("</root>\n")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("".join(lines), encoding="utf-8")


def main(argv: list) -> int:
    if len(argv) < 3:
        print(__doc__)
        return 1
    command, path = argv[1], Path(argv[2])
    entries = read(path)
    if command == "set":
        pairs = argv[3:]
        for key, value in zip(pairs[::2], pairs[1::2]):
            entries[key] = value
        write(path, entries)
    elif command == "load":
        for raw in Path(argv[3]).read_text(encoding="utf-8").splitlines():
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            entries[key.strip()] = value.strip().replace("\\n", "\n")
        write(path, entries)
    elif command == "keys":
        print("\n".join(sorted(entries)))
    else:
        print(__doc__)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
