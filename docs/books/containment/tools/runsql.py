import sys, re, sqlite3

db, sqlfile = sys.argv[1], sys.argv[2]
params = {}
for a in sys.argv[3:]:
    k, v = a.split('=', 1)
    params[k.lstrip(':')] = int(v) if v.lstrip('-').isdigit() else v

text = open(sqlfile, encoding='utf-8').read()
text = '\n'.join(l for l in text.splitlines() if not l.strip().startswith('.'))

con = sqlite3.connect(db)
con.create_function('regexp', 2, lambda p, s: 1 if s is not None and re.search(p, s) else 0)
cur = con.cursor()
for raw in text.split(';'):
    stmt = '\n'.join(l for l in raw.splitlines() if not l.strip().startswith('--')).strip()
    if not stmt:
        continue
    cur.execute(stmt, params)
    if cur.description is None:
        continue
    for row in cur.fetchall():
        print('|'.join('' if c is None else str(c) for c in row))
con.close()
