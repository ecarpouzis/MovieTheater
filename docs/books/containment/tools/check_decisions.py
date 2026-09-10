"""Run pass2's coverage contract over every decision file WITHOUT stopping at the first failure.

pass2.py raises SystemExit on the first file that does not cover its shelf, which is right when the sheet
is being expanded for import but useless while a pass is in flight: one stale file from an earlier session
hides whether the twenty written today are sound. This reports every file's verdict and exits non-zero if
any failed.

`python check_decisions.py [S94547 S94548 ...]`   with no arguments, every file.
"""
import os
import runpy
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")

mod = runpy.run_path(os.path.join(HERE, "pass2.py"), run_name="not_main") if False else None

# pass2.py calls main() at import time, so its parse/load_packets are re-implemented by exec'ing the
# source with the trailing main() call stripped.
src = open(os.path.join(HERE, "pass2.py"), encoding="utf-8").read().replace("\nmain()\n", "\n")
ns = {"__name__": "pass2mod", "__file__": os.path.join(HERE, "pass2.py")}
exec(compile(src, "pass2.py", "exec"), ns)                                   # noqa: S102 - our own file

want = set(sys.argv[1:])
files = sorted(f for f in os.listdir(DEC) if f.startswith("S") and f.endswith(".txt")
               and (not want or f[:-4] in want or f in want))
packets = ns["load_packets"]()
bad = 0
for f in files:
    try:
        sid, spans, unknowns, flags, notes = ns["parse"](os.path.join(DEC, f), packets)
        if want:
            print(f"  OK   {f:<14} {len(spans):>3} ranges {len(unknowns):>3} refusals {len(flags):>2} flags")
    except SystemExit as e:
        bad += 1
        print(f"  FAIL {f:<14} {e}")
print(f"{len(files)} files, {bad} failing")
sys.exit(1 if bad else 0)
