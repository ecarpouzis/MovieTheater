"""Online backup of the live Books files before the containment run.

conn.backup() is SQLite's Online Backup API: safe with the host still serving and reading WAL,
unlike copying the file. Writes to data/books/v2/backup-<stamp>/.
"""
import os, sqlite3, sys, time

SRC = r"F:\Work\MovieTheater\data\books\v2"
stamp = time.strftime("%Y%m%d-%H%M%S")
dst_dir = os.path.join(SRC, f"backup-{stamp}")
os.makedirs(dst_dir, exist_ok=True)

for name in ("books.db", "books-legs.db"):
    src = os.path.join(SRC, name)
    dst = os.path.join(dst_dir, name)
    t0 = time.time()
    con = sqlite3.connect(f"file:{src}?mode=ro", uri=True)
    out = sqlite3.connect(dst)
    con.backup(out)
    out.close()
    con.close()
    print(f"{name}: {os.path.getsize(dst):,} bytes in {time.time()-t0:.1f}s -> {dst}", flush=True)

print("BACKUP DIR", dst_dir)
