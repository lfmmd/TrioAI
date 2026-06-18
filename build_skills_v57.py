#!/usr/bin/env python3
"""
Build TrioAI skills from MP V5.7 mkdocs help ZIPs (en/zh x triobasic/iec/plcopen).

V5.7 help layout (per ZIP, unpacked):
  <COMMAND>.html      mkdocs page, real body inside
                      <article class="md-content__inner md-typeset">...</article>
  <COMMAND>/          per-command image folder (image1.png ...)  -- DROPPED
  index.html / 404.html  nav pages                               -- DROPPED
  assets/             shared mkdocs CSS/JS                        -- DROPPED

For each command page this script:
  1. extracts the <article> inner HTML (drops ~90% nav chrome),
  2. strips <img> tags (text-only API never renders images; image folders dropped),
  3. overwrites a CLEAN <COMMAND>.html under skills/<lib>/<lang>/,
  4. records name/type/desc into skills/<lib>/<lang>/index.json.

type extraction (best-effort, differs per lib):
  - TrioBASIC : <h2>Type</h2> value (e.g. "Axis Command")
  - IEC       : <em><strong>X</strong></em> right after <h1> (e.g. "Function Block")
  - PLCopen   : filename prefix heuristic (MC_ -> Motion Function Block, ...)
Missing type => "" (SummarizeTypes in AiSkills.cs skips empties).
"""
import json
import re
import sys
import zipfile
from pathlib import Path

V57_ROOT = Path(r"C:\Program Files\TrioMotion\MotionPerfectV5.7\Help\HTML\sites")
OUT_ROOT = Path(__file__).parent / "skills"

# lib -> ZIP stem under sites/{en,zh}/
LIB_ZIP = {
    "triobasic": "TrioBASIC",
    "iec":       "IEC",
    "plcopen":   "PLCOpen",
}

ARTICLE_RE = re.compile(
    r'<article class="md-content__inner md-typeset">(.*?)</article>',
    re.DOTALL)
H1_RE = re.compile(r'<h1[^>]*>(.*?)</h1>', re.DOTALL)
IMG_RE = re.compile(r'<img\b[^>]*/?>', re.IGNORECASE)
TAG_RE = re.compile(r'<[^>]+>')
# IEC type marker: <em><strong>Function Block</strong></em>
EM_STRONG_RE = re.compile(
    r'<em>\s*<strong>\s*([^<]*?)\s*</strong>\s*</em>', re.IGNORECASE)
# section boundary: next <h2/<h3, a <div, or end. Intentionally NOT <p>, so a
# heading's chunk can span its multiple paragraphs.
SEC_END = r'(?=<h2|<h3|<div\b|$)'
# paragraph boundary: also stops at next <p> (mkdocs <p> has no close tag).
# <p\b uses a word boundary so <pre>/<param> are NOT treated as a new paragraph.
BLK_END = r'(?=<h2|<h3|<p\b|<div\b|$)'


def strip_tags(s):
    return ' '.join(TAG_RE.sub('', s).split()).strip()


def text_after_h2(article, heading):
    """Text of the first <p> block following <h2>heading</h2>."""
    pat = re.compile(
        r'<h2[^>]*>\s*' + re.escape(heading) + r'\s*</h2>(.*?)' + SEC_END,
        re.DOTALL | re.IGNORECASE)
    m = pat.search(article)
    if not m:
        return ''
    chunk = m.group(1)
    pm = re.search(r'<p[^>]*>(.*?)' + BLK_END, chunk, re.DOTALL)
    text = pm.group(1) if pm else chunk
    return strip_tags(text)


def first_substantive_p(article):
    """First non-empty <p> text after <h1>, skipping the IEC em>strong type marker."""
    after_h1 = H1_RE.split(article)[-1] if H1_RE.search(article) else article
    for pm in re.finditer(r'<p[^>]*>(.*?)' + BLK_END, after_h1, re.DOTALL):
        inner = pm.group(1)
        if EM_STRONG_RE.fullmatch(inner.strip()):
            continue
        txt = strip_tags(inner)
        if len(txt) >= 3:   # allow short but valid IEC descs like "PID loop."
            return txt
    return ''


def type_from_filename(fn):
    base = fn[:-5] if fn.endswith('.html') else fn
    prefixes = [
        ('MC_', 'Motion Function Block'),
        ('MCV_', 'Motion View Function Block'),
        ('SL_', 'Standard Library Operation'),
        ('AO-', 'Application Object'),
        ('Arith-', 'Arithmetic Operation'),
    ]
    for prefix, typ in prefixes:
        if base.startswith(prefix):
            return typ
    return ''


def extract_name(article, fallback):
    m = H1_RE.search(article)
    n = strip_tags(m.group(1)) if m else ''
    return n or fallback


def extract_type(article, filename):
    for h in ('Type', '类型'):   # en / zh
        t = text_after_h2(article, h)
        if t:
            return t
    m = EM_STRONG_RE.search(article)
    if m:
        return m.group(1).strip()
    return type_from_filename(filename)


def extract_desc(article):
    for h in ('Description', '描述'):   # en / zh
        d = text_after_h2(article, h)
        if d:
            return d
    return first_substantive_p(article)


def extract_syntax(article):
    """Signature line from the Syntax/语法 section's first <code> (e.g.
    'MOVE(distance1, ...)', 'value = ABS(expression)'). Authoritative source —
    AiValidation.ParseSignature consumes it for arg-count / assignability checks.
    Returns '' when no Syntax section (keyword pages like IF/FOR have none)."""
    for h in ('Syntax', '语法'):   # en / zh
        s = text_after_h2(article, h)
        if s:
            return s
    return ''


def build(lib, lang):
    zip_path = V57_ROOT / lang / (LIB_ZIP[lib] + '.zip')
    if not zip_path.exists():
        print(f"  SKIP: {zip_path} not found")
        return
    out_dir = OUT_ROOT / lib / lang
    if out_dir.exists():
        import shutil
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True)

    entries = []
    seen = set()
    with zipfile.ZipFile(zip_path) as z:
        for name in z.namelist():
            if not name.endswith('.html'):
                continue
            base = name.rsplit('/', 1)[-1]
            if '/' in name:          # only top-level command pages
                continue
            if base in ('index.html', '404.html'):
                continue
            if base in seen:
                continue
            try:
                raw = z.read(name).decode('utf-8')
            except Exception as ex:
                print(f"    WARN read {base}: {ex}")
                continue
            m = ARTICLE_RE.search(raw)
            if not m:
                continue
            article = m.group(1)
            clean = IMG_RE.sub('', article).strip()
            (out_dir / base).write_text(clean, encoding='utf-8')
            seen.add(base)

            nm = extract_name(article, base[:-5])
            typ = extract_type(article, base)
            desc = extract_desc(article)
            entries.append({
                "name": nm,
                "type": typ,
                "desc": desc,
                "sig": extract_syntax(article),
                "file": base,
            })

    entries.sort(key=lambda e: e["file"])
    lines = ["["]
    for i, e in enumerate(entries):
        comma = "," if i < len(entries) - 1 else ""
        d = " ".join((e["desc"] or "").split())
        if len(d) > 80:
            d = d[:77].rstrip() + "..."
        obj = {"name": e["name"], "type": e["type"], "desc": d, "file": e["file"]}
        if e["sig"]:
            obj["sig"] = e["sig"]
        payload = json.dumps(obj, ensure_ascii=False, separators=(", ", ": "))
        lines.append(" " + payload + comma)
    lines.append("]")
    (out_dir / "index.json").write_text("\n".join(lines) + "\n", encoding="utf-8")
    # type coverage report
    typed = sum(1 for e in entries if e["type"])
    print(f"  {lib}/{lang}: {len(entries)} entries ({typed} typed) -> {out_dir}")


def main():
    libs = [sys.argv[1]] if len(sys.argv) > 1 else list(LIB_ZIP)
    langs = [sys.argv[2]] if len(sys.argv) > 2 else ["en", "zh"]
    for lib in libs:
        if lib not in LIB_ZIP:
            print(f"unknown lib {lib}; choices: {list(LIB_ZIP)}")
            continue
        print(f"=== {lib} ===")
        for lang in langs:
            build(lib, lang)


if __name__ == "__main__":
    main()
