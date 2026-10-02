"""Comic filename normalizer shared by the FTP-vs-library diff.

Produces, for any comic file basename:
  stem      lowercased, tag-stripped "series + issue" text
  series    normalized series key
  issue     normalized issue number ('' when none)
  kind      'issue' | 'vol' | 'oneshot'
  year      int or None
  tags      the parenthetical tag groups (scan group, digital, covers, ...)
  exact     fully-folded whole basename (incl. ext) for identity matching
"""
import re, unicodedata

ARCHIVE_EXT = re.compile(r"\.(cbz|cbr|cb7|cbt|pdf|epub|mobi|azw3|zip|rar|7z|djvu)$", re.I)

# tag words that mark a parenthetical as scene metadata rather than part of the title
SCENE_WORDS = re.compile(
    r"\b(digital|dcp|empire|minutemen|scan|c2c|covers?|cover|webrip|rip|repack|re-?scan|rescan|"
    r"fixed|noads|no ads|paper|zone|nerd|team|hd|f\d|dc[ p]|son of ultron|the last kryptonian|"
    r"theproletariat|thecultofdanverse|shan|salem|pyrate|leduch|kileko|mephisto|bchry|glorith|"
    r"joe-?empire|darkness-?empire|zzzzz|phillywilly|natsume|danke|anherogold|1r0nm4n|megan-?empire|"
    r"yoink|okc|guyver|movielover|inkybrown|thegroup|gmc|buchanan|deluxe scan|hourman|batman-?dcp|"
    r"knight ripper|the seeker|random stranger|superscan|tomjoad|steam-?dcp|remix|marika|greengiant|"
    r"ashkelon|magicians|unknown|incomplete|missing|partial|nsfw|adults? only|f\s?\d{1,2})\b", re.I)

FORMAT_WORDS = re.compile(
    r"\b(tpb|hc|sc|gn|ogn|omnibus|collection|collected|complete|limited series|mini[- ]?series|"
    r"series|one[- ]?shot|annual|special|treasury|epic collection|masterworks|"
    r"deluxe edition|absolute edition|compendium|digest|trade)\b", re.I)

COLLECTION_HINT = re.compile(
    r"\b(tpb|omnibus|collection|collected|compendium|epic collection|masterworks|"
    r"deluxe edition|absolute edition|complete collection|the complete|digest|"
    r"vol(?:ume)?s?\s*\.?\s*\d+|v\d{2,3}|book \d+)\b", re.I)

ROMAN = {"i":1,"ii":2,"iii":3,"iv":4,"v":5,"vi":6,"vii":7,"viii":8,"ix":9,"x":10}


def _fold(s):
    s = unicodedata.normalize("NFKD", s)
    s = "".join(ch for ch in s if not unicodedata.combining(ch))
    s = s.replace("’", "'").replace("‘", "'")
    # an apostrophe joins, it does not split: "Assassin's" = "Assassins", "Kirby's" = "Kirbys"
    s = s.replace("'", "")
    s = s.replace("“", '"').replace("”", '"')
    s = s.replace("–", "-").replace("—", "-").replace("−", "-")
    s = s.replace("&", " and ")
    s = s.lower()
    s = re.sub(r"[^a-z0-9]+", " ", s)
    return re.sub(r"\s+", " ", s).strip()


# Series folders tag the year as "(1995)" but just as often as a RANGE:
# "No Hero (2008-2009)", "The Punisher (1986-2019)", "v1 (1992-95)",
# "Starlord UK (01 - 22 +Extras) (1978 - 1982)". The opening year is the one we want.
FOLDER_YEAR = re.compile(
    r"\((1[89]\d{2}|20[0-4]\d)(?:\s*[-–]\s*(?:\d{2,4})?)?\s*\)")


def folder_year(d):
    """Year from the nearest year-tagged ancestor folder, reading right to left."""
    for seg in reversed(d.split("/")):
        m = FOLDER_YEAR.findall(seg)
        if m:
            return int(m[-1])
    return None


def _from_underscores(stem):
    """Scene names that lost every space and paren to underscores.

    'Gravel_01__2008___Minutemen-Zone_'  ->  'Gravel 01 (2008) (Minutemen-Zone)'
    A doubled underscore is a group boundary, so splitting on runs of 2+ rebuilds
    the parentheses exactly. Where the name uses single underscores throughout
    ('Crisis_001_Fleetway_1988-09-17_Slinky_J_') there is no boundary to find, so
    the issue token is located instead and everything after it becomes tags.
    """
    # some of these were URL-encoded before the spaces were lost: _28 = "(", _29 = ")".
    # Decoding restores the real parens, so the ordinary path can take it from here.
    if "_28" in stem and "_29" in stem:
        stem = stem.replace("_28", "(").replace("_29", ")")
        return re.sub(r"\s+", " ", stem.replace("_", " ")).strip()

    parts = [p for p in re.split(r"_{2,}", stem.strip("_")) if p]
    if len(parts) > 1:
        head = parts[0].replace("_", " ").strip()
        groups = [p.replace("_", " ").strip() for p in parts[1:]]
        return head + "".join(" (%s)" % g for g in groups if g)

    toks = [t for t in stem.strip("_").split("_") if t]
    if len(toks) < 2:
        return stem.replace("_", " ")
    # zero-padded numbers are issue numbers; a bare 4-digit number is usually a year
    cut = None
    for i, t in enumerate(toks[1:], 1):
        if re.fullmatch(r"0\d{1,4}", t):
            cut = i
            break
    if cut is None:
        for i, t in enumerate(toks[1:], 1):
            if t.isdigit() and not re.fullmatch(r"1[89]\d{2}|20[0-4]\d", t):
                cut = i
    if cut is None:
        return stem.replace("_", " ")
    head = " ".join(toks[:cut + 1])
    rest = " ".join(toks[cut + 1:])
    return head + (" (%s)" % rest if rest else "")


def _strip_ext(name):
    m = ARCHIVE_EXT.search(name)
    return (name[:m.start()], m.group(1).lower()) if m else (name, "")


def _split_groups(stem):
    """Split trailing (...) / [...] groups off the stem. Returns (head, groups)."""
    groups = []
    s = stem.strip()
    while True:
        m = re.search(r"[\(\[]\s*([^()\[\]]*)\s*[\)\]]\s*$", s)
        if not m:
            break
        groups.append(m.group(1))
        s = s[:m.start()].strip()
    groups.reverse()
    return s, groups


ISSUE_TAIL = re.compile(
    r"(?:^|\s|#)"
    r"(?P<num>"
    r"v\d{1,3}(?:\.\d+)?"                       # v01
    r"|\d{1,5}(?:\.\d+)?[a-z]{0,3}"             # 034, 12.1, 5AU
    r"|[ivx]{1,5}"                              # roman
    r")\s*$", re.I)

# a collected volume: "X Vol. 01", "X Vol. 01 - Subtitle", "X Book 2: Subtitle", "X v03".
# The number is a VOLUME, never an issue, and a trailing subtitle is not the series.
VOL_MARK = re.compile(
    r"\s(?:vol(?:ume)?\.?|book|tpb|v(?=\d))\s*0*(\d{1,3})(?=\s*(?:[-:,(]|$))", re.I)
OF_TAIL = re.compile(r"\s*\(?\s*of\s+\d{1,4}\s*\)?\s*$", re.I)
PAGE_COUNT = re.compile(r"\s\d{1,4}\s?(?:p|pg|pgs|pages)$", re.I)


def parse(basename):
    raw = basename
    stem, ext = _strip_ext(raw)

    # underscore-style scene names: Foo_-_Bar_004__2008___Steam-DCP_
    if "_" in stem and " " not in stem:
        stem = _from_underscores(stem)

    head, groups = _split_groups(stem)

    year = None
    for g in groups:
        m = re.search(r"\b(1[89]\d{2}|20[0-4]\d)\b", g)
        if m:
            year = int(m.group(1))
            break
    if year is None:
        m = re.search(r"\b(19[3-9]\d|20[0-4]\d)\b", head)
        # a leading year is the title ("2000 AD"), never a publication date
        if m and m.start() > 0:
            year = int(m.group(1))

    # Peel "(of 04)" and bare page counts off the tail. A page count sits OUTSIDE the
    # parens ("Gravel 012 (2009-Avatar) 25p (Minutemen-Mantooth)"), so removing it can
    # expose a further group that still needs peeling - repeat until nothing changes.
    while True:
        stripped = OF_TAIL.sub("", head).strip()
        stripped = PAGE_COUNT.sub("", stripped).strip()
        if stripped == head:
            break
        head, more = _split_groups(stripped)
        groups = more + groups

    # date-style names carry the issue only in a '(#165)' group:
    # "Superman, 2000-12-00 (#165) (digital) (Glorith-HD)" -> "Superman 165"
    hashg = [g for g in groups if re.fullmatch(r"\s*#\s*\d{1,4}(?:\.\d+)?[a-z]?\s*", g)]
    if hashg:
        head = re.sub(r",?\s*(?:1[89]|20)\d\d-\d\d(?:-\d\d)?\s*$", "", head).strip() + " " + hashg[0].strip().lstrip("#").strip()
        if year is None:
            m = re.search(r"\b(1[89]\d\d|20[0-4]\d)-\d\d", stem)
            year = int(m.group(1)) if m else None
    elif re.search(r",\s*(?:1[89]|20)\d\d-\d\d(?:-\d\d)?\s*$", head):
        # a date-only name has no issue number: never read '-00' as #0
        m = re.search(r"(1[89]\d\d|20[0-4]\d)", head[-12:])
        year = year or (int(m.group(1)) if m else None)
        head = re.sub(r",\s*(?:1[89]|20)\d\d-\d\d(?:-\d\d)?\s*$", "", head)

    issue, kind = "", "oneshot"
    mvol = VOL_MARK.search(head)
    if mvol and mvol.start() > 0:
        issue, kind = "v%d" % int(mvol.group(1)), "vol"
        head = head[:mvol.start()].strip(" -#")
        m = None
    else:
        m = ISSUE_TAIL.search(head)
    if m:
        num = m.group("num")
        cut = head[:m.start()].strip(" -#–")
        if re.match(r"^v\d", num, re.I):
            issue = "v%d" % int(re.sub(r"[^0-9]", "", num.split(".")[0]))
            kind = "vol"
        elif re.match(r"^[ivx]+$", num, re.I) and num.lower() in ROMAN and not num.lower() == "i":
            issue, kind = str(ROMAN[num.lower()]), "issue"
        elif re.match(r"^[ivx]+$", num, re.I):
            issue, kind = "", "oneshot"
            cut = head
        else:
            mm = re.match(r"^(\d+)(?:\.(\d+))?([a-z]{0,3})$", num, re.I)
            if mm:
                issue = str(int(mm.group(1)))
                if mm.group(2):
                    issue += "." + mm.group(2).lstrip("0").rjust(1, "0")
                if mm.group(3):
                    issue += mm.group(3).lower()
                kind = "issue"
        if issue and cut:
            head = cut

    # a volume marker sitting between series and issue: "Green Arrow v3 25"
    vol = None
    mv = re.search(r"[\s.\-]v(?:ol)?\.?\s*(\d{1,3})\s*$", head, re.I)
    if mv:
        vol = int(mv.group(1))
        head = head[:mv.start()].strip(" -.")

    series = _fold(head)
    series = re.sub(r"\s(1[89]\d{2}|20[0-4]\d)$", "", series).strip()
    # "Vol. 30" / "Volume 30" / "v30" all mean the same volume token
    series = re.sub(r"\bv(?:ol(?:ume)?)?\.?\s*(\d{1,3})\b", lambda m: "v" + str(int(m.group(1))), series)
    # a dotted reading-order prefix ("532. Ninjak") is never part of a title
    series = re.sub(r"^\d{1,4} (?=[a-z])", "", series) if re.match(r"^\d{1,4}[.)]\s", head) else series
    # fallback alias with a bare numeric ordering prefix removed ("008 Mutant X")
    alias = re.sub(r"^\d{1,3} (?=[a-z])", "", series)
    # numbers inside the title get zero-padded on one side only:
    # "Scott Pilgrim Vol. 02 (of 06)" vs "Vol. 2 (of 6)", "Part 1," vs "Part 01 -"
    alias = re.sub(r"\b0+(\d)", r"\1", alias)

    scene = [g for g in groups if SCENE_WORDS.search(g)]
    titley = [g for g in groups if not SCENE_WORDS.search(g)
              and not re.fullmatch(r"\s*(1[89]\d{2}|20[0-4]\d)\s*", g)]

    is_collection = bool(COLLECTION_HINT.search(stem)) or kind == "vol"

    return {
        "raw": raw,
        "ext": ext,
        "series": series,
        "issue": issue,
        "kind": kind,
        "year": year,
        "scene": scene,
        "extra": titley,
        "is_collection": is_collection,
        "vol": vol,
        "alias": alias,
        "k_alias": "%s|%s" % (alias, issue),
        # space-free series ("2000AD" vs "2000 AD", "Spider-Man" vs "SpiderMan"),
        # articles dropped ("Legacy of Vader" vs "The Legacy of Vader")
        "k_tight": "%s|%s" % (
            re.sub(r"\b(the|a|an)\b", "", alias).replace(" ", ""), issue),
        "exact": _fold(stem),
        # core identity, year-free
        "k_id": "%s|%s" % (series, issue),
        # strict identity
        "k_idy": "%s|%s|%s" % (series, issue, year or ""),
        # title-ish stem with non-scene groups folded back in (catches subtitle-only diffs)
        "k_stem": _fold(head + " " + " ".join(titley)),
    }


if __name__ == "__main__":
    import sys
    for line in sys.stdin:
        line = line.rstrip("\n")
        if not line:
            continue
        p = parse(line)
        print("%-70s | %-42s | %-6s | %-5s | %s" % (
            line[:70], p["series"][:42], p["issue"], p["year"] or "", "COLL" if p["is_collection"] else ""))
