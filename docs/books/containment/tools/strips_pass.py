"""Write the decision files for the newspaper-strip and webcomic shelves under `Webcomics and Strips`.

PLAN.md §14.10: a newspaper strip has no issue ladder at all. Its collected editions are keyed by the
YEAR they reprint (`1980-1981`, `1943-1944`), or by a book title and nothing else, and a strip is not an
issue. So every shelf here is a REFUSAL — an explicit one, per book, with the reason read off the shelf.

The prose and the reasons below are mine; this file exists so the ITEM IDS are transcribed by the
catalog and every edition is covered, which is what pass2.py checks. It writes nothing anywhere near
the library and never overwrites an existing decision file.
"""
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")

YEAR = ("a newspaper-strip reprint keyed by the YEARS it reprints and by nothing else; the strip is the "
        "unit and it carries no issue, volume or chapter number, so there is no coordinate a range could "
        "be written in (PLAN.md §14.10)")
BOOK = ("a collection of newspaper strips published as a titled book; the strips inside carry no issue "
        "number and the shelf holds no numbered base units, so there is no coordinate for a range")
WEB = ("a webcomic collection: the pages inside are dated web updates, not numbered issues, and the "
       "shelf holds no numbered base units, so there is no coordinate for a range")

SHELVES = [
    # ---- newspaper strips reprinted by year -------------------------------------------------------
    (66443, YEAR,
     "# Bloom County - The Complete Digital Library (S66443) - nine IDW volumes, each named for the\n"
     "# YEARS of strips it reprints: Vol. 01 1980-1981, then one volume per year 1982..1989, 233-378pp\n"
     "# each. §14.10 exactly: the year is the identifier. Berkeley Breathed's strip was never issued in\n"
     "# numbered instalments, nothing on this shelf is a numbered base unit, and a range in any\n"
     "# coordinate would be invented.",
     "every volume is titled by the calendar years of strips it reprints (1980-1981, 1982, ... 1989); the "
     "year is the coordinate and there is no issue ladder on this shelf", []),

    (6856, YEAR,
     "# For Better or For Worse: The Complete Library (S6856) - eight IDW volumes, each named for the\n"
     "# span of years it reprints (1979-1982, 1983-1986, ... 2003-2006), 512-567pp each. Lynn Johnston's\n"
     "# strip has no issue numbering; the year span IS the identifier (§14.10).",
     "the volume is titled by the span of years of strips it reprints (1979-1982 ... 2003-2006); the year is "
     "the coordinate and there is no issue ladder on this shelf", []),

    (66495, YEAR,
     "# The Phantom - The Complete Newspaper Dailies (S66495) - and the same book line is ALSO filed on\n"
     "# six one-book shelves, S19349 through S19354, each named after the years its volume covers. Both\n"
     "# halves are refused for the same reason and the split is noted.\n"
     "#\n"
     "# Every volume is `The Phantom - The Complete Newspaper Dailies vNN - <year> - <year>`. Lee Falk's\n"
     "# daily strip has no issue numbering at all; the years are the identifier (§14.10).",
     "the volume reprints the daily strip for the years named in its title; a daily strip carries no issue "
     "number, so there is no coordinate a range could be written in", []),

    (19349, YEAR,
     "# The Phantom - The Complete Newspaper Dailies v03, 1939-1940 (S19349). One book, on its own shelf;\n"
     "# the rest of the same IDW line sits on S66495 and S19350-S19354.\n"
     "#\n"
     "# DEFECT: this item's stored IssueNo is `1939` - the YEAR from the title stored as an issue number,\n"
     "# the fault §14.10 warns about. It cannot sort, compare or attach against anything.",
     "a newspaper-strip reprint keyed by the years 1939-1940; the daily strip carries no issue number and "
     "there is no coordinate a range could be written in", [(114540, "issue-numbers-wrong",
      "stored IssueNo is 1939, which is the YEAR from the volume title, not an issue number (§14.10)")]),

    (19350, YEAR,
     "# The Phantom - The Complete Newspaper Dailies v05, 1943-1944 (S19350). One book on its own shelf.\n"
     "# DEFECT: stored IssueNo is `1943` - the year read as an issue number.",
     "a newspaper-strip reprint keyed by the years 1943-1944; the daily strip carries no issue number and "
     "there is no coordinate a range could be written in", [(114541, "issue-numbers-wrong",
      "stored IssueNo is 1943, which is the YEAR from the volume title, not an issue number (§14.10)")]),

    (19351, YEAR,
     "# The Phantom - The Complete Newspaper Dailies v06, 1944-1946 (S19351). One book on its own shelf.\n"
     "# DEFECT: stored IssueNo is `1944` - the year read as an issue number.",
     "a newspaper-strip reprint keyed by the years 1944-1946; the daily strip carries no issue number and "
     "there is no coordinate a range could be written in", [(114542, "issue-numbers-wrong",
      "stored IssueNo is 1944, which is the YEAR from the volume title, not an issue number (§14.10)")]),

    (19352, YEAR,
     "# The Phantom - The Complete Newspaper Dailies v07, 1946-1947 (S19352). One book on its own shelf.\n"
     "# DEFECT: stored IssueNo is `1946` - the year read as an issue number.",
     "a newspaper-strip reprint keyed by the years 1946-1947; the daily strip carries no issue number and "
     "there is no coordinate a range could be written in", [(114543, "issue-numbers-wrong",
      "stored IssueNo is 1946, which is the YEAR from the volume title, not an issue number (§14.10)")]),

    (19353, YEAR,
     "# The Phantom - The Complete Newspaper Dailies v08, 1947-1949 (S19353). One book on its own shelf.\n"
     "# DEFECT: stored IssueNo is `1947` - the year read as an issue number.",
     "a newspaper-strip reprint keyed by the years 1947-1949; the daily strip carries no issue number and "
     "there is no coordinate a range could be written in", [(114544, "issue-numbers-wrong",
      "stored IssueNo is 1947, which is the YEAR from the volume title, not an issue number (§14.10)")]),

    (19354, YEAR,
     "# The Phantom - The Complete Newspaper Dailies v09, 1949-1950 (S19354). One book on its own shelf.\n"
     "# DEFECT: stored IssueNo is `1949` - the year read as an issue number.",
     "a newspaper-strip reprint keyed by the years 1949-1950; the daily strip carries no issue number and "
     "there is no coordinate a range could be written in", [(114545, "issue-numbers-wrong",
      "stored IssueNo is 1949, which is the YEAR from the volume title, not an issue number (§14.10)")]),

    (66435, YEAR,
     "# Al Capp's Li'l Abner - The Complete Dailies & Color Sundays (S66435) - two IDW volumes, v07\n"
     "# (1947-1948) and v08 (1949-1950). Keyed by year (§14.10).\n"
     "#\n"
     "# DEFECT: both items carry PageCount 0 while their files are 633 MB and 707 MB, so the page count\n"
     "# never got read for this shelf. Page count is the first thing §8 asks of a file, and it is missing.",
     "a daily-and-Sunday newspaper-strip reprint keyed by the years in its title (1947-1948, 1949-1950); the "
     "strip carries no issue number and there is no coordinate a range could be written in", []),

    (66452, YEAR,
     "# Garfield - Complete Works (S66452) - v01 (1978 & 1979) and v02 (1980 & 1981). §14.10 names\n"
     "# `Garfield 1982` as the example: the year is the identifier.",
     "the volume is titled by the calendar years of strips it reprints (1978 & 1979, 1980 & 1981); the year "
     "is the coordinate and the strip carries no issue number", []),

    (68135, YEAR,
     "# The Complete Funky Winkerbean (S68135) - one volume, v01 - 1972-1974. Keyed by year (§14.10).",
     "a newspaper-strip reprint titled by the years it covers (1972-1974); the strip carries no issue number "
     "and there is no coordinate a range could be written in", []),

    (66669, YEAR,
     "# Berkeley Breathed's Outland - The Complete Library - Sunday Comics, 1989-1995 (S66669). One book,\n"
     "# titled by the years of Sunday strips it reprints.",
     "a Sunday-strip reprint titled by the years it covers (1989-1995); the strip carries no issue number and "
     "there is no coordinate a range could be written in", []),

    (66670, YEAR,
     "# Berkley Breathed's Opus - The Complete Library - Sunday Comics, 2003-2008 (S66670). One book,\n"
     "# titled by the years of Sunday strips it reprints.",
     "a Sunday-strip reprint titled by the years it covers (2003-2008); the strip carries no issue number and "
     "there is no coordinate a range could be written in", []),

    (68131, BOOK,
     "# The Complete Cul de Sac (S68131) - the two-volume Andrews McMeel set of Richard Thompson's strip.\n"
     "# Titled v01/v02 with no year and no issue numbering: a strip collection with no coordinate at all.",
     "a newspaper-strip collection published as a two-book set; the strips inside carry no issue number and "
     "the volume number is a book ordinal, not a coordinate the strips are numbered in", []),

    (68132, BOOK,
     "# The Complete Dream of the Rarebit Fiend (S68132) - two volumes of Winsor McCay's 1904-1913\n"
     "# newspaper strip. The strips are dated, never numbered.",
     "a newspaper-strip collection; McCay's strips are dated, not numbered, so there is no issue, volume or "
     "chapter coordinate a range could be written in", []),

    (12704, BOOK,
     "# Nancy by Olivia Jaimes (S12704) - Vol. 01 of the modern Nancy strip. §14.10 names `Nancy` among\n"
     "# the year-keyed strips; this volume is titled by nothing but its ordinal.",
     "a newspaper-strip collection; the daily Nancy strips inside carry no issue number, and Vol. 01 is a book "
     "ordinal rather than a coordinate", []),

    (18161, BOOK,
     "# The Boondocks v01 - Because I Know You Don't Read the Newspaper (S18161). Aaron McGruder's daily\n"
     "# strip collected as a titled book.",
     "a newspaper-strip collection published as a titled book; the strips carry no issue number and there is "
     "no coordinate a range could be written in", []),

    (21692, YEAR,
     "# Will Eisner's The Spirit Archives (S21692) - eight DC volumes, and each one states its contents in\n"
     "# its own filename as a DATE RANGE: v01 (194006-194012), v02 (194101-194106), ... v08 (194401-194406).\n"
     "# The Spirit ran as a weekly newspaper insert; its sections are dated, not numbered, and the six\n"
     "# months a volume covers is the identifier. A fourth-coordinate case (§14.10) with the coordinate\n"
     "# spelled out in the file name.",
     "the archive volume reprints the weekly Spirit newspaper sections for the six months its filename names "
     "(e.g. 194006-194012); the sections are DATED, not numbered, so the coordinate is a date and no issue "
     "range can be written", []),

    # ---- webcomic collections ----------------------------------------------------------------------
    (7579, WEB,
     "# Girl Genius (S7579) - Volumes 01-13 of the Foglios' webcomic, 81-174pp each. Girl Genius ran as\n"
     "# printed issues only for its first few years and this rip is the WEBCOMIC ('(Webcomic) (Helga\n"
     "# Phugly)' in every filename): the pages inside are dated web updates. No numbered base unit is on\n"
     "# this shelf, and the volume ordinal is not a coordinate the pages are numbered in.",
     "a webcomic volume: the pages inside are dated web updates, not numbered issues, and the shelf holds no "
     "numbered base units, so there is no coordinate for a range", []),

    (7580, WEB,
     "# Girl Genius Extras (S7580) - ten `Volume NN Extras` files, 2pp to 54pp, sitting in an `Extras`\n"
     "# subfolder of the same webcomic rip. Two of them are 2pp and 5pp. §14.6: a file that short is a\n"
     "# cover or a pin-up, not a collection; while it is flagged IsCollection any provider willing to\n"
     "# guess a range gets to nest the shelf under it.",
     "an `Extras` file from a webcomic rip - between 2 and 54 pages of bonus art with no chapters, no issues "
     "and no numbering of any kind; it collects nothing", [
         (114099, "label-ambiguous", "2pp, flagged IsCollection=1: a 2-page extras file is a pin-up, not a collection (§14.6)"),
         (114101, "label-ambiguous", "5pp, flagged IsCollection=1: too short to be a collection (§14.6)"),
         (114106, "label-ambiguous", "8pp, flagged IsCollection=1: too short to be a collection (§14.6)"),
         (114107, "label-ambiguous", "6pp, flagged IsCollection=1: too short to be a collection (§14.6)"),
     ]),

    (18417, WEB,
     "# The Devil's Panties (S18417) - Vols 01-07 of Jennie Breeden's daily webcomic, 229-350pp each.\n"
     "# Dated daily strips; no issue, volume or chapter numbering inside.",
     "a daily webcomic collected by volume; the strips inside are dated, never numbered, so there is no "
     "coordinate a range could be written in", []),

    (997, WEB,
     "# Anders Nilsen's The Monologuist Web Archive (S997) - three volumes, each titled by the years it\n"
     "# archives (2008-2009, 2010-2011, 2012-2013). A year-keyed web archive, §14.10.",
     "a web archive titled by the years of posts it collects (2008-2009, 2010-2011, 2012-2013); the year is "
     "the coordinate and there is no issue ladder", []),

    (6134, WEB,
     "# Emitown (S6134) - two volumes of Emi Lenox's dated sketchbook-diary webcomic, 407-408pp each.",
     "a dated diary webcomic collected by volume; its pages are days, not numbered issues, so there is no "
     "coordinate a range could be written in", []),

    (1131, WEB,
     "# Antlers - The Holistic Comics Journal of Joseph Morris v01 (S1131). A 47pp self-published comics\n"
     "# journal; nothing on the shelf is numbered.",
     "a self-published comics journal, 47pp, with no chapters and nothing numbered inside; it is a book, not "
     "a collection of numbered units", []),

    (13405, WEB,
     "# Order of the Stick - Dungeon Crawlin' Fools (S13405). The Rich Burlew webcomic collected as a PDF\n"
     "# book. Order of the Stick numbers its STRIPS on the web, but the book is titled and the shelf holds\n"
     "# no numbered base unit to range over.",
     "a webcomic collected as a titled PDF book; the shelf holds no numbered base units and the strips inside "
     "are not issues, so there is no coordinate for a range", []),
    (13406, WEB,
     "# Order of the Stick - No Cure for the Paladin Blues (S13406). One titled PDF book of the webcomic.",
     "a webcomic collected as a titled PDF book; the shelf holds no numbered base units and the strips inside "
     "are not issues, so there is no coordinate for a range", []),
    (13407, WEB,
     "# Order of the Stick - Utterly Dwarfed (S13407). One titled PDF book of the webcomic.",
     "a webcomic collected as a titled PDF book; the shelf holds no numbered base units and the strips inside "
     "are not issues, so there is no coordinate for a range", []),
    (13408, WEB,
     "# Order of the Stick - War and XPs (S13408). One titled PDF book of the webcomic.",
     "a webcomic collected as a titled PDF book; the shelf holds no numbered base units and the strips inside "
     "are not issues, so there is no coordinate for a range", []),
]

# The Peanuts Facsimile Edition: ONE ten-volume Fantagraphics line, filed as TEN one-book shelves, each
# named after that book's subtitle. Every one is a reprint of a 1950s Peanuts paperback - daily strips,
# no issue numbering anywhere.
PEANUTS = {
    13656: ("v01 - Peanuts", 114733), 13655: ("v02 - More Peanuts", 114734),
    13645: ("v03 - Good Grief, More Peanuts", 114735), 13646: ("v04 - Good Ol' Charlie Brown", 114736),
    13661: ("v05 - Snoopy", 114737), 13669: ("v06 - You're Out of Your Mind, Charlie Brown!", 114738),
    13643: ("v07 - But We Love You, Charlie Brown", 114739), 13658: ("v08 - Peanuts Revisited", 114740),
    13644: ("v09 - Go Fly a Kite, Charlie Brown", 114741), 13657: ("v10 - Peanuts Every Sunday", 114742),
}
for _sid, (_vol, _iid) in PEANUTS.items():
    SHELVES.append((_sid, BOOK,
        f"# Peanuts Facsimile Edition {_vol} (S{_sid}) - one book of the ten-volume Fantagraphics facsimile\n"
        f"# line, which reproduces the 1950s Peanuts paperbacks. The whole line is filed as TEN separate\n"
        f"# one-book shelves (S13643, S13644, S13645, S13646, S13655, S13656, S13657, S13658, S13661,\n"
        f"# S13669), each named after its book's subtitle rather than after the line.\n"
        f"#\n"
        f"# Peanuts is a daily newspaper strip: the strips are dated and never numbered, so no volume here\n"
        f"# has an issue, volume or chapter coordinate to be ranged in (§14.10).",
        "a facsimile reprint of a 1950s Peanuts paperback; the daily strips inside are dated, never numbered, "
        "so there is no coordinate a range could be written in", []))

# Calvin and Hobbes: the same title on TWELVE shelves - the eleven-book Complete Collection remix plus
# the Deluxe/treasury editions, each treasury on its own shelf.
CH = {
    3353: ("v04 - Attack of the Deranged Mutant Killer Monster Snow Goons", 114645),
    3354: ("v06 - Homicidal Psycho Jungle Cat", 114647),
    3355: ("v07 - It's A Magical World", 114648),
    3356: ("Complete Collection - M - Miscellaneous", 114639),
    3357: ("Complete Collection - S - The Sunday Pages", 114640),
    3358: ("v02 - The Authoritative Calvin and Hobbes", 114643),
    3359: ("v05 - The Days Are Just Packed", 114646),
    3360: ("v03 - The Indispensable Calvin and Hobbes", 114644),
    3361: ("v08 - There's Treasure Everywhere", 114649),
    3362: ("Complete Collection - X - 10th Anniversary", 114641),
}
for _sid, (_vol, _iid) in CH.items():
    SHELVES.append((_sid, BOOK,
        f"# Calvin and Hobbes - {_vol} (S{_sid}). One book on its own shelf; the same title is spread over\n"
        f"# twelve Series ids, with the eleven-book `Complete Collection` remix on S3352 and each treasury\n"
        f"# or extra volume filed alone.\n"
        f"#\n"
        f"# Calvin and Hobbes is a daily newspaper strip. The strips are dated, never numbered, and the\n"
        f"# treasuries REPRINT strips that also appear in the numbered paperbacks - so even the containment\n"
        f"# that does exist here is book-to-book, not book-to-issue. There is no issue coordinate.",
        "a Calvin and Hobbes book: the daily strips inside are dated, never numbered, so there is no issue, "
        "volume or chapter coordinate a range could be written in", []))

SHELVES.append((3352, BOOK,
    "# Calvin and Hobbes (S3352) - the eleven-book `Complete Collection` remix (01 Calvin and Hobbes\n"
    "# through 11 It's a Magical World, 163-176pp each) plus `The Essential Calvin and Hobbes`, a 335pp\n"
    "# treasury. Ten more books of the same title sit on their own shelves, S3353-S3362.\n"
    "#\n"
    "# The strips are dated newspaper dailies and Sundays with no numbering of any kind. The treasury\n"
    "# genuinely CONTAINS the paperbacks' strips - but in a coordinate that does not exist here, so a\n"
    "# range would have to be invented. Refused, and the containment is noted instead.\n"
    "#\n"
    "# `The Essential Calvin and Hobbes` carries a stored IssueNo of 1, which is not an issue number.",
    "a Calvin and Hobbes book: the daily strips inside are dated, never numbered, so there is no issue, "
    "volume or chapter coordinate a range could be written in",
    [(114642, "label-ambiguous",
      "The Essential Calvin and Hobbes is a 335pp treasury that reprints the strips of the first two "
      "paperbacks, but its stored IssueNo is 1 and the strips it reprints carry no number, so the "
      "containment it really has cannot be expressed as a range")]))


def write(sid, why, head, note, flags):
    dst = os.path.join(DEC, f"S{sid}.txt")
    if os.path.exists(dst):
        print(f"S{sid}: a decision file already exists - left alone")
        return 0
    rows = con.execute("""
        SELECT i.Id FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
        ORDER BY i.FileName""", (sid,)).fetchall()
    if not rows:
        print(f"S{sid}: no collected editions - skipped")
        return 0
    with open(dst, "w", encoding="utf-8") as out:
        out.write(head.rstrip() + "\n\n")
        out.write(f"N {sid} {note}\n\n")
        for iid, kind, detail in flags:
            out.write(f"F {iid} {kind} | {detail}\n")
        if flags:
            out.write("\n")
        for (iid,) in rows:
            out.write(f"u {iid} {why}\n")
    print(f"S{sid}: {len(rows)} refusals, {len(flags)} flags")
    return len(rows)


con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
total = 0
for sid, why, head, note, flags in SHELVES:
    total += write(sid, why, head, note, flags)
print(f"\n{{ shelves: {len(SHELVES)}, refusals: {total} }}")
