"""Read-only inventory of the comics share: one TSV line per file (path, size, mtime-seconds).

Chunked + resumable: the BFS frontier and the done-count live in <out>/state.json and are rewritten
after every chunk of directories, together with an fsync'd append to inventory.tsv. A kill costs at most
one chunk; re-running continues (directories already listed are never listed twice because the frontier
only ever holds directories not yet listed, and a chunk's lines are appended BEFORE the state that
records it -- a crash between the two re-lists that chunk, so dedupe on read: last line per path wins).

Never writes beside a library file. Never follows anything but the share.

usage: python inventory_walk.py --out data/books/rescan/20261002 [--chunk 400] [--max-chunks N]
"""
import argparse, json, os, sys, time

ROOT = r"\\Library\Public\5 - Comics"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--chunk", type=int, default=400, help="directories per chunk")
    ap.add_argument("--max-chunks", type=int, default=0)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    state_p = os.path.join(a.out, "state.json")
    inv_p = os.path.join(a.out, "inventory.tsv")
    err_p = os.path.join(a.out, "errors.tsv")

    if os.path.exists(state_p):
        st = json.load(open(state_p, encoding="utf-8"))
    else:
        if not os.path.isdir(ROOT):
            sys.exit(f"root unreachable: {ROOT}")
        st = {"frontier": [ROOT], "dirs": 0, "files": 0, "bytes": 0, "errors": 0, "started": time.time()}

    chunks = 0
    while st["frontier"]:
        batch, st["frontier"] = st["frontier"][: a.chunk], st["frontier"][a.chunk :]
        lines, errs, newdirs = [], [], []
        for d in batch:
            try:
                with os.scandir(d) as it:
                    for e in it:
                        try:
                            if e.is_dir(follow_symlinks=False):
                                newdirs.append(e.path)
                            elif e.is_file(follow_symlinks=False):
                                s = e.stat(follow_symlinks=False)
                                lines.append(f"{e.path}\t{s.st_size}\t{int(s.st_mtime)}\n")
                                st["bytes"] += s.st_size
                        except OSError as ex:
                            errs.append(f"{e.path}\t{ex}\n")
            except OSError as ex:
                errs.append(f"{d}\t{ex}\n")
        with open(inv_p, "a", encoding="utf-8") as f:
            f.writelines(lines); f.flush(); os.fsync(f.fileno())
        if errs:
            with open(err_p, "a", encoding="utf-8") as f:
                f.writelines(errs)
        st["frontier"] = newdirs + st["frontier"]  # depth-leaning keeps the frontier small
        st["dirs"] += len(batch); st["files"] += len(lines); st["errors"] += len(errs)
        tmp = state_p + ".tmp"
        json.dump(st, open(tmp, "w", encoding="utf-8"))
        os.replace(tmp, state_p)
        chunks += 1
        el = time.time() - st["started"]
        print(json.dumps({"processed_dirs": st["dirs"], "files": st["files"], "gb": round(st["bytes"] / 2**30, 1),
                          "frontier": len(st["frontier"]), "errors": st["errors"], "elapsed_s": int(el)}), flush=True)
        if a.max_chunks and chunks >= a.max_chunks:
            break
    if not st["frontier"]:
        print("DONE", flush=True)


if __name__ == "__main__":
    main()
