# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.

# The CSV file that bench/compare.bjo and bench/compare/polars_bench.py both
# read: 1 000 000 rows of an id, a region of four, a price with two decimals,
# a quantity from 1 to 40, and a note that two rows of three leave empty.
#
#     python3 bench/compare/make_data.py scratch/compare.csv

import random
import sys

path = sys.argv[1] if len(sys.argv) > 1 else "scratch/compare.csv"
rng = random.Random(1)
regions = ["north", "south", "east", "west"]

with open(path, "w") as f:
    f.write("id,region,price,qty,note\n")
    for i in range(1_000_000):
        note = f"item {i}" if i % 3 == 0 else ""
        f.write(f"{i},{rng.choice(regions)},{rng.uniform(1, 500):.2f},{rng.randint(1, 40)},{note}\n")
