"""Package TrioAI as TrioAI.MPPlugin (ZIP with TrioAI/ subdirectory structure).

Picks up:
  - DLL from bin/Release/
  - Every file under skills/<name>/ recursively (HTML pages + _files images +
    hhc + index.json). New skill packs (iec, plcopen, ...) are picked up
    automatically — no need to edit this file when adding one.
"""
import zipfile
from pathlib import Path

ROOT = Path(__file__).parent
SRC = ROOT / "bin" / "Release"
OUT = ROOT / "TrioAI.MPPlugin"

files = [("TrioAI.MPPlugIn.dll", "TrioAI/TrioAI.MPPlugIn.dll")]

# Recursively include skills/<name>/** from bin/Release/skills (the .csproj
# mirrors skills/ -> bin/Release/skills via the skills\** glob).
skills_src = SRC / "skills"
if skills_src.exists():
    for p in sorted(skills_src.rglob("*")):
        if p.is_file():
            rel = p.relative_to(SRC).as_posix()
            files.append((rel, "TrioAI/" + rel))

if OUT.exists():
    bak = ROOT / "TrioAI.MPPlugin.bak"
    if bak.exists():
        bak.unlink()
    OUT.rename(bak)
    print(f"Backed up old package to {bak.name}")

with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for rel_src, rel_dst in files:
        src_file = SRC / rel_src
        if not src_file.exists():
            print(f"MISSING: {src_file}")
            continue
        z.write(src_file, rel_dst)
        print(f"  + {rel_dst}  ({src_file.stat().st_size} bytes)")

print(f"\nCreated: {OUT}")
print(f"Size: {OUT.stat().st_size} bytes ({OUT.stat().st_size / 1024 / 1024:.1f} MB)\n")
print("Contents (summary by top-level dir):")
with zipfile.ZipFile(OUT, "r") as z:
    by_top = {}
    for info in z.infolist():
        top = info.filename.split("/")[1] if "/" in info.filename else info.filename
        by_top[top] = by_top.get(top, 0) + 1
    for k, v in sorted(by_top.items()):
        print(f"  {k}: {v} files")
