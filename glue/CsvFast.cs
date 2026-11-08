/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// CsvFast: the pieces of a large CSV file into columns, with no string made
// for a field and no array for a row.
//
// (text csv) gives each row as an array of strings, which CsvColumn then
// parses. Here each field is a span of the file's bytes, given to its
// CsvColumn as it is found: a quoted field with a doubled quote is the one
// field that is copied, into a buffer that is used again. read-csv cuts the
// bytes with (text csv)'s csv-cut-rows, and the pieces are read on the
// compute threads.
//
// It reads what (text csv) reads, RFC 4180, or gives up: a row ends at a
// line feed, after a carriage return or not; a quoted field may hold the
// separator and line feeds, and two quotes are one. On anything else (a
// blank line, a carriage return or a quote inside an unquoted field, a row
// of another number of fields, a field that is not of its column's type)
// it answers None, and read-csv reads the file on one thread as before,
// which says what is wrong and where.

using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using BjoString;

namespace Bjostat;

public static class CsvFast
{
    /// <summary>
    /// The names in the header line, the bytes up to <paramref name="headerEnd"/>,
    /// or None: also when they are not UTF-8.
    /// </summary>
    public static BjolangRuntime.Option<Utf8String[]> Names(byte[] bytes, int headerEnd, int separator)
    {
        var header = bytes.AsSpan(0, headerEnd);
        if (!System.Text.Unicode.Utf8.IsValid(header)) return BjolangRuntime.None<Utf8String[]>();
        var names = new List<Utf8String>();
        var reader = new Fields(header, (byte)separator);
        while (true)
        {
            var step = reader.Next(out var field);
            if (step == Step.Bad || step == Step.End) return BjolangRuntime.None<Utf8String[]>();
            names.Add(Utf8String.FromUtf8(field.ToArray()));
            if (step == Step.RowEnd) return BjolangRuntime.Some(names.ToArray());
        }
    }

    /// <summary>
    /// The kind of each of the <paramref name="width"/> columns, guessed from
    /// the first <paramref name="rows"/> rows of the bytes from
    /// <paramref name="from"/> to <paramref name="to"/>, rows after the
    /// header, as CsvColumn guesses a field, String for a column of nulls
    /// only; or None.
    /// </summary>
    public static BjolangRuntime.Option<int[]> Guess(byte[] bytes, int from, int to, int width, int separator, int rows,
                                                     Utf8String[] nulls)
    {
        var kinds = new int[width];
        System.Array.Fill(kinds, CsvColumn.Empty);
        var spelled = Spelled(nulls);
        var reader = new Fields(bytes.AsSpan(from, to - from), (byte)separator);
        for (int row = 0; row < rows; row++)
        {
            for (int c = 0; ; c++)
            {
                var step = reader.Next(out var field);
                if (step == Step.End && c == 0) goto done;
                if (step == Step.Bad || step == Step.End || c >= width) return BjolangRuntime.None<int[]>();
                if (!IsNull(field, spelled))
                    kinds[c] = CsvColumn.Join(kinds[c], CsvColumn.GuessSpan(field));
                if (step == Step.RowEnd)
                {
                    if (c != width - 1) return BjolangRuntime.None<int[]>();
                    break;
                }
            }
        }
        done:
        for (int c = 0; c < width; c++)
            if (kinds[c] == CsvColumn.Empty) kinds[c] = CsvColumn.String;
        return BjolangRuntime.Some(kinds);
    }

    /// <summary>
    /// The table of the pieces of the bytes, piece k from cuts[k] to
    /// cuts[k + 1], each whole rows after the header, read on the compute
    /// threads with the columns of the kinds; or None. A column that
    /// <paramref name="guessed"/> says was guessed as long becomes doubles in
    /// a piece with a decimal, and then in all of them.
    /// </summary>
    public static BjolangRuntime.Option<RecordBatch> Read(byte[] bytes, int[] cuts, Utf8String[] names, int[] kinds,
                                                          bool[] guessed, int separator, Utf8String[] nulls)
    {
        var spelled = Spelled(nulls);
        int count = cuts.Length - 1;
        var parts = new CsvColumn[count][];
        int failed = 0;
        Workers.For(count, k =>
        {
            var columns = ReadChunk(bytes.AsSpan(cuts[k], cuts[k + 1] - cuts[k]), kinds, guessed, (byte)separator, spelled);
            if (columns == null) Interlocked.Exchange(ref failed, 1);
            parts[k] = columns!;
        });
        if (failed != 0) return BjolangRuntime.None<RecordBatch>();

        int width = kinds.Length;
        var fields = new Field[width];
        var columnsOut = new IArrowArray[width];
        Workers.For(width, c =>
        {
            bool widened = parts.Any(p => p[c].Kind == CsvColumn.Double) && kinds[c] == CsvColumn.Long;
            var pieces = new List<IArrowArray>(parts.Length);
            foreach (var p in parts)
            {
                if (widened) p[c].WidenToDouble();
                pieces.Add(p[c].Build());
            }
            var type = pieces[0].Data.DataType;
            columnsOut[c] = pieces.Count == 1 ? pieces[0] : Reshape.Concat(pieces, type);
            fields[c] = new Field(names[c].ToString(), type, true);
        });
        var schema = new Schema.Builder();
        foreach (var f in fields) schema.Field(f);
        int length = width == 0 ? 0 : columnsOut[0].Length;
        return BjolangRuntime.Some(new RecordBatch(schema.Build(), columnsOut, length));
    }

    // The rows of one piece into new columns, or null: also when its bytes
    // are not UTF-8, which is checked here, on the piece's thread.
    private static CsvColumn[]? ReadChunk(ReadOnlySpan<byte> chunk, int[] kinds, bool[] guessed, byte separator, byte[][] spelled)
    {
        if (!System.Text.Unicode.Utf8.IsValid(chunk)) return null;
        int width = kinds.Length;
        var columns = new CsvColumn[width];
        for (int c = 0; c < width; c++) columns[c] = new CsvColumn(kinds[c]);
        var reader = new Fields(chunk, separator);
        while (true)
        {
            for (int c = 0; ; c++)
            {
                var step = reader.Next(out var field);
                if (step == Step.End && c == 0) return columns;
                if (step == Step.Bad || step == Step.End || c >= width) return null;
                if (IsNull(field, spelled)) field = default;
                var column = columns[c];
                if (!column.AddSpan(field))
                {
                    // A guessed column of longs that meets a decimal becomes
                    // doubles, as on one thread.
                    if (!(guessed[c] && CsvColumn.GuessSpan(field) == CsvColumn.Double
                          && column.WidenToDouble() && column.AddSpan(field)))
                        return null;
                }
                if (step == Step.RowEnd)
                {
                    if (c != width - 1) return null;
                    break;
                }
            }
        }
    }

    private static byte[][] Spelled(Utf8String[] nulls) => nulls.Select(n => n.AsSpan().ToArray()).ToArray();

    private static bool IsNull(ReadOnlySpan<byte> field, byte[][] spelled)
    {
        foreach (var s in spelled)
            if (field.SequenceEqual(s)) return true;
        return false;
    }

    private enum Step { Field, RowEnd, End, Bad }

    /// <summary>The fields of CSV bytes, one at a time.</summary>
    private ref struct Fields(ReadOnlySpan<byte> text, byte separator)
    {
        private readonly ReadOnlySpan<byte> _text = text;
        private int _at;
        // A quoted field with doubled quotes, without them.
        private byte[] _unquoted = new byte[256];

        /// <summary>
        /// The next field: Field when a separator follows it, RowEnd when the
        /// end of its row does, End when there is nothing left (no field),
        /// Bad when the text is not what this reader reads.
        /// </summary>
        public Step Next(out ReadOnlySpan<byte> field)
        {
            field = default;
            var text = _text;
            if (_at >= text.Length) return Step.End;
            if (text[_at] == (byte)'"') return Quoted(out field);
            int start = _at;
            int i = text.Slice(start).IndexOfAny(separator, (byte)'\n', (byte)'"');
            if (i < 0) i = text.Length - start;
            int end = start + i;
            if (end < text.Length && text[end] == (byte)'"') return Step.Bad;
            field = text.Slice(start, i);
            if (field.IndexOf((byte)'\r') >= 0)
            {
                // A carriage return only before the line feed that ends the row.
                if (field.IndexOf((byte)'\r') != field.Length - 1 || end >= text.Length || text[end] != (byte)'\n')
                    return Step.Bad;
                field = field[..^1];
            }
            return After(end);
        }

        private Step Quoted(out ReadOnlySpan<byte> field)
        {
            var text = _text;
            int start = _at + 1, at = start;
            int length = 0;
            bool copied = false;
            while (true)
            {
                int i = text.Slice(at).IndexOf((byte)'"');
                if (i < 0) { field = default; return Step.Bad; }
                int quote = at + i;
                if (quote + 1 < text.Length && text[quote + 1] == (byte)'"')
                {
                    // Two quotes: one quote in the field, which is copied.
                    Append(text.Slice(at, quote + 1 - at), ref length);
                    copied = true;
                    at = quote + 2;
                    continue;
                }
                if (copied)
                {
                    Append(text.Slice(at, quote - at), ref length);
                    field = _unquoted.AsSpan(0, length);
                }
                else field = text.Slice(start, quote - start);
                int end = quote + 1;
                if (end < text.Length && text[end] == (byte)'\r')
                {
                    if (end + 1 >= text.Length || text[end + 1] != (byte)'\n') return Step.Bad;
                    end++;
                }
                if (end < text.Length && text[end] != separator && text[end] != (byte)'\n') return Step.Bad;
                return After(end);
            }
        }

        private void Append(ReadOnlySpan<byte> bytes, ref int length)
        {
            if (length + bytes.Length > _unquoted.Length)
                System.Array.Resize(ref _unquoted, Math.Max(_unquoted.Length * 2, length + bytes.Length));
            bytes.CopyTo(_unquoted.AsSpan(length));
            length += bytes.Length;
        }

        // The step after a field that ends at `end`: a separator, a line
        // feed, or the end of the text. A blank line is Bad, as (text csv)
        // skips it, which this reader does not do.
        private Step After(int end)
        {
            var text = _text;
            if (end >= text.Length)
            {
                _at = end;
                return Step.RowEnd;
            }
            if (text[end] == separator)
            {
                _at = end + 1;
                // A separator at the very end leaves an empty last field.
                if (_at >= text.Length) return Step.Bad;
                return Step.Field;
            }
            _at = end + 1;
            if (_at < text.Length && (text[_at] == (byte)'\n' || text[_at] == (byte)'\r')) return Step.Bad;
            return Step.RowEnd;
        }
    }
}
