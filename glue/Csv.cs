/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Csv: the cells of a CSV file into Arrow columns, for (bjostat csv).
//
// (text csv) gives each field as a Bjolang string, which is UTF-8. A number
// is parsed from those bytes and a string is kept as those bytes, so no
// cell becomes a UTF-16 string. A field that does not parse answers false,
// not an exception, because a guess tries several types for each field.
//
// A column keeps its values in plain arrays that double as they fill, and
// makes Arrow buffers of them once, at the end. Arrow's builders append a
// validity bit for each value; here the bitmap is made only at the first
// null, so a column without nulls has none. The arrays come from the shared
// pools; see Rent.
//
// On a file of 1 000 000 rows and five columns (bench/csv.bjo), Arrow's
// builders took 256 ms, these arrays 238 ms, and these arrays rented from
// the pools 175 ms, against 95 ms for (text csv) reading the rows alone. The
// arrays that were not rented caused five collections of generation 2 in
// each read; the pools cause none.

using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using BjoString;

namespace Bjostat;

/// <summary>
/// One column of a CSV file as it is read. An empty field is a null, in a
/// column of any type.
/// </summary>
public sealed class CsvColumn
{
    // The kinds. Long, Double and String are also the order of a guess:
    // each holds every value that the one before it holds.
    public const int Empty = -1;
    public const int Long = 0;
    public const int Double = 1;
    public const int String = 2;
    public const int Int = 3;
    public const int Float = 4;
    // ISO 8601: a date, a date and time with no zone, and one with a zone
    // ('Z' or an offset), which is an instant. See Times.cs.
    public const int Date = 5;
    public const int DateTime = 6;
    public const int Instant = 7;

    private int _kind;
    private int _count;

    // One of these holds the values, by the kind. A string column holds its
    // bytes end to end in _bytes, and where each ends in _ends.
    private int[]? _ints;
    private long[]? _longs;
    private float[]? _floats;
    private double[]? _doubles;
    private byte[]? _bytes;
    private int _byteCount;
    private int[]? _ends;

    // A bit for each value, 1 when it is valid. Null until the first null.
    private byte[]? _valid;
    private int _nulls;

    private const int FirstCapacity = 1024;

    public CsvColumn(int kind)
    {
        _kind = kind;
        switch (kind)
        {
            case Int:
            case Date: _ints = Rent<int>(FirstCapacity); break;
            case Long:
            case DateTime:
            case Instant: _longs = Rent<long>(FirstCapacity); break;
            case Float: _floats = Rent<float>(FirstCapacity); break;
            case Double: _doubles = Rent<double>(FirstCapacity); break;
            case String:
                _bytes = Rent<byte>(FirstCapacity * 8);
                _ends = Rent<int>(FirstCapacity);
                break;
            default: throw new ArgumentException($"(bjostat csv): no column kind {kind}");
        }
    }

    public int Kind => _kind;

    /// <summary>Appends the field. False when it is not a value of the column's type.</summary>
    public bool Add(Utf8String field)
    {
        var s = field.AsSpan();
        if (s.IsEmpty)
        {
            AddNull();
            return true;
        }
        switch (_kind)
        {
            case Int:
                if (!TryInt(s, out int i)) return false;
                Put(ref _ints!, i);
                break;
            case Long:
                if (!TryLong(s, out long l)) return false;
                Put(ref _longs!, l);
                break;
            case Float:
                if (!ParseDouble(s, out double f)) return false;
                Put(ref _floats!, (float)f);
                break;
            case Double:
                if (!ParseDouble(s, out double d)) return false;
                Put(ref _doubles!, d);
                break;
            case Date:
                if (!TryDate(s, out int days)) return false;
                Put(ref _ints!, days);
                break;
            case DateTime:
            case Instant:
                if (!TryTimestamp(s, out long micros, out bool zoned) || zoned != (_kind == Instant)) return false;
                Put(ref _longs!, micros);
                break;
            default:
                PutBytes(s);
                break;
        }
        if (_valid != null) SetValid(_count);
        _count++;
        return true;
    }

    private void AddNull()
    {
        // The value under a null is 0, or no bytes.
        switch (_kind)
        {
            case Int:
            case Date: Put(ref _ints!, 0); break;
            case Long:
            case DateTime:
            case Instant: Put(ref _longs!, 0L); break;
            case Float: Put(ref _floats!, 0f); break;
            case Double: Put(ref _doubles!, 0.0); break;
            default: PutBytes(default); break;
        }
        if (_valid == null)
        {
            // Every value before the first null is valid.
            _valid = Rent<byte>(Math.Max(FirstCapacity, _count) / 8 + 1);
            _valid.AsSpan().Clear();
            _valid.AsSpan(0, _count / 8).Fill(0xFF);
            for (int i = _count & ~7; i < _count; i++) SetValid(i);
        }
        else if (_count / 8 >= _valid.Length)
        {
            GrowBitmap();
        }
        _nulls++;
        _count++;
    }

    private void SetValid(int i)
    {
        if (i / 8 >= _valid!.Length) GrowBitmap();
        _valid[i / 8] |= (byte)(1 << (i % 8));
    }

    private void Put<T>(ref T[] values, T v)
    {
        if (_count == values.Length) Grow(ref values, values.Length * 2, _count);
        values[_count] = v;
    }

    private void PutBytes(ReadOnlySpan<byte> s)
    {
        if (_byteCount + s.Length > _bytes!.Length)
            Grow(ref _bytes, Math.Max(_bytes.Length * 2, _byteCount + s.Length), _byteCount);
        s.CopyTo(_bytes.AsSpan(_byteCount));
        _byteCount += s.Length;
        Put(ref _ends!, _byteCount);
    }

    /// <summary>
    /// Makes a column of longs a column of doubles, with the values read so
    /// far as doubles. False for a column of another kind. A guess of long
    /// that a later field breaks with a decimal is widened so.
    /// </summary>
    public bool WidenToDouble()
    {
        if (_kind != Long) return false;
        var doubles = Rent<double>(_longs!.Length);
        for (int i = 0; i < _count; i++) doubles[i] = _longs[i];
        ArrayPool<long>.Shared.Return(_longs);
        _longs = null;
        _doubles = doubles;
        _kind = Double;
        return true;
    }

    public IArrowArray Build()
    {
        var bitmap = _valid == null
            ? ArrowBuffer.Empty
            : new ArrowBuffer(_valid.AsSpan(0, (_count + 7) / 8).ToArray());

        ArrayData data = _kind switch
        {
            Int => Primitive(Int32Type.Default, _ints!, bitmap),
            Long => Primitive(Int64Type.Default, _longs!, bitmap),
            Float => Primitive(FloatType.Default, _floats!, bitmap),
            Double => Primitive(DoubleType.Default, _doubles!, bitmap),
            Date => Primitive(Date32Type.Default, _ints!, bitmap),
            DateTime => Primitive(TimeKind.ArrowType(TimeKind.DateTime), _longs!, bitmap),
            Instant => Primitive(TimeKind.ArrowType(TimeKind.Instant), _longs!, bitmap),
            _ => Strings(bitmap),
        };
        Release();
        return ArrowArrayFactory.BuildArray(data);
    }

    // ---------------------------------------------------------------------
    // The arrays that grow are rented from the shared pools, and each one a
    // larger one replaces is given back. An array of more than 85 000 bytes
    // is on the large object heap, and allocating many of them causes
    // collections of generation 2; a pool keeps them for the next column and
    // the next read. Only the buffers of the built column are new.

    private static T[] Rent<T>(int n) => ArrayPool<T>.Shared.Rent(n);

    private static void Grow<T>(ref T[] a, int n, int used)
    {
        var bigger = ArrayPool<T>.Shared.Rent(n);
        a.AsSpan(0, used).CopyTo(bigger);
        ArrayPool<T>.Shared.Return(a);
        a = bigger;
    }

    private void GrowBitmap()
    {
        int old = _valid!.Length;
        Grow(ref _valid, old * 2, old);
        _valid.AsSpan(old).Clear();
    }

    private void Release()
    {
        if (_ints != null) ArrayPool<int>.Shared.Return(_ints);
        if (_longs != null) ArrayPool<long>.Shared.Return(_longs);
        if (_floats != null) ArrayPool<float>.Shared.Return(_floats);
        if (_doubles != null) ArrayPool<double>.Shared.Return(_doubles);
        if (_bytes != null) ArrayPool<byte>.Shared.Return(_bytes);
        if (_ends != null) ArrayPool<int>.Shared.Return(_ends);
        if (_valid != null) ArrayPool<byte>.Shared.Return(_valid);
        _ints = null; _longs = null; _floats = null; _doubles = null;
        _bytes = null; _ends = null; _valid = null;
    }

    private ArrayData Primitive<T>(IArrowType type, T[] values, ArrowBuffer bitmap) where T : struct
    {
        var bytes = MemoryMarshal.AsBytes(values.AsSpan(0, _count)).ToArray();
        return new ArrayData(type, _count, _nulls, 0, new[] { bitmap, new ArrowBuffer(bytes) });
    }

    private ArrayData Strings(ArrowBuffer bitmap)
    {
        // Arrow's offsets are where each string starts, and then the end.
        var offsets = new int[_count + 1];
        _ends.AsSpan(0, _count).CopyTo(offsets.AsSpan(1));
        var offsetBytes = MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray();
        var bytes = _bytes.AsSpan(0, _byteCount).ToArray();
        return new ArrayData(StringType.Default, _count, _nulls, 0,
            new[] { bitmap, new ArrowBuffer(offsetBytes), new ArrowBuffer(bytes) });
    }

    // ---------------------------------------------------------------------
    // Parsing. All of the field has to be the number: no white space, no
    // thousands separators, the decimal point a '.'.

    private static bool TryInt(ReadOnlySpan<byte> s, out int value) =>
        Utf8Parser.TryParse(s, out value, out int used) && used == s.Length;

    private static bool TryLong(ReadOnlySpan<byte> s, out long value) =>
        Utf8Parser.TryParse(s, out value, out int used) && used == s.Length;

    private static bool TryDouble(ReadOnlySpan<byte> s, out double value) =>
        Utf8Parser.TryParse(s, out value, out int used) && used == s.Length;

    // A column of doubles parses through Utf8Number, which tries the
    // Eisel-Lemire parser of BjoString first and is about twice as fast as
    // Utf8Parser on decimals. It throws on text that is not a number, and in
    // a column of doubles that text ends the read, so it is rare.
    private static bool ParseDouble(ReadOnlySpan<byte> s, out double value)
    {
        try
        {
            value = Utf8Number.ParseDouble(s);
            return true;
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
    }

    /// <summary>The narrowest kind that holds the field, or Empty.</summary>
    public static int Guess(Utf8String field)
    {
        var s = field.AsSpan();
        if (s.IsEmpty) return Empty;
        if (TryLong(s, out _)) return Long;
        if (TryDouble(s, out _)) return Double;
        if (TryDate(s, out _)) return Date;
        if (TryTimestamp(s, out _, out bool zoned)) return zoned ? Instant : DateTime;
        return String;
    }

    /// <summary>
    /// The narrowest kind that holds the values of two kinds: longs and
    /// doubles are doubles, dates and dates with times are dates with times,
    /// and other kinds together are strings.
    /// </summary>
    public static int Join(int a, int b)
    {
        if (a == Empty) return b;
        if (b == Empty || a == b) return a;
        if ((a == Long && b == Double) || (a == Double && b == Long)) return Double;
        if ((a == Date && b == DateTime) || (a == DateTime && b == Date)) return DateTime;
        return String;
    }

    // ---------------------------------------------------------------------
    // Dates and times, ISO 8601: 2026-10-28 is a date; 2026-10-28T14:05,
    // with seconds and a fraction if any, and 'T' or ' ' between, is a date
    // and time; one that ends in 'Z', +02:00, -0530 or +02 is an instant. A
    // date alone is a date and time at midnight where a column has both.

    private static int Digits(ReadOnlySpan<byte> s, int at, int n)
    {
        int v = 0;
        for (int i = at; i < at + n; i++)
        {
            int d = s[i] - '0';
            if ((uint)d > 9) return -1;
            v = v * 10 + d;
        }
        return v;
    }

    private static bool TryDate(ReadOnlySpan<byte> s, out int days)
    {
        days = 0;
        if (s.Length != 10 || s[4] != '-' || s[7] != '-') return false;
        int y = Digits(s, 0, 4), m = Digits(s, 5, 2), d = Digits(s, 8, 2);
        if (y < 1 || m < 1 || m > 12 || d < 1 || d > System.DateTime.DaysInMonth(y, m)) return false;
        days = new DateOnly(y, m, d).DayNumber - TimeKind.EpochDayNumber;
        return true;
    }

    private static bool TryTimestamp(ReadOnlySpan<byte> s, out long micros, out bool zoned)
    {
        micros = 0;
        zoned = false;
        if (s.Length == 10)
        {
            if (!TryDate(s, out int dateOnly)) return false;
            micros = dateOnly * 86_400_000_000L;
            return true;
        }
        if (s.Length < 16 || (s[10] != 'T' && s[10] != ' ') || s[13] != ':') return false;
        if (!TryDate(s[..10], out int days)) return false;
        int h = Digits(s, 11, 2), min = Digits(s, 14, 2);
        if (h < 0 || h > 23 || min < 0 || min > 59) return false;
        long ticks = h * TimeSpan.TicksPerHour + min * TimeSpan.TicksPerMinute;
        int at = 16;
        if (at < s.Length && s[at] == ':')
        {
            if (at + 3 > s.Length) return false;
            int sec = Digits(s, at + 1, 2);
            if (sec < 0 || sec > 59) return false;
            ticks += sec * TimeSpan.TicksPerSecond;
            at += 3;
            if (at < s.Length && s[at] == '.')
            {
                int start = ++at;
                long fraction = 0, scale = TimeSpan.TicksPerSecond;
                while (at < s.Length && (uint)(s[at] - '0') <= 9)
                {
                    if (scale > 1)
                    {
                        scale /= 10;
                        fraction += (s[at] - '0') * scale;
                    }
                    at++;
                }
                if (at == start) return false;
                ticks += fraction;
            }
        }
        if (at < s.Length)
        {
            int offset;
            if (s[at] == 'Z' && at + 1 == s.Length) offset = 0;
            else if (s[at] == '+' || s[at] == '-')
            {
                var z = s[(at + 1)..];
                int oh, om = 0;
                if (z.Length == 2) oh = Digits(z, 0, 2);
                else if (z.Length == 4) { oh = Digits(z, 0, 2); om = Digits(z, 2, 2); }
                else if (z.Length == 5 && z[2] == ':') { oh = Digits(z, 0, 2); om = Digits(z, 3, 2); }
                else return false;
                if (oh < 0 || oh > 23 || om < 0 || om > 59) return false;
                offset = (oh * 60 + om) * (s[at] == '-' ? -1 : 1);
            }
            else return false;
            zoned = true;
            ticks -= offset * TimeSpan.TicksPerMinute;
        }
        micros = days * 86_400_000_000L + (ticks >= 0 ? ticks / 10 : -((-ticks + 9) / 10));
        return true;
    }

    // Static forms, for Bjolang's import/extern.

    public static CsvColumn Make(int kind) => new(kind);

    public static bool AddTo(CsvColumn c, Utf8String field) => c.Add(field);

    public static bool Widen(CsvColumn c) => c.WidenToDouble();

    public static int KindOf(CsvColumn c) => c.Kind;

    public static int JoinKinds(int a, int b) => Join(a, b);

    public static IArrowArray BuildOf(CsvColumn c) => c.Build();
}
