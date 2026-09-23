"""The conventions ledger, TAGGED, and the per-batch slice of it (TOOLS_TODO 20).

`python ledger.py --batch X-062 [--out FILE]`   the `## Conventions for this batch` block for an emitted batch
`python ledger.py --sids 123 456`               the same block for any shelves
`python ledger.py --rewrite-block A-035`        replace ONLY the conventions block of an emitted batch file in place
                                                (the packet body below it is kept byte for byte)
`python ledger.py --stats`                      entries, tags, and any entry no shelf could reach
`python ledger.py --migrate OLD_BRIEF.md`       ONE-TIME: cut the ledger out of a brief into LEDGER.md (refuses
                                                to overwrite an existing LEDGER.md without --force)

Why this exists. READER_BRIEF.md reached 110 KB, 96 KB of it the ledger — 328 one-paragraph conventions that
every worker read before its first packet and then carried through ~100 tool calls. Nearly all of them are
about ONE publisher, ONE folder shape or ONE packet signal: a reader deciding forty Manga shelves was paying
for the Dynamite trade-link rate, the 2000 AD prog shape and the Heavy Metal special. So the ledger moves to
LEDGER.md with every entry tagged, and the batch file carries only the entries its own shelves call for.
Nothing is summarised and nothing is dropped: an entry's TEXT is the reader's evidence of how a provider lied
the last time, so it is kept verbatim and only tags are added (selftest proves the round trip).

How an entry is matched — per SHELF, then ranked (tightened 2026-09-22, after the first cut attached 169 of
328 entries to A-035 because one tier:A tag and 28 Marvel shelves pulled in everything tagged with either):
  SCOPE tags   pub:<publisher> · folder:<folder shape> · kind:<kind of comic>
  TOPIC tags   sig:<a packet signal>  — round2 links, an open overlap flag, a ComicInfo assertion, a missing leg …
  tier: / pass:  say where a lesson was learned; they NEVER qualify an entry on their own.
An entry matches a shelf on its most specific namespace: a publisher entry on the publisher (and, if it names a
folder shape, on that too); a folder entry on the folder; a kind entry on the kind; a topic-only entry on the
signal. Inside the BIG houses (DC, Marvel, Image, IDW, Dark Horse, BOOM, Dynamite) a publisher match is not
enough: an entry that names a kind needs the kind, and one naming neither folder nor kind needs one of its own
KEYWORDS (capitalised names and backticked folder tokens from its text — a sub-line, an imprint word like Epic /
Masterworks / Omnibus, an event, a title) to appear in the shelf's folder path or name. Entries are then ranked
by (shelves matched × the weight of what matched) and the block prints the best CAP_ENTRIES / CAP_BYTES,
listing the ids of every other matched entry at its foot, so nothing matched is silently unreachable.
A shelf's tags come from `ShelfTagger` — the folders its files sit in, the publisher columns every leg prints,
the flags, the per-file methods — with the SAME slugs the entries use; `--stats` and selftest refuse an entry
no shelf could ever match.

`ruling` marks the handful of entries that apply everywhere. They are copied verbatim into the brief's
Rulings and are NOT attached to batches (the reader already has them).
"""
import os
import re
import sys
from collections import Counter, defaultdict

import idbase

LEDGER = os.path.join(idbase.ROOT, "LEDGER.md")
BRIEF = os.path.join(idbase.ROOT, "READER_BRIEF.md")
RX_TAGLINE = re.compile(r"^\[L-(\d{3,4})\]\s*(.*)$")
SCOPE_NS = ("pub", "folder", "kind", "tier", "pass")

# ── one table of slugs, used BOTH ways: to tag an entry from its text (migration) and to tag a shelf from
# its folders / publisher columns (every batch). A slug that only one side can produce is an unreachable
# entry, which is exactly the failure the round-trip test exists to catch.
# (regex on text, tags). Publisher names first; the entry side runs these over the entry's prose.
PUBLISHERS = [
    (r"\bAC Comics\b|\bAC's\b|\bAC\b(?! Comics Presents)", "pub:ac"),
    (r"Aftershock|AfterShock", "pub:aftershock"),
    (r"Action Lab", "pub:action-lab"),
    (r"\bAblaze\b", "pub:ablaze"),
    (r"\bAhoy\b", "pub:ahoy"),
    (r"American Gothic", "pub:american-gothic"),
    (r"\bArchie\b", "pub:archie"),
    (r"\bAvatar\b", "pub:avatar"),
    (r"2000 ?AD|Fleetway|Rebellion|Judge Dredd|\bIPC\b|\bprogs?\b|Egmont UK", "pub:2000ad"),
    (r"\bBOOM\b|\bBoom\b|Boom Studios|BOOM! Studios|Archaia|Storyteller", "pub:boom"),
    (r"Berger Books", "pub:berger"),
    (r"Cinebook", "pub:cinebook"),
    (r"\bDC\b|DC Comics|Vertigo|Wildstorm|WildStorm|Batman|Superman|Wonder Woman|Nightwing|Green Lantern|Justice League"
     r"|Detective Comics|Action Comics|Harley Quinn|Injustice|Bombshells|New 52|Rebirth|Black Label|\bFlash\b"
     r"|Titans|Supergirl|Batgirl|Young Animal|DC Ink|DC Zoom|America's Best|Americas Best", "pub:dc"),
    (r"Marvel|Spider-Man|X-Men|Avengers|Epic Collection|Masterworks|Krakoa|Wolverine|Skottie|Secret Wars"
     r"|\bThor\b|\bHulk\b|Deadpool|Daredevil|Captain America|Fantastic Four|\bIcon\b|X-Force|Infinity Comics"
     r"|Infinite Comics|Star Wars", "pub:marvel"),
    (r"Star Wars", "pub:star-wars"),
    (r"Dark Horse|Hellboy|B\.P\.R\.D\.|Baltimore|Lobster Johnson|Abe Sapien|Kabuki|The Goon|Usagi|Jinxworld"
     r"|Millarworld|Berger Books|EC Comics & Others", "pub:dark-horse"),
    (r"Dynamite|Red Sonja|Green Hornet|Vampirella|Turok|Bettie Page", "pub:dynamite"),
    (r"\bIDW\b|Hasbro|\bTMNT\b|G\.I\. Joe|Transformers|Black Crown", "pub:idw"),
    (r"\bImage\b|Skybound|Top Cow|Cyberforce|Cyber Force", "pub:image"),
    (r"Europe Comics|Dargaud|Dupuis|Le Lombard", "pub:europe-comics"),
    (r"Delcourt|Soleil", "pub:delcourt"),
    (r"\bTitan\b|Doctor Who", "pub:titan"),
    (r"Valiant|Acclaim|X-O Manowar|Ninjak", "pub:valiant"),
    (r"\bOni\b|Oni Press", "pub:oni"),
    (r"\bScout\b|Black Caravan", "pub:scout"),
    (r"Zenescope|Grimm", "pub:zenescope"),
    (r"Papercutz|\bNBM\b", "pub:papercutz-nbm"),
    (r"Humanoids", "pub:humanoids"),
    (r"First ?Second", "pub:first-second"),
    (r"Lion Forge|Magnetic Press|Magnetic", "pub:lion-forge-magnetic"),
    (r"Drawn & Quarterly|\bD&Q\b", "pub:drawn-quarterly"),
    (r"Fantagraphics", "pub:fantagraphics"),
    (r"Top Shelf", "pub:top-shelf"),
    (r"\bTKO\b", "pub:tko"),
    (r"Legendary", "pub:legendary"),
    (r"Mad ?Cave|MadCave", "pub:mad-cave"),
    (r"ComiXology|Comixology", "pub:comixology"),
    (r"Panel Syndicate|MonkeyBrain|Monkeybrain", "pub:digital-indie"),
    (r"Heavy[ _]Metal", "pub:heavy-metal"),
    (r"Disney", "pub:disney"),
    (r"Gold Key|\bDell\b|Western|Whitman", "pub:gold-key-dell"),
    (r"Hanna-Barbera", "pub:hanna-barbera"),
    (r"Markosia|LeDuch|Digital Manga", "pub:markosia"),
    (r"Millarworld", "pub:millarworld"),
    (r"Jinxworld|Bendis", "pub:jinxworld"),
    (r"\bEC\b|Crime Does Not Pay|Frontline Combat|War Against Crime|Creepy|Eerie|Warren", "pub:ec-archives"),
    (r"\bMAD\b", "pub:mad"),
    (r"DSTLRY", "pub:dstlry"),
    (r"Bongo|Treehouse of Horror|Simpsons", "pub:bongo"),
    (r"Caliber|Arrow Comics|Deadworld", "pub:caliber"),
    (r"Bubble Comics|Exlibrium|Major Grom|Space Between|Strip For Me|Studio D\.|Imagine Bin|SAF Comics"
     r"|Lost His Keys|Silver Sprocket|Storm King|Space Goat|Avery Hill|Behemoth|Source Point|ComixTribe"
     r"|Alterna|\bVault\b|Black Mask|\bAWA\b|Substack|Kickstarter|Albatross|\bSLG\b|New England Comics"
     r"|Red5|Dead Sky|Paper Films|Abstract Studio|Eclipse|Defiant|Charlton|Hammer|Eagle Comics|Comico"
     r"|Legal Defense|CBLDF|American Mythology|PanelxPanel|Harvey", "pub:small-press"),
    (r"Scholastic|Abrams|HarperCollins|Andrews McMeel|Ballantine|Berkley|Grosset|Charter|Jove|Tempo"
     r"|Graphix|Golden Books", "pub:trade-house"),
]
# The SHELF side names a publisher only by COMPANY name — the top folder of the path and the publisher column
# of a leg or of ComicInfo. The entry side above also recognises titles ("Batman", "Hellboy"), because the
# ledger's prose names the comic more often than the house; run over a folder path those title words tagged an
# Oni batch `pub:dc` for a folder called "Flash Gordon". Every slug here is also one the entry table produces.
SHELF_PUBLISHERS = [
    (r"\bAC Comics\b", "pub:ac"), (r"Aftershock|AfterShock", "pub:aftershock"), (r"Action Lab", "pub:action-lab"),
    (r"\bAblaze\b", "pub:ablaze"), (r"\bAhoy\b", "pub:ahoy"), (r"American Gothic", "pub:american-gothic"),
    (r"\bArchie\b", "pub:archie"), (r"\bAvatar\b", "pub:avatar"),
    (r"2000 ?AD|Fleetway|Rebellion|\bIPC\b", "pub:2000ad"),
    (r"\bBOOM\b|\bBoom\b|Archaia|KaBOOM", "pub:boom"), (r"Berger Books", "pub:berger"),
    (r"Cinebook", "pub:cinebook"),
    (r"^DC\b|\bDC Comics\b|^Vertigo|^Wildstorm|WildStorm|^DC |Americas Best|America's Best|Young Animal|DC Ink|DC Zoom",
     "pub:dc"),
    (r"^Marvel|Marvel Comics|\bMarvel\b|^Icon\b|^Epic$", "pub:marvel"), (r"Star Wars", "pub:star-wars"),
    (r"Dark Horse", "pub:dark-horse"), (r"Dynamite", "pub:dynamite"), (r"\bIDW\b", "pub:idw"),
    (r"^Image\b|\bImage Comics\b|Skybound|Top Cow", "pub:image"),
    (r"Europe Comics|Dargaud|Dupuis|Le Lombard", "pub:europe-comics"), (r"Delcourt|Soleil", "pub:delcourt"),
    (r"^Titan|Titan Comics|Titan Books", "pub:titan"), (r"Valiant|Acclaim", "pub:valiant"),
    (r"^Oni\b|Oni Press", "pub:oni"), (r"^Scout|Scout Comics|Black Caravan", "pub:scout"),
    (r"Zenescope", "pub:zenescope"), (r"Papercutz|\bNBM\b", "pub:papercutz-nbm"), (r"Humanoids", "pub:humanoids"),
    (r"First ?Second", "pub:first-second"), (r"Lion Forge|Magnetic", "pub:lion-forge-magnetic"),
    (r"Drawn & Quarterly|Drawn and Quarterly", "pub:drawn-quarterly"), (r"Fantagraphics", "pub:fantagraphics"),
    (r"Top Shelf", "pub:top-shelf"), (r"\bTKO\b", "pub:tko"), (r"Legendary", "pub:legendary"),
    (r"Mad ?Cave|MadCave", "pub:mad-cave"), (r"ComiXology|Comixology", "pub:comixology"),
    (r"Panel Syndicate|MonkeyBrain|Monkeybrain", "pub:digital-indie"), (r"Heavy Metal", "pub:heavy-metal"),
    (r"Disney", "pub:disney"), (r"Gold Key|\bDell\b|Whitman|Western Publishing", "pub:gold-key-dell"),
    (r"Hanna-Barbera", "pub:hanna-barbera"), (r"Markosia", "pub:markosia"), (r"Millarworld", "pub:millarworld"),
    (r"Jinxworld", "pub:jinxworld"), (r"EC Comics|^EC\b|E\. ?C\. Publications", "pub:ec-archives"),
    (r"^MAD\b|E\.C\. Publications", "pub:mad"), (r"DSTLRY", "pub:dstlry"), (r"Bongo", "pub:bongo"),
    (r"Caliber|Arrow Comics", "pub:caliber"),
    (r"Bubble|Space Between|Strip For Me|Studio D\.|Imagine Bin|SAF Comics|Silver Sprocket|Storm King|Space Goat"
     r"|Avery Hill|Behemoth|Source Point|ComixTribe|Alterna|\bVault\b|Black Mask|\bAWA\b|Substack|Kickstarter"
     r"|Albatross|\bSLG\b|New England Comics|Red5|Dead Sky|Paper Films|Abstract Studio|Eclipse|Defiant|Charlton"
     r"|Hammer|Eagle Comics|Comico|Legal Defense|American Mythology|PanelxPanel|Harvey|Warren Ellis|Radical"
     r"|Aspen|Sumerian|Clover Press|Jet City|Cadence|Alien Books|Frank Miller|Comics Journal", "pub:small-press"),
    (r"Scholastic|Abrams|HarperCollins|Andrews McMeel|Ballantine|Berkley|Grosset|Graphix", "pub:trade-house"),
]
FOLDERS = [
    (r"Variant Covers|variant[- ]cover|\\covers\b|\bCovers\b|\b1pp\b", "folder:variant-covers"),
    (r"#DC Events|[Ee]vent folders?|read-order|reading order|READING order|Chronolog|chronology|Lost Dimension",
     "folder:events"),
    (r"\b_Trades|_Minis|[Oo]ne[- ][Ss]hots|bucket|_Archie|_Betty|_Deluxe|_X-Men TPBs|_2099", "folder:bucket"),
    (r"`v2 - |v2 - <|\bsubfolders?\b|`NN <Title>|\"01 …|`05 …`", "folder:sequel-subfolder"),
    (r"`vN \(|vN \(year\)|vN \(<year>\)|<Title> vN|`vN`|vNN", "folder:vn-year"),
    (r"Skottie", "folder:skottie"),
    (r"Graphic Novels", "folder:graphic-novels"),
    (r"Facsimile", "folder:facsimile"),
]
KINDS = [
    (r"[Mm]anga|Kodansha|\bViz\b|Vertical|tankōbon|scanlation|Tokyopop|TokyoPop|Shonen|Shōnen|Japanese", "kind:manga"),
    (r"newspaper|web[- ]?strip|[Ss]trip archive|[Ss]trips?\b|Doonesbury|Peanuts|Garfield|Bloom County|Webcomic",
     "kind:strip"),
    (r"[Mm]agazine|Weekly Shonen Jump|Fangoria", "kind:magazine"),
    (r"\bweekly\b|\bweeklies\b|Marvel UK|British", "kind:weekly"),
    (r"[Aa]rchive|[Rr]eprint LIBRARY|[Rr]eprint[- ]library|Library Edition|IDW Collection|Facsimile|Golden-Age ORIGINAL",
     "kind:archive"),
    (r"Epic Collection|Masterworks|[Oo]mnibus|collected LINE|collected line|trade LINE|trade line|Deluxe|"
     r"Complete Collection|creator-named|Collected Series|collected-edition|chain[- ]of[- ]minis|ONE TRADE PER MINI",
     "kind:collected-line"),
    (r"digital-first|Infinite Comics|Infinity Comics|[Dd]igital [Cc]hapters|chapters|digital-mobile|MDCE|"
     r"Digital Comics Unlimited|digital serial|webrip|mobile", "kind:digital"),
    (r"[Ff]oreign|Panini|Glénat|French|German|Dutch|Spanish|[Ll]anguage|Carlsen|Egmont Ehapa|Splitter|Éditions Lug"
     r"|translation|Norma|Kazé|Ki-oon", "kind:foreign"),
    (r"\bOGNs?\b|graphic novel", "kind:ogn"),
    (r"[Ff]an[- ]made|[Ff]an compiles|STORY EXTRACTS|extracts|DCP|FraBig|Pencils only|compilation", "kind:fan"),
    (r"2022-25|2024-2025|2024-25|past the rip's horizon|too new", "kind:new"),
    (r"Golden[- ]Age|Golden Age|1938|1940s", "kind:golden-age"),
    (r"[Kk]ids|all-ages|[Jj]uvenile|Golden Books|Super Hero Girls|DC Zoom", "kind:kids"),
    (r"trade-only|TRADE-ONLY|trade shelves|trade-only shelves|trades only|single trade|lone trade|TPB",
     "kind:trade-only"),
]
TOPICS = [
    (r"round2", "sig:round2"),
    (r"stale|overlap-in-series|conflated", "sig:stale-flag"),
    (r"ComicInfo|`Web` id|Web ids?", "sig:comicinfo"),
    (r"barcode|EAN|\bUPC\b|ISBN", "sig:barcode"),
    (r"count-1|same-title count-1|matches-our-holdings|matches our holdings|TPB record|collected record|trade record",
     "sig:trade-link"),
    (r"[Ll]egacy|RELAUNCH|relaunch|wrong DECADE|wrong-relaunch|legacy-volume", "sig:legacy-stamp"),
    (r"merge-with|collision|[Ss]hared stored|SHARED STORED|--all", "sig:collision"),
    (r"provider-missing|GCD hole|total GCD hole|holes? in|hides|hide a|spelling|lookup\.py|probe|--contains",
     "sig:probe"),
    (r"twin|TWIN", "sig:twin"),
    (r"withheld", "sig:withheld"),
    (r"gcd_reprint|gcd_series\.notes|notes", "sig:gcd-notes"),
    (r"split-needed|several runs|co-equal|R \+ split", "sig:split"),
    (r"stamp|per-file (?:CV |GCD )?matcher|per-file pick", "sig:stamp"),
]
PASSES = [
    (r"ITEM PASS|ITEM ROUND|X-\d{3}|`I` line|an `I`|`C` line|a `C`|no-record", "pass:item"),
    (r"\bR-0\d\d\b|revisit|second-read", "pass:revisit"),
    (r"TIER D|tier D|tier-D", "tier:D"),
]

# Entries that apply everywhere — copied into the brief's Rulings, never attached to a batch. Keyed on a
# verbatim opening so a re-tag cannot silently move them.
RULING_OPENINGS = (
    "- `<Publisher>\\<Title> (<year>)\\",
    "- LOCG bridges named a DIFFERENT comic",
    "- A provider count ABOVE our holdings",
    "- **A ladder that runs past the linked volume's COUNT",
    "- cvref's `normName` DROPS \"of\"",
    "- `I` lines are never optional",
    "- The packet's \"GCD dump: NNNNN",
    "- `gcd=s<series>` is valid ONLY on `I` lines",
    "- Packet phrase \"vol X … 1 issue Y\"",
    "- ITEM PASS (X-010..X-012): confidence is exactly one of",
    "- C-028..C-030: self-check before every S",
    "- RESIDUE, defined",
    "- Where the arithmetic refuses the run",
)


def tags_for_text(text, tables=None):
    """The slugs a piece of prose names. Used on ledger entries at migration and on folder paths / publisher
    strings for every shelf, so the two sides speak one vocabulary."""
    out = set()
    for table in (tables or (PUBLISHERS, FOLDERS, KINDS, TOPICS, PASSES)):
        for rx, tag in table:
            if re.search(rx, text or ""):
                out.add(tag)
    return out


# ── the ledger file ───────────────────────────────────────────────────────────────────────────────
def parse_brief_ledger(text):
    """The entries of the OLD brief's `## Conventions ledger` section, verbatim, in order. An entry is a
    `- ` line plus its two-space continuation lines; blank lines between groups are separators, not text."""
    lines = text.splitlines()
    try:
        start = next(i for i, l in enumerate(lines) if l.startswith("## Conventions ledger"))
    except StopIteration:
        return []
    out, cur = [], None
    for l in lines[start + 1:]:
        if l.startswith("## "):
            break
        if l.startswith("- "):
            if cur is not None:
                out.append("\n".join(cur))
            cur = [l]
        elif l.startswith("  ") and cur is not None:
            cur.append(l)
        elif not l.strip():
            if cur is not None:
                out.append("\n".join(cur))
                cur = None
    if cur is not None:
        out.append("\n".join(cur))
    return out


def load(path=LEDGER):
    """-> [{"id": "L-001", "tags": set, "text": str (verbatim, may be several lines)}]"""
    if not os.path.exists(path):
        return []
    entries, cur = [], None
    for raw in open(path, encoding="utf-8"):
        l = raw.rstrip("\n")
        m = RX_TAGLINE.match(l)
        if m:
            if cur:
                entries.append(cur)
            cur = {"id": f"L-{m.group(1)}", "tags": set(m.group(2).split()), "lines": []}
            continue
        if cur is None:
            continue
        if not l.strip():
            if cur["lines"]:
                entries.append(cur)
                cur = None
            continue
        cur["lines"].append(l)
    if cur:
        entries.append(cur)
    for e in entries:
        e["text"] = "\n".join(e.pop("lines"))
    return entries


# ── the match (tightened 2026-09-22 after A-035 attached 169 entries) ────────────────────────────────
# Per SHELF, not per batch union: an entry is scored by how many of the batch's shelves it matches, and only
# the best ~40 (~12 KB) print. `tier:` / `pass:` never qualify an entry on their own — a lesson true of a whole
# tier belongs in the brief. Inside the big houses a publisher match alone is not enough: DC holds 55 entries
# and Marvel 66, and a batch with one Vertigo shelf does not need the Batman relaunch stamps. So a big-house
# entry that names a folder shape or a kind needs that too, and one that names neither needs a KEYWORD from
# its own text (a sub-line, an imprint word, an event, a title) to hit a shelf's folder path or name.
BIG_HOUSES = {"pub:dc", "pub:marvel", "pub:image", "pub:idw", "pub:dark-horse", "pub:boom", "pub:dynamite"}
WEIGHT = {"pub": 3, "folder": 2, "kind": 2, "sig": 1}
# signals nearly every shelf shows (a missing leg, a book on the shelf) — too broad to admit a big-house entry
BROAD_SIGS = {"sig:probe", "sig:gcd-notes", "sig:twin"}
# the block as RENDERED (tag line + text), before the "+N more" id list at its foot
CAP_ENTRIES, CAP_BYTES = 40, 11000
# Capitalised words that name nothing: sentence starts, the grammar, the providers, the generic shelf words.
KW_STOP = set("""
a an the and or of in on at to by for from with into over under as is are was were be been this that these those it
its their there here when where which who what why how not no never only every each all any both either one two
three four five six seven eight nine ten first second third last same other another but if then than so also just
more most less many much some such via per while after before since until once still even
gcd cv comicvine dc marvel image idw boom dynamite dark horse comics comic lead ruling eric reader readers item pass
tier batch batches shelf shelves line lines record records run runs trade trades volume volumes vol issue issues
series book books packet packets edition editions collection collections collected tpb special specials annual
annuals graphic novel novels digital year years new english french german dutch spanish japanese original
provider providers leg legs stored link links per-file matcher pick picks count counts range ranges ids id
""".split())
# For the rare big-house entry whose text yields no usable keyword. selftest names any that need one.
KEYWORD_OVERRIDES = {
    "L-039": ["dv8"],                  # the one example is a 3-character name the capitalised-word rule drops
}
RX_CAPS = re.compile(r"(?<![\w`])([A-Z][\w'.&!:-]*(?:\s+(?:of|the|and|&|vs\.?|de|du)?\s*[A-Z0-9][\w'.&!:-]*){0,4})")
RX_TICK = re.compile(r"`([^`]{3,60})`")


def keywords(entry):
    """Words from the entry's own text that a relevant shelf's folder path or name would contain: capitalised
    names (whole phrase, and each content word of it) and backticked folder tokens. Lower-cased; cached."""
    if "_kw" in entry:
        return entry["_kw"]
    text, out = entry["text"], set(KEYWORD_OVERRIDES.get(entry["id"], ()))
    for m in RX_TICK.finditer(text):
        tok = m.group(1).strip("\\ ")
        if re.search(r"[<=|#]", tok) or not re.search(r"[A-Za-z]{3}", tok):
            continue
        out.add(tok.lower())
    for m in RX_CAPS.finditer(text):
        phrase = m.group(1).strip(" .:!'")
        words = [w.strip(".:!'&") for w in phrase.split()]
        content = [w for w in words if w and w.lower() not in KW_STOP and not re.fullmatch(r"[A-Z0-9]{1,3}", w)]
        if not content:
            continue
        if len(words) > 1 and len(phrase) >= 5:
            out.add(phrase.lower())
        out |= {w.lower() for w in content if len(w) >= 4}
    out = {k for k in out if k not in KW_STOP and len(k) >= 4} | set(KEYWORD_OVERRIDES.get(entry["id"], ()))
    entry["_kw"] = out
    entry["_rx"] = re.compile("|".join(r"(?<![a-z0-9])" + re.escape(k) + r"(?![a-z0-9])"
                                       for k in sorted(out, key=len, reverse=True))) if out else None
    return out


def is_scope(tag):
    return tag.split(":", 1)[0] in SCOPE_NS


def match_shelf(entry, stags, hay):
    """-> the namespace an entry matched ONE shelf on ("pub" / "folder" / "kind" / "sig"), or None.
    `stags` = the shelf's tags, `hay` = its folder paths + name, lower-cased (the keyword haystack)."""
    t = entry["tags"]
    if "ruling" in t:
        return None
    pubs = {x for x in t if x.startswith("pub:")}
    folders = {x for x in t if x.startswith("folder:")}
    kinds = {x for x in t if x.startswith("kind:")}
    sigs = {x for x in t if x.startswith("sig:")}
    if pubs:
        hit = pubs & stags
        if not hit or (folders and not folders & stags):
            return None
        if hit <= BIG_HOUSES:
            if kinds and not kinds & stags:
                return None
            if not folders and not kinds:
                # a keyword from the entry in the shelf's path or name — or, for a house-wide lesson about one
                # signal ("the stored link is the count-1 trade on 30% of Dynamite shelves"), that signal on
                # the shelf. The near-universal signals do not count: they would re-admit the whole house.
                keywords(entry)
                if not (entry["_rx"] is not None and entry["_rx"].search(hay)) \
                        and not (sigs - BROAD_SIGS) & stags:
                    return None
        return "pub"
    if folders:
        return "folder" if folders & stags else None
    if kinds:
        return "kind" if kinds & stags else None
    if sigs:
        return "sig" if sigs & stags else None
    return None                                   # tier / pass only: never qualifies


def matches(entry_tags, shelf_tags, text=""):
    """Test helper: does an entry with these tags (and this text, for keywords) match a shelf with these tags
    whose haystack is that same text."""
    return match_shelf({"id": "L-000", "tags": set(entry_tags), "text": text}, set(shelf_tags),
                       text.lower()) is not None


def rank(entries, shelves):
    """shelves = [(tags, hay)] -> [(score, entry, n_shelves)] best first. Score = shelves matched × the weight
    of the namespace matched, so a publisher lesson outranks a generic signal tip hit on as many shelves."""
    out = []
    for e in entries:
        n, ns = 0, None
        for stags, hay in shelves:
            m = match_shelf(e, stags, hay)
            if m:
                n += 1
                ns = m
        if n:
            out.append((n * WEIGHT[ns], e, n))
    out.sort(key=lambda x: (-x[0], x[1]["id"]))
    return out


# ── the shelf side ────────────────────────────────────────────────────────────────────────────────
class ShelfTagger:
    """Every shelf's tag set, from what its packet would show. Loaded in sweeps (the Evidence pattern): the
    folders its files sit in, the publisher named by the stored CV volume, the per-file GCD series and the
    ComicInfo Publisher, plus the packet signals the topic tags name."""

    def __init__(self, ev):
        self.ev = ev
        c = ev.con
        self.folders = defaultdict(Counter)
        for sid, path in c.execute("""SELECT SeriesId, Path FROM Item WHERE Kind = 0 AND coalesce(IsExcluded,0) = 0
                                      AND SeriesId IS NOT NULL"""):
            if sid in ev.shelf_set:
                self.folders[sid][idbase.short_path(os.path.dirname(path or ""))] += 1
        self.ci_pub = defaultdict(set)
        self.ci_web = set()
        for sid, pub, web, vol in c.execute("""SELECT i.SeriesId, e.Publisher, e.Web, e.Volume FROM ComicEmbedded e
                                               JOIN Item i ON i.Id = e.ItemId
                                               WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0"""):
            if sid in ev.shelf_set:
                if pub:
                    self.ci_pub[sid].add(pub)
                if web or (vol and str(vol).isdigit() and len(str(vol)) >= 4 and not str(vol).startswith(("19", "20"))):
                    self.ci_web.add(sid)
        self.barcode = {r[0] for r in c.execute("""SELECT DISTINCT i.SeriesId FROM legs.BarcodeScan b
                                                   JOIN Item i ON i.Id = b.ItemId
                                                   WHERE b.CodesJson IS NOT NULL AND b.CodesJson NOT IN ('','[]')""")}
        stored = Counter(s["cvVolumeId"] for s in ev.series.values() if s["cvVolumeId"])
        self.shared_cv = {sid for sid, s in ev.series.items() if s["cvVolumeId"] and stored[s["cvVolumeId"]] > 1}

    def tags(self, sid):
        ev = self.ev
        s = ev.series.get(sid)
        if s is None:
            return set()
        out = set()
        folders = self.folders.get(sid, Counter())
        for f in folders:
            out |= tags_for_text(f.split("\\")[0], (SHELF_PUBLISHERS,))
            out |= tags_for_text(f, (FOLDERS, KINDS))
            # `<Title> (<year>)` is the house shape of every folder; only the `vN` disambiguator marks a
            # shelf cut out of a franchise by relaunch, which is what the vn-year entries are about
            if re.search(r"\bv\d+\b", f):
                out.add("folder:vn-year")
            if re.search(r"\\(?:v\d+ - |\d\d )", f):
                out.add("folder:sequel-subfolder")
        # the publisher columns every leg prints — the same prose the ledger names publishers in
        pubs = set(self.ci_pub.get(sid, ()))
        v = ev.cv_volume.get(s["cvVolumeId"]) if s["cvVolumeId"] else None
        if v and v.get("publisher"):
            pubs.add(v["publisher"])
        for gid in ev.gcd_ids(sid):
            g = ev.gcd_series.get(gid)
            if g and g.get("publisher"):
                pubs.add(g["publisher"])
        for p in pubs:
            out |= tags_for_text(p, (SHELF_PUBLISHERS,))
            if re.search(r"Panini|Glénat|Carlsen|Egmont|Norma|Planeta|Dargaud|Delcourt|Splitter|Kazé|Ki-oon|"
                         r"Éditions|Editions|Edizioni|Verlag|Semic|Lug\b", p):
                out.add("kind:foreign")
        if s["muSeriesId"]:
            out.add("kind:manga")
        if re.search(r"Star Wars", s["name"] or ""):
            out.add("pub:star-wars")
        if s["yearStart"] and int(s["yearStart"]) < 1956:
            out.add("kind:golden-age")
        if s["yearStart"] and int(s["yearStart"]) >= 2022:
            out.add("kind:new")
        # packet signals (the topic tags)
        meth = ev.gcd_methods.get(sid, Counter())
        if any(m in meth for m in ("round2-folder", "round2-series")):
            out.add("sig:round2")
        if any(f["open"] and f["flag"] in ("conflated-series", "overlap-in-series") for f in ev.flags.get(sid, ())):
            out |= {"sig:stale-flag", "sig:split"}
        if sid in self.ci_web:
            out.add("sig:comicinfo")
        if sid in self.barcode:
            out.add("sig:barcode")
        if sid in self.shared_cv:
            out.add("sig:collision")
        ncol, nnum = ev.collections.get(sid, 0), ev.numbered.get(sid, 0)
        if ncol:
            out.add("sig:gcd-notes")
            if not nnum:
                out |= {"kind:trade-only", "kind:collected-line"}
        if v and v.get("countOfIssues") == 1 and ev.size.get(sid, 0) > 1:
            out.add("sig:trade-link")
        if v and v.get("startYear") and s["yearStart"] and abs(int(v["startYear"]) - int(s["yearStart"])) > 1:
            out.add("sig:legacy-stamp")
        cvf = ev.cv_files.get(sid, {})
        if len(cvf) > 1 or (cvf and s["cvVolumeId"] and set(cvf) != {s["cvVolumeId"]}):
            out |= {"sig:stamp", "sig:legacy-stamp"}
        if not ev.gcd_ids(sid) or not s["cvVolumeId"]:
            out.add("sig:probe")
        if ev.dupe_numbers.get(sid, 0) >= 2:
            out.add("sig:split")
        t = ev.tier(sid)[0]
        out.add(f"tier:{t}")
        return out

    def hay(self, sid):
        """The keyword haystack: every folder the shelf's files sit in, and its name, lower-cased."""
        s = self.ev.series.get(sid) or {}
        return (" | ".join(self.folders.get(sid, ())) + " | " + (s.get("name") or "")).lower()


def vocabulary():
    """Every tag a shelf or batch can carry — the reachability test's universe."""
    v = {t for table in (SHELF_PUBLISHERS, FOLDERS, KINDS) for _rx, t in table}
    v |= {"sig:round2", "sig:stale-flag", "sig:split", "sig:comicinfo", "sig:barcode", "sig:collision",
          "sig:gcd-notes", "sig:trade-link", "sig:legacy-stamp", "sig:stamp", "sig:probe",
          "folder:vn-year", "folder:sequel-subfolder", "kind:trade-only", "kind:collected-line", "kind:manga",
          "kind:foreign", "kind:golden-age", "kind:new", "tier:A", "tier:B", "tier:C", "tier:D",
          "pass:item", "pass:revisit", "pass:s2"}
    # these come from the batch KIND or a shelf's books rather than a sweep (see shelf_views)
    v |= {"sig:withheld", "sig:twin"}
    return v


def shelf_views(tagger, sids, kind=None):
    """[(tags, hay)] per shelf, with the batch kind's own topic tags: an `R-` / S.2 batch re-reads decided
    shelves, so the withheld-pair and collision lessons apply to each of them."""
    out = []
    for s in sids:
        ts = set(tagger.tags(s))
        if kind in ("R", "S2"):
            ts |= {"sig:withheld", "sig:collision"}
        # a twin is a GCD leg fact the sweep cannot see cheaply; a shelf with books or barcodes can meet one
        if "sig:gcd-notes" in ts or "sig:barcode" in ts:
            ts.add("sig:twin")
        out.append((ts, tagger.hay(s)))
    return out


BLOCK_RULE = "=" * 100


def block(entries, shelves, n_total):
    """The `## Conventions for this batch` block: the best-ranked entries verbatim, capped, and the ids of
    every other matched entry at the foot — nothing matched is silently dropped."""
    ranked = rank(entries, shelves)
    shown, size = [], 0
    for _score, e, n in ranked:
        cost = len(e["text"].encode("utf-8")) + 60
        if len(shown) >= CAP_ENTRIES or (shown and size + cost > CAP_BYTES):
            break
        shown.append((e, n))
        size += cost
    rest = [e["id"] for _s, e, _n in ranked[len(shown):]]
    per = Counter(t for ts, _h in shelves for t in ts if t.split(":", 1)[0] in ("pub", "folder", "kind"))
    top = " · ".join(f"{t} ×{n}" for t, n in per.most_common(8))
    L = [f"## Conventions for this batch ({len(shown)} shown of {len(ranked)} matched; {n_total} in LEDGER.md; "
         f"{len(shelves)} shelf/shelves: {top or '—'})",
         "LEDGER.md entries matching this batch's shelves, best first (ledger.py explains the match). Context for",
         "reading, never a rule applied across a folder. Do not read LEDGER.md whole.", ""]
    for e, n in shown:
        L.append(f"[{e['id']}] ({n} shelf/shelves) " + " ".join(sorted(e["tags"])))
        L.append(e["text"])
    if rest:
        L.append("")
        L.append(f"+{len(rest)} more matched entries not shown: {', '.join(rest)} (grep LEDGER.md for them)")
    L += ["", BLOCK_RULE, ""]
    return L


def block_for(ev, sids, kind=None, tagger=None, entries=None):
    tagger = tagger or ShelfTagger(ev)
    entries = entries if entries is not None else load()
    return block(entries, shelf_views(tagger, sids, kind), len(entries))


def split_block(text):
    """(conventions block, packet body) of a batch file — the block ends at its `====` rule line + blank."""
    # batch files are written in text mode, so on Windows their lines end CRLF — the marker follows the file
    nl = "\r\n" if "\r\n" in text[:4000] else "\n"
    marker = nl + BLOCK_RULE + nl + nl
    if text.startswith("## Conventions for this batch") and marker in text:
        k = text.index(marker) + len(marker)
        return text[:k], text[k:]
    return "", text


def unreachable_reason(e):
    """None if some shelf could match the entry: its qualifying tags are producible, and a big-house entry that
    must be keyed has keywords. Otherwise why not."""
    vocab = vocabulary()
    qual = {t for t in e["tags"] if t.split(":", 1)[0] in ("pub", "folder", "kind", "sig")}
    if not qual:
        return "only tier:/pass: tags, which never qualify"
    if not qual <= vocab:
        return f"tags no shelf produces: {sorted(qual - vocab)}"
    hay = " ".join(sorted(keywords(e)))
    return None if match_shelf(e, set(qual), hay) else \
        "a big-house entry with no folder/kind tag, no keyword and no specific signal"


# ── the one-time migration ───────────────────────────────────────────────────────────────────────
HEAD = """# Conventions ledger (tagged) — context for reading, never a rule applied across a folder

Moved out of READER_BRIEF.md on 2026-09-22 (TOOLS_TODO 20). **Readers do not read this file whole**: every batch
file opens with a `## Conventions for this batch` block holding the entries whose tags match its shelves
(`tools/ledger.py` — the match rule is in its docstring). The lead adds new conventions HERE, each under a tag
line, and the next emitted batch carries them.

Entry format: a tag line `[L-NNN] <tag> <tag> …`, then the entry text verbatim, then a blank line.
Tag namespaces — SCOPE: `pub:` publisher / imprint · `folder:` folder shape · `kind:` kind of comic · `tier:` ·
`pass:` (item / revisit / s2). TOPIC: `sig:` a packet signal. An entry matches on its MOST SPECIFIC namespace
only (pub, then folder, then kind, then tier/pass, then sig); an entry naming both a publisher and a folder shape
needs both. So a DC relaunch lesson reaches DC batches, a bucket-folder lesson naming no publisher reaches every
batch with a bucket folder, and a topic-only lesson reaches every batch showing that signal. `ruling` = copied
into the brief's Rulings (applies everywhere), not attached to batches. Use only slugs `ledger.py --stats` lists —
a tag no shelf can produce makes the entry unreachable, and selftest fails on it.

"""


def auto_tags(text):
    tags = tags_for_text(text)
    if text.startswith(RULING_OPENINGS):
        tags.add("ruling")
    if not tags:
        tags.add("sig:probe")
    return tags


def migrate(old_brief, force=False):
    if os.path.exists(LEDGER) and not force:
        raise SystemExit(f"{LEDGER} exists — the migration is one-time (pass --force to redo it)")
    entries = parse_brief_ledger(open(old_brief, encoding="utf-8").read())
    out = [HEAD]
    for k, text in enumerate(entries, 1):
        out.append(f"[L-{k:03d}] " + " ".join(sorted(auto_tags(text))))
        out.append(text)
        out.append("")
    with open(LEDGER, "w", encoding="utf-8") as f:
        f.write("\n".join(out))
    print({"entries": len(entries), "ledger": LEDGER})


def stats(entries):
    tags = Counter(t for e in entries for t in e["tags"])
    vocab = vocabulary()
    print(f"{len(entries)} entries, {len(tags)} distinct tags")
    for t, n in sorted(tags.items()):
        print(f"   {n:>4}  {t}{'' if t in vocab or t == 'ruling' else '   ⚠ NO SHELF CAN PRODUCE THIS TAG'}")
    bad = [(e["id"], unreachable_reason(e)) for e in entries if "ruling" not in e["tags"] and unreachable_reason(e)]
    print(f"unreachable entries: {bad or 'none'}")


def batch_ids(name, ev):
    """The SHELF ids of an emitted batch (an `X-` batch's .ids are books: mapped to their shelves)."""
    ids = [int(x) for x in open(os.path.join(idbase.BATCHES, name + ".ids"), encoding="utf-8").read().split()]
    kind = name.split("-")[0]
    if kind == "X":
        ids = sorted({r[0] for r in ev.con.execute(
            f"SELECT SeriesId FROM Item WHERE Id IN ({','.join('?' * len(ids))})", ids) if r[0]})
    return ids, kind


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    a = sys.argv[1:]
    if "--migrate" in a:
        migrate(a[a.index("--migrate") + 1], force="--force" in a)
        return
    entries = load()
    if "--stats" in a:
        stats(entries)
        return
    out = a[a.index("--out") + 1] if "--out" in a else None
    ev = idbase.Evidence()
    if "--rewrite-block" in a:
        # Replace the conventions block of an EMITTED batch in place, and nothing else: the packet body is the
        # text a decision file is written against, so it must come out byte-identical.
        name = a[a.index("--rewrite-block") + 1]
        path = os.path.join(idbase.BATCHES, name + ".txt")
        raw = open(path, "rb").read()
        old_block, body = split_block(raw.decode("utf-8"))
        if not old_block:
            raise SystemExit(f"{name}.txt has no conventions block to replace")
        ids, kind = batch_ids(name, ev)
        # write_batch joins block + packets with "\n", so the block's own text ends with one more newline
        nl = "\r\n" if "\r\n" in old_block else "\n"
        head = ("\n".join(block_for(ev, ids, kind, entries=entries)) + "\n").replace("\n", nl)
        _nb, new_body = split_block(head + body)
        if new_body.encode("utf-8") != body.encode("utf-8"):
            raise SystemExit("refusing: the body would not survive byte for byte")
        with open(path, "wb") as f:
            f.write((head + body).encode("utf-8"))
        print({"batch": name, "old block bytes": len(old_block.encode("utf-8")),
               "new block bytes": len(head.encode("utf-8")), "body bytes (unchanged)": len(body.encode("utf-8"))})
        return
    kind = None
    if "--batch" in a:
        ids, kind = batch_ids(a[a.index("--batch") + 1], ev)
    else:
        ids = [int(x) for x in a[a.index("--sids") + 1:] if x.isdigit()] if "--sids" in a else []
    if not ids:
        raise SystemExit(__doc__)
    L = block_for(ev, ids, kind, entries=entries)
    text = "\n".join(L)
    if out:
        with open(out, "w", encoding="utf-8") as f:
            f.write(text)
        print(L[0])
        print(f"-> {out}")
    else:
        print(text)


if __name__ == "__main__":
    main()
