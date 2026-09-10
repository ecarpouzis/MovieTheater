"""The indie tail: derive a verdict from the TITLE alone.

What is left after the author pass and the bulk per-book batches is ~1,500 self-published books,
one per author, with no publisher, no ISBN and no series — nothing but a title. A title is a weak
signal, so this writes weak verdicts on purpose:

  * `maturity 2 / mature` always. It is the modal truth for adult-audience indie fiction and it is
    the CONSERVATIVE direction: it never clears a book for a child, and `MaturityFilter` already
    fails closed on the unrated, so nothing here can loosen a kid's view.
  * `confidence: Low` always, so `Transforms.ModelRank` still ranks it under any real signal that
    lands later (an ISBN fold, a Calibre edit) and a reader can see the verdict is a guess.
  * genres ONLY when a keyword in the title actually implies one. A title that says nothing gets an
    audience and no genre rather than an invented shelf — an empty `genre` list is legal and keeps
    the facets clean.

Adult tells are matched FIRST and promote to maturity 3, because the harm the owner named is
under-filtering, not over-filtering.

  python scripts/books/tail_title_verdicts.py --batch bbatch-NN.json --out bverdicts-NN.jsonl
"""
import argparse
import json
import re
import sys

ADULT = re.compile(
    r"\b(erotic|erotica|orgy|nympho|slut|whore|harlot|incest|bdsm|bondage|spank|submissive|"
    r"threesome|swinger|voyeur|hardcore|xxx|lust|seduc\w*ss|escort|call girl|stripper|"
    r"stepbrother|stepsister|stepdaddy|daddy's|milf|cuckold|fetish|kinky|naughty bits)\b", re.I)

# (pattern, genres) — first match wins. Deliberately conservative: a word has to really imply a shelf.
RULES = [
 (r"\b(vampire|werewolf|zombie|undead|ghoul|banshee|demon|wraith|witch|coven|haunt\w*|ghost|"
  r"phantom|spectre|specter|poltergeist)\b", ["paranormal", "supernatural"]),
 (r"\b(dragon|wizard|sorcer\w+|mage|magick?|spell|elf|elves|dwarv\w+|realm|kingdom|sword|"
  r"amulet|talisman|prophec\w+|quest|scimitar|knight|chronicles? of)\b", ["fantasy"]),
 (r"\b(galax\w+|star(ship|s)?|space|planet|alien|robot|android|cyborg|cyber\w*|teleport\w*|"
  r"orbit|mars|lunar|moonbase|nebula|warp|clone|nanite|dystopi\w+|apocalyps\w+)\b", ["sci-fi"]),
 (r"\b(murder|homicide|detective|killer|corpse|forensic|noir|heist|hitman|serial|"
  r"crime|felon|gangster|mafia)\b", ["crime", "mystery"]),
 (r"\b(spy|espionage|assassin|conspirac\w+|manhunt|sniper|covert|operative)\b", ["thriller", "spy"]),
 (r"\b(terror|horror|nightmare|dread|blood|butcher|slaughter|carnage|the dead)\b", ["horror"]),
 (r"\b(love|heart|kiss|bride|wedding|romance|valentine|sweetheart|courtship|honeymoon)\b",
  ["romance"]),
 (r"\b(war|battle|regiment|soldier|marine|platoon|combat|siege|front line)\b", ["war"]),
 (r"\b(cowboy|ranch|frontier|outlaw|saloon|prairie|deputy|sheriff)\b", ["western"]),
 (r"\b(memoir|diary|diaries|journal|my life|autobiograph\w+|confessions?)\b", ["autobiography"]),
 (r"\b(recipes?|cookbook|cooking|kitchen)\b", ["cooking"]),
 (r"\b(poems?|poetry|verse|sonnets?)\b", ["poetry"]),
 (r"\b(god|jesus|christ|bible|prayer|faith|redeem\w*|salvation|sin|angel|heaven|rapture|"
  r"scripture|parable)\b", ["religion"]),
 (r"\b(guide|handbook|manual|how to|introduction to|dictionary|encyclopedia)\b", ["reference"]),
 (r"\b(school|high school|college|campus|graduat\w+|teen|prom)\b", ["coming-of-age"]),
 (r"\b(stories|tales|collection|anthology)\b", ["short-stories"]),
]


def verdict(title):
    t = title or ""
    if ADULT.search(t):
        return {"maturity": 3, "audience": "adult", "genres": ["erotica"], "confidence": "Medium"}
    for rx, genres in RULES:
        if re.search(rx, t, re.I):
            return {"maturity": 2, "audience": "mature", "genres": genres, "confidence": "Low"}
    return {"maturity": 2, "audience": "mature", "genres": [], "confidence": "Low"}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--batch", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    with open(args.batch, encoding="utf-8") as f:
        batch = json.load(f)["batch"]
    counts = {}
    with open(args.out, "w", encoding="utf-8") as out:
        for b in batch:
            v = verdict(b.get("title"))
            key = ",".join(v["genres"]) or "(audience only)"
            counts[key] = counts.get(key, 0) + 1
            out.write(json.dumps({"id": b["id"], **v}, ensure_ascii=False) + "\n")
    print(f"wrote {args.out}: {len(batch)} verdicts")
    for k, n in sorted(counts.items(), key=lambda kv: -kv[1]):
        print(f"  {n:5d}  {k}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
