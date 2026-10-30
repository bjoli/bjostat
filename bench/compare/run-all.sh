#!/bin/bash
# The whole comparison of plan.org, one run after another, so that no two
# share the machine; a file of output for each run in the directory given
# (default scratch/compare-runs). Needs scratch/compare.csv and
# scratch/compare-wide.csv (make_data.py), Polars and pandas in
# scratch/venv, and R in scratch/mamba/r.
#
#     bash bench/compare/run-all.sh scratch/bench-2026-10-30
cd "$(dirname "$0")/../.."
out=${1:-scratch/compare-runs}
mkdir -p "$out"
for f in compare compare-wide; do
  csv=scratch/$f.csv
  echo "== bjostat all workers $f $(date +%T)"
  COMPARE_CSV=$csv bjo bench bench/compare.bjo --all -N 20 > "$out/bjostat-all-$f.txt" 2>&1
  echo "== bjostat 1 worker $f $(date +%T)"
  BJOSTAT_WORKERS=1 COMPARE_CSV=$csv bjo bench bench/compare.bjo --all -N 20 > "$out/bjostat-1-$f.txt" 2>&1
  echo "== polars 24 $f $(date +%T)"
  scratch/venv/bin/python bench/compare/polars_bench.py $csv > "$out/polars-24-$f.txt" 2>&1
  echo "== polars 1 $f $(date +%T)"
  POLARS_MAX_THREADS=1 scratch/venv/bin/python bench/compare/polars_bench.py $csv > "$out/polars-1-$f.txt" 2>&1
  echo "== pandas $f $(date +%T)"
  scratch/venv/bin/python bench/compare/pandas_bench.py $csv > "$out/pandas-$f.txt" 2>&1
  echo "== R $f $(date +%T)"
  scratch/mamba/r/bin/Rscript bench/compare/r_bench.R $csv > "$out/r-$f.txt" 2>&1
done
echo "== done $(date +%T)"
