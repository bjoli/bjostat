# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.

# The operations of bench/compare.bjo, in pandas, on the same file, as
# polars_bench.py does them: each 20 times after one to warm up, the median
# and the fastest printed, and a result to compare. pandas 3 keeps strings
# in pyarrow, whose thread pools are set to one thread here, as Polars runs
# with POLARS_MAX_THREADS=1 and data.table with setDTthreads(1).
#
#     python bench/compare/pandas_bench.py scratch/compare.csv

import statistics
import sys
import time

import pandas as pd
import pyarrow as pa

pa.set_cpu_count(1)
pa.set_io_thread_count(1)

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


print(f"pandas {pd.__version__}, pyarrow {pa.__version__} on {pa.cpu_count()} thread")
df = pd.read_csv(path)

bench("read csv", lambda: len(pd.read_csv(path)))
bench("filter, kept rows", lambda: len(df[(df["price"] > 250.0) & (df["qty"] > 20)]))
bench("new column", lambda: df.assign(revenue=df["price"] * df["qty"]).shape[1])
bench("group by string, sum",
      lambda: df.groupby("region", sort=True)["price"].sum().round(2).tolist())
bench("group by long, sum and count",
      lambda: len(df.groupby("qty")["price"].agg(["sum", "count"])))
bench("sort by a number", lambda: len(df.sort_values("price", kind="stable")))
bench("sort by a string, then a number",
      lambda: len(df.sort_values(["region", "price"], ascending=[True, False], kind="stable")))
rates = pd.DataFrame({"region": ["north", "south", "east", "west"], "rate": [1.0, 1.5, 2.0, 2.5]})
scores = pd.DataFrame({"id": range(len(df)), "score": [float(i % 7) for i in range(len(df))]})
bench("join by a string to 4 rows", lambda: len(df.merge(rates, on="region", how="inner")))
bench("join by a long, one to one", lambda: len(df.merge(scores, on="id", how="inner")))
bench("standard deviation", lambda: df["price"].std())
bench("median", lambda: df["price"].median())
bench("correlation", lambda: df["price"].corr(df["qty"]))
bench("summary of the table", lambda: len(df.describe(include="all")))
two_prices = pd.DataFrame({"id": df["id"], "price": df["price"], "price2": df["price"]})
prices_long = two_prices.melt(id_vars="id", value_vars=["price", "price2"], var_name="name", value_name="value")
by_codes = df.assign(region=df["region"].astype("category"))
rates_codes = rates.assign(region=pd.Categorical(rates["region"], categories=by_codes["region"].cat.categories))
bench("categorize a string column", lambda: len(df["region"].astype("category")))
bench("group by category, sum", lambda: len(by_codes.groupby("region", observed=True)["price"].sum()))
bench("sort by a category, then a number",
      lambda: len(by_codes.sort_values(["region", "price"], ascending=[True, False], kind="stable")))
bench("count by a category", lambda: len(by_codes.groupby("region", observed=True).size()))
bench("join by a category to 4 rows", lambda: len(by_codes.merge(rates_codes, on="region", how="inner")))
bench("distinct by a string and a long", lambda: len(df.drop_duplicates(["region", "qty"])))
bench("distinct rows", lambda: len(df.drop_duplicates()))
bench("append, two tables", lambda: len(pd.concat([df, df], ignore_index=True)))
bench("pivot longer, two columns",
      lambda: len(two_prices.melt(id_vars="id", value_vars=["price", "price2"], var_name="name", value_name="value")))
bench("pivot wider, two columns",
      lambda: len(prices_long.pivot(index="id", columns="name", values="value").reset_index()))
bench("count by a string and a long", lambda: len(df.groupby(["region", "qty"]).size()))
bench("crosstab of a string and a long", lambda: len(pd.crosstab(df["region"], df["qty"])))
