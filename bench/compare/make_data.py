# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.

# The CSV files that bench/compare.bjo and the scripts beside this one read,
# 1 000 000 rows each, in two shapes:
#
# - narrow (the default): an id in order, a region of four, a price with two
#   decimals, a quantity from 1 to 40, and a note that two rows of three
#   leave empty. No nulls in the numbers.
# - wide: the same columns, and six more numbers, x1 to x6. The id is in no
#   order; the region is one of 20 000, a few of them common and most rare;
#   the quantity is from 1 to 100; one price and one quantity in twenty are
#   empty, and one x in fifty.
#
#     python3 bench/compare/make_data.py scratch/compare.csv
#     python3 bench/compare/make_data.py scratch/compare-wide.csv wide

import random
import sys

path = sys.argv[1] if len(sys.argv) > 1 else "scratch/compare.csv"
shape = sys.argv[2] if len(sys.argv) > 2 else "narrow"
rows = 1_000_000
rng = random.Random(1)

if shape == "narrow":
    regions = ["north", "south", "east", "west"]
    with open(path, "w") as f:
        f.write("id,region,price,qty,note\n")
        for i in range(rows):
            note = f"item {i}" if i % 3 == 0 else ""
            f.write(f"{i},{rng.choice(regions)},{rng.uniform(1, 500):.2f},{rng.randint(1, 40)},{note}\n")
elif shape == "wide":
    ids = list(range(rows))
    rng.shuffle(ids)

    def maybe(text, every):
        return "" if rng.randrange(every) == 0 else text

    with open(path, "w") as f:
        f.write("id,region,price,qty,note,x1,x2,x3,x4,x5,x6\n")
        for i in range(rows):
            # The cube of a uniform number: region-0 is the most common.
            region = f"region-{int(20_000 * rng.random() ** 3)}"
            price = maybe(f"{rng.uniform(1, 500):.2f}", 20)
            qty = maybe(str(rng.randint(1, 100)), 20)
            note = f"item {i}" if i % 3 == 0 else ""
            xs = ",".join(maybe(f"{rng.gauss(0, 1):.4f}", 50) for _ in range(6))
            f.write(f"{ids[i]},{region},{price},{qty},{note},{xs}\n")
else:
    sys.exit(f"no shape '{shape}': narrow or wide")
