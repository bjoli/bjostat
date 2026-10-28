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
        CheckRows(picked, t.Length);
        var columns = new IArrowArray[t.ColumnCount];
        for (int c = 0; c < columns.Length; c++)
            columns[c] = TakeColumn(t.Column(c), picked);
        return new RecordBatch(t.Schema, columns, count);
    }

    // Every row is checked once, here, so that the loops below read through
    // refs with no test of their own. With the lengths of the buffers checked
    // against the column's length where each is read, no read is outside.
    private static void CheckRows(ReadOnlySpan<int> rows, int length)
    {
        for (int i = 0; i < rows.Length; i++)
            if ((uint)rows[i] >= (uint)length)
                throw new ArgumentOutOfRangeException(nameof(rows), $"(bjostat query): row {rows[i]} of a table of {length}");
    }

    private static void CheckBuffer(ReadOnlySpan<byte> buffer, long needed)
    {
        if (buffer.Length < needed)
            throw new InvalidDataException("(bjostat query): a column's buffer is shorter than its length says");
    }

    private static IArrowArray TakeColumn(IArrowArray a, ReadOnlySpan<int> rows)
    {
        var data = a.Data;
        var bitmap = TakeValidity(data, rows, out int nulls);
        ArrayData taken = data.DataType switch
        {
            BooleanType => new ArrayData(data.DataType, rows.Length, nulls, 0,
                new[] { bitmap, TakeBits(data.Buffers[1].Span, data.Offset, data.Length, rows) }),
            StringType or BinaryType => TakeBinary(data, rows, nulls, bitmap),
            FixedWidthType fw when fw.BitWidth % 8 == 0 => new ArrayData(data.DataType, rows.Length, nulls, 0,
                new[] { bitmap, TakeFixed(data.Buffers[1].Span, data.Offset, data.Length, fw.BitWidth / 8, rows) }),
            _ => TakeBySlices(a, rows).Data,
        };
        return ArrowArrayFactory.BuildArray(taken);
    }

    // The validity bits of the rows, read from the bitmap itself rather than
    // through IsValid, which is an interface call for each row.
    private static ArrowBuffer TakeValidity(ArrayData data, ReadOnlySpan<int> rows, out int nulls)
    {
        nulls = 0;
        if (data.NullCount == 0 || data.Buffers[0].IsEmpty) return ArrowBuffer.Empty;
        var valid = data.Buffers[0].Span;
        int offset = data.Offset;
        CheckBuffer(valid, ((long)offset + data.Length + 7) / 8);
        var bits = new byte[(rows.Length + 7) / 8];
        ref byte from = ref MemoryMarshal.GetReference(valid);
        for (int i = 0; i < rows.Length; i++)
        {
            int j = offset + rows[i];
            if ((Unsafe.Add(ref from, j >> 3) & (1 << (j & 7))) != 0) bits[i >> 3] |= (byte)(1 << (i & 7));
            else nulls++;
        }
        return nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(bits);
    }

    private static ArrowBuffer TakeBits(ReadOnlySpan<byte> values, int offset, int length, ReadOnlySpan<int> rows)
    {
        CheckBuffer(values, ((long)offset + length + 7) / 8);
        var bits = new byte[(rows.Length + 7) / 8];
        ref byte from = ref MemoryMarshal.GetReference(values);
        for (int i = 0; i < rows.Length; i++)
        {
            int j = offset + rows[i];
            if ((Unsafe.Add(ref from, j >> 3) & (1 << (j & 7))) != 0) bits[i >> 3] |= (byte)(1 << (i & 7));
        }
        return new ArrowBuffer(bits);
    }

    private static ArrowBuffer TakeFixed(ReadOnlySpan<byte> values, int offset, int length, int width, ReadOnlySpan<int> rows)
    {
        CheckBuffer(values, ((long)offset + length) * width);
        // Every byte is written below, so the array need not be zeroed first.
        var bytes = GC.AllocateUninitializedArray<byte>(rows.Length * width);
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
        ref T from = ref Unsafe.Add(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(values)), offset);
        ref T to = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(into));
        for (int i = 0; i < rows.Length; i++)
            Unsafe.Add(ref to, i) = Unsafe.ReadUnaligned<T>(ref Unsafe.As<T, byte>(ref Unsafe.Add(ref from, rows[i])));
    }

    // The new offsets first, which give the size of the bytes, and then each
    // string's bytes, copied with no span made for it.
    private static ArrayData TakeBinary(ArrayData data, ReadOnlySpan<int> rows, int nulls, ArrowBuffer bitmap)
    {
        var offsetBytes = data.Buffers[1].Span;
        CheckBuffer(offsetBytes, ((long)data.Offset + data.Length + 1) * sizeof(int));
        ref int offsets = ref Unsafe.Add(ref Unsafe.As<byte, int>(ref MemoryMarshal.GetReference(offsetBytes)), data.Offset);
        var bytes = data.Buffers[2].Span;
        CheckBuffer(bytes, Unsafe.Add(ref offsets, data.Length));

        var newOffsetBytes = GC.AllocateUninitializedArray<byte>((rows.Length + 1) * sizeof(int));
        ref int newOffsets = ref Unsafe.As<byte, int>(ref MemoryMarshal.GetArrayDataReference(newOffsetBytes));
        int total = 0;
        newOffsets = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            int r = rows[i];
            total += Unsafe.Add(ref offsets, r + 1) - Unsafe.Add(ref offsets, r);
            Unsafe.Add(ref newOffsets, i + 1) = total;
        }

        var newBytes = GC.AllocateUninitializedArray<byte>(total);
        ref byte from = ref MemoryMarshal.GetReference(bytes);
        ref byte to = ref MemoryMarshal.GetArrayDataReference(newBytes);
        for (int i = 0; i < rows.Length; i++)
        {
            int r = rows[i];
            int start = Unsafe.Add(ref offsets, r);
            Unsafe.CopyBlockUnaligned(
                ref Unsafe.Add(ref to, Unsafe.Add(ref newOffsets, i)),
                ref Unsafe.Add(ref from, start),
                (uint)(Unsafe.Add(ref offsets, r + 1) - start));
        }
        return new ArrayData(data.DataType, rows.Length, nulls, 0,
            new[] { bitmap, new ArrowBuffer(newOffsetBytes), new ArrowBuffer(newBytes) });
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

    internal static IArrowType ArrowType() =>
        typeof(T) == typeof(double) ? DoubleType.Default
        : typeof(T) == typeof(float) ? FloatType.Default
        : typeof(T) == typeof(long) ? Int64Type.Default
        : typeof(T) == typeof(int) ? Int32Type.Default
        : typeof(T) == typeof(short) ? Int16Type.Default
        : UInt8Type.Default;
}

/// <summary>
/// A column of T whose number of rows is known before its first value, as
/// a new column of a table's is.
/// </summary>
/// <remarks>
/// <para>
/// A column of numbers is written into the buffer Arrow is then given: one
/// array of the exact size, not zeroed first, and no copy when it is built.
/// The growing <see cref="Builder{T}"/> copies as it grows and once more at the
/// end, which moved six times the column's bytes for a column of a million
/// doubles. A column of strings or bools goes to a Builder made as large as
/// the column, which never grows.
/// </para>
/// <para>
/// Fewer values than rows are allowed, as a <c>:when</c> may skip some: the
/// part written is then copied out. Under a null the value is written as 0, so
/// that nothing of what the array held before can be read in the column.
/// </para>
/// </remarks>
public sealed class SizedBuilder<T>
{
    private readonly int _length;
    private readonly byte[]? _bytes;
    private readonly Builder<T>? _other;
    private byte[]? _valid;
    private int _count;
    private int _nulls;

    private static bool IsNumber =>
        typeof(T) == typeof(double) || typeof(T) == typeof(float) || typeof(T) == typeof(long)
        || typeof(T) == typeof(int) || typeof(T) == typeof(short) || typeof(T) == typeof(byte);

    public SizedBuilder(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        _length = length;
        if (IsNumber) _bytes = GC.AllocateUninitializedArray<byte>(length * Unsafe.SizeOf<T>());
        else _other = new Builder<T>(length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T v)
    {
        if (_other != null)
        {
            _other.Add(v);
            return;
        }
        if ((uint)_count >= (uint)_length) TooMany();
        Unsafe.WriteUnaligned(
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_bytes!), (nint)_count * Unsafe.SizeOf<T>()), v);
        _count++;
    }

    public void AddNull()
    {
        if (_other != null)
        {
            _other.AddNull();
            return;
        }
        if ((uint)_count >= (uint)_length) TooMany();
        if (_valid == null)
        {
            _valid = new byte[(_length + 7) / 8];
            _valid.AsSpan().Fill(0xFF);
        }
        _valid[_count >> 3] &= (byte)~(1 << (_count & 7));
        Unsafe.WriteUnaligned(
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_bytes!), (nint)_count * Unsafe.SizeOf<T>()), default(T));
        _nulls++;
        _count++;
    }

    private void TooMany() =>
        throw new InvalidOperationException($"(bjostat query): more values than the {_length} rows the column was made for");

    public IArrowArray Build()
    {
        if (_other != null) return _other.Build();
        int size = _count * Unsafe.SizeOf<T>();
        var bytes = _count == _length ? _bytes! : _bytes.AsSpan(0, size).ToArray();
        var bitmap = _valid == null ? ArrowBuffer.Empty : new ArrowBuffer(_valid.AsSpan(0, (_count + 7) / 8).ToArray());
        var data = new ArrayData(Builder<T>.ArrowType(), _count, _nulls, 0, new[] { bitmap, new ArrowBuffer(bytes) });
        return ArrowArrayFactory.BuildArray(data);
    }
}

public static class SizedBuilderOps
{
    public static SizedBuilder<T> Make<T>(int length) => new(length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SizedBuilder<T> Add<T>(SizedBuilder<T> b, T v)
    {
        b.Add(v);
        return b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SizedBuilder<T> AddOption<T>(SizedBuilder<T> b, BjolangRuntime.Option<T> v)
    {
        if (v.IsSome) b.Add(v.Value);
        else b.AddNull();
        return b;
    }

    public static IArrowArray Build<T>(SizedBuilder<T> b) => b.Build();
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

    /// <summary>
    /// The row, kept only when <paramref name="keep"/> is true, with no branch
    /// on it: the row is written at the end either way, and the count grows by
    /// one or by none. A test that keeps rows at random costs no mispredicted
    /// jump, as a <c>:when</c> before <c>Add</c> does.
    /// </summary>
    public static Selection AddIf(Selection s, int row, bool keep)
    {
        if (s._count == s._rows.Length) System.Array.Resize(ref s._rows, s._rows.Length * 2);
        s._rows[s._count] = row;
        s._count += Unsafe.As<bool, byte>(ref keep);
        return s;
    }

    /// <summary>The table of those rows of <paramref name="t"/>.</summary>
    public static RecordBatch Take(Selection s, RecordBatch t) => Query.Take(t, s._rows, s._count);
}
