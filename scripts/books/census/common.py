"""Shared paths/helpers for the v1 books census (R0). Read-only against the source; writes only under data/books."""
from __future__ import annotations
import json, os, sqlite3
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
DATA = REPO / "data" / "books"
V1 = DATA / "v1"
CENSUS = DATA / "census"
DOCS = REPO / "docs" / "books"

# The live MyBooks source database (opened read-only, once, by freeze.py only).
LIVE_DB = Path(r"F:\Work\MyBooks\MyBooks\src\Mybooks\mybooks.db")
LIVE_CACHE = Path(r"F:\Work\MyBooks\MyBooks\src\Mybooks\cache")
MYBOOKS_REPO = Path(r"F:\Work\MyBooks\MyBooks")
MYBOOKS_OUTER = Path(r"F:\Work\MyBooks")


def frozen_db() -> Path:
    cands = sorted(V1.glob("mybooks-v1-frozen-*.db"))
    if not cands:
        raise SystemExit("no frozen copy yet - run freeze.py first")
    return cands[-1]


def census_db() -> Path:
    return V1 / "mybooks-v1-census.db"


def ro(path: Path) -> sqlite3.Connection:
    con = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    return con


def tables(con: sqlite3.Connection) -> list[str]:
    return [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]


def is_fts_shadow(name: str) -> bool:
    return name.startswith("ComicFts_") or name == "sqlite_sequence"


def dump_json(path: Path, obj) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(obj, indent=2, default=str), encoding="utf-8")
    os.replace(tmp, path)
