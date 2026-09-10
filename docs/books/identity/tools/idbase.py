"""The evidence layer shared by every identity tool: one read-only view of what each shelf carries.

The containment pass kept its DB access in each script's head (`export_packets_v3.py`, `brief.py`,
`coverage_ledger.py` all open `file:...?mode=ro` and hard-code the paths). That is right for a script
someone reads in one sitting. It is wrong here for one reason only: the TIER RULE of PLAN §7-S has to be
computed identically by `identity_coverage.py` (which must make the tiers sum to the population) and by
`identity_packet.py` (which stamps a tier on each packet). Two copies of that rule would drift, and a
drifted tier is a shelf read under the wrong assumption. So the rule lives here once, with the loaders it
needs, and nothing else moves in.

Read-only, always. `apply_identity.py` is the only tool in this directory that opens books.db for writing.
"""
import json
import os
import re
import sqlite3
from collections import Counter, defaultdict

HOT = r"F:/Work/MovieTheater/data/books/v2/books.db"
LEGS = r"F:/Work/MovieTheater/data/books/v2/books-legs.db"
GCD_DUMP = r"F:/Work/MovieTheater/data/books/archive/mybooks/GrandComicsDatabase-06-01-06.db"
CV_RIP = r"F:/Work/MovieTheater/data/books/archive/mybooks/comicdb_comicvine_20260122.db"
CV_REF = r"F:/Work/MovieTheater/data/books/archive/mybooks/cvref.db"

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, os.pardir))          # docs/books/identity
BATCHES = os.path.join(ROOT, "batches")
DECISIONS = os.path.join(ROOT, "decisions")
STATE = os.path.join(ROOT, "state.json")
UNDO = os.path.join(ROOT, "undo")

PREFIX = "\\\\Library\\Public\\5 - Comics\\"

# PLAN §6.8. Provider is stored as the enum INT everywhere (SeriesKeyLink, ItemProviderLink,
# LinkCandidates), which is why every query below compares against a number, never a name.
P_CV, P_EXTERNAL, P_LOCG, P_GCD, P_MU, P_BARNEY = 0, 1, 2, 3, 4, 5
ST_MATCHED, ST_MANUAL = 1, 5

# The population, stated once. Every count in every tool is defined over exactly this set.
SHELF_SQL = """SELECT s.Id FROM Series s
               WHERE s.CanonicalKey NOT LIKE 'book:%'
                 AND EXISTS (SELECT 1 FROM Item i
                             WHERE i.SeriesId = s.Id AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0)"""
ITEM_SQL = """SELECT count(*) FROM Item i WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0"""

CONFIDENCES = ("1.0", "0.95", "0.9", "0.7")
FLAGS = {"conflated-series", "split-needed", "merge-with", "wrong-cv-link", "needs-fetch",
         "not-a-run", "provider-missing", "misfiled", "duplicate-shelf", "partial-rip", "mobile-rip",
         "needs-web"}
OPEN_FLAG_STATES = (None, "", "Pending", "Open")

RX_NUM = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")
RX_DIGITS = re.compile(r"\d+")
RX_CV_WEB = re.compile(r"comicvine\.gamespot\.com/[^/]+/4000-(\d+)")
RX_ART = re.compile(r"^(the|a|an)\s+", re.I)
RX_NONWORD = re.compile(r"[^a-z0-9]+")


def num(s):
    """A filename's issue coordinate as a number, or None. Deliberately strict: `217 (GL only)` is not
    a number, and the containment pass proved that guessing one is how a file lands in a range that does
    not contain it."""
    m = RX_NUM.match(str(s) if s is not None else "")
    if not m:
        return None
    return float(m.group(1)) + (float("0." + m.group(2)) if m.group(2) else 0.0)


def norm_name(s):
    """The spelling cvref.db stores in `normName`: lowercased, leading article dropped, punctuation gone.
    Sampled against the rip — 'The Human Torch' is stored as 'human torch' — so an equality lookup on
    this string hits `ix_vol_norm` instead of scanning 153,805 volumes."""
    s = (s or "").strip().lower()
    s = RX_ART.sub("", s)
    return RX_NONWORD.sub(" ", s).strip()


def short_path(p):
    p = p or ""
    return p[len(PREFIX):] if p.startswith(PREFIX) else p


def open_hot(write=False):
    if write:
        return sqlite3.connect(HOT)
    con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
    con.execute(f"ATTACH DATABASE 'file:{LEGS}?mode=ro' AS legs")
    return con


def open_gcd_dump():
    return sqlite3.connect(f"file:{GCD_DUMP}?mode=ro", uri=True) if os.path.exists(GCD_DUMP) else None


def open_cv_ref():
    return sqlite3.connect(f"file:{CV_REF}?mode=ro", uri=True) if os.path.exists(CV_REF) else None


def open_cv_rip():
    return sqlite3.connect(f"file:{CV_RIP}?mode=ro", uri=True) if os.path.exists(CV_RIP) else None


# ── the shelf-level evidence, loaded in bulk ─────────────────────────────────────────────────────
class Evidence:
    """Every shelf-level fact the tier rule and the packet need, read once in whole-table sweeps.

    Per-shelf queries would be ~15 round trips × 20,385 shelves. These are seven sweeps of tables whose
    largest is ItemProviderLink at 321,521 rows, and they finish in a couple of seconds."""

    def __init__(self, con=None):
        self.con = con or open_hot()
        c = self.con
        self.shelves = [r[0] for r in c.execute(SHELF_SQL + " ORDER BY s.Id")]
        self.shelf_set = set(self.shelves)
        self.total_items = c.execute(ITEM_SQL).fetchone()[0]

        self.series = {}
        for row in c.execute("""SELECT Id, coalesce(DisplayNameOverride, Name), CanonicalKey, ParsedKey,
                                       YearStart, YearEnd, CvVolumeId, TitleId, MuSeriesId, ExternalWorkId,
                                       Franchise, PublisherId
                                FROM Series"""):
            if row[0] in self.shelf_set:
                self.series[row[0]] = dict(zip(
                    ("id", "name", "canonicalKey", "parsedKey", "yearStart", "yearEnd", "cvVolumeId",
                     "titleId", "muSeriesId", "externalWorkId", "franchise", "publisherId"), row))

        # the shelf's parsed keys: SeriesAlias is the alias set, Series.ParsedKey the survivor's own.
        # PLAN §6.4 — LinkCandidates and SeriesKeyLink are BOTH keyed on this raw spelling, not on Id.
        self.keys = defaultdict(set)
        for sid, k in c.execute("SELECT SeriesId, ParsedKey FROM SeriesAlias WHERE ParsedKey IS NOT NULL"):
            if sid in self.shelf_set:
                self.keys[sid].add(k)
        for sid, k in c.execute("SELECT Id, ParsedKey FROM Series WHERE ParsedKey IS NOT NULL AND ParsedKey <> ''"):
            if sid in self.shelf_set:
                self.keys[sid].add(k)

        self.size = Counter()
        self.collections = Counter()
        self.numbered = Counter()          # issue files carrying a NUMERIC coordinate — the ratio's numerator
        self.orphan_items = 0
        for sid, iscol, n in c.execute("""SELECT i.SeriesId, coalesce(cd.IsCollection,0), count(*)
                                          FROM Item i LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
                                          WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
                                          GROUP BY i.SeriesId, coalesce(cd.IsCollection,0)"""):
            if sid is None or sid not in self.shelf_set:
                self.orphan_items += n
                continue
            self.size[sid] += n
            if iscol:
                self.collections[sid] += n

        # A shelf of trades has no issue coordinate, so it has no count ratio and must not be demoted on
        # 0/N — the ratio is only meaningful over files that carry a NUMBER (PLAN §7-S).
        for sid, ino in c.execute("""SELECT i.SeriesId, cd.IssueNo
                                     FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                                     WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
                                       AND coalesce(cd.IsCollection,0) = 0 AND cd.IssueNo IS NOT NULL
                                       AND i.SeriesId IS NOT NULL"""):
            if sid in self.shelf_set and num(ino) is not None:
                self.numbered[sid] += 1

        # Issue numbers that name more than one REAL file (>= 10pp, so cover packs and 1pp variant scans do
        # not count). Two runs that both start at #1 on one shelf is the shape a reading worker found five
        # times in tier A — a sequel filed in a `v2 - <Subtitle>` subfolder — and it is invisible to every
        # provider, because each provider answers about one of the two.
        seen = defaultdict(Counter)
        for sid, ino in c.execute("""SELECT i.SeriesId, cd.IssueNo
                                     FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                                     WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
                                       AND coalesce(cd.IsCollection,0) = 0 AND cd.IssueNo IS NOT NULL
                                       AND coalesce(i.PageCount,0) >= 10 AND i.SeriesId IS NOT NULL"""):
            x = num(ino)
            if x is not None and sid in self.shelf_set:
                seen[sid][x] += 1
        self.dupe_numbers = Counter({sid: sum(1 for v in c2.values() if v > 1) for sid, c2 in seen.items()})

        # per-file provider links, rolled up to the shelf. `Applied` is a v1 artefact (§2) and is never read.
        self.gcd_files = defaultdict(Counter)      # sid -> {GcdSeriesId: files}
        self.gcd_methods = defaultdict(Counter)
        self.cv_files = defaultdict(Counter)       # sid -> {CvVolumeId: files}
        for sid, prov, sec, method in c.execute("""
                SELECT i.SeriesId, l.Provider, l.SecondaryKey, l.Method
                FROM ItemProviderLink l JOIN Item i ON i.Id = l.ItemId
                WHERE l.Status = 1 AND l.Provider IN (0, 3)
                  AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL"""):
            if sid not in self.shelf_set or not sec or not str(sec).strip().isdigit():
                continue
            k = int(sec)
            if prov == P_GCD:
                self.gcd_files[sid][k] += 1
                self.gcd_methods[sid][method or "?"] += 1
            else:
                self.cv_files[sid][k] += 1

        self.cv_volume = {r[0]: dict(zip(("id", "name", "startYear", "publisher", "countOfIssues"), r))
                          for r in c.execute("SELECT Id, Name, StartYear, PublisherName, CountOfIssues FROM CvVolume")}
        # The cached issue list, read once (70,591 rows). The span is computed NUMERICALLY: IssueNumber is
        # TEXT, so SQL's min/max called a 13-issue volume "#1-9" because '9' sorts above '13'. A packet that
        # prints a run as shorter than it is invites the reader to reject the right volume on arithmetic.
        self.cv_issue_nums = defaultdict(list)
        self.cv_issue_text = defaultdict(list)
        for vid, n in c.execute("SELECT VolumeId, IssueNumber FROM CvIssue"):
            x = num(n)
            if x is not None:
                self.cv_issue_nums[vid].append(x)
            elif n:
                self.cv_issue_text[vid].append(str(n))
        self.cv_issue_span = {}
        for vid in set(self.cv_issue_nums) | set(self.cv_issue_text):
            xs, ts = self.cv_issue_nums.get(vid, []), self.cv_issue_text.get(vid, [])
            total = len(xs) + len(ts)
            if xs:
                self.cv_issue_span[vid] = (total, f"{min(xs):g}", f"{max(xs):g}")
            else:
                self.cv_issue_span[vid] = (total, min(ts), max(ts))

        self.gcd_series = {r[0]: dict(zip(("id", "name", "yearBegan", "yearEnded", "publisher", "format",
                                           "issueCount"), r))
                           for r in c.execute("""SELECT GcdSeriesId, Name, YearBegan, YearEnded, Publisher,
                                                        Format, IssueCount FROM legs.GcdSeries""")}

        # open flags. ReviewState is NULL/'Pending' while the question stands; anything else was answered.
        self.flags = defaultdict(list)
        for sid, iid, flag, detail, state in c.execute(
                "SELECT SeriesId, ItemId, Flag, Detail, ReviewState FROM ContainmentFlag WHERE SeriesId IS NOT NULL"):
            if sid in self.shelf_set:
                self.flags[sid].append({"itemId": iid, "flag": flag, "detail": detail, "state": state,
                                        "open": state in OPEN_FLAG_STATES})

        self.tier_cache = {}

    # ── derived per-shelf views ──────────────────────────────────────────────────────────────────
    def open_flags(self, sid):
        return [f for f in self.flags.get(sid, ()) if f["open"]]

    def conflated_open(self, sid):
        return any(f["flag"] == "conflated-series" and f["open"] for f in self.flags.get(sid, ()))

    def gcd_ids(self, sid):
        return self.gcd_files.get(sid) or Counter()

    # A single stray GCD link is not a contradiction. v1 matched 63,299 files by issue NUMBER inside a
    # series it had chosen by name (§2), so one file of a 40-file run landing on a neighbouring series is
    # noise, not evidence that the shelf holds two runs. PLAN §7-S sets the threshold: a minority GCD id
    # counts only when it covers >= 2 files or >= 10% of the shelf's linked files.
    def gcd_verdict(self, sid):
        """-> (the series id the shelf's files agree on or None, contested?)"""
        g = self.gcd_files.get(sid)
        if not g:
            return (None, False)
        ranked = g.most_common()
        if len(ranked) == 1:
            return (ranked[0][0], False)
        total = sum(g.values())
        for gid, n in ranked[1:]:
            if n >= 2 or n >= 0.10 * total:
                return (None, True)
        return (ranked[0][0], False)

    def our_issue_years(self, sid):
        return self.series[sid].get("yearStart"), self.series[sid].get("yearEnd")

    def arithmetic(self, sid):
        """The lie detectors of PLAN §4.4, computed but NEVER applied as a rule — printed in the packet
        and used only to DEMOTE a shelf into a tier that gets read more carefully."""
        s = self.series[sid]
        vol = self.cv_volume.get(s["cvVolumeId"]) if s["cvVolumeId"] else None
        gid, _contested = self.gcd_verdict(sid)
        g = self.gcd_series.get(gid) if gid else None
        ours_n = self.numbered.get(sid, 0)
        out = {"ourYear": s["yearStart"], "ourIssueFiles": ours_n,
               "cvYear": vol["startYear"] if vol else None,
               "cvCount": vol["countOfIssues"] if vol else None,
               "gcdYear": g["yearBegan"] if g else None,
               "gcdCount": g["issueCount"] if g else None,
               "yearGap": None, "ratio": None, "cvGcdYearGap": None}
        their_year = out["cvYear"] if out["cvYear"] else out["gcdYear"]
        if s["yearStart"] and their_year:
            out["yearGap"] = abs(int(s["yearStart"]) - int(their_year))
        their_count = out["cvCount"] if out["cvCount"] else out["gcdCount"]
        if their_count and ours_n >= 1:
            out["ratio"] = ours_n / float(their_count)
        if out["cvYear"] and out["gcdYear"]:
            out["cvGcdYearGap"] = abs(int(out["cvYear"]) - int(out["gcdYear"]))
        return out

    def tier(self, sid):
        """PLAN §7-S's four tiers, as a total function on the population.

        Evaluated in this order so that every shelf lands in exactly one tier and the reason is nameable:
        a contradiction or an open question outranks how many legs a shelf has, because a shelf that is
        several runs cannot have one identity however many providers name it (§3.3)."""
        if sid in self.tier_cache:
            return self.tier_cache[sid]
        s = self.series.get(sid)
        if s is None:
            return ("?", "not a file-holding comic shelf", "")
        gids = self.gcd_ids(sid)
        gid, contested = self.gcd_verdict(sid)
        cvf = self.cv_files.get(sid, {})
        has_cv = s["cvVolumeId"] is not None
        a = self.arithmetic(sid)
        # Only conflated-series demotes. label-ambiguous / overlap-in-series / provider-disagrees /
        # duplicate-edition are containment facts — printed on the packet, but they say nothing about
        # whether this shelf is ONE run (PLAN §7-S).
        conflated = self.conflated_open(sid)

        if conflated:
            r = ("C", "an open conflated-series flag", "several runs number from #1")
        elif self.size.get(sid, 0) > 100:
            r = ("C", "more than 100 files", f"{self.size.get(sid,0)} files")
        elif contested:
            r = ("C", "several GCD series", f"{len(gids)} named, the minority on >= 2 files or >= 10%")
        elif self.dupe_numbers.get(sid, 0) >= 2 and (len(self.keys.get(sid, ())) >= 2
                                                     or (a["ratio"] is not None and a["ratio"] >= 1.5)):
            # Two or more numbers each naming two real files, on a shelf that either answers to more than
            # one parsed key or holds half again what the provider says the run is: that is two runs, not a
            # duplicate scan. A single-key shelf whose only sign is repeated numbers (Avatar's cover packs)
            # stays where it was — the second condition is what keeps this from firing on those.
            r = ("C", "issue numbers naming more than one file",
                 f"{self.dupe_numbers[sid]} number(s) x2+, {len(self.keys.get(sid, ()))} parsed key(s)"
                 + (f", ratio {a['ratio']:.2f}" if a["ratio"] is not None else ""))
        elif not has_cv and not gids and len(cvf) > 1:
            r = ("C", "several v1 per-file CV volumes and nothing above them", f"{len(cvf)} volumes")
        elif a["yearGap"] is not None and a["yearGap"] > 1:
            r = ("C", "year gap > 1", f"gap {a['yearGap']}")
        elif a["ratio"] is not None and not (0.5 <= a["ratio"] <= 2.0):
            r = ("C", "count ratio outside 0.5-2", f"ratio {a['ratio']:.2f}")
        elif a["cvGcdYearGap"] is not None and a["cvGcdYearGap"] > 1:
            r = ("C", "CV and GCD disagree on the start year", f"by {a['cvGcdYearGap']}")
        elif has_cv and gid:
            r = ("A", "CV volume and one GCD series, arithmetic clean", "")
        elif has_cv or gid or len(cvf) == 1:
            legs = "CV volume" if has_cv else "one GCD series" if gid else "a unanimous v1 per-file CV volume"
            r = ("B", "one leg, arithmetic clean", legs)
        else:
            r = ("D", "no leg at all", "")
        self.tier_cache[sid] = r
        return r

    def cv_issue_numbers(self, vid):
        return sorted(self.cv_issue_nums.get(vid, ()))

    def candidates(self, sid):
        """LinkCandidates joined the only way it can be joined: through the shelf's RAW parsed keys.
        Marked '(v1 search)' in the packet because that is exactly what it is — a name search someone ran
        once, with a score that PLAN §4.7 says is not trust."""
        out, seen = [], set()
        keys = sorted(self.keys.get(sid, ()))
        if not keys:
            return out
        q = ",".join("?" * len(keys))
        for key, js in self.con.execute(
                f"SELECT Key, CandidatesJson FROM legs.LinkCandidates WHERE Scope=1 AND Provider=0 AND Key IN ({q})",
                keys):
            try:
                for cand in json.loads(js or "[]"):
                    vid = cand.get("VolumeId")
                    if vid in seen:
                        continue
                    seen.add(vid)
                    out.append(cand)
            except (ValueError, TypeError):
                continue
        return sorted(out, key=lambda c: -(c.get("Score") or 0))


def resolve_decision_file(arg):
    """Accept a bare batch name (`A-001`), a name with its extension, or any path — relative to the shell's
    cwd or to docs/books/identity. The tools are run from several directories (the repo root, the tools
    directory, the identity directory), and a checker that cannot find the file it was handed is a checker
    people stop running."""
    # batches/ is deliberately NOT searched: `A-001` there is the PACKET, and silently checking a packet
    # as though it were a decision file would report a batch as covered when nothing had been decided.
    cands = [arg, arg + ".txt",
             os.path.join(DECISIONS, arg), os.path.join(DECISIONS, arg + ".txt"),
             os.path.join(ROOT, arg), os.path.join(ROOT, arg + ".txt")]
    for c in cands:
        if os.path.isfile(c):
            return os.path.abspath(c)
    raise SystemExit(f"no such decision file: {arg!r}\n  tried:\n    " + "\n    ".join(cands))


RX_REVISIT = re.compile(r"^R-(\d+)$")
RX_DECIDES = re.compile(r"^([SR])\s+S?(\d+)\b")
RX_FLAG = re.compile(r"^F\s+S?(\d+)\s+(\S+)")


def revisit_rank(path):
    """R-003 -> 3; a tier batch -> None. A revisit file supersedes earlier decisions for its shelves, so
    the ONLY thing that decides precedence is this number — never the file's mtime, which a re-save moves."""
    m = RX_REVISIT.match(os.path.splitext(os.path.basename(path))[0])
    return int(m.group(1)) if m else None


def scan_decisions(paths=None):
    """Which file decides which shelf, and which file WINS. One copy of the precedence rule.

    Deliberately a light scan: it reads only the shelf id off each `S`/`R` line and the flag off each `F`.
    Grammar is `check_identity.py`'s job and stays there — this exists so that the checker, the applier and
    the coverage readout cannot disagree about which line is in force, which is the failure mode that
    matters once a shelf has been decided twice on purpose.

    -> (decides, winner, superseded, dupes)
       decides    {file: {"sids": set, "rank": int|None, "kinds": {sid: 'S'|'R'},
                          "confs": {sid: str|None}, "flags": {sid: [flag, ...]}}}
       winner     {sid: file}                     the line in force
       superseded {sid: [file, ...]}              earlier files a revisit overrode, oldest first
       dupes      {sid: [file, ...]}              decided in two or more NON-revisit files — a defect
    """
    if paths is None:
        paths = ([os.path.join(DECISIONS, f) for f in sorted(os.listdir(DECISIONS)) if f.endswith(".txt")]
                 if os.path.isdir(DECISIONS) else [])
    decides = {}
    for p in paths:
        rec = {"sids": set(), "rank": revisit_rank(p), "kinds": {}, "confs": {}, "flags": {},
               "flags_full": {}, "cv": {}, "gcd": {}}
        for raw in open(p, encoding="utf-8"):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            m = RX_DECIDES.match(line)
            if m:
                sid = int(m.group(2))
                rec["sids"].add(sid)
                rec["kinds"][sid] = m.group(1)
                conf = None
                if m.group(1) == "S":
                    head = line.split("|", 1)[0].split()
                    conf = next((t for t in head[2:] if "=" not in t), None)
                    kv = dict((t.split("=", 1) + [""])[:2] for t in head[2:] if "=" in t)
                    if kv.get("cv", "-").isdigit():
                        rec["cv"][sid] = int(kv["cv"])
                    g = kv.get("gcd", "-").lstrip("s")
                    if g.isdigit():
                        rec["gcd"][sid] = int(g)
                rec["confs"][sid] = conf
                continue
            mf = RX_FLAG.match(line)
            if mf:
                rec["flags"].setdefault(int(mf.group(1)), []).append(mf.group(2).split("=")[0])
                # the whole token too: `merge-with=64225` names its target, and a merge check that cannot
                # read the target can only ask "is there a flag", which is not the question
                rec.setdefault("flags_full", {}).setdefault(int(mf.group(1)), []).append(mf.group(2))
        decides[p] = rec

    winner, superseded, dupes = {}, {}, {}
    by_sid = {}
    for p, rec in decides.items():
        for sid in rec["sids"]:
            by_sid.setdefault(sid, []).append(p)
    for sid, files in by_sid.items():
        revs = sorted((f for f in files if decides[f]["rank"] is not None),
                      key=lambda f: decides[f]["rank"])
        plain = [f for f in files if decides[f]["rank"] is None]
        if len(plain) > 1:
            dupes[sid] = sorted(plain)
        if revs:
            winner[sid] = revs[-1]
            superseded[sid] = sorted(plain) + revs[:-1]
        else:
            winner[sid] = sorted(plain)[0] if plain else files[0]
    return decides, winner, superseded, dupes


def load_state():
    if os.path.exists(STATE):
        with open(STATE, encoding="utf-8") as f:
            return json.load(f)
    return {"cursors": {"A": 0, "B": 0, "C": 0, "D": 0}, "emitted": [], "checked": [], "landed": [],
            "waves": []}


def save_state(st):
    tmp = STATE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(st, f, indent=1)
    os.replace(tmp, STATE)
