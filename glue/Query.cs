/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Query: what (bjostat query) does to whole columns. Take gathers the rows a
// `where` kept from every column of a table; Builder makes the column a
// `create` computes; WithColumn puts it in the table.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using BjoString;

namespace Bjostat;

public static class Query
{
    // ---------------------------------------------------------------------
    // Take

    /// <summary>
    /// The table of the rows at the first <paramref name="count"/> indices of
    /// <paramref name="rows"/>, in that order, in every column.
    /// </summary>
    public static RecordBatch Take(RecordBatch t, int[] rows, int count)
    {
        var picked = rows.AsSpan(0, count);
        var columns = new IArrowArray[t.ColumnCount];
        for (int c = 0; c < columns.Length; c++)
            columns[c] = TakeColumn(t.Column(c), picked);
        return new RecordBatch(t.Schema, columns, count);
    }

    private static IArrowArray TakeColumn(IArrowArray a, ReadOnlySpan<int> rows)
    {
        var data = a.Data;
        var bitmap = TakeValidity(a, rows, out int nulls);
        ArrayData taken = data.DataType switch
        {
            BooleanType => new ArrayData(data.DataType, rows.Length, nulls, 0,
                new[] { bitmap, TakeBits(data.Buffers[1].Span, data.Offset, rows) }),
            StringType or BinaryType => TakeBinary(data, rows, nulls, bitmap),
            FixedWidthType fw when fw.BitWidth % 8 == 0 => new ArrayData(data.DataType, rows.Length, nulls, 0,
                new[] { bitmap, TakeFixed(data.Buffers[1].Span, data.Offset, fw.BitWidth / 8, rows) }),
            _ => TakeBySlices(a, rows).Data,
        };
        return ArrowArrayFactory.BuildArray(taken);
    }

    private static ArrowBuffer TakeValidity(IArrowArray a, ReadOnlySpan<int> rows, out int nulls)
    {
        nulls = 0;
        if (a.NullCount == 0) return ArrowBuffer.Empty;
        var bits = new byte[(rows.Length + 7) / 8];
        for (int i = 0; i < rows.Length; i++)
        {
            if (a.IsValid(rows[i])) bits[i >> 3] |= (byte)(1 << (i & 7));
            else nulls++;
        }
        return nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(bits);
    }

    private static ArrowBuffer TakeBits(ReadOnlySpan<byte> values, int offset, ReadOnlySpan<int> rows)
    {
        var bits = new byte[(rows.Length + 7) / 8];
        for (int i = 0; i < rows.Length; i++)
        {
            int j = offset + rows[i];
            if ((values[j >> 3] & (1 << (j & 7))) != 0) bits[i >> 3] |= (byte)(1 << (i & 7));
        }
        return new ArrowBuffer(bits);
    }

    private static ArrowBuffer TakeFixed(ReadOnlySpan<byte> values, int offset, int width, ReadOnlySpan<int> rows)
    {
        var bytes = new byte[rows.Length * width];
        switch (width)
        {
            case 8: Gather<long>(values, offset, rows, bytes); break;
            case 4: Gather<int>(values, offset, rows, bytes); break;
            case 2: Gather<short>(values, offset, rows, bytes); break;
            case 1: Gather<byte>(values, offset, rows, bytes); break;
            default:
                for (int i = 0; i < rows.Length; i++)
                    values.Slice((offset + rows[i]) * width, width).CopyTo(bytes.AsSpan(i * width));
                break;
        }
        return new ArrowBuffer(bytes);
    }

    private static void Gather<T>(ReadOnlySpan<byte> values, int offset, ReadOnlySpan<int> rows, byte[] into)
        where T : struct
    {
        var from = MemoryMarshal.Cast<byte, T>(values).Slice(offset);
        var to = MemoryMarshal.Cast<byte, T>(into.AsSpan());
        for (int i = 0; i < rows.Length; i++) to[i] = from[rows[i]];
    }

    private static ArrayData TakeBinary(ArrayData data, ReadOnlySpan<int> rows, int nulls, ArrowBuffer bitmap)
    {
        var offsets = MemoryMarshal.Cast<byte, int>(data.Buffers[1].Span).Slice(data.Offset);
        var bytes = data.Buffers[2].Span;
        var newOffsets = new int[rows.Length + 1];
        int total = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            total += offsets[rows[i] + 1] - offsets[rows[i]];
            newOffsets[i + 1] = total;
        }
        var newBytes = new byte[total];
        for (int i = 0; i < rows.Length; i++)
        {
            int from = offsets[rows[i]];
            bytes.Slice(from, offsets[rows[i] + 1] - from).CopyTo(newBytes.AsSpan(newOffsets[i]));
        }
        return new ArrayData(data.DataType, rows.Length, nulls, 0,
            new[] { bitmap, new ArrowBuffer(MemoryMarshal.AsBytes(newOffsets.AsSpan()).ToArray()), new ArrowBuffer(newBytes) });
    }

    // Any other type: each run of rows that follow one another is a slice,
    // and the slices are concatenated. Fast when the runs are long.
    private static IArrowArray TakeBySlices(IArrowArray a, ReadOnlySpan<int> rows)
    {
        var slices = new List<IArrowArray>();
        int i = 0;
        while (i < rows.Length)
        {
            int start = rows[i], n = 1;
            while (i + n < rows.Length && rows[i + n] == start + n) n++;
            slices.Add(ArrowArrayFactory.Slice(a, start, n));
            i += n;
        }
        if (slices.Count == 0) return ArrowArrayFactory.Slice(a, 0, 0);
        return ArrowArrayConcatenator.Concatenate(slices);
    }

    // ---------------------------------------------------------------------
    // A table with one more column

    /// <summary>
    /// The table with the column under the name: in place of the column of
    /// that name, or after the others when there is none.
    /// </summary>
    public static RecordBatch WithColumn(RecordBatch t, string name, IArrowArray column)
    {
        var fields = t.Schema.FieldsList.ToList();
        var columns = Enumerable.Range(0, t.ColumnCount).Select(t.Column).ToList();
        var field = new Field(name, column.Data.DataType, true);
        int at = t.Schema.GetFieldIndex(name);
        if (at < 0)
        {
            fields.Add(field);
            columns.Add(column);
        }
        else
        {
            fields[at] = field;
            columns[at] = column;
        }
        var schema = new Schema.Builder();
        foreach (var f in fields) schema.Field(f);
        return new RecordBatch(schema.Build(), columns, t.Length);
    }
}

/// <summary>
/// A column of T built one value at a time. T is a number, bool or a Bjolang
/// string; it has no constraint in C#, as Cells has none, so that a Bjolang
/// collector over it needs none.
/// </summary>
public sealed class Builder<T>
{
    private T[] _values;
    private byte[]? _valid;
    private int _count;
    private int _nulls;

    // The values are in an array of the shared pool, as in CsvColumn: an
    // array that doubles is copied many times, and each copy over 85 000
    // bytes would be on the large object heap. Build gives it back. An array
    // of strings is cleared then, so that the pool does not keep them.
    public Builder(int capacity)
    {
        CheckType();
        _values = System.Buffers.ArrayPool<T>.Shared.Rent(Math.Max(capacity, 16));
    }

    private static void CheckType()
    {
        if (!(typeof(T) == typeof(double) || typeof(T) == typeof(float) || typeof(T) == typeof(long)
              || typeof(T) == typeof(int) || typeof(T) == typeof(short) || typeof(T) == typeof(byte)
              || typeof(T) == typeof(bool) || typeof(T) == typeof(Utf8String)))
            throw new ArgumentException($"(bjostat query): a column of {typeof(T).Name} cannot be made");
    }

    public int Count => _count;

    public void Add(T v)
    {
        if (_count == _values.Length) Grow();
        _values[_count++] = v;
    }

    public void AddNull()
    {
        if (_count == _values.Length) Grow();
        if (_valid == null)
        {
            _valid = new byte[_values.Length / 8 + 1];
            _valid.AsSpan().Fill(0xFF);
        }
        _valid[_count >> 3] &= (byte)~(1 << (_count & 7));
        _nulls++;
        _count++;
    }

    private void Grow()
    {
        var bigger = System.Buffers.ArrayPool<T>.Shared.Rent(_values.Length * 2);
        _values.AsSpan(0, _count).CopyTo(bigger);
        Release();
        _values = bigger;
        if (_valid != null)
        {
            int old = _valid.Length;
            System.Array.Resize(ref _valid, _values.Length / 8 + 1);
            _valid.AsSpan(old).Fill(0xFF);
        }
    }

    public IArrowArray Build()
    {
        var bitmap = _valid == null ? ArrowBuffer.Empty : new ArrowBuffer(_valid.AsSpan(0, (_count + 7) / 8).ToArray());
        ArrayData data;
        if (typeof(T) == typeof(Utf8String))
        {
            // T is Utf8String here, so the array is a Utf8String[].
            var strings = Unsafe.As<T[], Utf8String[]>(ref _values).AsSpan(0, _count);
            var offsets = new int[_count + 1];
            int total = 0;
            for (int i = 0; i < strings.Length; i++)
            {
                total += strings[i].AsSpan().Length;
                offsets[i + 1] = total;
            }
            var bytes = new byte[total];
            for (int i = 0; i < strings.Length; i++) strings[i].AsSpan().CopyTo(bytes.AsSpan(offsets[i]));
            data = new ArrayData(StringType.Default, _count, _nulls, 0,
                new[] { bitmap, new ArrowBuffer(MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray()), new ArrowBuffer(bytes) });
        }
        else if (typeof(T) == typeof(bool))
        {
            var bools = Unsafe.As<T[], bool[]>(ref _values).AsSpan(0, _count);
            var bits = new byte[(_count + 7) / 8];
            for (int i = 0; i < bools.Length; i++)
                if (bools[i]) bits[i >> 3] |= (byte)(1 << (i & 7));
            data = new ArrayData(BooleanType.Default, _count, _nulls, 0, new[] { bitmap, new ArrowBuffer(bits) });
        }
        else
        {
            // A number: its bytes, as they are in the array.
            var bytes = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(_values)),
                _count * Unsafe.SizeOf<T>()).ToArray();
            data = new ArrayData(ArrowType(), _count, _nulls, 0, new[] { bitmap, new ArrowBuffer(bytes) });
        }
        Release();
        _values = System.Array.Empty<T>();
        _count = 0;
        return ArrowArrayFactory.BuildArray(data);
    }

    private void Release() =>
        System.Buffers.ArrayPool<T>.Shared.Return(_values, clearArray: typeof(T) == typeof(Utf8String));

    private static IArrowType ArrowType() =>
        typeof(T) == typeof(double) ? DoubleType.Default
        : typeof(T) == typeof(float) ? FloatType.Default
        : typeof(T) == typeof(long) ? Int64Type.Default
        : typeof(T) == typeof(int) ? Int32Type.Default
        : typeof(T) == typeof(short) ? Int16Type.Default
        : UInt8Type.Default;
}

public static class BuilderOps
{
    public static Builder<T> Make<T>(int capacity) => new(capacity);

    public static Builder<T> Add<T>(Builder<T> b, T v)
    {
        b.Add(v);
        return b;
    }

    public static Builder<T> AddOption<T>(Builder<T> b, BjolangRuntime.Option<T> v)
    {
        if (v.IsSome) b.Add(v.Value);
        else b.AddNull();
        return b;
    }

    public static Builder<T> AddNull<T>(Builder<T> b)
    {
        b.AddNull();
        return b;
    }

    public static IArrowArray Build<T>(Builder<T> b) => b.Build();
}

/// <summary>The numbers of the rows a loop kept, in the order it kept them.</summary>
public sealed class Selection
{
    private int[] _rows = new int[1024];
    private int _count;

    public static Selection Make() => new();

    public static Selection Add(Selection s, int row)
    {
        if (s._count == s._rows.Length) System.Array.Resize(ref s._rows, s._rows.Length * 2);
        s._rows[s._count++] = row;
        return s;
    }

    /// <summary>The table of those rows of <paramref name="t"/>.</summary>
    public static RecordBatch Take(Selection s, RecordBatch t) => Query.Take(t, s._rows, s._count);
}
