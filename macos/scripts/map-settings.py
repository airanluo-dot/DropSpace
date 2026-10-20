#!/usr/bin/env python3
"""Reconcile reviewed settings records without resetting their evidence or prose."""
import argparse
import json
import pathlib

root = pathlib.Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--check", action="store_true", help="Report drift without changing files.")
args = parser.parse_args()
path = root / "docs/settings-inventory.json"
data = json.loads(path.read_text())
rows = {}
for line in (root / "docs/SETTINGS.md").read_text().splitlines():
    if not line.startswith("| ") or line.count("|") != 5:
        continue
    cells = [value.strip() for value in line.split("|")[1:-1]]
    if "." not in cells[0]:
        continue
    if cells[0] in rows:
        raise SystemExit("Duplicate reviewed settings identity: " + cells[0])
    rows[cells[0]] = cells

identities = {field["id"] for field in data["fields"]}
if identities != rows.keys():
    raise SystemExit("Settings identities differ; review the immutable inventory and table before syncing.")
changed = []
for field in data["fields"]:
    _, mapping, status, difference = rows[field["id"]]
    if not all((mapping, status, difference)):
        raise SystemExit("Incomplete reviewed row: " + field["id"])
    values = {"macMapping": None if mapping == "—" else mapping, "status": status, "difference": difference}
    if any(field.get(key) != value for key, value in values.items()):
        changed.append(field["id"])
    field.update(values)

if args.check and changed:
    raise SystemExit("Settings record drift: " + ", ".join(changed))
if not args.check and changed:
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n")
print(f"Reviewed {len(identities)} settings identities; {len(changed)} differing records. "
      "Source facts, Mac-only additions and manual Markdown are preserved.")
