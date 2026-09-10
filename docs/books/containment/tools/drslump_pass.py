"""Dr. Slump (S5778): a whole-shelf refusal, because the books print titles where a number would be.

Akira Toriyama's Dr. Slump was serialised as short, self-contained gag chapters, and Viz's edition
prints a `Table of Contents!` in every volume that lists them BY TITLE with a page number and no chapter
number anywhere:

    DR. SLUMP Vol. 1              DR. SLUMP Vol. 10
    The Birth of Arale!    5      Here Comes the Tsun              5
    Here Comes Arale!     21      The Tsuns and the Norimakis     21
    Something's Missing!  37      High School Champion, Part 1    37
    ...                           ...
    Is It a Girl!? Is It a Boy!? 173   The Horrors of Dating, Part 2  175

v01, v02 and v10 were read to be sure it is the whole shelf's convention and not one volume's. The
archives are flat and name no chapter either, and the shelf holds no loose issues. So there is no
number in any coordinate to write a range in - not on the page, not in the archive, not in the book.

The ids come from the catalog so every edition is covered; the judgement and the reason are mine.
"""
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")
SID = 5778

WHY = ("the volume's own printed Table of Contents lists its chapters BY TITLE with a page number and no "
       "chapter number, and the archive names no chapter either; there is no numbered coordinate on this "
       "shelf for a range to be written in")

HEAD = """# Dr. Slump (S5778) — the 18-volume Viz Shonen Jump edition of Akira Toriyama's gag manga, digital
# rips. No loose issues on the shelf.
#
# REFUSED, all 18, because the books print titles where a number would be. Every volume carries a
# `Table of Contents!` page (p4), and every entry on it is a chapter TITLE and a page number:
#
#     DR. SLUMP Vol. 1                        DR. SLUMP Vol. 10
#     The Birth of Arale!            5        Here Comes the Tsun                 5
#     Here Comes Arale!             21        The Tsuns and the Norimakis        21
#     Something's Missing!          37        High School Champion, Part 1       37
#     ...                                     ...
#     Is It a Girl!? Is It a Boy!? 173        The Horrors of Dating, Part 2     175
#
# v01, v02 and v10 were read to confirm it is the shelf's convention rather than one volume's habit.
# The archives are flat and carry no chapter token, and there are no loose issues here for a range to
# nest. Dr. Slump's chapters are short self-contained gags and this edition never numbers them, so a
# range would have to invent its own coordinate — which is the one thing a span must never do."""


def main():
    dst = os.path.join(DEC, f"S{SID}.txt")
    if os.path.exists(dst):
        raise SystemExit(f"{dst} already exists - left alone")
    con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
    rows = con.execute("""
        SELECT i.Id FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
        ORDER BY i.FileName""", (SID,)).fetchall()
    with open(dst, "w", encoding="utf-8") as out:
        out.write(HEAD + "\n\n")
        out.write(f"N {SID} every volume prints a Table of Contents that names its chapters by TITLE and "
                  f"page number only — no chapter numbers anywhere, in the book or in the archive — so the "
                  f"shelf has no coordinate a range could be written in (read in v01, v02 and v10)\n\n")
        for (iid,) in rows:
            out.write(f"u {iid} {WHY}\n")
    print(f"S{SID}: {len(rows)} refusals")


main()
