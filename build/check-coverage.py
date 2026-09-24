"""Fails when the schema engine's core falls below the coverage target (Definition of done, M3).

Usage: python3 build/check-coverage.py TestResults/coverage.cobertura.xml 90
"""

import sys
import xml.etree.ElementTree as ET

# The pure core of the schema engine: the differ decides what changes, the renderer writes the SQL.
TARGETS = ("SchemaEngine/SchemaDiffer.cs", "Engine/MySqlSqlRenderer.cs")


def main(report: str, minimum: float) -> int:
    lines_by_file: dict[str, dict[int, bool]] = {}
    for cls in ET.parse(report).getroot().iter("class"):
        filename = cls.get("filename", "").replace("\\", "/")
        target = next((t for t in TARGETS if filename.endswith(t)), None)
        if target is None:
            continue
        lines = lines_by_file.setdefault(target, {})
        for line in cls.iter("line"):
            number = int(line.get("number"))
            lines[number] = lines.get(number, False) or int(line.get("hits")) > 0

    failed = False
    for target in TARGETS:
        lines = lines_by_file.get(target)
        if not lines:
            print(f"{target}: not in the report")
            failed = True
            continue
        percent = 100 * sum(lines.values()) / len(lines)
        ok = percent >= minimum
        failed |= not ok
        print(f"{target}: {percent:.1f}% line coverage ({'ok' if ok else f'below {minimum:g}%'})")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], float(sys.argv[2])))
