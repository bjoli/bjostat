/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Times: dates and timestamps in Arrow columns.
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

    /// <summary>The ticks of 1970-01-01T00:00.</summary>
    public const long EpochTicks = 621355968000000000L;

    /// <summary>The ticks of a timestamp of the unit.</summary>
    public static long Ticks(long raw, TimeUnit unit) => EpochTicks + unit switch
    {
        TimeUnit.Second => raw * 10_000_000,
        TimeUnit.Millisecond => raw * 10_000,
        TimeUnit.Microsecond => raw * 10,
        _ => FloorDiv(raw, 100),
    };

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        return a % b < 0 ? q - 1 : q;
    }

    /// <summary>The microseconds since 1970 of the ticks, rounded down.</summary>
    public static long Micros(long ticks)
    {
        return FloorDiv(ticks - EpochTicks, 10);
    }

    /// <summary>The Arrow type of a column of the kind that this package makes.</summary>
    public static IArrowType ArrowType(int kind) => kind switch
    {
        Date => Date32Type.Default,
        DateTime => new TimestampType(TimeUnit.Microsecond, (string?)null),
        _ => new TimestampType(TimeUnit.Microsecond, "UTC"),
    };
}

/// <summary>The cells of columns of dates and timestamps, for (bjostat table).</summary>
public static class TimeCells
{
    /// <summary>The days since 1970 of a column of dates, or None for a column of another type.</summary>
    public static BjolangRuntime.Option<Cells<int>> Days(Apache.Arrow.IArrowArray a) =>
        a is Apache.Arrow.Date32Array
            ? BjolangRuntime.Some(new Cells<int>(a))
            : BjolangRuntime.None<Cells<int>>();

    /// <summary>
    /// The microseconds since 1970 of a column of timestamps with a time zone
    /// when <paramref name="zoned"/>, or with none when not; None for a column
    /// of another type. A column of another unit is made one of microseconds
    /// first, rounded down.
    /// </summary>
    public static BjolangRuntime.Option<Cells<long>> Micros(Apache.Arrow.IArrowArray a, bool zoned)
    {
        if (a.Data.DataType is not TimestampType ts || string.IsNullOrEmpty(ts.Timezone) == zoned)
            return BjolangRuntime.None<Cells<long>>();
        if (ts.Unit == TimeUnit.Microsecond) return BjolangRuntime.Some(new Cells<long>(a));
        var data = a.Data;
        var raw = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(data.Buffers[1].Span);
        var micros = new long[data.Offset + data.Length];
        for (int i = data.Offset; i < micros.Length; i++)
            micros[i] = TimeKind.Micros(TimeKind.Ticks(raw[i], ts.Unit));
        var type = new TimestampType(TimeUnit.Microsecond, ts.Timezone);
        var converted = new Apache.Arrow.ArrayData(type, data.Length, data.NullCount, data.Offset,
            new[] { data.Buffers[0], new Apache.Arrow.ArrowBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(micros.AsSpan()).ToArray()) });
        return BjolangRuntime.Some(new Cells<long>(Apache.Arrow.ArrowArrayFactory.BuildArray(converted)));
    }
}
