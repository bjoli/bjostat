# bjostat/examples/shop-data.py — makes the data of examples/shop.bjo.
#
#     python3 examples/shop-data.py scratch/shop
#
# Writes customers.csv (600 customers in 6 countries) and orders.csv (a year
# of orders, 2025). The seed is fixed, so each run makes the same files. Some
# customers have no orders, and some orders have no discount code: those
# cells are empty.

import csv
import datetime
import os
import random
import sys

out = sys.argv[1] if len(sys.argv) > 1 else "."
os.makedirs(out, exist_ok=True)
rng = random.Random(2025)

countries = [("Norway", 1.3), ("Sweden", 1.0), ("Denmark", 0.8),
             ("Germany", 0.9), ("France", 0.7), ("Spain", 0.6)]
products = [("coffee beans", 14.0), ("grinder", 89.0), ("kettle", 49.0),
            ("mug", 9.5), ("filters", 4.0), ("espresso machine", 420.0)]

customers = []
with open(os.path.join(out, "customers.csv"), "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(["customer", "country", "signed_up"])
    for i in range(600):
        name = f"c{i:04d}"
        country, spend = rng.choice(countries)
        signed = datetime.date(2023, 1, 1) + datetime.timedelta(days=rng.randrange(900))
        customers.append((name, spend, signed))
        w.writerow([name, country, signed.isoformat()])

# A fifth of the customers never order.
buyers = [c for c in customers if rng.random() > 0.2]

with open(os.path.join(out, "orders.csv"), "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(["order", "day", "customer", "product", "quantity", "unit_price", "code"])
    for n in range(30000):
        name, spend, _ = rng.choice(buyers)
        day = datetime.date(2025, 1, 1) + datetime.timedelta(days=rng.randrange(365))
        # More orders before Christmas.
        if day.month < 11 and rng.random() < 0.25:
            day = datetime.date(2025, 12, 1) + datetime.timedelta(days=rng.randrange(24))
        product, price = rng.choice(products)
        quantity = 1 + int(rng.expovariate(1.0 / (3.0 if price < 20 else 0.3)))
        unit_price = round(price * spend * rng.uniform(0.9, 1.1), 2)
        code = rng.choice(["XMAS10", "WELCOME", ""]) if rng.random() < 0.3 else ""
        w.writerow([n + 1, day.isoformat(), name, product, quantity, unit_price, code])
