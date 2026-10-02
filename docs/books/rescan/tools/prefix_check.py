"""New items whose ISSUE NUMBER came from a leading reading-order prefix ("02 Hellboy in Love - Black Eyes").
Collectors number the files of a folder "01 …", "02 …" to set reading order; the parser can read that prefix as
the issue number and drop a one-shot onto its parent run as "#2". Lists every new item whose name starts with
`NN ` and whose IssueNo equals that prefix while the rest of the name carries no issue number of its own.
usage: python prefix_check.py --first-new-id 245531
"""
import argparse, re, sqlite3, sys, os
sys.path.insert(0, os.path.dirname(__file__))
import comicname as cn

ap = argparse.ArgumentParser(); ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--first-new-id", type=int, required=True)
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
for iid, fn, key, issue, coll, sid, name in c.execute("""select i.Id, i.FileName, d.ParsedSeriesKey, d.IssueNo, d.IsCollection, i.SeriesId, s.Name
        from Item i join ComicDetail d on d.ItemId=i.Id left join Series s on s.Id=i.SeriesId
        where i.Id >= ? and i.IsExcluded=0""", (a.first_new_id,)):
    m = re.match(r"^(\d{1,3})[ .\-]+(.+)$", fn)
    if not m or issue is None: continue
    try:
        if float(issue) != float(m.group(1)): continue
    except ValueError: continue
    rest = cn.parse(m.group(2))
    if rest["issue"]: continue          # the remainder names its own number: the prefix is not what was read
    print(f"{iid}\t{fn[:80]}\tkey={key}\t#{issue}\tS{sid} {name}")
