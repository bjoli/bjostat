# The tables of plan.org from the output of run-all.sh:
#
#     python3 bench/compare/tables.py scratch/bench-2026-10-30
import os, sys
os.chdir(sys.argv[1] if len(sys.argv) > 1 else "scratch/compare-runs")
import re
rx = re.compile(r'^(\S.*?)\s{2,}([\d.]+) (ms|µs)\b')
def parse(path, section=None):
    out, cur = {}, None
    for line in open(path, encoding='utf-8'):
        s = line.rstrip('\n')
        if s.strip() in ('data.table', 'dplyr'):
            cur = s.strip(); continue
        if section and cur != section: continue
        m = rx.match(s)
        if m:
            v = float(m.group(2)) / (1000 if m.group(3) == 'µs' else 1)
            out.setdefault(m.group(1).strip(), v)
    return out
ops = ["read csv", "filter, kept rows", "new column", "group by string, sum", "group by long, sum and count",
       "sort by a number", "sort by a string, then a number", "join by a string to the keys",
       "join by a long, one to one", "standard deviation", "median", "correlation", "summary of the table",
       "categorize a string column", "group by category, sum", "sort by a category, then a number",
       "count by a category", "join by a category to the keys", "distinct by a string and a long",
       "distinct rows", "append, two tables", "pivot longer, two columns", "pivot wider, two columns",
       "count by a string and a long", "crosstab of a string and a long"]
def fmt(v):
    if v is None: return "–"
    return f"{v:.3g}" if v < 100 else f"{v:.0f}"
for f in ["compare", "compare-wide"]:
    cols = [parse(f"bjostat-all-{f}.txt"), parse(f"bjostat-1-{f}.txt"), parse(f"polars-1-{f}.txt"),
            parse(f"pandas-{f}.txt"), parse(f"r-{f}.txt", "data.table"), parse(f"r-{f}.txt", "dplyr"),
            parse(f"polars-24-{f}.txt")]
    for c in cols[:2]:
        c["group by string, sum, group-of!"] = c.get("group by string, sum")
        c["group by string, sum"] = c.get("group by string, sum, string-groups")
    print(f"== {f}")
    print("| Operation | bjostat, 24 workers | bjostat, 1 worker | Polars, 1 | pandas | data.table | dplyr | Polars, 24 |")
    print("|-----+-----+-----+-----+-----+-----+-----+-----|")
    for op in ops:
        print("| " + op + " | " + " | ".join(fmt(c.get(op)) for c in cols) + " |")
    print("group-of!:", fmt(cols[0]["group by string, sum, group-of!"]), fmt(cols[1]["group by string, sum, group-of!"]))
    missing = [op for op in ops if any(c.get(op) is None for c in cols)]
    print("missing somewhere:", missing)

print("== counts")
for f in ["compare", "compare-wide"]:
    cols = [parse(f"bjostat-all-{f}.txt"), parse(f"bjostat-1-{f}.txt"), parse(f"polars-1-{f}.txt"),
            parse(f"pandas-{f}.txt"), parse(f"r-{f}.txt", "data.table"), parse(f"r-{f}.txt", "dplyr"),
            parse(f"polars-24-{f}.txt")]
    for c in cols[:2]:
        c["group by string, sum"] = c.get("group by string, sum, string-groups")
    fastest = slowest = faster24 = 0
    slow_ops = []
    for op in ops:
        one = [c.get(op) for c in [cols[1]] + cols[2:6]]
        vals = [v for v in one if v is not None]
        if one[0] == min(vals): fastest += 1
        if one[0] == max(vals): slowest += 1; slow_ops.append(op)
        if cols[0].get(op) < cols[6].get(op): faster24 += 1
    print(f, "one thread: fastest", fastest, "slowest", slowest, slow_ops, "| all workers faster than Polars 24:", faster24, "of", len(ops))
