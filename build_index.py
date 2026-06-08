#!/usr/bin/env python3
"""
Build a slim index.json from skills.json.
Each entry in skills.json is at 2-space indentation: '  {' to '  }'.
Output format: [{"name": "ABS", "type": "...", "start": 2, "end": 18}, ...]
"""
import json
import sys
from pathlib import Path

skills_path = Path(__file__).parent / "skills.json"
index_path = Path(__file__).parent / "index.json"

# Load and parse the full file once to get name+type for each entry
with open(skills_path, "r", encoding="utf-8-sig") as f:
    entries = json.load(f)

# Walk line-by-line to find each top-level array element's line range.
# Top-level elements are at exactly 2-space indentation: '  {' / '  }'
ranges = []
current_start = None
depth = 0  # depth inside an entry, 0 = between elements

with open(skills_path, "r", encoding="utf-8-sig") as f:
    for lineno, raw in enumerate(f, start=1):
        line = raw.rstrip("\n").rstrip("\r")
        if line == "  {":
            current_start = lineno
            depth = 1
            continue
        if depth > 0:
            # closing line is '  }' or '  },' (last entry has no comma)
            if line == "  }" or line == "  },":
                ranges.append((current_start, lineno))
                depth = 0
                current_start = None

if len(ranges) != len(entries):
    print(f"ERROR: found {len(ranges)} ranges but {len(entries)} entries", file=sys.stderr)
    sys.exit(1)

# Build the slim index
slim = []
for (start, end), entry in zip(ranges, entries):
    name = entry.get("name")
    typ = entry.get("type", "")
    if not name:
        print(f"WARNING: entry at lines {start}-{end} has no name", file=sys.stderr)
        continue
    # Trim description to ~80 chars (single line, no embedded newlines)
    desc = entry.get("description", "") or ""
    desc = " ".join(desc.split())  # collapse whitespace
    if len(desc) > 80:
        desc = desc[:77].rstrip() + "..."
    slim.append({"name": name, "type": typ, "desc": desc, "start": start, "end": end})

# Write compact JSON (1 line per entry, matching original index.json style)
lines_out = ["["]
for i, e in enumerate(slim):
    comma = "," if i < len(slim) - 1 else ""
    obj = {
        "name": e["name"],
        "type": e["type"],
        "desc": e["desc"],
        "start": e["start"],
        "end": e["end"],
    }
    payload = json.dumps(obj, ensure_ascii=False, separators=(", ", ": "))
    lines_out.append(" " + payload + comma)
lines_out.append("]")
index_path.write_text("\n".join(lines_out) + "\n", encoding="utf-8")

print(f"Wrote {len(slim)} entries to {index_path}")
print(f"First 3: {slim[:3]}")
print(f"Last: {slim[-1]}")
