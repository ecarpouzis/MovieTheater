"""View one series' v3 evidence packet. `python render3.py <seriesId> [...]`

The v2 renderer reduced. This one does not, and that is the whole point of v3: Baltimore's answer was in
the issue FILENAMES ("Baltimore 016 - The Infernal Train 01 (of 03)"), which carry the library's continuous
number and the arc's own at once, and a render that printed `heldIssues: [1,2,3,4,5,...]` threw it away.

Every issue file is printed with its name. Every collected edition is printed with its page count, its file
size, every source's claim, LOCG's role and prose, and v1's book-inspection note verbatim.
"""
import json, os, sys

WANT = {int(x) for x in sys.argv[1:]}
if not WANT:
    raise SystemExit("usage: python render3.py <seriesId> [...]")

HERE = os.path.dirname(os.path.abspath(__file__))
PACKETS = os.path.join(HERE, "packets3.jsonl")
SRC = {"locg": "locg", "gcd": "gcd ", "cv": "cv  ", "curated": "CUR "}


def g(x):
    return "?" if x is None else (f"{x:g}" if isinstance(x, float) else str(x))


def nums(xs, cap=40):
    xs = [x for x in xs if x is not None]
    s = ",".join(f"{x:g}" for x in xs[:cap])
    return s + ("…" if len(xs) > cap else "")


def show_locg(b, pad):
    if not b:
        return
    print(f"{pad}LOCG {b['id']} role={b['role']} (container in {b['asContainer']}, contained in {b['asContained']})")
    if b.get("prose"):
        print(f"{pad}  prose: {b['prose'][:200]}")
    if b.get("contains"):
        resolved = [c["issue"] for c in b["contains"] if c.get("issue") is not None]
        print(f"{pad}  contains {len(b['contains'])} chapters, {len(resolved)} with a number: {nums(resolved)}")
        for c in b["contains"][:6]:
            print(f"{pad}    #{g(c.get('issue')):>6} {str(c.get('series') or '')[:34]:<34} {str(c.get('chapter') or '')[:40]}")
    if b.get("claimedBy"):
        print(f"{pad}  claimed by {len(b['claimedBy'])} edition(s):")
        for c in b["claimedBy"][:6]:
            print(f"{pad}    {c['container']} {str(c.get('format') or '')[:12]:<12} {str(c.get('title') or '')[:56]}")


def show_v1(v, pad):
    if not v:
        return
    rng = "SKIP (no issue range)" if v.get("skip") else f"#{g(v.get('start'))}-{g(v.get('end'))}"
    imp = "" if v.get("imported") else "   [NEVER IMPORTED]"
    print(f"{pad}v1 read the book: {rng} conf {v.get('confidence')}{imp}")
    if v.get("evidence"):
        print(f"{pad}  {v['evidence'][:220]}")


found = set()
for line in open(PACKETS, encoding="utf-8"):
    p = json.loads(line)
    if p["seriesId"] not in WANT:
        continue
    found.add(p["seriesId"])
    print(f"\n{'=' * 110}")
    print(f"=== S{p['seriesId']}  {p['name']}   [{p['key']}]  {g(p['years'][0])}-{g(p['years'][1])}")
    print(f"    {len(p['collections'])} collected edition(s), {len(p['issues'])} other file(s)")
    for f in p["folders"]:
        print(f"    folder: {f}")

    print(f"\n  ---- the {len(p['issues'])} non-collection files, as named on disk ----")
    for i in p["issues"]:
        role = (i.get("locg") or {}).get("role", "")
        print(f"   [{i['id']:>7}] #{g(i['issueNo']):>6} {g(i['pages']):>4}pp {g(i['mb']):>6}MB "
              f"{str(i.get('format') or '')[:14]:<14} {role[:11]:<11} {i['file'][:78]}")

    print(f"\n  ---- the {len(p['collections'])} collected editions ----")
    for c in sorted(p["collections"], key=lambda x: (x["vol"] is None, x["vol"] or 0, x["id"])):
        node = c.get("node") or {}
        cur = next((s for s in c.get("spans", []) if s["src"] == "curated"), None)
        claim = f"#{g(cur['start'])}-{g(cur['end'])}" if cur else "—"
        per = ""
        if cur and c.get("pages"):
            n = int(cur["end"]) - int(cur["start"]) + 1
            if n > 0:
                per = f"  {c['pages'] / n:.0f}pp/issue"
        print(f"\n   [{c['id']:>7}] vol {g(c['vol']):>3}  claims {claim:<12} {g(c['pages']):>4}pp {g(c['mb']):>7}MB{per}")
        print(f"        {c['file'][:100]}")
        print(f"        node: label={node.get('label')} contains={node.get('contains')} spanSource={node.get('spanSource')}")
        if cur:
            tag = "gold" if cur.get("gold") else (cur.get("providerRef") or "")
            print(f"        CUR  #{g(cur['start'])}-{g(cur['end'])} conf={cur.get('conf')} [{tag}]")
            if cur.get("note"):
                print(f"          note: {cur['note'][:220]}")
        for s in c.get("spans", []):
            if s["src"] == "curated":
                continue
            print(f"        {SRC.get(s['src'], s['src'])} #{g(s['start'])}-{g(s['end'])} conf={s.get('conf')}"
                  + (f'  "{s["title"][:44]}"' if s.get("title") else ""))
            if s.get("note"):
                print(f"          note: {s['note'][:150]}")
        if c.get("gcd"):
            r = c["gcd"]
            print(f"        GCD reprints [{str(r['series'])[:34]}] via {r.get('method')}: {nums(r['issues'])}")
            if r.get("other"):
                print(f"          guest stories from: {', '.join(str(x)[:24] for x in r['other'])}")
        show_locg(c.get("locg"), "        ")
        show_v1(c.get("v1"), "        ")

for sid in sorted(WANT - found):
    print(f"\n=== S{sid}: not in packets3.jsonl (no collected edition, or a book: series)")
