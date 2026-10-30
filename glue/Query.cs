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
        CheckRows(rows.AsSpan(0, count), t.Length);
        var columns = new IArrowArray[t.ColumnCount];
        // The columns are copied on bjostat's workers when there is enough to
        // copy, each in parts of rows, so that one long column of strings
        // does not take as long as all of them on one thread. Most of the
        // time of a copy is spent to get new memory: the operating system
        // gives each page of a new array when it is first written. The
        // threads get their pages at the same time. A column's exception is
        // thrown as one thread would throw it.
        if (count >= 2 * TakePart)
            return new RecordBatch(t.Schema, TakeInParts(t, rows, count), count);
        if (columns.Length > 1 && (long)count * columns.Length >= ParallelRows)
        {
            Workers.For(columns.Length, c => columns[c] = TakeColumn(t.Column(c), rows.AsSpan(0, count)));
        }
        else
            for (int c = 0; c < columns.Length; c++)
                columns[c] = TakeColumn(t.Column(c), rows.AsSpan(0, count));
        return new RecordBatch(t.Schema, columns, count);
    }

    // The rows of a part of a copy: a multiple of 8, so that each part writes
    // whole bytes of a bitmap.
    private const int TakePart = 1 << 16;

    // The columns at the rows, each column in parts of rows. One For over
    // every part of every column: numbers and bits are copied, and a part of
    // strings adds up the lengths of its strings. The sums give each part of
    // strings where its bytes start, and a second For copies them. Not one
    // For inside another: a worker that waits there could wait for workers
    // that wait too.
    private static IArrowArray[] TakeInParts(RecordBatch t, int[] rows, int count)
    {
        int parts = (count + TakePart - 1) / TakePart;
        var plans = new PartedTake[t.ColumnCount];
        for (int c = 0; c < plans.Length; c++) plans[c] = new PartedTake(t.Column(c), rows, count, parts);
        Workers.For(plans.Length * parts, u => plans[u / parts].First(u % parts));
        bool strings = false;
        foreach (var plan in plans) strings |= plan.Between();
        if (strings) Workers.For(plans.Length * parts, u => plans[u / parts].Second(u % parts));
        return plans.Select(plan => plan.Result()).ToArray();
    }

    /// <summary>The copy of one column at the rows, in parts of <see cref="TakePart"/> rows.</summary>
    private sealed class PartedTake
    {
        private readonly IArrowArray _a;
        private readonly ArrayData _data;
        private readonly int[] _rows;
        private readonly int _count;
        private readonly int _parts;
        // Numbers: the width of a value, and the new values.
        private readonly int _width;
        private readonly byte[]? _values;
        // Strings: the new offsets, each part's bytes, and the new bytes.
        private readonly bool _strings;
        private readonly byte[]? _offsets;
        private readonly int[]? _partBytes;
        private byte[]? _bytes;
        private int _total;
        // The valid bits, and each part's count of them.
        private readonly byte[]? _valid;
        private readonly int[] _set;
        // Another type: copied whole by part 0, as before.
        private IArrowArray? _whole;

        public PartedTake(IArrowArray a, int[] rows, int count, int parts)
        {
            _a = a;
            _data = a.Data;
            _rows = rows;
            _count = count;
            _parts = parts;
            _set = new int[parts];
            var type = _data.DataType;
            bool fixedWidth = type switch
            {
                BooleanType => false,
                DictionaryType { IndexType: FixedWidthType iw } => iw.BitWidth % 8 == 0 && (_width = iw.BitWidth / 8) > 0,
                FixedWidthType fw => fw.BitWidth % 8 == 0 && (_width = fw.BitWidth / 8) > 0,
                _ => false,
            };
            _strings = type is StringType or BinaryType;
            if (!fixedWidth && !_strings) return;
            if (_data.NullCount != 0 && !_data.Buffers[0].IsEmpty)
            {
                CheckBuffer(_data.Buffers[0].Span, ((long)_data.Offset + _data.Length + 7) / 8);
                _valid = GC.AllocateUninitializedArray<byte>((count + 7) / 8);
            }
            if (fixedWidth)
            {
                CheckBuffer(_data.Buffers[1].Span, ((long)_data.Offset + _data.Length) * _width);
                _values = GC.AllocateUninitializedArray<byte>(count * _width);
            }
            else
            {
                var offsetBytes = _data.Buffers[1].Span;
                CheckBuffer(offsetBytes, ((long)_data.Offset + _data.Length + 1) * sizeof(int));
                var offsets = MemoryMarshal.Cast<byte, int>(offsetBytes).Slice(_data.Offset);
                CheckBuffer(_data.Buffers[2].Span, offsets[_data.Length]);
                _offsets = GC.AllocateUninitializedArray<byte>((count + 1) * sizeof(int));
                _partBytes = new int[parts];
            }
        }

        private (int from, int to) Range(int p) => (p * TakePart, Math.Min(_count, (p + 1) * TakePart));

        /// <summary>The bits and the numbers of part p, or the lengths of its strings.</summary>
        public void First(int p)
        {
            if (_values == null && !_strings)
            {
                if (p == 0) _whole = TakeColumn(_a, _rows.AsSpan(0, _count));
                return;
            }
            var (from, to) = Range(p);
            var rows = _rows.AsSpan(from, to - from);
            if (_valid != null)
            {
                var bits = TakeBitsOf(_data.Buffers[0].Span, _data.Offset, rows, out _set[p]);
                bits.CopyTo(_valid, from >> 3);
            }
            if (_values != null)
            {
                var values = _data.Buffers[1].Span;
                var into = _values.AsSpan(from * _width, (to - from) * _width);
                switch (_width)
                {
                    case 8: GatherInto<long>(values, _data.Offset, rows, into); break;
                    case 4: GatherInto<int>(values, _data.Offset, rows, into); break;
                    case 2: GatherInto<short>(values, _data.Offset, rows, into); break;
                    case 1: GatherInto<byte>(values, _data.Offset, rows, into); break;
                    default:
                        for (int i = 0; i < rows.Length; i++)
                            values.Slice((_data.Offset + rows[i]) * _width, _width).CopyTo(into.Slice(i * _width));
                        break;
                }
                return;
            }
            var offsets = MemoryMarshal.Cast<byte, int>(_data.Buffers[1].Span).Slice(_data.Offset);
            int sum = 0;
            foreach (int r in rows) sum += offsets[r + 1] - offsets[r];
            _partBytes![p] = sum;
        }

        /// <summary>For strings: where each part's bytes start, and the array of them. Whether there are strings.</summary>
        public bool Between()
        {
            if (!_strings) return false;
            long total = 0;
            for (int p = 0; p < _parts; p++)
            {
                int bytes = _partBytes![p];
                _partBytes[p] = (int)total;
                total += bytes;
            }
            if (total > int.MaxValue) throw new InvalidDataException("(bjostat query): the strings are too many bytes for one column");
            _total = (int)total;
            _bytes = GC.AllocateUninitializedArray<byte>(_total + 16);
            MemoryMarshal.Cast<byte, int>(_offsets.AsSpan())[_count] = _total;
            return true;
        }

        /// <summary>For strings: the offsets and the bytes of part p.</summary>
        public void Second(int p)
        {
            if (!_strings) return;
            var (from, to) = Range(p);
            ref int offsets = ref Unsafe.Add(ref Unsafe.As<byte, int>(ref MemoryMarshal.GetReference(_data.Buffers[1].Span)), _data.Offset);
            var bytes = _data.Buffers[2].Span;
            ref byte source = ref MemoryMarshal.GetReference(bytes);
            ref int newOffsets = ref Unsafe.As<byte, int>(ref MemoryMarshal.GetArrayDataReference(_offsets!));
            ref byte to0 = ref MemoryMarshal.GetArrayDataReference(_bytes!);
            int lastWhole = bytes.Length - 16;
            int at = _partBytes![p];
            // Where the next part's bytes start: a copy of 16 bytes must not
            // end after it, or it could write over the next part's first
            // string, which another thread may have written.
            int end = p + 1 < _parts ? _partBytes[p + 1] : _total + 16;
            for (int i = from; i < to; i++)
            {
                int r = _rows[i];
                int start = Unsafe.Add(ref offsets, r);
                int length = Unsafe.Add(ref offsets, r + 1) - start;
                Unsafe.Add(ref newOffsets, i) = at;
                ref byte dest = ref Unsafe.Add(ref to0, at);
                if (length <= 16 && start <= lastWhole && at + 16 <= end)
                    Unsafe.WriteUnaligned(ref dest, Unsafe.ReadUnaligned<System.Runtime.Intrinsics.Vector128<byte>>(ref Unsafe.Add(ref source, start)));
                else
                    Unsafe.CopyBlockUnaligned(ref dest, ref Unsafe.Add(ref source, start), (uint)length);
                at += length;
            }
        }

        public IArrowArray Result()
        {
            if (_whole != null) return _whole;
            int set = 0;
            foreach (int c in _set) set += c;
            int nulls = _valid == null ? 0 : _count - set;
            var bitmap = nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(_valid!);
            ArrayData taken = _strings
                ? new ArrayData(_data.DataType, _count, nulls, 0,
                    new[] { bitmap, new ArrowBuffer(_offsets!), new ArrowBuffer(new ReadOnlyMemory<byte>(_bytes!, 0, _total)) })
                : new ArrayData(_data.DataType, _count, nulls, 0,
                    new[] { bitmap, new ArrowBuffer(_values!) }, null, _data.Dictionary);
            return ArrowArrayFactory.BuildArray(taken);
        }
    }

    private static void GatherInto<T>(ReadOnlySpan<byte> values, int offset, ReadOnlySpan<int> rows, Span<byte> into)
        where T : struct
    {
        ref T from = ref Unsafe.Add(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(values)), offset);
        ref T to = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(into));
        for (int i = 0; i < rows.Length; i++)
            Unsafe.Add(ref to, i) = Unsafe.ReadUnaligned<T>(ref Unsafe.As<T, byte>(ref Unsafe.Add(ref from, rows[i])));
    }

    // Below this many cells, a copy on one thread is faster than the start of
    // the others.
    private const long ParallelRows = 65536;

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
            // A category: its codes, with the same levels. A DictionaryType
            // is a FixedWidthType too, so this comes first.
            DictionaryType { IndexType: FixedWidthType iw } when iw.BitWidth % 8 == 0 => new ArrayData(data.DataType, rows.Length, nulls, 0,
                new[] { bitmap, TakeFixed(data.Buffers[1].Span, data.Offset, data.Length, iw.BitWidth / 8, rows) },
                null, data.Dictionary),
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
        var bits = TakeBitsOf(valid, offset, rows, out int set);
        nulls = rows.Length - set;
        return nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(bits);
    }

    private static ArrowBuffer TakeBits(ReadOnlySpan<byte> values, int offset, int length, ReadOnlySpan<int> rows)
    {
        CheckBuffer(values, ((long)offset + length + 7) / 8);
        return new ArrowBuffer(TakeBitsOf(values, offset, rows, out _));
    }

    // The bits at the rows, and how many of them are set. Each bit is added
    // to a byte with no jump that depends on it, so a bitmap that is set at
    // random costs no mispredicted jumps. Each byte is written once, when it
    // is full, so the array need not be zeroed first.
    private static byte[] TakeBitsOf(ReadOnlySpan<byte> bitmap, int offset, ReadOnlySpan<int> rows, out int set)
    {
        var bits = GC.AllocateUninitializedArray<byte>((rows.Length + 7) / 8);
        ref byte from = ref MemoryMarshal.GetReference(bitmap);
        int bytes = 0, count = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            int j = offset + rows[i];
            int b = (Unsafe.Add(ref from, j >> 3) >> (j & 7)) & 1;
            bytes |= b << (i & 7);
            count += b;
            if ((i & 7) == 7)
            {
                bits[i >> 3] = (byte)bytes;
                bytes = 0;
            }
        }
        if ((rows.Length & 7) != 0) bits[rows.Length >> 3] = (byte)bytes;
        set = count;
        return bits;
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

        // A string of at most 16 bytes is copied as 16 bytes, with one load
        // and one store: the bytes after it are written over by the next
        // string, or are in the 16 bytes after the end, which the buffer
        // leaves out. A string whose 16 bytes would end after the bytes of
        // the column is copied by its length.
        var newBytes = GC.AllocateUninitializedArray<byte>(total + 16);
        ref byte from = ref MemoryMarshal.GetReference(bytes);
        ref byte to = ref MemoryMarshal.GetArrayDataReference(newBytes);
        int lastWhole = bytes.Length - 16;
        for (int i = 0; i < rows.Length; i++)
        {
            int r = rows[i];
            int start = Unsafe.Add(ref offsets, r);
            int length = Unsafe.Add(ref offsets, r + 1) - start;
            ref byte at = ref Unsafe.Add(ref to, Unsafe.Add(ref newOffsets, i));
            if (length <= 16 && start <= lastWhole)
                Unsafe.WriteUnaligned(ref at, Unsafe.ReadUnaligned<System.Runtime.Intrinsics.Vector128<byte>>(ref Unsafe.Add(ref from, start)));
            else
                Unsafe.CopyBlockUnaligned(ref at, ref Unsafe.Add(ref from, start), (uint)length);
        }
        return new ArrayData(data.DataType, rows.Length, nulls, 0,
            new[] { bitmap, new ArrowBuffer(newOffsetBytes), new ArrowBuffer(new ReadOnlyMemory<byte>(newBytes, 0, total)) });
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
/// <remarks>
/// <para>
/// The arrays come from the shared pools, as in CsvColumn: an array that
/// doubles is copied many times, and each copy over 85 000 bytes would be on
/// the large object heap. Without the pools a column of a million doubles
/// took four times as long, for the collections of generation 2.
/// </para>
/// <para>
/// A column of numbers is kept as bytes, so that the array it grew in can be
/// the buffer Arrow is given, with no copy at the end. That array is then
/// not given back to the pool. When more than a quarter of it would be
/// empty, the bytes are copied to an array of their size instead, so that a
/// column does not hold much memory it does not use.
/// </para>
/// </remarks>
public sealed class Builder<T>
{
    // Numbers: their bytes. Strings and bools: the values.
    private byte[]? _bytes;
    private T[]? _values;
    private byte[]? _valid;
    private int _capacity;
    private int _count;
    private int _nulls;

    private static bool IsNumber =>
        typeof(T) == typeof(double) || typeof(T) == typeof(float) || typeof(T) == typeof(long)
        || typeof(T) == typeof(int) || typeof(T) == typeof(short) || typeof(T) == typeof(byte);

    public Builder(int capacity)
    {
        CheckType();
        _capacity = Math.Max(capacity, 16);
        if (IsNumber)
        {
            _bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(_capacity * Unsafe.SizeOf<T>());
            _capacity = _bytes.Length / Unsafe.SizeOf<T>();
        }
        else
        {
            _values = System.Buffers.ArrayPool<T>.Shared.Rent(_capacity);
            _capacity = _values.Length;
        }
    }

    private static void CheckType()
    {
        if (!(IsNumber || typeof(T) == typeof(bool) || typeof(T) == typeof(Utf8String)))
            throw new ArgumentException($"(bjostat query): a column of {typeof(T).Name} cannot be made");
    }

    public int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T v)
    {
        if (_count == _capacity) Grow();
        if (_bytes != null)
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_bytes), (nint)_count * Unsafe.SizeOf<T>()), v);
        else
            _values![_count] = v;
        _count++;
    }

    public void AddNull()
    {
        if (_count == _capacity) Grow();
        if (_valid == null)
        {
            _valid = new byte[_capacity / 8 + 1];
            _valid.AsSpan().Fill(0xFF);
        }
        _valid[_count >> 3] &= (byte)~(1 << (_count & 7));
        // The value under a null is 0, so that nothing the array held before
        // can be read in the column.
        if (_bytes != null)
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_bytes), (nint)_count * Unsafe.SizeOf<T>()), default(T));
        else
            _values![_count] = default!;
        _nulls++;
        _count++;
    }

    /// <summary>
    /// <paramref name="n"/> more values at the end, not null. Their values
    /// are written next, through <see cref="Span"/>.
    /// </summary>
    public void Extend(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (_capacity - _count < n) Grow(_count + n);
        _count += n;
    }

    /// <summary>The values from <paramref name="start"/>, <paramref name="n"/> of them, to write.</summary>
    public Span<T> Span(int start, int n)
    {
        if (start < 0 || n < 0 || start > _count - n) throw new ArgumentOutOfRangeException(nameof(start));
        if (_bytes != null)
            return MemoryMarshal.CreateSpan(
                ref Unsafe.As<byte, T>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_bytes), (nint)start * Unsafe.SizeOf<T>())), n);
        return _values.AsSpan(start, n);
    }

    /// <summary>Makes value <paramref name="i"/> null, with 0 under it, as <see cref="AddNull"/> does.</summary>
    public void SetNull(int i)
    {
        if ((uint)i >= (uint)_count) throw new IndexOutOfRangeException();
        if (_valid == null)
        {
            _valid = new byte[_capacity / 8 + 1];
            _valid.AsSpan().Fill(0xFF);
        }
        if ((_valid[i >> 3] & (1 << (i & 7))) == 0) return;
        _valid[i >> 3] &= (byte)~(1 << (i & 7));
        Span(i, 1)[0] = default!;
        _nulls++;
    }

    // Room for at least `wanted` values, and at least twice as many as now.
    private void Grow(int wanted = 0)
    {
        if (_bytes != null)
        {
            int used = _count * Unsafe.SizeOf<T>();
            var bigger = System.Buffers.ArrayPool<byte>.Shared.Rent(
                Math.Max(_bytes.Length * 2, wanted * Unsafe.SizeOf<T>()));
            _bytes.AsSpan(0, used).CopyTo(bigger);
            System.Buffers.ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = bigger;
            _capacity = bigger.Length / Unsafe.SizeOf<T>();
        }
        else
        {
            var bigger = System.Buffers.ArrayPool<T>.Shared.Rent(Math.Max(_values!.Length * 2, wanted));
            _values.AsSpan(0, _count).CopyTo(bigger);
            ReleaseValues();
            _values = bigger;
            _capacity = bigger.Length;
        }
        if (_valid != null)
        {
            int old = _valid.Length;
            System.Array.Resize(ref _valid, _capacity / 8 + 1);
            _valid.AsSpan(old).Fill(0xFF);
        }
    }

    public IArrowArray Build() => Build(null);

    /// <summary>
    /// The column, of <paramref name="type"/> when it is given: a column of
    /// ints can be one of dates, and one of longs one of timestamps.
    /// </summary>
    public IArrowArray Build(IArrowType? type)
    {
        var bitmap = _valid == null ? ArrowBuffer.Empty : new ArrowBuffer(_valid.AsSpan(0, (_count + 7) / 8).ToArray());
        ArrayData data;
        if (_bytes != null)
        {
            int used = _count * Unsafe.SizeOf<T>();
            ArrowBuffer buffer;
            if (_bytes.Length - used <= used / 4)
            {
                // The array is the column's now, and not the pool's.
                buffer = new ArrowBuffer(new ReadOnlyMemory<byte>(_bytes, 0, used));
            }
            else
            {
                buffer = new ArrowBuffer(_bytes.AsSpan(0, used).ToArray());
                System.Buffers.ArrayPool<byte>.Shared.Return(_bytes);
            }
            _bytes = System.Array.Empty<byte>();
            data = new ArrayData(type ?? ArrowType(), _count, _nulls, 0, new[] { bitmap, buffer });
        }
        else if (typeof(T) == typeof(Utf8String))
        {
            // T is Utf8String here, so the array is a Utf8String[].
            var strings = Unsafe.As<T[], Utf8String[]>(ref _values!).AsSpan(0, _count);
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
            ReleaseValues();
        }
        else
        {
            var bools = Unsafe.As<T[], bool[]>(ref _values!).AsSpan(0, _count);
            var bits = new byte[(_count + 7) / 8];
            for (int i = 0; i < bools.Length; i++)
                if (bools[i]) bits[i >> 3] |= (byte)(1 << (i & 7));
            data = new ArrayData(BooleanType.Default, _count, _nulls, 0, new[] { bitmap, new ArrowBuffer(bits) });
            ReleaseValues();
        }
        _count = 0;
        _capacity = 0;
        return ArrowArrayFactory.BuildArray(data);
    }

    // An array of strings is cleared when it goes back, so that the pool does
    // not keep the strings.
    private void ReleaseValues()
    {
        System.Buffers.ArrayPool<T>.Shared.Return(_values!, clearArray: typeof(T) == typeof(Utf8String));
        _values = System.Array.Empty<T>();
    }

    internal static IArrowType ArrowType() =>
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

    /// <summary>A column of dates of a builder of days since 1970.</summary>
    public static IArrowArray BuildDates(Builder<int> b) => b.Build(TimeKind.ArrowType(TimeKind.Date));

    /// <summary>
    /// A column of timestamps of a builder of microseconds since 1970: with
    /// the time zone UTC when <paramref name="zoned"/>, and with none when not.
    /// </summary>
    public static IArrowArray BuildMicros(Builder<long> b, bool zoned) =>
        b.Build(TimeKind.ArrowType(zoned ? TimeKind.Instant : TimeKind.DateTime));

    public static int Count<T>(Builder<T> b) => b.Count;

    public static Builder<T> Extend<T>(Builder<T> b, int n)
    {
        b.Extend(n);
        return b;
    }

    public static Span<T> Span<T>(Builder<T> b, int start, int n) => b.Span(start, n);

    public static void SetNull<T>(Builder<T> b, int i) => b.SetNull(i);
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

    public static int Count(Selection s) => s._count;

    /// <summary><paramref name="n"/> more rows at the end, written next through <see cref="Span"/>.</summary>
    public static Selection Grow(Selection s, int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (s._rows.Length - s._count < n)
            System.Array.Resize(ref s._rows, Math.Max(s._rows.Length * 2, s._count + n));
        s._count += n;
        return s;
    }

    public static Span<int> Span(Selection s, int start, int n)
    {
        if (start < 0 || n < 0 || start > s._count - n) throw new ArgumentOutOfRangeException(nameof(start));
        return s._rows.AsSpan(start, n);
    }

    /// <summary>A row number is never None, so a vector loop never calls this.</summary>
    public static void NoNone(Selection s, int i) =>
        throw new InvalidOperationException("a selected row cannot be None");

    /// <summary>The table of those rows of <paramref name="t"/>.</summary>
    public static RecordBatch Take(Selection s, RecordBatch t) => Query.Take(t, s._rows, s._count);
}
