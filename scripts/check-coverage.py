#!/usr/bin/env python3
"""Enforce coverage of the event and operation logic from a Cobertura report."""

import glob
import os
import sys
import xml.etree.ElementTree as ET


reports = glob.glob(os.path.join(sys.argv[1], "**", "coverage.cobertura.xml"), recursive=True)
if not reports:
    raise SystemExit("Expected at least one coverage report")

roots = [ET.parse(report).getroot() for report in reports]
lines_valid = sum(int(root.attrib["lines-valid"]) for root in roots)
lines_covered = sum(int(root.attrib["lines-covered"]) for root in roots)
branches_valid = sum(int(root.attrib["branches-valid"]) for root in roots)
branches_covered = sum(int(root.attrib["branches-covered"]) for root in roots)
overall = lines_covered / lines_valid if lines_valid else 0
branch = branches_covered / branches_valid if branches_valid else 0
minimums = {
    "Aimie.Shared/EventProcessing.cs": 0.95,
    "Aimie.Operations/GenerationHandler.cs": 0.95,
    "Aimie.Operations/DocumentGenerationHandler.cs": 0.95,
}
failed = overall < 0.65 or branch < 0.65
print(f"Overall line coverage: {overall:.1%} (minimum 65%)")
print(f"Overall branch coverage: {branch:.1%} (minimum 65%)")

for filename, minimum in minimums.items():
    lines = {}
    for root in roots:
        for cls in root.findall(".//class"):
            if cls.attrib["filename"] == filename:
                for line in cls.findall(".//line"):
                    number = int(line.attrib["number"])
                    lines[number] = max(lines.get(number, 0), int(line.attrib["hits"]))
    rate = sum(hits > 0 for hits in lines.values()) / len(lines) if lines else 0
    print(f"{filename}: {rate:.1%} (minimum {minimum:.0%})")
    failed |= rate < minimum

if failed:
    raise SystemExit("Coverage is below the required level")
