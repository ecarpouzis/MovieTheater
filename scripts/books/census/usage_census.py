"""R0 step 5: code-usage census - which v1 entity columns does code actually read?

Parses the EF entity classes (src/Mybooks.Db/Entities/*.cs), maps each entity to its SQLite table
(by [Table] attribute, else best column-set overlap with the frozen schema), then looks every
property name up in an identifier index of the reader classes (word-boundary exact match):
  runtime   src/Mybooks/{Controllers,Services,Middleware,Security,*.cs} (minus Program.cs), src/Mybooks.Services, src/Mybooks.Opds, src/Mybooks.Db/*.cs (minus ComicDb.cs and Entities/)
  startup   src/Mybooks/Program.cs (boot DDL + backfills)
  tools     src/Mybooks.Tools
  offline   the outer scrape workspace *.py, _gcd, scratch, book-tools, locg-sync, claude-tools, repo-root *.py
  tests     src/Mybooks.Tests
A runtime hit is "scoped" when the same file also names the entity class or its DbSet. Property
names shared by several entities carry an ambiguity count; a ZERO is unambiguous, a non-zero on a
shared name is only "possibly used". Separately, every ComicSummary field is checked for client
(ClientApp) references by its camelCase name.

Outputs: data/books/census/usage.csv, usage_summary.json, summary_fields.csv. Progress per entity.
"""
from __future__ import annotations
import csv, json, re, time
from collections import defaultdict
from common import MYBOOKS_REPO, MYBOOKS_OUTER, CENSUS, frozen_db, ro, tables, dump_json

SRC = MYBOOKS_REPO / "src"
ROOTS = {
    "runtime": [SRC / "Mybooks" / "Controllers", SRC / "Mybooks" / "Services", SRC / "Mybooks" / "Middleware",
                SRC / "Mybooks" / "Security", SRC / "Mybooks.Services", SRC / "Mybooks.Opds"]
               + [p for p in (SRC / "Mybooks.Db").glob("*.cs") if p.name != "ComicDb.cs"]
               + [p for p in (SRC / "Mybooks").glob("*.cs") if p.name != "Program.cs"],
    "startup": [SRC / "Mybooks" / "Program.cs"],
    "tools": [SRC / "Mybooks.Tools"],
    "tests": [SRC / "Mybooks.Tests"],
    "offline": [MYBOOKS_OUTER / "_gcd", MYBOOKS_OUTER / "scratch", MYBOOKS_REPO / "book-tools",
                MYBOOKS_REPO / "locg-sync", MYBOOKS_REPO / "claude-tools"]
               + list(MYBOOKS_OUTER.glob("*.py")) + list(MYBOOKS_REPO.glob("*.py")),
}
CLIENT = SRC / "Mybooks" / "ClientApp" / "src"
ENTITIES = SRC / "Mybooks.Db" / "Entities"
RG_GLOBS = ["-g", "!node_modules", "-g", "!bin", "-g", "!obj", "-g", "!*.json", "-g", "!*.csv", "-g", "!*.jsonl"]


_INDEX = {}  # root-class -> {file: Counter(tokens)}
_TOKEN = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_EXT = {".cs", ".ts", ".tsx", ".py", ".js", ".mjs"}
_SKIP = {"node_modules", "bin", "obj", ".git", "test-results", "playwright-report"}


def _walk(roots):
    for r in roots:
        if not r.exists():
            continue
        if r.is_file():
            if r.suffix in _EXT:
                yield r
            continue
        for p in r.rglob("*"):
            if p.is_file() and p.suffix in _EXT and not (set(p.parts) & _SKIP):
                yield p


def _index(key, roots):
    """Tokenize every source file under roots once: file -> Counter of identifiers (+ lowercase twin)."""
    if key in _INDEX:
        return _INDEX[key]
    from collections import Counter
    idx = {}
    for f in _walk(roots):
        try:
            text = f.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        toks = _TOKEN.findall(text)
        idx[str(f).replace("\\", "/")] = (Counter(toks), Counter(t.lower() for t in toks))
    _INDEX[key] = idx
    return idx


def rg_files(pattern: str, roots, extra=()):
    """Word-boundary identifier search (replaces ripgrep). pattern = r'Name' or r'(A|B)'; extra=('-i',) = case-insensitive."""
    bs = chr(92)  # the patterns are word-boundary regexes like rf"(A|B)"; reduce them to bare identifier names
    names = pattern.replace(bs + "b", "").replace(bs, "").strip("()").split("|")
    ci = "-i" in extra
    key = (tuple(str(r) for r in roots), tuple(x for x in extra if x != "-i"))
    idx = _index(key, roots)
    out = {}
    for f, (cnt, lcnt) in idx.items():
        if extra and "*.ts" in extra and not f.endswith((".ts", ".tsx")):
            continue
        n = sum((lcnt[nm.lower()] if ci else cnt[nm]) for nm in names)
        if n:
            out[f] = n
    return out


CLASS_RE = re.compile(r"public\s+(?:sealed\s+|partial\s+|abstract\s+)?class\s+(\w+)")
PROP_RE = re.compile(r"public\s+(?:virtual\s+)?([\w<>\[\]?,.\s]+?)\s+(\w+)\s*\{\s*get;")
TABLE_ATTR_RE = re.compile(r'\[Table\("(\w+)"\)\]')


def parse_entities():
    ents = {}
    for f in sorted(ENTITIES.glob("*.cs")):
        text = f.read_text(encoding="utf-8", errors="replace")
        # split into class segments
        marks = [(m.start(), m.group(1)) for m in CLASS_RE.finditer(text)]
        for i, (pos, name) in enumerate(marks):
            end = marks[i + 1][0] if i + 1 < len(marks) else len(text)
            seg = text[pos:end]
            head = text[max(0, pos - 200):pos]
            ta = TABLE_ATTR_RE.findall(head)
            props = []
            for m in PROP_RE.finditer(seg):
                typ, pname = m.group(1).strip(), m.group(2)
                props.append((pname, typ))
            ents[name] = {"file": f.name, "table_attr": ta[-1] if ta else None, "props": props}
    # drop enums / non-entities: keep classes with >= 2 props
    return {k: v for k, v in ents.items() if len(v["props"]) >= 2}


def dbsets():
    text = (SRC / "Mybooks.Db" / "ComicDb.cs").read_text(encoding="utf-8", errors="replace")
    sets = {}
    for m in re.finditer(r"DbSet<(\w+)>\s+(\w+)", text):
        sets.setdefault(m.group(1), m.group(2))
    totable = dict(re.findall(r"Entity<(\w+)>\(\)\s*\.ToTable\(\"(\w+)\"", text))
    return sets, totable


def main():
    t0 = time.time()
    ents = parse_entities()
    sets, totable = dbsets()
    con = ro(frozen_db())
    cols = {t: {r[1] for r in con.execute(f'PRAGMA table_info("{t}")')} for t in tables(con)}
    entity_names = set(ents)
    # entity -> table
    for name, e in ents.items():
        t = e["table_attr"] or totable.get(name)
        if not t and sets.get(name) in cols:
            t = sets[name]  # DbSet name == table name (EF default) beats the column-overlap heuristic
        if not t:
            pnames = {p for p, _ in e["props"]}
            best = max(cols.items(), key=lambda kv: len(kv[1] & pnames) / max(1, len(kv[1] | pnames)))
            t = best[0] if len(best[1] & pnames) >= 2 else None
        e["table"] = t
        e["dbset"] = sets.get(name)
        e["columns"] = cols.get(t, set())
    # property -> entities sharing it
    share = defaultdict(list)
    for name, e in ents.items():
        for p, _ in e["props"]:
            share[p].append(name)
    # per-entity scope files (files mentioning the class or DbSet)
    print(f"{len(ents)} entities, {len(share)} distinct property names; scoping...", flush=True)
    scope = {}
    for name, e in ents.items():
        pat = rf"\b({name}" + (rf"|{e['dbset']}" if e["dbset"] else "") + r")\b"
        scope[name] = set(rg_files(pat, ROOTS["runtime"] + ROOTS["startup"] + ROOTS["tools"] + ROOTS["tests"]))
    # property greps (one rg per name per class of roots)
    hits = {}
    names = sorted(share)
    for i, p in enumerate(names, 1):
        pat = rf"\b{re.escape(p)}\b"
        hits[p] = {
            "runtime": rg_files(pat, ROOTS["runtime"]),
            "startup": rg_files(pat, ROOTS["startup"]),
            "tools": rg_files(pat, ROOTS["tools"]),
            "tests": rg_files(pat, ROOTS["tests"]),
            "offline": rg_files(pat, ROOTS["offline"], extra=("-i",)),
        }
        if i % 100 == 0:
            print({"props": i, "remaining": len(names) - i, "secs": round(time.time() - t0)}, flush=True)
    rows = []
    summary = {}
    for name, e in sorted(ents.items()):
        tsum = summary.setdefault(e["table"] or f"(unmapped:{name})", {
            "entities": [], "runtime_scoped_files": set(), "tools_files": set(), "offline_files": set(),
            "zero_runtime_columns": [], "columns_in_db": len(e["columns"])})
        tsum["entities"].append(name)
        for p, typ in e["props"]:
            is_nav = (typ.replace("?", "") in entity_names) or typ.startswith(("ICollection", "List<", "IList", "HashSet"))
            h = hits[p]
            rt_files = set(h["runtime"])
            scoped = rt_files & scope[name]
            proj = any(f.endswith("/ComicSummary.cs") for f in rt_files)
            in_db = p in e["columns"]
            row = {
                "entity": name, "table": e["table"], "column": p, "type": typ, "is_nav": is_nav, "in_db": in_db,
                "ambiguity": len(share[p]),
                "runtime_files": len(rt_files), "runtime_scoped_files": len(scoped), "projection": proj,
                "startup_files": len(h["startup"]), "tools_files": len(h["tools"]), "tests_files": len(h["tests"]),
                "offline_files": len(h["offline"]),
                "scoped_files": ";".join(sorted(f.split("/src/")[-1] for f in scoped)[:8]),
            }
            rows.append(row)
            if not is_nav:
                tsum["runtime_scoped_files"] |= scoped
                tsum["tools_files"] |= set(h["tools"])
                tsum["offline_files"] |= set(h["offline"])
                if in_db and len(scoped) == 0 and not proj:
                    tsum["zero_runtime_columns"].append(p)
    # DB columns with no entity property at all
    mapped_tables = {e["table"] for e in ents.values() if e["table"]}
    orphan_cols = {}
    for t, cs in cols.items():
        if t in mapped_tables:
            props = set().union(*[{p for p, _ in e["props"]} for e in ents.values() if e["table"] == t])
            extra = sorted(cs - props)
            if extra:
                orphan_cols[t] = extra
    entityless_tables = sorted(t for t in cols if t not in mapped_tables and not t.startswith("ComicFts") and t != "sqlite_sequence")
    # ComicSummary fields -> client usage
    cs_text = (SRC / "Mybooks" / "ComicSummary.cs").read_text(encoding="utf-8", errors="replace")
    cs_fields = [m.group(2) for m in PROP_RE.finditer(cs_text.split("public static readonly")[0])]
    sfields = []
    for f in cs_fields:
        camel = f[0].lower() + f[1:]
        files = rg_files(rf"\b{re.escape(camel)}\b", [CLIENT], extra=("-g", "*.ts", "-g", "*.tsx"))
        sfields.append({"field": f, "client_files": len(files), "files": ";".join(sorted(x.split("/src/")[-1] for x in files)[:6])})
    with open(CENSUS / "usage.csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
        w.writeheader(); w.writerows(rows)
    with open(CENSUS / "summary_fields.csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["field", "client_files", "files"])
        w.writeheader(); w.writerows(sfields)
    out = {
        "entities": len(ents), "properties": len(rows),
        "tables": {t: {"entities": v["entities"], "runtime_scoped_files": sorted(x.split("/src/")[-1] for x in v["runtime_scoped_files"]),
                       "tools_files": len(v["tools_files"]), "offline_files": len(v["offline_files"]),
                       "zero_runtime_columns": sorted(v["zero_runtime_columns"]), "columns_in_db": v["columns_in_db"]}
                   for t, v in summary.items()},
        "zero_runtime_tables": sorted(t for t, v in summary.items() if not v["runtime_scoped_files"]),
        "entityless_tables": entityless_tables,
        "db_columns_without_entity_property": orphan_cols,
        "unused_summary_fields": [s["field"] for s in sfields if s["client_files"] == 0],
        "secs": round(time.time() - t0),
    }
    dump_json(CENSUS / "usage_summary.json", out)
    print("zero-runtime tables:", out["zero_runtime_tables"])
    print("entity-less tables:", entityless_tables)
    print("unused ComicSummary fields:", out["unused_summary_fields"])
    print("done in", out["secs"], "s")


if __name__ == "__main__":
    main()
