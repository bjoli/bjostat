/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// The types a column can hold, in one place.
//
// A column is a .NET array of T whose Arrow type is the one T means, so the
// question "can a column hold this type?" is the question this file answers,
// and it is the only place that answers it: the builder that makes a column,
// the cells that read one, and the message a refusal carries all ask here.
// Adding a type is adding a line to each of the four tests below.

using Apache.Arrow;
using BjoString;
using Apache.Arrow.Types;

namespace Bjostat;

public static class ColumnTypes
{
    /// <summary>
    /// Whether T is kept as bytes: a number, whose values are its bits. The
    /// others — a bool, a string — are kept as values of T.
    /// </summary>
    public static bool IsNumber<T>() =>
        typeof(T) == typeof(double) || typeof(T) == typeof(float) || typeof(T) == typeof(long)
        || typeof(T) == typeof(int) || typeof(T) == typeof(short) || typeof(T) == typeof(byte)
        || typeof(T) == typeof(ushort) || typeof(T) == typeof(uint) || typeof(T) == typeof(ulong);

    /// <summary>
    /// The Arrow type a column of T has. For a number it is the Arrow type of
    /// its width, and for a bool or a string the one Arrow has for those.
    /// </summary>
    public static IArrowType ArrowType<T>() =>
        typeof(T) == typeof(double) ? DoubleType.Default
        : typeof(T) == typeof(float) ? FloatType.Default
        : typeof(T) == typeof(long) ? Int64Type.Default
        : typeof(T) == typeof(int) ? Int32Type.Default
        : typeof(T) == typeof(short) ? Int16Type.Default
        : typeof(T) == typeof(byte) ? UInt8Type.Default
        : typeof(T) == typeof(ushort) ? UInt16Type.Default
        : typeof(T) == typeof(uint) ? UInt32Type.Default
        : typeof(T) == typeof(ulong) ? UInt64Type.Default
        : typeof(T) == typeof(Utf8String) ? StringType.Default
        : BooleanType.Default;

    /// <summary>Whether a column can hold T at all.</summary>
    public static bool CanHold<T>() =>
        IsNumber<T>() || typeof(T) == typeof(bool) || typeof(T) == typeof(Utf8String);

    /// <summary>Bjolang's name for T, for a message about a column of it.</summary>
    public static string Name<T>() =>
        typeof(T) == typeof(double) ? "double"
        : typeof(T) == typeof(float) ? "float"
        : typeof(T) == typeof(long) ? "long"
        : typeof(T) == typeof(int) ? "int"
        : typeof(T) == typeof(short) ? "short"
        : typeof(T) == typeof(byte) ? "byte"
        : typeof(T) == typeof(ushort) ? "ushort"
        : typeof(T) == typeof(uint) ? "uint"
        : typeof(T) == typeof(ulong) ? "ulong"
        : typeof(T) == typeof(bool) ? "bool"
        : typeof(T) == typeof(Utf8String) ? "string"
        // The types a column cannot hold, named as Bjolang names them, so the
        // refusal reads as the program does.
        : typeof(T) == typeof(decimal) ? "decimal"
        : typeof(T) == typeof(Int128) ? "int128"
        : typeof(T) == typeof(UInt128) ? "uint128"
        : typeof(T) == typeof(System.Numerics.BigInteger) ? "bigint"
        : typeof(T).Name;

    /// <summary>
    /// Whether the array is the one a column of T is read from: a column of
    /// ints can be one of dates, so an int is read from an Int32Array and from
    /// nothing else, and the date cells are a wrapper the Bjolang side puts on
    /// one.
    /// </summary>
    public static bool Is<T>(IArrowArray a) =>
        typeof(T) == typeof(double) ? a is DoubleArray
        : typeof(T) == typeof(float) ? a is FloatArray
        : typeof(T) == typeof(long) ? a is Int64Array
        : typeof(T) == typeof(int) ? a is Int32Array
        : typeof(T) == typeof(short) ? a is Int16Array
        : typeof(T) == typeof(byte) ? a is UInt8Array
        : typeof(T) == typeof(ushort) ? a is UInt16Array
        : typeof(T) == typeof(uint) ? a is UInt32Array
        : typeof(T) == typeof(ulong) ? a is UInt64Array
        : typeof(T) == typeof(bool) ? a is BooleanArray
        : typeof(T) == typeof(Utf8String) && (a is StringArray || Categories.Is(a));

    /// <summary>
    /// Bjolang's name for the Arrow type of a column, for a message that names
    /// one. A type with no name here is named as Arrow names it.
    /// </summary>
    public static string ArrowName(IArrowType t) =>
        t is DoubleType ? "double"
        : t is FloatType ? "float"
        : t is Int64Type ? "long"
        : t is Int32Type ? "int"
        : t is Int16Type ? "short"
        : t is UInt8Type ? "byte"
        : t is UInt16Type ? "ushort"
        : t is UInt32Type ? "uint"
        : t is UInt64Type ? "ulong"
        : t is StringType ? "string"
        : t is Date32Type ? "date"
        : t is TimestampType { Timezone: not (null or "") } ? "instant"
        : t is TimestampType ? "datetime"
        : t is BooleanType ? "bool"
        : Categories.IsType(t) ? "category"
        : t.Name;

    /// <summary>
    /// Why a column cannot hold T, for the message that refuses it. A type
    /// Arrow has a column for, but not this one, says what is missing and what
    /// to do instead; anything else is a type a column is not.
    /// </summary>
    public static string Refusal<T>() =>
        typeof(T) == typeof(decimal)
            ? "a decimal carries a scale of its own, and an Arrow decimal column has one scale for the whole column: write it as a double, or as an integer with the scale stated"
        : typeof(T) == typeof(Int128) || typeof(T) == typeof(UInt128)
            ? "Arrow's widest integer column is decimal128 with precision 38, which reaches 10^38 - 1, and this type reaches further: write it as a long, or as two columns"
        : typeof(T) == typeof(System.Numerics.BigInteger)
            ? "an Arrow column has no integer wider than decimal128: write it as a double, or as a string"
        : $"a column holds a double, a float, a long, an int, a short, a byte, a ushort, a uint, a ulong, a bool or a string, and this is {Name<T>()}";
}
