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

    public readonly int Length;
    public readonly int NullCount;

    internal Cells(IArrowArray array)
    {
        _array = array;
        Length = array.Length;
        NullCount = array.NullCount;
        _offset = array.Data.Offset;
        if (array is not StringArray && array is not BooleanArray)
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
                _pin = _values.Pin();
                _pointer = (byte*)_pin.Pointer + _offset * Unsafe.SizeOf<T>();
            }
        }
    }

    public IArrowArray Array => _array;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Ref(int i)
    {
        if (typeof(T) == typeof(Utf8String))
        {
            var s = Utf8String.FromUtf8(((StringArray)_array).GetBytes(i));
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
    public bool IsValid(int i) => NullCount == 0 || _array.IsValid(i);
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
        : typeof(T) == typeof(Utf8String) && a is StringArray;

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

    public static int NullCount<T>(Cells<T> c) => c.NullCount;
}
