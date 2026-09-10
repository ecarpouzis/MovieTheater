"""Decision files for the Heavy Metal Magazine shelves.

Heavy Metal is a MAGAZINE. Every file under `Heavy Metal\\Heavy Metal Magazine (1977)\\_Back Issues` is
one issue of it - 88 to 133 pages, named `Heavy Metal v05 007 (1981-10)`: volume, number within the
volume, and the cover month. Not one of them is a collected edition, and none of them collects a
numbered series: a magazine issue is an anthology of stories, and the stories are not numbered units
this catalog holds.

They are flagged IsCollection=1 with FormatRaw 'TPB' because the `vNN` in the file name reads as a
volume - PLAN.md §14.6, the 2,274 single issues carrying IsCollection=1. While that flag stands each of
these is a container waiting for a range, and any provider willing to guess one gets to nest a shelf
under a 96-page magazine.

So: an explicit refusal for every one, and ONE flag per shelf naming the whole population rather than
139 identical flags in Eric's queue.

The prose and the reasons are mine; the ids come from the catalog so the coverage pass2.py checks is
complete.
"""
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ISSUE = ("one issue of Heavy Metal magazine, 88-133pp, keyed by volume, number-within-volume and cover "
         "month; it is a base unit and not a collection, and the stories inside it are not numbered units "
         "this catalog holds, so there is no coordinate a range could be written in")

VOLS = {
    94623: ("v01 (1977)", "v01 001 (1977-04) through v01 013 (1978-04), thirteen monthly issues"),
    94624: ("v02 (1978)", "v02 001 (1978-05) through v02 012 (1979-04), twelve monthly issues"),
    94625: ("v03 (1979)", "v03 001 (1979-05) through v03 011 (1980-03), eleven monthly issues"),
    94626: ("v04 (1980)", "v04 001 (1980-04) through v04 012 (1981-04), twelve monthly issues"),
    94627: ("v05 (1981)", "v05 001 (1981-04) through v05 012 (1982-03), twelve monthly issues"),
    94628: ("v06 (1982)", "v06 001 (1982-04) through v06 012 (1983-03), twelve monthly issues"),
    94629: ("v07 (1983)", "v07 001 (1983-04) through v07 012 (1984-03), twelve monthly issues"),
    94630: ("v08 (1984)", "v08 001 (1984-04) through v08 013 (1985-04), thirteen monthly issues"),
    94631: ("v09 (1985)", "v09 002 (1985-05) through v09 010 (1986-Winter); the run turns quarterly at "
                          "its end and v09 001 is not held"),
    94632: ("v10 (1986)", "v10 001 (1986-Spring) through v10 004 (Winter 1987), four quarterly issues"),
    94633: ("v11 (1987)", "v11 001 (1987-Spring) through v11 004 (1988-Winter), four quarterly issues"),
    94634: ("v12 (1987)", "v12 001 (1988-Spring) through v12 004 (1989-Winter), four quarterly issues"),
}

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)


def rows(sid):
    return con.execute("""
        SELECT i.Id, i.FileName, i.PageCount FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
        ORDER BY i.FileName""", (sid,)).fetchall()


def write(sid, head, note, flags, per_item):
    dst = os.path.join(DEC, f"S{sid}.txt")
    if os.path.exists(dst):
        print(f"S{sid}: already decided - left alone")
        return 0
    rs = rows(sid)
    with open(dst, "w", encoding="utf-8") as out:
        out.write(head.rstrip() + "\n\n")
        out.write(f"N {sid} {note}\n\n")
        for iid, kind, detail in flags:
            out.write(f"F {iid} {kind} | {detail}\n")
        if flags:
            out.write("\n")
        for iid, fn, pc in rs:
            out.write(f"u {iid} {per_item(iid, fn, pc)}\n")
    print(f"S{sid}: {len(rs)} refusals, {len(flags)} flags")
    return len(rs)


total = 0
for sid, (label, span) in VOLS.items():
    rs = rows(sid)
    head = (
        f"# Heavy Metal {label} (S{sid}) — {len(rs)} issues of the magazine: {span}.\n"
        f"#\n"
        f"# Heavy Metal is a magazine, and every file on this shelf is one issue of it — 88 to 133 pages,\n"
        f"# named `Heavy Metal {label.split()[0]} NNN (YYYY-MM)`: volume, number within the volume, and the\n"
        f"# cover month. A magazine issue is a base unit, not a collected edition. It is an anthology of\n"
        f"# stories, and those stories are not numbered units this catalog holds, so there is nothing for a\n"
        f"# range to be written in — not an issue coordinate, not a volume one, not a chapter one.\n"
        f"#\n"
        f"# Every one of them is nevertheless flagged IsCollection=1 with FormatRaw 'TPB', because the `vNN`\n"
        f"# in the file name reads as a volume. That is PLAN.md §14.6 exactly: while the flag stands, each\n"
        f"# of these hundred-page magazines is a container waiting for a range, and a provider willing to\n"
        f"# guess one would get to nest files under it. Flagged once for the shelf rather than {len(rs)}\n"
        f"# times."
    )
    note = (f"the whole shelf is Heavy Metal magazine issues ({span}); each is a single ~100pp issue "
            f"wrongly flagged IsCollection=1 FormatRaw=TPB because the vNN in its name reads as a volume "
            f"(§14.6). None of them collects a numbered series")
    flag = [(rs[0][0], "label-ambiguous",
             f"all {len(rs)} files on this shelf are single ~100pp issues of Heavy Metal magazine flagged "
             f"IsCollection=1 FormatRaw=TPB; the vNN in the file name is the magazine's volume, not a "
             f"collected edition")]
    total += write(sid, head, note, flag, lambda iid, fn, pc: ISSUE)

# ---- S8533: the leftovers - loose back issues from 1989-2009 that never got a per-volume shelf, plus
# two things that are not magazine issues at all.
EXTRA = {
    29396: ("Heavy Metal Book 1993 - Bad Blood - The Vampire Collection, 145pp: a themed anthology that "
            "reprints vampire stories from many different issues of the magazine. It genuinely collects "
            "something, but what it collects are STORIES scattered across two decades of a magazine, not a "
            "contiguous run of numbered units, so no range can describe it"),
}
rs8533 = rows(8533)
head = (
    "# Heavy Metal (S8533) — the leftovers of the magazine: 21 collected-flagged files spread over the\n"
    "# 1989, 1990, 1992, 1997, 1999, 2003, 2007, 2008 and 2009 back-issue folders, the Annuals and\n"
    "# Specials folder and Related_Publications, alongside 64 loose issues of the same magazine.\n"
    "#\n"
    "# Nineteen of the twenty-one are ordinary issues — `Heavy Metal 2007 Vol.31 No.03 (July)`,\n"
    "# `Heavy Metal v27 004 (2003)` — 111 to 133 pages each, flagged IsCollection=1 because of the volume\n"
    "# token in the name (§14.6). A magazine issue is a base unit and collects nothing numbered.\n"
    "#\n"
    "# One is different and is refused for a different reason: `Heavy Metal Book 1993 Bad Blood - The\n"
    "# Vampire Collection` really is an anthology, but what it gathers are stories picked out of many\n"
    "# issues over many years. That is containment with no coordinate — there is no contiguous run of\n"
    "# numbered units for a range to name.\n"
    "#\n"
    "# The same shelf mixes issues numbered three different ways — `Vol.13 No.05`, `v27 001`, `1992-11\n"
    "# (V17 04)` — which is worth knowing before anyone tries to build a ladder here."
)
total += write(8533,
               head,
               "21 collected-flagged files, nineteen of them ordinary magazine issues wrongly flagged "
               "IsCollection=1 (§14.6) and one a story anthology with no coordinate; the shelf mixes three "
               "different ways of writing the magazine's own numbering (Vol.13 No.05, v27 001, 1992-11 (V17 04))",
               [(29426, "label-ambiguous",
                 "nineteen of this shelf's twenty-one collected-flagged files are single ~120pp issues of "
                 "Heavy Metal magazine; the volume token in the file name is the magazine's volume, not a "
                 "collected edition")],
               lambda iid, fn, pc: EXTRA.get(iid, (ISSUE,))[0] if iid in EXTRA else ISSUE)

# ---- S35373: one special issue, filed on a shelf named after a strip inside it.
head = (
    "# Heavy Metal Presents: Arzach (S35373) — the shelf is named for Moebius's Arzach, but the one\n"
    "# collected-flagged file on it is `Heavy Metal - SE v17 002 (Fantasy Issue) (2003)`, 129pp: a\n"
    "# special issue of the magazine. Nine loose files sit beside it.\n"
    "#\n"
    "# A themed special issue of a magazine is still a magazine issue. It reprints stories, not numbered\n"
    "# units, and there is no coordinate for a range."
)
total += write(35373, head,
               "the one collected-flagged file here is a Heavy Metal special issue (SE v17 002, the 2003 "
               "Fantasy Issue), not a collected edition; the shelf is named after a strip printed inside it",
               [(29543, "label-ambiguous",
                 "a 129pp special issue of Heavy Metal magazine flagged IsCollection=1; it reprints stories "
                 "from the magazine's own back catalogue, which is not a numbered coordinate")],
               lambda iid, fn, pc: ("a themed special issue of Heavy Metal magazine, 129pp; it reprints "
                                    "stories rather than numbered units, so there is no coordinate a range "
                                    "could be written in"))

print(f"\n{{ shelves: {len(VOLS) + 2}, refusals: {total} }}")
