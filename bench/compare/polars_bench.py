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
bench("standard deviation", lambda: df["price"].std())
bench("median", lambda: df["price"].median())
bench("correlation", lambda: df.select(pl.corr("price", "qty")).item())
bench("summary of the table", lambda: df.describe().height)
two_prices = df.select("id", "price", pl.col("price").alias("price2"))
prices_long = two_prices.unpivot(["price", "price2"], index="id", variable_name="name", value_name="value")
with pl.StringCache():
    by_codes = df.with_columns(pl.col("region").cast(pl.Categorical))
    rates_codes = rates.with_columns(pl.col("region").cast(pl.Categorical))
    bench("categorize a string column", lambda: df.with_columns(pl.col("region").cast(pl.Categorical)).height)
    bench("group by category, sum", lambda: by_codes.group_by("region").agg(pl.col("price").sum()).height)
    bench("sort by a category, then a number",
          lambda: by_codes.sort(["region", "price"], descending=[False, True], maintain_order=True).height)
    bench("join by a category to 4 rows",
          lambda: by_codes.join(rates_codes, on="region", how="inner", maintain_order="left").height)
bench("distinct by a string and a long",
      lambda: df.unique(subset=["region", "qty"], keep="first", maintain_order=True).height)
bench("distinct rows", lambda: df.unique(keep="first", maintain_order=True).height)
bench("append, two tables", lambda: pl.concat([df, df], rechunk=True).height)
bench("pivot longer, two columns",
      lambda: two_prices.unpivot(["price", "price2"], index="id", variable_name="name", value_name="value").height)
bench("pivot wider, two columns",
      lambda: prices_long.pivot(on="name", index="id", values="value", maintain_order=True).height)
