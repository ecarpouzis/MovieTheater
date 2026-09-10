"""Close the container leak: 26 books flagged as single issues were nesting 604 files.

They were invisible to the decision machinery, which only inspects items flagged as collections, so no
decision file could reach them. Each one's span is retracted by name and reason; the ones that are plainly
books are also corrected to collections so a later pass can see them (and so they stop sitting in the base
ladder, where two of them had been swallowed by omnibuses).
"""
import json
import os
import sqlite3

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
OUT = r"F:\Work\MovieTheater\docs\books\containment\tools\container-leak-retract.jsonl"

# itemId: (why-it-must-be-retracted, is-a-book)
R = {
    31744: ("a 312pp reprint of Bob Montana's Archie NEWSPAPER STRIP, whose coordinate is a date, not an issue; "
            "LOCG's #1-494 was nesting 353 Archie comic-book issues under it", True),
    77251: ("LOCG's #1-248 on a 200pp collection was nesting 86 files including Captain America (2024) #12-14, "
            "a run published fourteen years after the book", True),
    74836: ("LOCG's #1-103 on a 230pp collection was nesting 19 variant covers of Titans: Beast World #1", True),
    7973: ("Tom Strong and the Planet of Peril was nesting Tom Strong and the Robots of Doom #1-6, a different "
           "miniseries that shares its low coordinates", True),
    77381: ("Red Menace collects Captain America v5 (2006); its curated #15-21 was nesting Captain America (2024) "
            "#15-16, a different run at the same coordinates", True),
    67542: ("JLA: Year One is a 1998 twelve-issue maxiseries; LOCG's #18-46 was nesting JLA v3 #28-35 instead", True),
    23785: ("The Corpse-Makers was nesting Will Eisner's The Spirit (2015) #1-12, a different series at the same "
            "coordinates", True),
    31822: ("Crossing Over's curated #1-8 was nesting the Ghostbusters Holiday Special, Deviations and Answer the "
            "Call - three different comics, each numbered #1", True),
    93027: ("LOCG says #5-95 and ComicVine says #5-14 on the same 287pp book; unread, and the wider claim shows "
            "neither was read off it", True),
    93972: ("Byrne's THE RETURN collects the back half of Sensational She-Hulk; its claims were nesting #1-8, the "
            "opening the book does not contain", True),
    81893: ("a curated #26-32 on a 216pp book flagged as a single issue; unread, and the shelf's own run is not "
            "established to be the one Quesada drew", True),
    110554: ("Titan's SOLO album 01 was nesting Solo (2016) #1-5, a different comic sharing the coordinates", True),
    104137: ("Tales from Vader's Castle was nesting Shadow of Vader's Castle and Ghosts of Vader's Castle - the "
             "later minis, not its own chapters", True),
    79604: ("Straczynski's Fantastic Four run is #527-543; LOCG's #1-5 was nesting Fantastic Four (1961) #1-5, the "
            "Lee and Kirby originals", True),
    68983: ("a 69pp single-issue FACSIMILE of Police Comics #1; ComicVine's #103-107 was nesting five issues of a "
            "run the reprint has nothing to do with", False),
    21728: ("the Sky Lights Collection's curated #1-5 is plausible for The Green Hornet (2020) but was never read, "
            "and the book itself was sitting in the base ladder inside a Green Hornet omnibus", True),
    14122: ("Knight Errant was nesting Dragon Age: Blue Wraith, Dark Fortress and Deception - three separate minis, "
            "none of them its own chapters", True),
    14514: ("an ART BOOK, THE ART OF MASTERS OF THE UNIVERSE: REVOLUTION, was nesting the four issues of the comic "
            "it illustrates", True),
    14278: ("Brahman is a 114pp original graphic novel, not a collection; LOCG's #1-5 was nesting four unrelated "
            "Assassin's Creed trades", True),
    13437: ("Gunsmith Cats Revised Edition 02 was nesting Revised Editions 01, 02 and 03 - its own siblings and "
            "itself", True),
    13853: ("Empowered and the Soldier of Love IS one of the Empowered Specials; its curated #1-3 was nesting three "
            "of its own siblings", True),
    13852: ("Empowered and Sistah Spooky's High School Hell IS one of the Empowered Specials; its curated #1-6 was "
            "nesting three of its own siblings", True),
    105936: ("The Star Wars (Marvel Edition) was nesting the C-3PO special and the A New Hope Special Edition, "
             "neither of which is a chapter of it", True),
    29830: ("Crusades 01 was nesting Crusades 02 and 03, its own sibling albums", True),
    80213: ("Ghost Rider - The Complete Series by Rob Williams was nesting Spirits of Ghost Rider: Mother of Demons "
            "#1, a different comic", True),
}
KEEP = {74540: "The Flash by Grant Morrison and Mark Millar; its curated #130-141 IS the Morrison/Millar run and "
                "the file it holds is Flash v2 #135, inside that range - kept, and the flag corrected"}

con = sqlite3.connect(HOT)
series = {r[0]: r[1] for r in con.execute("SELECT Id, SeriesId FROM Item")}

with open(OUT, "w", encoding="utf-8") as out:
    for iid, (why, _book) in R.items():
        out.write(json.dumps({"itemId": iid, "seriesId": series[iid], "unknown": True,
                              "batch": "container-leak", "why": why}, ensure_ascii=False) + "\n")
print("wrote", len(R), "retractions to", OUT)

books = [i for i, (_w, b) in R.items() if b] + list(KEEP)
cur = con.execute("SELECT ItemId FROM ComicDetail WHERE IsCollection = 0 AND ItemId IN (%s)"
                  % ",".join(map(str, books)))
todo = [r[0] for r in cur]
con.executemany("UPDATE ComicDetail SET IsCollection = 1, Format = 1, FormatRaw = 'read-by-hand' WHERE ItemId = ?",
                [(i,) for i in todo])
con.commit()
print("corrected", len(todo), "book(s) from single issue to collection")
