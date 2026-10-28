# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.

# The operations of bench/compare.bjo, in Polars, on the same file. Each
# runs eagerly, so that its result is made, 20 times after one to warm up;
# the median and the fastest are printed, and a result to compare.
#
#     python bench/compare/polars_bench.py scratch/compare.csv
#     POLARS_MAX_THREADS=1 python bench/compare/polars_bench.py scratch/compare.csv

import statistics
import sys
import time

import polars as pl

path = sys.argv[1] if len(sys.argv) > 1 else "scratch/compare.csv"
runs = 20


def bench(name, f):
    f()
    times = []
    for _ in range(runs):
        t0 = time.perf_counter()
        result = f()
        times.append(time.perf_counter() - t0)
    print(f"{name:32} {statistics.median(times) * 1000:8.2f} ms  min {min(times) * 1000:8.2f} ms   {result}")


print(f"polars {pl.__version__}, {pl.thread_pool_size()} threads")
df = pl.read_csv(path)

bench("read csv", lambda: pl.read_csv(path).height)
bench("filter, kept rows", lambda: df.filter((pl.col("price") > 250.0) & (pl.col("qty") > 20)).height)
bench("new column", lambda: df.with_columns((pl.col("price") * pl.col("qty")).alias("revenue")).width)
bench("group by string, sum",
      lambda: df.group_by("region").agg(pl.col("price").sum()).sort("region")["price"].round(2).to_list())
bench("group by long, sum and count",
      lambda: df.group_by("qty").agg(pl.col("price").sum(), pl.len()).height)
bench("sort by a number", lambda: df.sort("price", maintain_order=True).height)
bench("sort by a string, then a number",
      lambda: df.sort(["region", "price"], descending=[False, True], maintain_order=True).height)
rates = pl.DataFrame({"region": ["north", "south", "east", "west"], "rate": [1.0, 1.5, 2.0, 2.5]})
scores = pl.DataFrame({"id": list(range(df.height)), "score": [float(i % 7) for i in range(df.height)]})
bench("join by a string to 4 rows", lambda: df.join(rates, on="region", how="inner", maintain_order="left").height)
bench("join by a long, one to one", lambda: df.join(scores, on="id", how="inner", maintain_order="left").height)
