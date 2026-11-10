/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Times: what the CSV reader and the column builder ask of dates and
// timestamps in Arrow columns. The cells of such columns are Bjolang, in
// src/table.bjo ("day-cells", "micro-cells").
//
// A column of dates is Arrow's Date32: days since 1970-01-01. A column of
// timestamps holds a number of a unit since 1970-01-01T00:00; one with a time
// zone is a point in time, an instant, and one without is a local date and
// time. A timestamp that this package makes is in microseconds, as Polars
// makes them. Cells give dates as their days and timestamps as microseconds,
// and (bjostat table) makes the values of (std datetime) of them with
// unix-days->date and unix-microseconds->instant.

using Apache.Arrow.Types;

namespace Bjostat;

internal static class TimeKind
{
    public const int None = 0;
    public const int Date = 1;
    public const int DateTime = 2;
    public const int Instant = 3;

    /// <summary>DateOnly.DayNumber of 1970-01-01.</summary>
    public const int EpochDayNumber = 719162;

    /// <summary>The Arrow type of a column of the kind that this package makes.</summary>
    public static IArrowType ArrowType(int kind) => kind switch
    {
        Date => Date32Type.Default,
        DateTime => new TimestampType(TimeUnit.Microsecond, (string?)null),
        _ => new TimestampType(TimeUnit.Microsecond, "UTC"),
    };
}
