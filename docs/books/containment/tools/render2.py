"""Render one series' full evidence packet for judgement. `python render2.py <seriesId> [...]`"""
import json, sys

WANT = {int(x) for x in sys.argv[1:]}
SRC = {"locg": "locg", "gcd": "gcd ", "cv": "cv  ", "curated": "CUR "}


def nums(xs, cap=32):
    xs = list(xs)
    s = ",".join(f"{x:g}" for x in xs[:cap])
    return s + ("…" if len(xs) > cap else "")


for line in open("packets2.jsonl", encoding="utf-8"):
    p = json.loads(line)
    if p["seriesId"] not in WANT:
        continue
    held = p["heldIssues"]
    print(f"\n=== S{p['seriesId']}  {p['name']}   [{p['key']}]  {p['years'][0]}-{p['years'][1]}")
    print(f"    library holds {len(held)} single issues: {nums(held, 48) or '(none)'}")
    ladders = {}
    for c in p["collections"]:
        ladders.setdefault(c["ladder"], []).append(c)
    for lk, cols in ladders.items():
        print(f"\n  -- ladder [{lk}]  ({len(cols)} editions)")
        for c in sorted(cols, key=lambda x: (x["vol"] is None, x["vol"] or 0, x["id"])):
            cur = next((s for s in c.get("spans", []) if s["src"] == "curated"), None)
            claim = f"#{cur['start']:g}-{cur['end']:g}" if cur else "—"
            per = ""
            if cur and c.get("pages"):
                n = int(cur["end"]) - int(cur["start"]) + 1
                if n > 0:
                    per = f"  {c['pages'] / n:.0f}pp/issue"
            print(f"   [{c['id']:>7}] vol {str(c['vol'] or '?'):>3}  {claim:<12} "
                  f"{c.get('pages') or 0:>4}pp {c.get('mb') or 0:>6}MB{per}")
            print(f"        {c['file'][:96]}")
            for s in c.get("spans", []):
                if s["src"] == "curated":
                    continue
                print(f"        {SRC.get(s['src'], s['src'])} says #{s['start']:g}-{s['end']:g}"
                      + (f"  \"{s['title'][:40]}\"" if s.get("title") else ""))
            if "gcd_reprints" in c:
                r = c["gcd_reprints"]
                print(f"        GCD reprints [{r['series'][:30]}]: {nums(r['issues'])}")
            if "locg_contains" in c:
                print(f"        LOCG contains: {nums(c['locg_contains'])}")
            if cur and cur.get("note"):
                print(f"        note: {cur['note'][:150]}")
            if "v1_read_the_book" in c:
                v = c["v1_read_the_book"]
                rng = "SKIP (no issue range)" if v.get("skip") else f"#{v.get('start')}-{v.get('end')}"
                imp = "" if v.get("imported") else "   [NEVER IMPORTED]"
                print(f"        v1 read the book: {rng} conf {v.get('confidence')}{imp}")
                if v.get("evidence"):
                    print(f"           {v['evidence'][:190]}")
