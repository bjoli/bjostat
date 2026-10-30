/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Cells: one column read at the type the Bjolang code uses it at.
//
// (column-as t name) has no type argument written: its %a comes from how the
// cells are used, as (> (cells-ref c i) 10.0) makes it double. In C# that is
// the T of Of<T>, so the check of the column's Arrow type against T is made
// here, once, when the cells are made.
//
// T is a value type for every column there is, so the JIT compiles each
// Cells<T> on its own, and the tests of typeof(T) below are decided then: a
// column of doubles has no test for strings in its Ref. T has no constraint
// in C#, because a Bjolang type that holds cells would then need one too,
// and Bjolang writes none on a type; Of<T> answers None for any T no column
// holds.

using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using BjoString;

namespace Bjostat;

/// <summary>A column, read as values of T. At a null the value is unspecified.</summary>
public sealed unsafe class Cells<T>
{
    private readonly IArrowArray _array;
    private readonly ReadOnlyMemory<byte> _values;
    private readonly int _offset;

    // The values' bytes, when the buffer is a managed array, as it is for
    // every column this package makes or reads: then Ref reads at an index
    // with one test of the bounds, and does not make a span for each value.
    // _start is the byte where value 0 is.
    private readonly byte[]? _bytes;
    private readonly int _start;

    // Otherwise the buffer is memory of Arrow's own, native unless an
    // allocator was given, and it is pinned once: _pointer is value 0. The
    // pin of native memory is a count, and nothing moves; the handle is
    // kept so that the pin lasts as long as the cells.
    private readonly byte* _pointer;
    private readonly System.Buffers.MemoryHandle _pin;

    // A column of strings as one string, and where it starts in Arrow's buffer.
    // A cell is a slice of it: no copy, and no check of the cell's bytes,
    // which the check of the whole made.
    private readonly Utf8String _text;
    private readonly int _textBase;

    // A category: its levels, whose text is _text, and the code of each row.
    // A cell is the slice of its level.
    private readonly StringArray? _levels;
    private readonly Int32Array? _codes;

    public readonly int Length;
    public readonly int NullCount;

    // The valid bitmap, read directly: its bytes when they are a managed
    // array, or its address in Arrow's native memory, which does not move,
    // and the bit of cell 0. Through Arrow's IsValid each cell was an
    // interface call and a span of the buffer.
    private readonly byte[]? _validBytes;
    private readonly int _validStart;
    private readonly byte* _validPointer;
    // How many bytes of the bitmap there are from _validBytes[0] or
    // _validPointer: a read of 8 bytes that ends after them is made a byte
    // at a time.
    private readonly int _validEnd;

    internal Cells(IArrowArray array)
    {
        _array = array;
        Length = array.Length;
        NullCount = array.NullCount;
        _offset = array.Data.Offset;
        if (NullCount != 0 && array.Data.Buffers.Length > 0 && !array.Data.Buffers[0].IsEmpty)
        {
            var bits = array.Data.Buffers[0].Memory;
            if (MemoryMarshal.TryGetArray(bits, out var bitSegment))
            {
                _validBytes = bitSegment.Array;
                _validStart = bitSegment.Offset * 8 + _offset;
                _validEnd = bitSegment.Offset + bitSegment.Count;
            }
            else if (MemoryMarshal.TryGetMemoryManager(bits, out MemoryManager<byte>? bitManager)
                     && bitManager.GetType().FullName == "Apache.Arrow.Memory.NativeMemoryManager")
            {
                _validPointer = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(bits.Span));
                _validStart = _offset;
                _validEnd = bits.Length;
            }
        }
        if (array is StringArray strings)
        {
            // The strings end to end, checked once as one string; a cell is
            // a slice of it. The offsets are where Arrow's buffer has each.
            var offsets = strings.ValueOffsets;
            _textBase = offsets[0];
            _text = Utf8String.FromUtf8(strings.ValueBuffer.Span.Slice(_textBase, offsets[Length] - _textBase));
        }
        else if (array is DictionaryArray { Dictionary: StringArray levels, Indices: Int32Array codes })
        {
            _levels = levels;
            _codes = codes;
            var offsets = levels.ValueOffsets;
            _textBase = offsets[0];
            _text = Utf8String.FromUtf8(levels.ValueBuffer.Span.Slice(_textBase, offsets[levels.Length] - _textBase));
        }
        else if (array is not BooleanArray)
        {
            _values = array.Data.Buffers[1].Memory;
            if (MemoryMarshal.TryGetArray(_values, out var segment)
                && segment.Count >= (_offset + Length) * Unsafe.SizeOf<T>())
            {
                _bytes = segment.Array;
                _start = segment.Offset + _offset * Unsafe.SizeOf<T>();
            }
            else if (_values.Length >= (_offset + Length) * Unsafe.SizeOf<T>())
            {
                // Arrow's native memory does not move, so it needs no pin:
                // a pin that is never released makes Arrow's finalizer throw
                // ("cannot free native memory while it is pinned") when the
                // buffer is collected. _array keeps the memory alive as long
                // as the cells. Memory of any other manager is pinned.
                if (MemoryMarshal.TryGetMemoryManager(_values, out MemoryManager<byte>? manager)
                    && manager.GetType().FullName == "Apache.Arrow.Memory.NativeMemoryManager")
                    _pointer = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(_values.Span))
                               + _offset * Unsafe.SizeOf<T>();
                else
                {
                    _pin = _values.Pin();
                    _pointer = (byte*)_pin.Pointer + _offset * Unsafe.SizeOf<T>();
                }
            }
        }
    }

    public IArrowArray Array => _array;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Ref(int i)
    {
        if (typeof(T) == typeof(Utf8String))
        {
            int k = i;
            StringArray strings;
            if (_codes != null)
            {
                // The code under a null can be any; one outside the levels
                // is the empty string.
                k = _codes.Values[i];
                strings = _levels!;
                if ((uint)k >= (uint)strings.Length)
                {
                    var empty = Utf8String.Empty;
                    return Unsafe.As<Utf8String, T>(ref empty);
                }
            }
            else strings = (StringArray)_array;
            var offsets = strings.ValueOffsets;
            int from = offsets[k];
            var s = _text.SliceBytes(from - _textBase, offsets[k + 1] - from);
            return Unsafe.As<Utf8String, T>(ref s);
        }
        if (typeof(T) == typeof(bool))
        {
            bool b = ((BooleanArray)_array).GetValue(i) ?? false;
            return Unsafe.As<bool, T>(ref b);
        }
        if (_bytes != null)
        {
            // The constructor made sure the array holds Length values from
            // _start, so this test is the only one needed.
            if ((uint)i >= (uint)Length) throw new IndexOutOfRangeException();
            return Unsafe.ReadUnaligned<T>(
                ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_bytes), _start + i * Unsafe.SizeOf<T>()));
        }
        if (_pointer != null)
        {
            if ((uint)i >= (uint)Length) throw new IndexOutOfRangeException();
            return Unsafe.ReadUnaligned<T>(_pointer + (nint)i * Unsafe.SizeOf<T>());
        }
        if ((uint)i >= (uint)Length) throw new IndexOutOfRangeException();
        return Unsafe.ReadUnaligned<T>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(_values.Span), (_offset + i) * Unsafe.SizeOf<T>()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsValid(int i)
    {
        if (NullCount == 0) return true;
        int bit = _validStart + i;
        if (_validBytes != null) return (_validBytes[bit >> 3] & (1 << (bit & 7))) != 0;
        if (_validPointer != null) return (_validPointer[bit >> 3] & (1 << (bit & 7))) != 0;
        return _array.IsValid(i);
    }

    /// <summary>
    /// The valid bits of the cells from <paramref name="i"/> on, as many as
    /// <paramref name="count"/> gives back: bit 0 is cell i. All ones without
    /// nulls. Cell i is in the cells.
    /// </summary>
    /// <remarks>
    /// One read of 8 bytes, shifted by the bit of cell i in its byte, gives
    /// at least 57 bits. The bits after the last cell are cleared, as Arrow
    /// does not say what they hold.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ValidWindow(int i, out int count)
    {
        int left = Length - i;
        count = left < 57 ? left : 57;
        ulong mask = left < 57 ? (1UL << left) - 1 : (1UL << 57) - 1;
        if (NullCount == 0) return mask;
        int bit = _validStart + i;
        int at = bit >> 3;
        ulong w;
        if (at + 8 <= _validEnd && (_validBytes != null || _validPointer != null))
            w = _validBytes != null
                ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_validBytes), at))
                : Unsafe.ReadUnaligned<ulong>(_validPointer + at);
        else
            w = WindowSlowly(i);
        return (w >> (bit & 7)) & mask;
    }

    /// <summary>
    /// The 8 bytes from the byte of cell i, read a byte at a time: at the end
    /// of the bitmap, or through Arrow when the bitmap is in other memory.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong WindowSlowly(int i)
    {
        int bit = _validStart + i;
        ulong w = 0;
        if (_validBytes != null || _validPointer != null)
        {
            int at = bit >> 3;
            for (int k = 0; k < 8 && at + k < _validEnd; k++)
                w |= (ulong)(_validBytes != null ? _validBytes[at + k] : _validPointer[at + k]) << (8 * k);
            return w;
        }
        // Through Arrow: the bits are placed as the shift in ValidWindow
        // expects them.
        int shift = bit & 7;
        for (int k = 0; k < 57 && i + k < Length; k++)
            if (_array.IsValid(i + k)) w |= 1UL << (k + shift);
        return w;
    }

    /// <summary>The first cell from <paramref name="i"/> on that is not null, or the length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextValid(int i)
    {
        if (NullCount == 0 || i >= Length) return i < Length ? i : Length;
        ulong w = ValidWindow(i, out int count);
        return w != 0 ? i + BitOperations.TrailingZeroCount(w) : NextValidAfter(i + count);
    }

    /// <summary>NextValid from <paramref name="i"/>, after a window with no valid cell.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int NextValidAfter(int i)
    {
        while (i < Length)
        {
            ulong w = ValidWindow(i, out int count);
            if (w != 0) return i + BitOperations.TrailingZeroCount(w);
            i += count;
        }
        return Length;
    }

    /// <summary>
    /// Whether <see cref="Values"/> and <see cref="Valid"/> give all the
    /// cells: the values are numbers in one buffer, and the valid bitmap,
    /// when there is one, starts at a whole byte.
    /// </summary>
    public bool IsContiguous =>
        (_bytes != null || _pointer != null) && (NullCount == 0 || _offset % 8 == 0);

    /// <summary>The values, one for each cell. Under a null the value is unspecified.</summary>
    public ReadOnlySpan<T> Values =>
        _bytes != null
            ? MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<byte, T>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_bytes), _start)), Length)
            : new ReadOnlySpan<T>(_pointer, Length);

    /// <summary>
    /// The valid bitmap from cell 0, or empty when no cell is null. Only for
    /// cells that <see cref="IsContiguous"/> is true for.
    /// </summary>
    public ReadOnlySpan<byte> Valid =>
        NullCount == 0 ? default : _array.Data.Buffers[0].Span.Slice(_offset >> 3);
}

public static class CellsOps
{
    /// <summary>
    /// The cells of the column as T, or None when the column holds another
    /// type.
    /// </summary>
    public static BjolangRuntime.Option<Cells<T>> Of<T>(IArrowArray a) =>
        Holds<T>(a) ? BjolangRuntime.Some(new Cells<T>(a)) : BjolangRuntime.None<Cells<T>>();

    private static bool Holds<T>(IArrowArray a) =>
        typeof(T) == typeof(double) ? a is DoubleArray
        : typeof(T) == typeof(float) ? a is FloatArray
        : typeof(T) == typeof(long) ? a is Int64Array
        : typeof(T) == typeof(int) ? a is Int32Array
        : typeof(T) == typeof(short) ? a is Int16Array
        : typeof(T) == typeof(byte) ? a is UInt8Array
        : typeof(T) == typeof(bool) ? a is BooleanArray
        : typeof(T) == typeof(Utf8String) && (a is StringArray || Categories.Is(a));

    /// <summary>
    /// Bjolang's name for T, for the error when a column is not of it. The
    /// argument only carries T.
    /// </summary>
    public static string NameOf<T>(BjolangRuntime.Option<Cells<T>> _) =>
        typeof(T) == typeof(double) ? "double"
        : typeof(T) == typeof(float) ? "float"
        : typeof(T) == typeof(long) ? "long"
        : typeof(T) == typeof(int) ? "int"
        : typeof(T) == typeof(short) ? "short"
        : typeof(T) == typeof(byte) ? "byte"
        : typeof(T) == typeof(bool) ? "bool"
        : typeof(T) == typeof(Utf8String) ? "string"
        : typeof(T).Name;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Ref<T>(Cells<T> c, int i) => c.Ref(i);

    /// <summary>The value at the index, or None at a null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BjolangRuntime.Option<T> Get<T>(Cells<T> c, int i) =>
        c.IsValid(i) ? BjolangRuntime.Some(c.Ref(i)) : BjolangRuntime.None<T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValid<T>(Cells<T> c, int i) => c.IsValid(i);

    public static int Length<T>(Cells<T> c) => c.Length;

    /// <summary>The first cell from <paramref name="i"/> on that is not null, or the length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextValid<T>(Cells<T> c, int i) => c.NextValid(i);

    // The first row from i on where no column is null, or `length`, the
    // length of the shortest column: the valid bits of 57 rows of each
    // column, and of them, and the first bit. A row of the shortest column
    // has a row in each.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextComplete<A, B>(Cells<A> a, Cells<B> b, int length, int i)
    {
        while (i < length)
        {
            ulong w = a.ValidWindow(i, out int n) & b.ValidWindow(i, out _) & Rows(length - i);
            if (w != 0) return i + BitOperations.TrailingZeroCount(w);
            i += n < length - i ? n : length - i;
        }
        return length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextComplete<A, B, C>(Cells<A> a, Cells<B> b, Cells<C> c, int length, int i)
    {
        while (i < length)
        {
            ulong w = a.ValidWindow(i, out int n) & b.ValidWindow(i, out _) & c.ValidWindow(i, out _)
                      & Rows(length - i);
            if (w != 0) return i + BitOperations.TrailingZeroCount(w);
            i += n < length - i ? n : length - i;
        }
        return length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextComplete<A, B, C, D>(Cells<A> a, Cells<B> b, Cells<C> c, Cells<D> d, int length, int i)
    {
        while (i < length)
        {
            ulong w = a.ValidWindow(i, out int n) & b.ValidWindow(i, out _) & c.ValidWindow(i, out _)
                      & d.ValidWindow(i, out _) & Rows(length - i);
            if (w != 0) return i + BitOperations.TrailingZeroCount(w);
            i += n < length - i ? n : length - i;
        }
        return length;
    }

    /// <summary>A mask of the first <paramref name="left"/> bits, at most 57.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Rows(int left) => left < 57 ? (1UL << left) - 1 : (1UL << 57) - 1;

    public static int NullCount<T>(Cells<T> c) => c.NullCount;

    public static bool IsContiguous<T>(Cells<T> c) => c.IsContiguous;

    public static ReadOnlySpan<T> Values<T>(Cells<T> c) => c.Values;

    public static ReadOnlySpan<byte> Valid<T>(Cells<T> c) => c.Valid;
}
