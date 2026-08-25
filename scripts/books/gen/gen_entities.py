"""R4: generate the Books v2 EF entities + DbContexts from docs/books/v2-mapping.json.

The mapping is the contract (the R3 gate approved it); this script turns its `v2` catalog into
C# so the two cannot drift by hand. Re-run after any catalog change, then `dotnet ef migrations add`.
Type rules (SQLite is typeless, so these are OUR choices, stated once here):
  - PK INTEGER -> int; FK INTEGER -> int (int? when marked NULL); other INTEGER -> int? unless
    named in BOOLS (bool), COUNTERS (int, default 0), LONGS (long) or ENUMS (the enum type).
  - REAL -> double?; TEXT -> string? (string, required, for PK/UNIQUE texts and REQUIRED_TEXT);
    TEXT columns ending in "At" -> DateTime? (v1 stores ISO text; the migration parses it).
Outputs: src/MovieTheater.Books.Db/{Hot,Legs}/Entities.cs, BooksDb.cs, BooksLegsDb.cs, Enums.cs.
"""
from __future__ import annotations
import json, re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
MAPPING = ROOT / "docs" / "books" / "v2-mapping.json"
OUT = ROOT / "src" / "MovieTheater.Books.Db"

BOOLS = {"IsCalibre", "Enabled", "HasIcon", "IsExcluded", "KeepInDirectory", "IsBroken", "IsCollection", "IsOngoing",
         "IsKey", "Completed", "Applied", "Contiguous", "IsCurrent", "IsOverride", "Recognized", "WantToRead", "Favorite",
         "HiddenFromHistory", "IsRead", "IsFavorite", "SoleFileInFolder", "HasIsbn", "HasBarcode", "IsAdmin"}
NULLABLE_BOOLS = {"BlackAndWhite"}
COUNTERS = {"Depth", "DirectChildCount", "DescendantItemCount", "IssueCount", "AttemptCount", "Ordinal", "Rank",
            "Processed", "Total", "RowCount", "ItemsSeen", "Added", "Changed", "Removed", "PagesScanned",
            "MaturityCeiling", "LastPage", "ContainsCount", "ReadCount", "EditionCount", "Support", "IsbnSupport"}
LONGS = {"FileSize", "CoverPHash", "MuSeriesId"}  # MangaUpdates ids are 11 digits
LONG_PKS = {("MuSeries", "Id")}
REQUIRED_TEXT = {"Path", "FileName", "CanonicalKey", "RebuildJob", "ModelId"}
# (table, column) -> enum type; "*" table = any table carrying that column with INTEGER affinity
ENUMS = {
    ("*", "Kind"): "ItemKind",
    ("Item", "ContainerFormat"): "ContainerFormat",
    ("ComicDetail", "Format"): "ComicFormat",
    ("*", "Confidence"): "Confidence",           # only when INTEGER (REAL Confidence stays double?)
    ("ComicDetail", "SeriesSource"): "ParseSource", ("ComicDetail", "IssueSource"): "ParseSource",
    ("ComicDetail", "YearSource"): "ParseSource", ("ComicDetail", "PublisherSource"): "ParseSource",
    ("ReadingOrderEntry", "Source"): "ReadingOrderSource",
    ("*", "ReadDatePrecision"): "DatePrecision", ("*", "ResolvedDatePrecision"): "DatePrecision",
    ("CollectionNode", "Level"): "CollectionLevel", ("CollectionNode", "TrackRole"): "TrackRole",
    ("CollectionNode", "SpanSource"): "SpanSource",
    ("*", "Provider"): "Provider",
    ("SeriesKeyLink", "Status"): "LinkStatus", ("MuSeriesLink", "Status"): "LinkStatus", ("ItemProviderLink", "Status"): "LinkStatus",
    ("ItemProviderLink", "Quality"): "LinkQuality",
    ("CollectedEditionSpan", "Source"): "EditionSource",
    ("Insight", "SubjectKind"): "SubjectKind", ("Rating", "TargetKind"): "SubjectKind", ("LinkCandidates", "Scope"): "SubjectKind",
    ("Rating", "Source"): "RatingSource",
    ("ItemCredit", "Source"): "TagSource", ("ItemTag", "Source"): "TagSource", ("SeriesTag", "Source"): "TagSource",
    ("UserItemState", "Status"): "ReadStatus",
    ("GroupMark", "GroupType"): "GroupType",
    ("*", "ResolvedSynopsisSource"): "SynopsisSource",
}
ENUM_DEFS = {  # name -> members (from the mapping's enums block; keyed here so the C# names are fixed)
    "ItemKind": "Item.Kind", "ContainerFormat": "Item.ContainerFormat", "ComicFormat": "ComicDetail.Format",
    "Confidence": "Confidence", "ParseSource": "ComicDetail.*Source", "ReadingOrderSource": "ReadingOrderEntry.Source",
    "DatePrecision": "DatePrecision", "CollectionLevel": "CollectionNode.Level", "TrackRole": "CollectionNode.TrackRole",
    "SpanSource": "CollectionNode.SpanSource", "Provider": "Provider", "LinkStatus": "LinkStatus", "LinkQuality": "LinkQuality",
    "EditionSource": "CollectedEditionSpan.Source", "SubjectKind": "Insight.SubjectKind / Rating.TargetKind",
    "RatingSource": "Rating.Source", "TagSource": "ItemCredit.Source / ItemTag.Source", "ReadStatus": "UserItemState.Status",
    "GroupType": "GroupMark.GroupType", "SynopsisSource": "Item.ResolvedSynopsisSource",
}
# navigations worth having (owner table, property, target table, fk column, one-to-one?)
NAVS = [
    ("Item", "Series", "Series", "SeriesId", False),
    ("Item", "Folder", "Folder", "FolderId", False),
    ("Item", "Publisher", "Publisher", "PublisherId", False),
    ("Item", "State", "ItemState", None, True),
    ("Item", "Signature", "ItemSignature", None, True),
    ("Item", "Embedded", "ComicEmbedded", None, True),
    ("Item", "Book", "BookDetail", None, True),
    ("Item", "Comic", "ComicDetail", None, True),
]
PLURAL = {"Series": "Series", "SeriesAlias": "SeriesAliases", "Category": "Categories"}


def plural(t: str) -> str:
    if t in PLURAL: return PLURAL[t]
    if t.endswith("s"): return t
    if t.endswith("y") and t[-2] not in "aeiou": return t[:-1] + "ies"
    return t + "s"


COL_RE = re.compile(r"^(\w+)\s+(INTEGER|TEXT|REAL)((?:\s+\w+)*)$")


def parse_cols(spec: str):
    cols = []
    for part in spec.split(","):
        part = part.strip()
        if part.startswith("rowid="): continue
        m = COL_RE.match(part)
        if not m: raise SystemExit(f"cannot parse column spec: {part!r}")
        name, typ, rest = m.group(1), m.group(2), m.group(3).split()
        fk = None
        if "FK" in rest: fk = rest[rest.index("FK") + 1]
        cols.append(dict(name=name, sql=typ, fk=fk, null="NULL" in rest, unique="UNIQUE" in rest))
    return cols


def cs_type(table: str, c: dict, pk: list[str]) -> tuple[str, str]:
    """-> (type, initializer)"""
    n, t = c["name"], c["sql"]
    is_pk = n in pk
    if t == "INTEGER":
        e = ENUMS.get((table, n)) or ENUMS.get(("*", n))
        if e: return (e + ("?" if c["null"] else ""), "")
        if (table, n) in LONG_PKS: return ("long", "")
        if n in LONGS: return ("long" + ("?" if n == "CoverPHash" or c["null"] else ""), "")
        if n in BOOLS: return ("bool", "")
        if n in NULLABLE_BOOLS: return ("bool?", "")
        if is_pk or (c["fk"] and not c["null"]): return ("int", "")
        if n in COUNTERS: return ("int", "")
        return ("int?", "")
    if t == "REAL": return ("double?", "")
    # TEXT
    if n.endswith("At"): return ("DateTime?", "")
    if is_pk or c["unique"] or n in REQUIRED_TEXT: return ("string", ' = "";')
    return ("string?", "")


def entity_cs(table: str, spec: dict) -> str:
    cols = parse_cols(spec["cols"])
    pk = spec["pk"]
    L = [f"    /// <summary>{spec['purpose']}</summary>", f"    public sealed class {table}", "    {"]
    for c in cols:
        typ, init = cs_type(table, c, pk)
        L.append(f"        public {typ} {c['name']} {{ get; set; }}{init}")
    for owner, prop, target, fk, one in NAVS:
        if owner == table:
            L.append(f"        public {target}? {prop} {{ get; set; }}")
    L.append("    }")
    return "\n".join(L)


def config_cs(table: str, spec: dict) -> str:
    cols = parse_cols(spec["cols"])
    pk = spec["pk"]
    L = [f"            modelBuilder.Entity<{table}>(e =>", "            {", f'                e.ToTable("{table}");']
    if len(pk) == 1:
        L.append(f"                e.HasKey(x => x.{pk[0]});")
        c0 = next(c for c in cols if c["name"] == pk[0])
        # preserved ids arrive from v1 and every other int PK is assigned by the migration/job
        if c0["sql"] == "INTEGER": L.append(f"                e.Property(x => x.{pk[0]}).ValueGeneratedNever();")
    else:
        L.append("                e.HasKey(x => new { " + ", ".join(f"x.{p}" for p in pk) + " });")
    for c in cols:
        # non-nullable scalars carry a DATABASE default so a partial INSERT (the migration writer, a job that owns
        # a few columns) never trips NOT NULL on a column another writer owns
        typ, _ = cs_type(table, c, pk)
        if c["name"] not in pk and not typ.endswith("?") and typ != "string":
            default = "false" if typ == "bool" else "0" if typ in ("int", "long") else f"{typ}.{ENUM_FIRST[typ]}"
            L.append(f"                e.Property(x => x.{c['name']}).HasDefaultValue({default});")
        if c["unique"]:
            L.append(f"                e.HasIndex(x => x.{c['name']}).IsUnique();")
        if c["fk"]:
            one = next((n for n in NAVS if n[2] == table and n[4]), None)
            nav = next((n for n in NAVS if n[0] == table and n[3] == c["name"]), None)
            if one and c["name"] == "ItemId":
                # a 1:1 satellite of Item: the FK is declared ONCE, from the dependent side, so EF does not
                # invent a second shadow FK for the Item.<Prop> navigation
                L.append(f"                e.HasOne<Item>().WithOne(x => x.{one[1]}).HasForeignKey<{table}>(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);")
            elif nav:
                L.append(f"                e.HasOne(x => x.{nav[1]}).WithMany().HasForeignKey(x => x.{c['name']}).OnDelete(DeleteBehavior.Restrict);")
            else:
                L.append(f"                e.HasOne<{c['fk']}>().WithMany().HasForeignKey(x => x.{c['name']}).OnDelete(DeleteBehavior.Restrict);")
    for idx in spec.get("idx", []):
        unique = idx.startswith("UNIQUE")
        body = idx[len("UNIQUE"):].strip() if unique else idx
        where = None
        if " WHERE " in body:
            body, where = body.split(" WHERE ", 1)
        inner = body.strip()[1:-1]
        parts = [p.strip() for p in inner.split(",")]
        names = [p.split()[0] for p in parts]
        descs = ["DESC" in p.upper() for p in parts]
        if len(names) == 1:
            L.append(f"                e.HasIndex(x => x.{names[0]})" + (".IsUnique()" if unique else "") +
                     (".IsDescending(true)" if descs[0] else "") + (f'.HasFilter("\\"{where.split()[0]}\\" = 1")' if where else "") + ";")
        else:
            L.append("                e.HasIndex(x => new { " + ", ".join(f"x.{n}" for n in names) + " })" +
                     (".IsUnique()" if unique else "") +
                     (".IsDescending(" + ", ".join("true" if d else "false" for d in descs) + ")" if any(descs) else "") +
                     (f'.HasFilter("\\"{where.split()[0]}\\" = 1")' if where else "") + ";")
    L.append("            });")
    return "\n".join(L)


def enums_cs(m: dict) -> str:
    L = ["// <auto-generated> by scripts/books/gen/gen_entities.py from docs/books/v2-mapping.json — do not edit by hand.",
         "namespace MovieTheater.Books.Db", "{"]
    for cs, key in ENUM_DEFS.items():
        members = m["enums"][key]
        L.append(f"    /// <summary>Stored as int. Vocabulary from v2-mapping.json enums[\"{key}\"].</summary>")
        L.append(f"    public enum {cs} {{ " + ", ".join(f"{v} = {i}" for i, v in enumerate(members)) + " }")
    L.append("}")
    return "\n".join(L) + "\n"


def context_cs(name: str, tables: list[tuple[str, dict]], doc: str) -> str:
    L = ["// <auto-generated> by scripts/books/gen/gen_entities.py from docs/books/v2-mapping.json — do not edit by hand.",
         "using Microsoft.EntityFrameworkCore;", "", "namespace MovieTheater.Books.Db", "{",
         f"    /// <summary>{doc}</summary>",
         f"    public sealed class {name} : DbContext", "    {",
         f"        public {name}(DbContextOptions<{name}> options) : base(options) {{ }}", ""]
    for t, _ in tables:
        L.append(f"        public DbSet<{t}> {plural(t)} => Set<{t}>();")
    L += ["", "        protected override void OnModelCreating(ModelBuilder modelBuilder)", "        {"]
    for t, s in tables:
        L.append(config_cs(t, s))
    L += ["        }", "    }", "}"]
    return "\n".join(L) + "\n"


def entities_file(tables: list[tuple[str, dict]], ns_doc: str) -> str:
    L = ["// <auto-generated> by scripts/books/gen/gen_entities.py from docs/books/v2-mapping.json — do not edit by hand.",
         "#nullable enable", "using System;", "", "namespace MovieTheater.Books.Db", "{", f"    // {ns_doc}"]
    for t, s in tables:
        L.append(entity_cs(t, s)); L.append("")
    L.append("}")
    return "\n".join(L) + "\n"


ENUM_FIRST: dict[str, str] = {}


def main():
    m = json.load(open(MAPPING, encoding="utf-8"))
    for cs, key in ENUM_DEFS.items(): ENUM_FIRST[cs] = m["enums"][key][0]
    hot = [(t, s) for t, s in m["v2"].items() if s["file"] == "hot" and t != "ItemFts"]
    legs = [(t, s) for t, s in m["v2"].items() if s["file"] == "legs"]
    (OUT / "Hot").mkdir(parents=True, exist_ok=True); (OUT / "Legs").mkdir(parents=True, exist_ok=True)
    (OUT / "Hot" / "Entities.cs").write_text(entities_file(hot, "books.db — the runtime's only file."), encoding="utf-8")
    (OUT / "Legs" / "Entities.cs").write_text(entities_file(legs, "books-legs.db — offline warehouse; no FK crosses the file boundary."), encoding="utf-8")
    (OUT / "BooksDb.cs").write_text(context_cs("BooksDb", hot, "The hot catalog (books.db). ItemFts (FTS5) is created by the migration's raw SQL and queried through FtsQueries, not mapped."), encoding="utf-8")
    (OUT / "BooksLegsDb.cs").write_text(context_cs("BooksLegsDb", legs, "The offline warehouse (books-legs.db): provider raw rows, containment edges, caches, candidate JSON."), encoding="utf-8")
    (OUT / "Enums.cs").write_text(enums_cs(m), encoding="utf-8")
    print("hot", len(hot), "legs", len(legs), "enums", len(ENUM_DEFS))


if __name__ == "__main__":
    main()
