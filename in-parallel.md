# `in-parallel`: loops in parts, with partial results

## Built

What exists, and where it differs from the plan below:

- `Partial` is Bjolang's `Combinable`, in `(std combine)`, with `combine` and
  `combine-all`. `(std parallel)`'s `in-parallel` runs the parts and combines
  their results; `in-parts` returns one result for each part. bjostat has no
  `in-parallel` of its own.
- The parts: `(table-rows t)`, cells of each kind, `skip-nulls` and
  `complete-rows` are `Chunkable` (src/table.bjo), as slices, cut at
  multiples of 64 rows. `cells-slice` slices by hand. `#:size n` of
  `in-parallel` is the plan's `#:rows`.
- Numbers are not `Combinable`, as the plan's last open point proposed.
- The collectors: `moments`, `co-moments` and `values` (src/stats.bjo),
  answering `Moments`, `CoMoments` and `Values`, with their finishes.
  `values` keeps the values as an immutable array.
- (bjostat table) and (bjostat stats) re-export `chunk-size`, `chunks`,
  `combine` and `combine-all`, which carry the traits to their importers.
- Tests: tests/parallel.bjo. Bench: bench/parallel.bjo, of a million rows
  on 24 threads: the standard deviation 3.29 ms in one loop, 0.29 ms as
  `moments` in parts.

Not yet: `Max`/`Min`, `PerGroup` and the `-by/partial` collectors, `Table`
and the columns as `Combinable`, `linear-fit`, `group-of!` refusing other
threads, and running on bjostat's workers rather than Bjolang's compute
threads (there are now two pools of as many threads as cores).

## The plan

A program's loop runs on bjostat's workers, in parts of the rows, and the
parts' results are combined in their order. The loop is the one API, written
as now, inside a function of a part. Nothing in Bjolang changes.

```scheme
(def (Tuple revenue orders m)
  (in-parallel t
    (fun (u)
      (columns/panic u (price) (qty "qty" long))
      (loop (:for p* price) (:for q* qty)
            (:when-let (Some p) p*) (:when-let (Some q) q*)
            (:acc (summing (* p (cast double q))))
            (:acc (counting p))
            (:acc (moments p))))))

(moments-standard-deviation m)
```

The parts' loops answer partial results: values that two parts' worth
combine into one, as a sum, a count or the moments of a mean do. The loop
finishes each part as it finishes any loop, so a collector that is to run in
parts is one whose finished value is still partial.

The other way, a `:parallel` clause of the loop (Bjolang's
parallel-clause.md), needs a rewrite in the compiler and accumulators that a
loop hands back unfinished. This one needs neither: the price is that the
function of a part is the program's, and nothing checks what it does.

## The functions

In a new module, (bjostat parallel):

```scheme
(: (-> Table (-> Table %p) (#:rows int) %p) (where (Partial %p)))
(defun (in-parallel t part #:rows 65536) ...)

(: (-> int (-> int int %p) (#:rows int) %p) (where (Partial %p)))
(defun (in-parallel/rows n part #:rows 65536) ...)
```

- `in-parallel` calls `part` with a slice of `t` for each part
  (`table-slice`, no copy). `in-parallel/rows` calls it with the first row
  and the row after the last, for columns read once before:
  `(cells-slice price from to)`.
- Parts of `#:rows` rows, the last shorter. The number of parts comes from
  the rows, not from the cores, so that a result is the same on every
  machine and with every number of workers.
- The parts run on bjostat's workers, as `computing-each`; the calling
  thread takes parts too. A fiber waits for them suspended and plain code
  parks, by colour, as `computed`.
- Fewer rows than two parts: `part` is called once, on the calling thread,
  with all the rows.
- The results are combined in part order with `partial-combine-all`.
- An exception in a part is the call's, as with `computing-each`; the parts
  not started then do not run.
- A part is ordinary code on a worker: it may not suspend (the function is
  `->`, so a body that does is a colour error), and a module-level `def`
  that calls `in-parallel` with a function of its module is refused by
  Bjolang's check of initialisers that wait for threads.

Reading the columns of a slice costs a lookup by name and a check of the
type for each column and part: about a microsecond, against 65 536 rows.

## `Partial`

```scheme
(def/trait (Partial %p)
  ;; a then b, a's rows before b's.
  (: partial-combine (-> %p %p %p))
  ;; The parts in their order.
  (: partial-combine-all (-> (Vec %p) %p)))
```

- Associative, not commutative: parts are combined in their order.
- `partial-combine-all` takes all the parts, so that a table or a column is
  put together once, its parts copied side by side on the workers, and not
  copied again for each part after it. Where that does not matter it is
  `partial-combine` from the left.
- The combine belongs to the *type* of the result. A type has one meaning as
  a partial result: a number adds, a vec appends. A result whose combine is
  something else has a type of its own (`Moments`, `PerGroup`, `Max`).
- bjostat declares the trait, so it implements it for the prelude's types
  too. If other libraries want it, it can move to the prelude later.

### Implementations

| Type                               | Combine                                              | Made by                        |
|------------------------------------|------------------------------------------------------|--------------------------------|
| `int`, `long`, `double`, `float`, `decimal` | `+`                                         | `summing`, `counting`          |
| `(Vec %a)`, `(List %a)`, `(Array %a)` | the parts one after the other                     | `vecing`, `listing`, `arraying` |
| `(Map %k %v)`, `(Partial %v)`      | the union; a key in two parts, its values combined   | `grouping` of a partial collector |
| `(Tuple %a %b)` … 6                | each side                                            | several `:acc`s, `pairing`     |
| `Unit`                             | nothing                                              | a loop of `:do`s only          |
| `Table`                            | the rows one after the other (`table-append`)        | `selecting`                    |
| a column (`AnyColumn`, `(Column %a)`) | the rows one after the other                      | `column-building`, `column-building/option`, the date builders |
| `Moments`                          | Chan, Golub and LeVeque's update                     | `moments`                      |
| `CoMoments`                        | the same, of pairs                                   | `co-moments`                   |
| `(Values %a)`                      | the values one after the other                       | `values`                       |
| `(Max %a)`, `(Min %a)`             | the larger, the smaller; `None` of no rows           | `maximizing`, `minimizing`     |
| `(PerGroup %a)`                    | element by element, by group number                  | `summing-by/partial` and the other `-by` |
| `LinearFit`                        | the two R factors stacked and rotated again         | `linear-fit`                   |

A tuple of partials is partial, so a loop of several accumulators needs
nothing more. `Option` is not partial: whether a part's `None` means "no
rows" or "no answer" is the collector's to say, which `Max` and `Min` do.

## The collectors

A collector that runs in parts answers its accumulator, or a value that
keeps what the accumulator knows. Its finish is a function the program calls
on the combined value.

| Partial collector                 | Answers      | Finished by                                         |
|-----------------------------------|--------------|-----------------------------------------------------|
| `summing`, `counting`             | a number     | itself                                              |
| `moments`                         | `Moments`    | `moments-count`, `moments-mean`, `moments-variance`, `moments-standard-deviation` |
| `co-moments`                      | `CoMoments`  | `co-moments-covariance`, `co-moments-correlation`   |
| `values`                          | `(Values double)` | `values-quantile`, `values-quantiles`, `values-median`, `values-summary` |
| `maximizing`, `minimizing`        | `(Max %a)`, `(Min %a)` | `max-value`, `min-value`: an `Option`     |
| `selecting`                       | `Table`      | itself                                              |
| `column-building`, `column-building/option` | a column | itself                                          |
| `grouping c`, `c` partial         | `(Map %k %p)` | itself, or the finish of `c` on each value         |
| `summing-by/partial` and the other `-by` | `(PerGroup %a)` | `per-group->column groups`               |
| `linear-fit`                      | `LinearFit`  | `linear-fit->model`, as `linear-model` makes        |

`mean`, `variance`, `standard-deviation`, `covariance`, `correlation`,
`median`, `quantile`, `quantiles`, `summarizing` and `linear-model` stay as
they are, for loops on one thread: each is its partial collector and its
finish in one. `moments` and the others are those collectors with the finish
left out: the accumulator types exist already (`Moments`, `CoMoments`,
the values of `quantile`), and only the collector that answers them and the
functions that finish them are new.

A loop on one thread may use the partial collectors too:
`(moments-mean (loop ... (:acc (moments p))))` is `mean`.

## Groups

`group-of!` writes one `GroupIndex`. Two parts that call it on one index
write the same map from two threads, and the groups come out wrong without
an error. So:

- *Groups numbered before the loop* run in parts: `string-groups` and
  `category-groups` number every group before the loop, and the loop only
  reads the index. The `-by` collectors' `/partial` forms answer a
  `(PerGroup %a)`, the array by group number, which parts combine element by
  element; `per-group->column groups` makes the column `groups->table` takes.
- *Groups a loop makes* run in parts as `grouping`: a map in each part's
  accumulator, merged by key in order. The groups of the merged map are in
  the order of their first rows, the parts being in order.
- `group-of!` raises when it is called on a thread other than the one that
  made the index, so that a part that calls it fails at once.

```scheme
(def (Tuple groups ids) (or-panic (string-groups t "region")))
(def (Tuple totals counts)
  (in-parallel/rows (table-row-count t)
    (fun (from to)
      (loop (:for (Tuple g p) (complete-rows (Tuple (cells-slice ids from to)
                                                    (cells-slice price from to))))
            (:acc (summing-by/partial groups (Tuple g p)))
            (:acc (counting-by/partial groups (Tuple g p)))))))
(groups->table groups "region"
               [(Tuple "total" (per-group->column groups totals))
                (Tuple "n" (per-group->column groups counts))])
```

## What a part may do

The function of a part is the program's, and nothing checks it. What it may
rely on, and what is wrong:

- Each part has its own collectors and accumulators: they are made inside the
  function.
- A `:finish` in a part ends that part's loop, not the others: a loop that
  stops at the first row of some kind is not one for parts.
- Values made outside the function are shared by all parts and must not be
  written: a `MutableVec`, a `MutableMap`, a `GroupIndex` (above), a
  `def/mutable` of the module.
- A part's rows are a slice: a row number inside it is from the slice's
  start. `selecting` takes the part's table, so that its rows are the part's.

## Results

The same result as the loop on one thread, but for rounding where a combine
rounds: a sum of doubles is the sum of the parts' sums, and `Moments` of
parts are combined by a formula that rounds otherwise than adding the rows
one by one. With parts by rows and combines in order, a result is the same
whatever the number of workers.

## Work

1. (bjostat parallel): `Partial` and the implementations for numbers, vecs,
   lists, arrays, maps, tuples and `Unit`; `in-parallel/rows` on the workers,
   with the fallback for few rows; `in-parallel` over `table-slice`.
2. `cells-slice` and the slices of the date cells, `NotNull` and
   `RowColumns`, with no copy. They are `Contiguous` where the whole column
   is, so a part's loop is a vector loop where the loop on one thread is.
3. The partial collectors and their finishes: `moments`, `co-moments`,
   `values`, `maximizing`, `minimizing`, the `-by/partial` collectors,
   `PerGroup`. `Partial` for `Table` and the columns, copied on the workers.
4. `group-of!` refuses other threads.
5. `linear-fit` and `LinearFit`, combined by stacking R factors.
6. Tests in tests/parallel.bjo: each partial collector through
   `in-parallel` against the same loop on one thread, exactly for integers,
   to 1e-12 for moments; row counts of 0, 1, rows − 1, rows, rows + 1 and
   3 × rows + 7; `BJOSTAT_WORKERS=1`; an exception in a part; `group-of!` in
   a part raises.
7. bench/compare.bjo: the group by long, the group by category, the standard
   deviation and the correlation through `in-parallel`, against Polars on 24
   threads. bench/latency.bjo with a loop in parts.
8. The Readme and plan.org: the section on parts, "Next" item 1 done.

## Open

- The size of a part. 65 536 rows was measured for bjostat's own
  operations; measure it for loops with few columns and for loops with many.
- Whether `Partial` goes to the prelude, so that other libraries' types and
  runners use it.
- A t-digest as a `Partial`, for quantiles of many rows without keeping
  them all (plan.org, "Next").
- Sums of doubles in lanes (`summing/fast`, Bjolang's
  Docs/Vectorisation-design.org) inside each part; the parts are still added
  in order.
- A number is partial by addition, so a part whose loop answers a number that
  is not a sum, a `folding` of a maximum, is combined wrong without an error.
  `maximizing` answers `Max` for that reason. Making every partial a type of
  its own (`(Sum double)`) would close the hole, at the cost of an unwrap at
  each use.
