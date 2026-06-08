"""Package TrioAI as TrioAI.MPPlugin (ZIP with TrioAI/ subdirectory structure)."""
import zipfile
from pathlib import Path

ROOT = Path(__file__).parent
SRC = ROOT / "bin" / "Release"
OUT = ROOT / "TrioAI.MPPlugin"

files = [
    ("TrioAI.MPPlugIn.dll", "TrioAI/TrioAI.MPPlugIn.dll"),
    ("skills/triobasic/index.json", "TrioAI/skills/triobasic/index.json"),
    ("skills/triobasic/skills.json", "TrioAI/skills/triobasic/skills.json"),
]

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
print(f"Size: {OUT.stat().st_size} bytes\n")
print("Contents:")
with zipfile.ZipFile(OUT, "r") as z:
    for info in z.infolist():
        print(f"  {info.filename}  ({info.file_size} bytes)")
