#!/usr/bin/env python3
"""
Build index.json for each skill backed by raw HTML extracted from a .chm.

Each skill lives under skills/<name>/ and contains:
  - The unpacked .chm: *.html + <stem>_files/ (images) + the *.hhc TOC.
  - This script writes index.json next to them.

index.json shape (one entry per concrete command/FB/operator):
  {"name": "MC_MoveAbsolute", "type": "Motion Function Block",
   "desc": "Commands a controlled motion to ...",
   "file": "MC_MoveAbsolute.html"}

The full HTML body is returned on demand by AiService.cs LoadFullEntry —
no skills.json is generated, the HTML file is the single source of truth.
"""
import json
import re
import sys
from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).parent


# ---------- HHC parser ----------
def parse_hhc(hhc_path):
    """Return list of (name, local_html, parent_section) in document order.

    parent_section is the nearest enclosing <li> heading that owns a <ul>
    containing this item. Used both for type fallback and to detect chapter
    pages (a page that is itself a parent should be skipped).
    """
    text = hhc_path.read_text(encoding="utf-8", errors="replace")
    name_re = re.compile(r'<param\s+name="Name"\s+value="([^"]*)"', re.IGNORECASE)
    local_re = re.compile(r'<param\s+name="Local"\s+value="([^"]*)"', re.IGNORECASE)
    obj_re = re.compile(
        r'<object[^>]*type="text/sitemap"[^>]*>(.*?)</object>',
        re.IGNORECASE | re.DOTALL,
    )

    tokens = []
    pos = 0
    while True:
        ul_m = text.find("<ul", pos)
        obj_m = obj_re.search(text, pos)
        close_m = text.find("</ul>", pos)
        candidates = [(p, k) for p, k in [
            (ul_m, "ul"), (obj_m.start() if obj_m else -1, "obj"),
            (close_m, "close"),
        ] if p >= 0]
        if not candidates:
            break
        candidates.sort()
        p, k = candidates[0]
        if k == "ul":
            tokens.append(("ul", None))
            gt = text.find(">", p)
            pos = (gt + 1) if gt >= 0 else len(text)
        elif k == "obj":
            body = obj_m.group(1)
            nm = name_re.search(body)
            lc = local_re.search(body)
            tokens.append(("obj", (nm.group(1) if nm else "",
                                   lc.group(1).strip() if lc else "")))
            pos = obj_m.end()
        else:
            tokens.append(("close", None))
            pos = close_m + len("</ul>")

    items = []
    depth = 0
    last_obj_at_depth = {}
    for kind, payload in tokens:
        if kind == "ul":
            depth += 1
        elif kind == "close":
            if depth in last_obj_at_depth:
                del last_obj_at_depth[depth]
            depth -= 1
        else:
            name, local = payload
            parent = last_obj_at_depth.get(depth - 1, "") if depth >= 2 else ""
            items.append((name, local, parent))
            last_obj_at_depth[depth] = name
    return items


# ---------- HTML parser (just enough to pull title + category + first body p) ----------
class _TableParser(HTMLParser):
    """Emit a flat event stream we scan for name/type/desc."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.events = []
        self._tag_stack = []
        self._cur_cell = None
        self._a_href = None
        self._a_text = None
        self._in_a = 0
        self._cur_p_cls = None
        self._buf = None
        self._heading = None

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag in ("h1", "h2", "h3"):
            self._heading = tag
            self._buf = []
        elif tag == "p":
            self._cur_p_cls = a.get("class", "")
            self._buf = []
        elif tag == "br":
            if self._cur_cell is not None:
                self._cur_cell.append("\n")
            elif self._buf is not None:
                self._buf.append("\n")
        elif tag == "a":
            href = a.get("href", "")
            if href:
                self._in_a += 1
                self._a_href = href
                self._a_text = []
            # <a name="..."> anchor: ignore, text falls through

    def handle_endtag(self, tag):
        if tag in ("h1", "h2", "h3"):
            txt = " ".join("".join(self._buf).split()) if self._buf else ""
            if txt:
                self.events.append({"k": tag, "t": txt})
            self._heading = None
            self._buf = None
        elif tag == "p":
            txt = "".join(self._buf) if self._buf else ""
            txt = " ".join(txt.split())
            if txt or self._cur_p_cls:
                self.events.append({"k": "p", "cls": self._cur_p_cls or "", "t": txt})
            self._cur_p_cls = None
            self._buf = None
        elif tag == "a":
            if self._in_a > 0:
                self._in_a -= 1
                href = self._a_href or ""
                a_text = "".join(self._a_text) if self._a_text else ""
                a_text = " ".join(a_text.split())
                if href and not href.startswith("#"):
                    self.events.append({"k": "a", "href": href, "t": a_text})
                self._a_href = None
                self._a_text = None

    def handle_data(self, data):
        if self._cur_cell is not None:
            self._cur_cell.append(data)
        elif self._in_a > 0 and self._a_text is not None:
            self._a_text.append(data)
        elif self._buf is not None:
            self._buf.append(data)


def _parse_html(html_path):
    text = html_path.read_text(encoding="utf-8", errors="replace")
    p = _TableParser()
    try:
        p.feed(text)
    except Exception:
        pass
    return p.events


# ---------- Field extractors ----------
def _h1(events):
    for ev in events:
        if ev["k"] == "h1":
            return ev["t"]
    return ""


def _category(events):
    """IEC/TrioBASIC pages put the type in <p class='Category'>."""
    for ev in events:
        if ev["k"] == "p" and ev.get("cls") == "Category" and ev["t"]:
            return ev["t"].strip()
    return ""


def _plcopen_type(events):
    """PLCOpen pages: <h2>Type:</h2> followed by a normal <p>."""
    capture = False
    for ev in events:
        if ev["k"] == "h2" and ev["t"].lower().rstrip(":").strip() == "type":
            capture = True
            continue
        if capture and ev["k"] == "p" and ev["t"]:
            return ev["t"].strip()
    return ""


def _description(events, max_paragraphs=2):
    """First 1-2 body paragraphs after the H1, skipping the type line."""
    out = []
    saw_h1 = False
    for ev in events:
        if ev["k"] == "h1":
            saw_h1 = True
            continue
        if not saw_h1:
            continue
        if ev["k"] != "p" or not ev["t"]:
            continue
        cls = ev.get("cls", "") or ""
        if cls == "Category":
            continue
        out.append(ev["t"])
        if len(out) >= max_paragraphs:
            break
    return " ".join(out)


def _type_fallback(parent_section):
    ps = (parent_section or "").lower()
    if "administrative" in ps:
        return "Administrative Function Block"
    if "motion function block" in ps:
        return "Motion Function Block"
    if "vendor" in ps:
        return "Vendor Specific Function Block"
    if "arithmetic" in ps:
        return "Arithmetic Operation"
    if "boolean" in ps:
        return "Boolean Operation"
    if "comparison" in ps:
        return "Comparison Operation"
    if "type conversion" in ps:
        return "Type Conversion"
    if "selector" in ps:
        return "Selector"
    if "register" in ps:
        return "Register Operation"
    if "counter" in ps:
        return "Counter"
    if "timer" in ps:
        return "Timer"
    if "mathematical" in ps:
        return "Mathematical Operation"
    if "standard library" in ps:
        return "Standard Library Operation"
    if "string" in ps:
        return "String Operation"
    if "real-time clock" in ps or "realtime clock" in ps:
        return "Real-Time Clock Function"
    if "advanced" in ps:
        return "Advanced Operation"
    if "axis parameter" in ps:
        return "Axis Parameter (Trio)"
    if "i/o parameter" in ps:
        return "I/O Parameter (Trio)"
    if "i/o function" in ps:
        return "I/O Function (Trio)"
    if "system parameter" in ps:
        return "System Parameter (Trio)"
    if "system function" in ps:
        return "System Function (Trio)"
    if "comm" in ps:
        return "Communications Function (Trio)"
    if "motion function" in ps:
        return "Motion Function (Trio)"
    return parent_section


# ---------- Per-skill driver ----------
def build_skill(skill_name, hhc_path, html_root):
    print(f"\n=== Building skill '{skill_name}' from {hhc_path.name} ===")
    items = parse_hhc(hhc_path)
    print(f"  hhc entries: {len(items)}")

    # A page that is itself a parent (i.e., owns a <ul> of children) is a
    # chapter TOC page — skip it. We resolve parent_name -> local via the
    # name map since hhc gives us names, not file paths, in parent_section.
    name_to_local = {}
    for nm, local, _ in items:
        if nm and local:
            name_to_local.setdefault(nm, local)
    parent_locals = {name_to_local[pn] for _, _, pn in items
                     if pn and pn in name_to_local}

    # A handful of pages we always drop regardless of hierarchy.
    ALWAYS_SKIP_SUFFIXES = ("_Errors.html",)
    SKIP_FILENAMES = {"Introduction.html"}

    entries = []
    seen = set()
    skipped = 0
    for hhc_name, local, parent_section in items:
        if "/" in local or "\\" in local:
            continue
        fn = local.rsplit("/", 1)[-1]
        if fn in parent_locals:
            skipped += 1
            continue
        if fn in SKIP_FILENAMES or any(fn.endswith(s) for s in ALWAYS_SKIP_SUFFIXES):
            skipped += 1
            continue
        html_path = html_root / fn
        if not html_path.exists():
            continue
        if fn in seen:
            continue
        seen.add(fn)
        try:
            events = _parse_html(html_path)
        except Exception as ex:
            print(f"  WARN: failed to parse {fn}: {ex}", file=sys.stderr)
            continue
        name = _h1(events) or hhc_name
        typ = _category(events) or _plcopen_type(events) or _type_fallback(parent_section)
        desc = _description(events)
        if not name or (not desc and not typ):
            skipped += 1
            continue
        entries.append({
            "name": name.strip(),
            "type": (typ or "").strip(),
            "desc": desc.strip(),
            "file": fn,
        })

    print(f"  kept: {len(entries)}, skipped: {skipped}")

    # Slim index: 1 line per entry, sorted by file for stable diffs.
    entries.sort(key=lambda e: e["file"])
    slim = []
    for e in entries:
        d = " ".join((e["desc"] or "").split())
        if len(d) > 80:
            d = d[:77].rstrip() + "..."
        slim.append({
            "name": e["name"],
            "type": e["type"],
            "desc": d,
            "file": e["file"],
        })
    lines = ["["]
    for i, item in enumerate(slim):
        comma = "," if i < len(slim) - 1 else ""
        payload = json.dumps(item, ensure_ascii=False, separators=(", ", ": "))
        lines.append(" " + payload + comma)
    lines.append("]")
    out_path = html_root / "index.json"
    out_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"  wrote {out_path.name} with {len(slim)} entries")
    print(f"  sample: {slim[0] if slim else '(none)'}")


def main():
    build_skill("triobasic", ROOT / "skills" / "triobasic" / "TrioBASIC.hhc",
                ROOT / "skills" / "triobasic")
    build_skill("iec", ROOT / "skills" / "iec" / "IEC.hhc",
                ROOT / "skills" / "iec")
    build_skill("plcopen", ROOT / "skills" / "plcopen" / "PLCopen.hhc",
                ROOT / "skills" / "plcopen")


if __name__ == "__main__":
    main()
