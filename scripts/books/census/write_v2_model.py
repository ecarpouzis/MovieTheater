"""R3: render the generated sections of docs/books/v2-model.md from v2-mapping.json + the census.

The prose of v2-model.md is hand-written. Everything between `<!-- generated:NAME -->` and
`<!-- /generated:NAME -->` is replaced by this script, so the doc can be regenerated after any
spec change without losing the prose. Sections: catalog, mapping, enums, size, coverage.
"""
from __future__ import annotations
import json, re
from collections import defaultdict
from common import CENSUS, DOCS

DOC = DOCS / "v2-model.md"


def load():
    m = json.load(open(DOCS / "v2-mapping.json", encoding="utf-8"))
    cols = {}
    for f in (CENSUS / "columns").glob("*.json"):
        d = json.load(open(f, encoding="utf-8")); cols[d["table"]] = {c["name"]: c for c in d.get("columns", [])}
    base = json.load(open(CENSUS / "baseline.json", encoding="utf-8"))
    return m, cols, base


def catalog(m):
    L = []
    for file_kind, title in (("hot", "books.db — the runtime's only file (`BooksDb`)"), ("legs", "books-legs.db — offline warehouse (`BooksLegsDb`; no FK crosses the file boundary)")):
        L += [f"#### {title}", "", "| Table | Key | Purpose | Columns | Indexes |", "|---|---|---|---|---|"]
        for t, s in m["v2"].items():
            if s["file"] != file_kind: continue
            L.append(f"| `{t}` | {', '.join(s['pk'])} | {s['purpose']} | `{s['cols']}` | {', '.join('`'+i+'`' for i in s.get('idx', [])) or '—'} |")
        L.append("")
    return "\n".join(L)


def mapping(m, cols):
    L = ["| v1 table | rows | → v2 (file) | stage | column rules (renames / transforms / drops) |", "|---|---:|---|---|---|"]
    base = json.load(open(CENSUS / "baseline.json", encoding="utf-8"))["rows"]
    for t, s in m["v1"].items():
        rows = base.get(t, 0)
        if not s["targets"]:
            L.append(f"| `{t}` | {rows:,} | **drop** | — | {s.get('drop','')} |"); continue
        tg = ", ".join(f"`{x}` ({m['v2'][x]['file']})" for x in s["targets"])
        rules = []
        for c, r in s["cols"].items():
            rules.append(f"`{c}` {r}")
        for c, r in s.get("derived", {}).items():
            rules.append(f"**+** `{c}` {r}")
        same = [c for c in cols.get(t, {}) if c not in s["cols"]]
        extra = f" _(+{len(same)} carried as-is)_" if same else ""
        note = f" — {s['note']}" if s.get("note") else ""
        L.append(f"| `{t}` | {rows:,} | {tg} | {s['stage']} | {'; '.join(rules) or 'all as-is'}{extra}{note} |")
    return "\n".join(L)


def enums(m):
    return "\n".join(f"- **{k}**: {', '.join(v)}" for k, v in m["enums"].items())


def size(m, cols):
    """Attribute each v1 column's census bytes to hot / legs / dropped by the mapping."""
    tot = defaultdict(int)
    detail = defaultdict(lambda: defaultdict(int))
    for t, s in m["v1"].items():
        for c, info in cols.get(t, {}).items():
            b = info.get("bytes", 0)
            if not s["targets"]:
                tot["dropped"] += b; continue
            rule = s["cols"].get(c, "same")
            if rule.startswith("drop:"):
                tot["dropped"] += b; continue
            tb = s["targets"][0]
            if rule.startswith("->") and "." in rule[2:].split(" ")[0]:
                tb = rule[2:].split(" ")[0].rsplit(".", 1)[0]
            elif rule.startswith("xf:") and ":" in rule[3:]:
                # e.g. xf:split_by_kind:ComicEmbedded.Summary|BookDetail.Description -> credit the first named table
                first = rule.split(":", 2)[2].split("|")[0].split("+")[0].split("(")[0]
                if "." in first and first.split(".")[0] in m["v2"]:
                    tb = first.split(".")[0]
            if t == "LocgComics" and tb == "LocgComic":
                # hot keeps only matched rows (84,874 links / 156,839 rows); the raw copy keeps all
                tot["hot"] += int(b * 84874 / 156839); tot["legs"] += b
                detail["hot"][tb] += int(b * 84874 / 156839); detail["legs"]["LocgComicRaw"] += b
                continue
            f = m["v2"].get(tb, {}).get("file", "hot")
            tot[f] += b; detail[f][tb] += b
    L = [f"Column payload attributed by the mapping (v1 total {sum(tot.values())/1e6:,.1f} MB of column bytes; the file also carries indexes/FTS/page overhead — v1 file was 700 MB for 501 MB of column bytes):", ""]
    for f in ("hot", "legs", "dropped"):
        L.append(f"- **{f}**: {tot[f]/1e6:,.1f} MB" + (" — largest: " + ", ".join(f"`{t}` {b/1e6:.1f}" for t, b in sorted(detail[f].items(), key=lambda kv: -kv[1])[:6]) if f != "dropped" else ""))
    L.append("")
    L.append("Not counted above (new in v2): `ItemCredit` (est. ≈ 8 MB from `CreatorsJson` for matched rows + ComicInfo creators), `ItemTag`/`SeriesTag` (≈ 12 MB replacing the five CSV rollups), `Item.Resolved*` scalars (≈ 15 MB; no synopsis text). Expected hot file ≈ **hot column bytes × 1.4** after indexes and FTS.")
    return "\n".join(L)


def coverage():
    p = CENSUS / "v2-mapping-report.md"
    return p.read_text(encoding="utf-8").split("\n", 1)[1] if p.exists() else "_(run v2_mapping_check.py)_"


def main():
    m, cols, base = load()
    doc = DOC.read_text(encoding="utf-8")
    sections = {"catalog": catalog(m), "mapping": mapping(m, cols), "enums": enums(m), "size": size(m, cols), "coverage": coverage()}
    for name, body in sections.items():
        pat = re.compile(rf"(<!-- generated:{name} -->)(.*?)(<!-- /generated:{name} -->)", re.S)
        if not pat.search(doc):
            raise SystemExit(f"marker for {name} missing in {DOC}")
        doc = pat.sub(lambda mm: f"{mm.group(1)}\n{body}\n{mm.group(3)}", doc)
    DOC.write_text(doc, encoding="utf-8")
    print("rendered", DOC, {k: len(v) for k, v in sections.items()})


if __name__ == "__main__":
    main()
