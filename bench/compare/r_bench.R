# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.

# The operations of bench/compare.bjo, in R, on the same file, as
# polars_bench.py does them: each 20 times after one to warm up, the median
# and the fastest printed, and a result to compare. Twice: with data.table
# on one thread, and with dplyr and tidyr on the same data as a tibble. The
# results to compare with the other libraries' are printed first. A sum or a
# statistic leaves out the nulls (na.rm = TRUE), as the others do.
#
#     Rscript bench/compare/r_bench.R scratch/compare.csv
#
# R and the packages, without root, from conda-forge by micromamba:
#
#     micromamba create -p scratch/mamba/r -c conda-forge r-base r-data.table r-dplyr r-tidyr

suppressPackageStartupMessages({
  library(data.table)
  library(dplyr)
  library(tidyr)
})

args <- commandArgs(trailingOnly = TRUE)
path <- if (length(args) > 0) args[1] else "scratch/compare.csv"
runs <- 20
setDTthreads(1)

bench <- function(name, f) {
  f()
  times <- numeric(runs)
  for (i in seq_len(runs)) {
    t0 <- Sys.time()
    result <- f()
    times[i] <- as.numeric(Sys.time() - t0, units = "secs")
  }
  cat(sprintf("%-32s %8.2f ms  min %8.2f ms   %s\n", name, median(times) * 1000, min(times) * 1000,
              paste(format(result), collapse = " ")))
}

cat(sprintf("%s, data.table %s on %d thread, dplyr %s, tidyr %s\n", R.version.string,
            packageVersion("data.table"), getDTthreads(), packageVersion("dplyr"), packageVersion("tidyr")))

# data.table
dt <- fread(path)
# Each region, in order, with a rate, and each id with a score.
keys <- sort(unique(dt$region))
rates <- data.table(region = keys, rate = 1.0 + 0.5 * ((seq_along(keys) - 1) %% 4))
scores <- data.table(id = seq_len(nrow(dt)) - 1L, score = as.numeric((seq_len(nrow(dt)) - 1L) %% 7))
two <- dt[, .(id, price, price2 = price)]
long <- melt(two, id.vars = "id", measure.vars = c("price", "price2"))
by_codes <- copy(dt)[, region := factor(region)]
rates_codes <- copy(rates)[, region := factor(region, levels = levels(by_codes$region))]

by_region <- dt[, .(price = sum(price, na.rm = TRUE)), by = region]
cat("kept rows:", nrow(dt[price > 250 & qty > 20]), "\n")
cat(sprintf("groups by string: %d, total %.2f\n", nrow(by_region), sum(by_region$price)))
cat("groups by long:", nrow(dt[, .(price = sum(price, na.rm = TRUE), n = .N), by = qty]), "\n")
cat("join by a string to the keys:", nrow(rates[dt, on = "region", nomatch = NULL]), "\n")
cat("join by a long:", nrow(scores[dt, on = "id", nomatch = NULL]), "\n")
cat("standard deviation:", format(sd(dt$price, na.rm = TRUE), digits = 17), "\n")
cat("median:", median(dt$price, na.rm = TRUE), "\n")
cat("correlation:", format(cor(dt$price, dt$qty, use = "complete.obs"), digits = 17), "\n")
cat("distinct by a string and a long:", nrow(unique(dt, by = c("region", "qty"))), "\n")
cat("pivot wider:", nrow(dcast(long, id ~ variable, value.var = "value")), "\n")
cat("count by a string and a long:", nrow(dt[, .N, keyby = .(region, qty)]), "\n")
cat("crosstab rows:", nrow(table(dt$region, dt$qty)), "\n")

cat("data.table\n")
bench("read csv", function() nrow(fread(path)))
bench("filter, kept rows", function() nrow(dt[price > 250 & qty > 20]))
# A new table with the column, as the others make one; := would change dt.
bench("new column", function() ncol(dt[, c(.SD, list(revenue = price * qty))]))
bench("group by string, sum", function() nrow(dt[, .(price = sum(price, na.rm = TRUE)), by = region]))
bench("group by long, sum and count", function() nrow(dt[, .(price = sum(price, na.rm = TRUE), n = .N), by = qty]))
bench("sort by a number", function() nrow(dt[order(price)]))
bench("sort by a string, then a number", function() nrow(dt[order(region, -price)]))
bench("join by a string to the keys", function() nrow(rates[dt, on = "region", nomatch = NULL]))
bench("join by a long, one to one", function() nrow(scores[dt, on = "id", nomatch = NULL]))
bench("standard deviation", function() sd(dt$price, na.rm = TRUE))
bench("median", function() median(dt$price, na.rm = TRUE))
bench("correlation", function() cor(dt$price, dt$qty, use = "complete.obs"))
bench("summary of the table", function() length(summary(dt)))
bench("categorize a string column", function() length(factor(dt$region)))
bench("group by category, sum", function() nrow(by_codes[, .(price = sum(price, na.rm = TRUE)), keyby = region]))
bench("sort by a category, then a number", function() nrow(by_codes[order(region, -price)]))
bench("count by a category", function() nrow(by_codes[, .N, keyby = region]))
bench("join by a category to the keys", function() nrow(rates_codes[by_codes, on = "region", nomatch = NULL]))
bench("distinct by a string and a long", function() nrow(unique(dt, by = c("region", "qty"))))
bench("distinct rows", function() nrow(unique(dt)))
bench("append, two tables", function() nrow(rbindlist(list(dt, dt))))
bench("pivot longer, two columns",
      function() nrow(melt(two, id.vars = "id", measure.vars = c("price", "price2"))))
bench("pivot wider, two columns", function() nrow(dcast(long, id ~ variable, value.var = "value")))
bench("count by a string and a long", function() nrow(dt[, .N, keyby = .(region, qty)]))
bench("crosstab of a string and a long", function() nrow(table(dt$region, dt$qty)))

# dplyr and tidyr, on the same rows as a tibble. The CSV is read by fread
# above; readr is not part of this comparison.
tb <- as_tibble(as.data.frame(dt))
rates_tb <- as_tibble(as.data.frame(rates))
scores_tb <- as_tibble(as.data.frame(scores))
two_tb <- as_tibble(as.data.frame(two))
long_tb <- pivot_longer(two_tb, c(price, price2), names_to = "name", values_to = "value")
codes_tb <- mutate(tb, region = factor(region))
rates_codes_tb <- mutate(rates_tb, region = factor(region, levels = levels(codes_tb$region)))

cat("dplyr\n")
bench("filter, kept rows", function() nrow(filter(tb, price > 250, qty > 20)))
bench("new column", function() ncol(mutate(tb, revenue = price * qty)))
bench("group by string, sum", function() nrow(summarise(group_by(tb, region), price = sum(price, na.rm = TRUE))))
bench("group by long, sum and count",
      function() nrow(summarise(group_by(tb, qty), price = sum(price, na.rm = TRUE), n = n())))
bench("sort by a number", function() nrow(arrange(tb, price)))
bench("sort by a string, then a number", function() nrow(arrange(tb, region, desc(price))))
bench("join by a string to the keys", function() nrow(inner_join(tb, rates_tb, by = "region")))
bench("join by a long, one to one", function() nrow(inner_join(tb, scores_tb, by = "id")))
bench("summary of the table", function() length(summary(tb)))
bench("categorize a string column", function() nrow(mutate(tb, region = factor(region))))
bench("group by category, sum", function() nrow(summarise(group_by(codes_tb, region), price = sum(price, na.rm = TRUE))))
bench("sort by a category, then a number", function() nrow(arrange(codes_tb, region, desc(price))))
bench("count by a category", function() nrow(count(codes_tb, region)))
bench("join by a category to the keys", function() nrow(inner_join(codes_tb, rates_codes_tb, by = "region")))
bench("distinct by a string and a long", function() nrow(distinct(tb, region, qty, .keep_all = TRUE)))
bench("distinct rows", function() nrow(distinct(tb)))
bench("append, two tables", function() nrow(bind_rows(tb, tb)))
bench("pivot longer, two columns",
      function() nrow(pivot_longer(two_tb, c(price, price2), names_to = "name", values_to = "value")))
bench("pivot wider, two columns",
      function() nrow(pivot_wider(long_tb, names_from = name, values_from = value)))
bench("count by a string and a long", function() nrow(count(tb, region, qty)))
