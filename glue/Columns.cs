/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Columns: what (bjostat table) cannot reach in Apache Arrow from Bjolang.
//
// Bjolang imports no member of a generic .NET type, so the members of
// PrimitiveArray<T> are reached through generic static methods here. The
// builders of Arrow are generic too, and a table needs its schema made from
// the arrays. A column with nulls holds an unspecified value at a null, so
// the aggregates skip them by the validity bitmap, here, in one loop.

using System.Globalization;
using System.Numerics;
using System.Numerics.Tensors;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Bjostat;

public static class Columns
{
    // ---------------------------------------------------------------------
    // Any column

    /// <summary>The name of the Arrow type id: Double, Int64, String and so on.</summary>
    public static string TypeName(IArrowArray a) => a.Data.DataType.TypeId.ToString();

    public static int Length(IArrowArray a) => a.Length;

    public static int NullCount(IArrowArray a) => a.NullCount;

    public static bool IsValid(IArrowArray a, int i) => a.IsValid(i);

    /// <summary>A column as an array of any type, for a table to hold.</summary>
    public static IArrowArray Any<T>(PrimitiveArray<T> a) where T : struct, IEquatable<T> => a;

    public static IArrowArray AnyStrings(StringArray a) => a;

    // ---------------------------------------------------------------------
    // Typed columns. The caller has checked the type by its name, so a cast
    // that fails here is a fault in (bjostat table).

    public static PrimitiveArray<double> AsDoubles(IArrowArray a) => (DoubleArray)a;

    public static PrimitiveArray<float> AsFloats(IArrowArray a) => (FloatArray)a;

    public static PrimitiveArray<long> AsLongs(IArrowArray a) => (Int64Array)a;

    public static PrimitiveArray<int> AsInts(IArrowArray a) => (Int32Array)a;

    public static StringArray AsStrings(IArrowArray a) => (StringArray)a;

    /// <summary>
    /// The values, in place. At a null the value is unspecified: read
    /// <c>IsValid</c> for a column with nulls.
    /// </summary>
    public static ReadOnlySpan<T> Values<T>(PrimitiveArray<T> a) where T : struct, IEquatable<T> => a.Values;

    public static int TypedLength<T>(PrimitiveArray<T> a) where T : struct, IEquatable<T> => a.Length;

    public static int TypedNullCount<T>(PrimitiveArray<T> a) where T : struct, IEquatable<T> => a.NullCount;

    public static bool TypedIsValid<T>(PrimitiveArray<T> a, int i) where T : struct, IEquatable<T> => a.IsValid(i);

    public static BjolangRuntime.Option<T> Ref<T>(PrimitiveArray<T> a, int i) where T : struct, IEquatable<T> =>
        a.IsValid(i) ? BjolangRuntime.Some(a.Values[i]) : BjolangRuntime.None<T>();

    public static int StringsLength(StringArray a) => a.Length;

    public static int StringsNullCount(StringArray a) => a.NullCount;

    /// <summary>
    /// The string at the index, or None at a null. Arrow holds strings as
    /// UTF-8, as Bjolang does, so the bytes are checked and copied once.
    /// </summary>
    public static BjolangRuntime.Option<BjoString.Utf8String> StringRef(StringArray a, int i) =>
        a.IsValid(i)
            ? BjolangRuntime.Some(BjoString.Utf8String.FromUtf8(a.GetBytes(i)))
            : BjolangRuntime.None<BjoString.Utf8String>();

    // ---------------------------------------------------------------------
    // Building columns

    private static IArrowType ArrowTypeOf<T>() =>
        typeof(T) == typeof(double) ? DoubleType.Default
        : typeof(T) == typeof(float) ? FloatType.Default
        : typeof(T) == typeof(long) ? Int64Type.Default
        : typeof(T) == typeof(int) ? Int32Type.Default
        : typeof(T) == typeof(short) ? Int16Type.Default
        : typeof(T) == typeof(byte) ? UInt8Type.Default
        : throw new ArgumentException($"(bjostat table): a column of {typeof(T).Name} is not supported");

    /// <summary>
    /// A column of the values, copied. <paramref name="valid"/> is empty for a
    /// column with no nulls, or as long as the values, false at each null.
    /// </summary>
    public static PrimitiveArray<T> FromArray<T>(T[] values, bool[] valid) where T : struct, IEquatable<T>
    {
        CheckValid(values.Length, valid);
        var data = new ArrowBuffer.Builder<T>(values.Length).AppendRange(values).Build();
        var bitmap = ArrowBuffer.Empty;
        int nulls = 0;

        if (valid.Length > 0)
        {
            var bits = new ArrowBuffer.BitmapBuilder(valid.Length);
            foreach (bool v in valid)
            {
                bits.Append(v);
                if (!v) nulls++;
            }
            bitmap = bits.Build();
        }

        var arrayData = new ArrayData(ArrowTypeOf<T>(), values.Length, nulls, 0, new[] { bitmap, data });
        return (PrimitiveArray<T>)ArrowArrayFactory.BuildArray(arrayData);
    }

    private static void CheckValid(int n, bool[] valid)
    {
        if (valid.Length != 0 && valid.Length != n)
            throw new ArgumentException($"(bjostat table): {valid.Length} validity flags for {n} values");
    }

    /// <summary>A column of the strings. <paramref name="valid"/> is as for <c>FromArray</c>.</summary>
    public static StringArray FromStrings(string[] values, bool[] valid)
    {
        CheckValid(values.Length, valid);
        var b = new StringArray.Builder();
        for (int i = 0; i < values.Length; i++)
        {
            if (valid.Length > 0 && !valid[i]) b.AppendNull();
            else b.Append(values[i]);
        }
        return b.Build();
    }

    // ---------------------------------------------------------------------
    // Aggregates. Each skips the nulls. With none, the numbers go to
    // TensorPrimitives on the span, which is vectorized.

    public static T Sum<T>(PrimitiveArray<T> a) where T : struct, IEquatable<T>, INumber<T>
    {
        if (a.NullCount == 0) return TensorPrimitives.Sum(a.Values);

        var values = a.Values;
        T sum = T.Zero;
        for (int i = 0; i < values.Length; i++)
            if (a.IsValid(i)) sum += values[i];
        return sum;
    }

    /// <summary>The mean of the values that are not null, or NaN when there are none.</summary>
    public static double Mean<T>(PrimitiveArray<T> a) where T : struct, IEquatable<T>, INumber<T>
    {
        int n = a.Length - a.NullCount;
        if (n == 0) return double.NaN;
        return double.CreateChecked(Sum(a)) / n;
    }

    /// <summary>The least value that is not null, or None when there is none.</summary>
    public static BjolangRuntime.Option<T> Min<T>(PrimitiveArray<T> a) where T : struct, IEquatable<T>, INumber<T>
    {
        if (a.Length == a.NullCount) return BjolangRuntime.None<T>();
        if (a.NullCount == 0) return BjolangRuntime.Some(TensorPrimitives.Min(a.Values));

        var values = a.Values;
        T? best = null;
        for (int i = 0; i < values.Length; i++)
            if (a.IsValid(i) && (best is null || values[i] < best.Value)) best = values[i];
        return BjolangRuntime.Some(best!.Value);
    }

    /// <summary>The greatest value that is not null, or None when there is none.</summary>
    public static BjolangRuntime.Option<T> Max<T>(PrimitiveArray<T> a) where T : struct, IEquatable<T>, INumber<T>
    {
        if (a.Length == a.NullCount) return BjolangRuntime.None<T>();
        if (a.NullCount == 0) return BjolangRuntime.Some(TensorPrimitives.Max(a.Values));

        var values = a.Values;
        T? best = null;
        for (int i = 0; i < values.Length; i++)
            if (a.IsValid(i) && (best is null || values[i] > best.Value)) best = values[i];
        return BjolangRuntime.Some(best!.Value);
    }

    // ---------------------------------------------------------------------
    // Text of a cell, for a printed table

    public static string CellText(IArrowArray a, int i)
    {
        if (a.IsNull(i)) return "null";
        var c = CultureInfo.InvariantCulture;
        return a switch
        {
            DoubleArray d => d.GetValue(i)!.Value.ToString("R", c),
            FloatArray f => f.GetValue(i)!.Value.ToString("R", c),
            Int64Array l => l.GetValue(i)!.Value.ToString(c),
            Int32Array n => n.GetValue(i)!.Value.ToString(c),
            Int16Array s => s.GetValue(i)!.Value.ToString(c),
            UInt8Array b => b.GetValue(i)!.Value.ToString(c),
            BooleanArray b => b.GetValue(i)!.Value ? "#t" : "#f",
            StringArray s => s.GetString(i),
            _ => "<" + a.Data.DataType.Name + ">",
        };
    }
}

public static class Tables
{
    /// <summary>A table of the columns, all of one length, under the names.</summary>
    public static RecordBatch Make(string[] names, IArrowArray[] columns, int length)
    {
        var schema = new Schema.Builder();
        for (int i = 0; i < names.Length; i++)
        {
            var type = columns[i].Data.DataType;
            schema.Field(f => f.Name(names[i]).DataType(type).Nullable(true));
        }
        return new RecordBatch(schema.Build(), columns, length);
    }

    public static string[] Names(RecordBatch t) => t.Schema.FieldsList.Select(f => f.Name).ToArray();

    public static int ColumnCount(RecordBatch t) => t.ColumnCount;

    public static int RowCount(RecordBatch t) => t.Length;

    public static IArrowArray Column(RecordBatch t, int i) => t.Column(i);

    /// <summary>The index of the column, or -1.</summary>
    public static int IndexOf(RecordBatch t, string name) => t.Schema.GetFieldIndex(name);

    /// <summary>The rows from <paramref name="from"/> up to <paramref name="to"/>, without a copy.</summary>
    public static RecordBatch Slice(RecordBatch t, int from, int to) => t.Slice(from, to - from);

    // ---------------------------------------------------------------------
    // Arrow IPC files

    /// <summary>
    /// The record batches of an Arrow IPC file as one table: each column's
    /// pieces are concatenated, so that a column is one span.
    /// </summary>
    public static RecordBatch ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new ArrowFileReader(stream);
        var batches = new List<RecordBatch>();
        RecordBatch? b;
        while ((b = reader.ReadNextRecordBatch()) != null) batches.Add(b);

        var schema = reader.Schema;
        if (batches.Count == 1) return batches[0];
        if (batches.Count == 0)
            throw new InvalidDataException($"{path} has a schema and no record batch, so it has no rows to read.");

        var columns = new IArrowArray[schema.FieldsList.Count];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = ArrowArrayConcatenator.Concatenate(batches.Select(x => x.Column(i)).ToList());
        return new RecordBatch(schema, columns, batches.Sum(x => x.Length));
    }

    public static void WriteFile(RecordBatch t, string path)
    {
        using var stream = File.Create(path);
        using var writer = new ArrowFileWriter(stream, t.Schema);
        writer.WriteRecordBatch(t);
        writer.WriteEnd();
    }
}
