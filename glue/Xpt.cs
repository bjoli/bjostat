/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Xpt: SAS transport files of version 5 (XPORT), the format of NHANES and of
// the FDA, into tables.
//
// The file is records of 80 bytes. Its library header comes first, then each
// member, a dataset: a member header, two records of the member's name and
// label, a header of the number of variables, the descriptors of the
// variables (one "namestr" of 140 bytes each, packed and padded to 80), the
// observation header, and the rows, packed and padded to 80 with blanks. The
// next member header, or the end of the file, ends the rows.
//
// A number is an IBM System/360 float, big-endian: a sign bit, an exponent of
// 16 offset by 64 in seven bits, and a fraction of 56 bits. A variable of
// fewer than 8 bytes keeps the first bytes of the 8. A missing value is '.'
// (or .A to .Z, or ._) in the first byte and zeros after it: a null, as an
// empty text, which SAS cannot tell from a missing one.
//
// A number of a date format (DATE9., MMDDYY10. and the others) is days since
// 1960-01-01, and becomes a column of dates; one of a datetime format is
// seconds since 1960-01-01T00:00, and becomes one of timestamps with no zone.
// Text is Latin-1, the trailing blanks dropped.
//
// The rows are read 80 bytes at a time through a buffer, so a file is never
// held whole: the minute files of NHANES are gigabytes. Where the rows end, a
// row of blanks is the padding when it lies in the last record, as in
// pandas and haven.

using System.Runtime.InteropServices;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Bjostat;

public static class Xpt
{
    private const int RecordLength = 80;

    private static readonly byte[] LibraryHeader = Ascii("HEADER RECORD*******LIBRARY HEADER RECORD!!!!!!!");
    private static readonly byte[] LibraryV8Header = Ascii("HEADER RECORD*******LIBV8   HEADER RECORD!!!!!!!");
    private static readonly byte[] MemberHeader = Ascii("HEADER RECORD*******MEMBER  HEADER RECORD!!!!!!!");
    private static readonly byte[] MemberV8Header = Ascii("HEADER RECORD*******MEMBV8  HEADER RECORD!!!!!!!");
    private static readonly byte[] DescriptorHeader = Ascii("HEADER RECORD*******DSCRPTR HEADER RECORD!!!!!!!");
    private static readonly byte[] NamestrHeader = Ascii("HEADER RECORD*******NAMESTR HEADER RECORD!!!!!!!");
    private static readonly byte[] ObsHeader = Ascii("HEADER RECORD*******OBS     HEADER RECORD!!!!!!!");
    private static readonly byte[] AnyHeader = Ascii("HEADER RECORD*******");

    // The kinds of a variable's column.
    private const int Number = 0;
    private const int Text = 1;
    private const int Date = 2;
    private const int DateTime = 3;

    // 1960-01-01, SAS's day 0, is 3653 days before 1970-01-01, and so many
    // seconds.
    private const int DaysTo1970 = 3653;
    private const double SecondsTo1970 = 3653.0 * 86400.0;

    // ---------------------------------------------------------------------
    // What Bjolang calls

    /// <summary>
    /// The table of the member of the file named <paramref name="member"/>,
    /// or of its first member when that is "". A TableProblem says what is
    /// wrong with the file.
    /// </summary>
    public static RecordBatch Read(string path, string member) =>
        WithFile(path, records => ReadMember(records, Find(records, path, member)));

    /// <summary>
    /// A table of a row for each variable of the member: its name, label,
    /// the type of its column, its length in bytes and its SAS format.
    /// </summary>
    public static RecordBatch Variables(string path, string member) =>
        WithFile(path, records =>
        {
            var m = Find(records, path, member);
            var names = new TextColumn();
            var labels = new TextColumn();
            var types = new TextColumn();
            var formats = new TextColumn();
            var lengths = new List<int>();
            foreach (var v in m.Variables)
            {
                names.Add(v.Name);
                labels.Add(v.Label);
                types.Add(v.Kind switch { Number => "double", Text => "string", Date => "date", _ => "datetime" });
                formats.Add(v.Format);
                lengths.Add(v.Length);
            }
            var lengthBytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(lengths)).ToArray();
            var lengthColumn = ArrowArrayFactory.BuildArray(new ArrayData(Int32Type.Default, lengths.Count, 0, 0,
                new[] { ArrowBuffer.Empty, new ArrowBuffer(lengthBytes) }));
            return Tables.Make(["name", "label", "type", "length", "format"],
                [names.Build(), labels.Build(), types.Build(), lengthColumn, formats.Build()],
                lengths.Count);
        });

    /// <summary>The names of the members, the datasets, of the file.</summary>
    public static string[] Members(string path) =>
        WithFile(path, records =>
        {
            Start(records, path);
            var names = new List<string>();
            while (records.Next(out var r))
            {
                if (!r.StartsWith(MemberHeader))
                    throw Problem(path, "a member header was expected where the last member's rows end.");
                var m = MemberAfterHeader(records, path, r);
                names.Add(m.Name);
                SkipRows(records);
            }
            return names.ToArray();
        });

    // ---------------------------------------------------------------------
    // The headers

    private sealed class Variable
    {
        public string Name = "";
        public string Label = "";
        public string Format = "";
        public int Kind;
        public int Length;
        public int Position;
    }

    private sealed class Member
    {
        public string Name = "";
        public string Label = "";
        public Variable[] Variables = [];
        public int RowLength;
    }

    private static T WithFile<T>(string path, Func<Records, T> read)
    {
        Stream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new TableProblem($"{path}: {e.Message}");
        }
        using (stream)
        {
            try
            {
                return read(new Records(stream));
            }
            catch (IOException e)
            {
                throw new TableProblem($"{path}: {e.Message}");
            }
        }
    }

    private static TableProblem Problem(string path, string what) =>
        new($"{path} is not a SAS transport file of version 5: {what}");

    // The library header and its two records.
    private static void Start(Records records, string path)
    {
        if (!records.Next(out var first)) throw Problem(path, "it is empty.");
        if (first.StartsWith(LibraryV8Header))
            throw new TableProblem($"{path} is a SAS transport file of version 8, and read-xpt reads version 5, "
                                   + "the format of NHANES. Version 8 has long names; save the dataset with the "
                                   + "XPORT engine, or as CSV.");
        if (!first.StartsWith(LibraryHeader))
            throw Problem(path, "it does not start with the library header. A file of SAS's CPORT, "
                                + "or a .sas7bdat, is another format.");
        if (!records.Next(out _) || !records.Next(out _)) throw Problem(path, "the library header is cut short.");
    }

    // The member named `wanted`, or the first when it is "", with the
    // records at its rows.
    private static Member Find(Records records, string path, string wanted)
    {
        Start(records, path);
        var seen = new List<string>();
        while (records.Next(out var r))
        {
            if (!r.StartsWith(MemberHeader))
                throw Problem(path, "a member header was expected where the last member's rows end.");
            var m = MemberAfterHeader(records, path, r);
            if (wanted == "" || string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase)) return m;
            seen.Add(m.Name);
            SkipRows(records);
        }
        if (seen.Count == 0) throw Problem(path, "it has no member.");
        throw new TableProblem($"{path} has no member '{wanted}'. Its members are: {string.Join(", ", seen)}.");
    }

    private static Member MemberAfterHeader(Records records, string path, ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(MemberV8Header))
            throw new TableProblem($"{path} has a member of version 8, and read-xpt reads version 5.");
        int size = Digits(header.Slice(74, 4));
        if (size != 140 && size != 136) throw Problem(path, $"a variable's descriptor is {size} bytes, not 140.");

        if (!records.Next(out var d) || !d.StartsWith(DescriptorHeader))
            throw Problem(path, "the descriptor header is missing.");
        var m = new Member();
        if (!records.Next(out var first)) throw Problem(path, "a member header is cut short.");
        m.Name = Latin1(first.Slice(8, 8));
        if (!records.Next(out var second)) throw Problem(path, "a member header is cut short.");
        m.Label = Latin1(second.Slice(32, 40));

        if (!records.Next(out var n) || !n.StartsWith(NamestrHeader))
            throw Problem(path, $"the header of the variables of '{m.Name}' is missing.");
        int count = Digits(n.Slice(54, 4));
        if (count < 0) throw Problem(path, $"the number of the variables of '{m.Name}' is not a number.");

        // The descriptors, packed over records and padded to a whole one.
        int total = count * size;
        var bytes = new byte[(total + RecordLength - 1) / RecordLength * RecordLength];
        for (int at = 0; at < bytes.Length; at += RecordLength)
        {
            if (!records.Next(out var r) || r.Length < RecordLength)
                throw Problem(path, $"the variables of '{m.Name}' are cut short.");
            r.CopyTo(bytes.AsSpan(at));
        }
        m.Variables = new Variable[count];
        for (int i = 0; i < count; i++)
        {
            var v = Descriptor(bytes.AsSpan(i * size, size), path, m.Name);
            m.Variables[i] = v;
            m.RowLength = Math.Max(m.RowLength, v.Position + v.Length);
        }

        // Version 8's labels would come here; then the rows.
        while (true)
        {
            if (!records.Next(out var r)) throw Problem(path, $"the rows of '{m.Name}' have no header.");
            if (r.StartsWith(ObsHeader)) return m;
            if (r.StartsWith(AnyHeader))
                throw new TableProblem($"{path}: '{m.Name}' has a header of version 8 "
                                       + $"({Latin1(r.Slice(20, 8))}), and read-xpt reads version 5.");
            throw Problem(path, $"the rows of '{m.Name}' have no header.");
        }
    }

    // One namestr: big-endian shorts and an int among names padded with
    // blanks.
    private static Variable Descriptor(ReadOnlySpan<byte> d, string path, string member)
    {
        int type = Short(d, 0);
        var v = new Variable
        {
            Length = Short(d, 4),
            Name = Latin1(d.Slice(8, 8)),
            Label = Latin1(d.Slice(16, 40)),
            Format = Latin1(d.Slice(56, 8)).ToUpperInvariant(),
            Position = Int(d, 84),
        };
        if (type == 1)
        {
            if (v.Length < 2 || v.Length > 8)
                throw Problem(path, $"'{v.Name}' of '{member}' is a number of {v.Length} bytes; a number is 2 to 8.");
            v.Kind = IsDateFormat(v.Format) ? Date : IsDateTimeFormat(v.Format) ? DateTime : Number;
        }
        else if (type == 2)
        {
            if (v.Length < 1) throw Problem(path, $"'{v.Name}' of '{member}' is text of {v.Length} bytes.");
            v.Kind = Text;
        }
        else throw Problem(path, $"'{v.Name}' of '{member}' is of type {type}; a variable is 1, a number, or 2, text.");
        if (v.Position < 0) throw Problem(path, $"'{v.Name}' of '{member}' is at {v.Position} in a row.");
        return v;
    }

    private static bool IsDateFormat(string f) =>
        f.StartsWith("MMDDYY") || f.StartsWith("DDMMYY") || f.StartsWith("YYMMDD")
        || f is "DATE" or "DAY" or "E8601DA" or "B8601DA" or "IS8601DA" or "JULIAN" or "MONYY" or "MMYY"
            or "YYMM" or "YYMON" or "YYQ" or "YEAR" or "QTR" or "MONTH" or "WEEKDATE" or "WEEKDATX"
            or "WEEKDAY" or "WORDDATE" or "WORDDATX" or "DOWNAME" or "MONNAME" or "NLDATE" or "EURDFDD";

    private static bool IsDateTimeFormat(string f) =>
        f is "DATETIME" or "E8601DT" or "B8601DT" or "IS8601DT" or "E8601DZ" or "DATEAMPM" or "MDYAMPM"
            or "NLDATM";

    // ---------------------------------------------------------------------
    // The rows

    // Past the rows of a member, to the next member header or the end.
    private static void SkipRows(Records records)
    {
        while (records.Next(out var r))
        {
            if (r.StartsWith(MemberHeader) || r.StartsWith(MemberV8Header))
            {
                records.Unread();
                return;
            }
        }
    }

    private static RecordBatch ReadMember(Records records, Member m)
    {
        var vars = m.Variables;
        var numbers = new NumberColumn?[vars.Length];
        var texts = new TextColumn?[vars.Length];
        for (int i = 0; i < vars.Length; i++)
            if (vars[i].Kind == Text) texts[i] = new TextColumn();
            else numbers[i] = new NumberColumn();

        int width = m.RowLength;
        var row = new byte[Math.Max(width, 1)];
        var blank = new byte[width];
        blank.AsSpan().Fill((byte)' ');
        int filled = 0;
        long regionBytes = 0;
        int pendingBlanks = 0;

        void Add(ReadOnlySpan<byte> r)
        {
            for (int i = 0; i < vars.Length; i++)
            {
                var v = vars[i];
                var cell = r.Slice(v.Position, v.Length);
                if (texts[i] is { } t) t.AddLatin1(cell);
                else numbers[i]!.Add(cell);
            }
        }

        while (width > 0 && records.Next(out var r))
        {
            if (r.StartsWith(MemberHeader) || r.StartsWith(MemberV8Header))
            {
                records.Unread();
                break;
            }
            regionBytes += r.Length;
            while (!r.IsEmpty)
            {
                int take = Math.Min(width - filled, r.Length);
                r[..take].CopyTo(row.AsSpan(filled));
                r = r[take..];
                filled += take;
                if (filled < width) continue;
                filled = 0;
                // A row of blanks may be the padding; it is a row when one
                // that is not blank comes after it.
                if (row.AsSpan().IndexOfAnyExcept((byte)' ') < 0) { pendingBlanks++; continue; }
                for (; pendingBlanks > 0; pendingBlanks--) Add(blank);
                Add(row);
            }
        }

        // The rows of blanks at the end that lie in the padding, under 80
        // bytes, are not rows.
        if (width > 0)
        {
            long rows = regionBytes / width;
            long padding = regionBytes - rows * width;
            while (pendingBlanks > 0 && padding + width < RecordLength)
            {
                pendingBlanks--;
                padding += width;
            }
            for (; pendingBlanks > 0; pendingBlanks--) Add(blank);
        }

        var columns = new IArrowArray[vars.Length];
        for (int i = 0; i < vars.Length; i++)
            columns[i] = texts[i] is { } t ? t.Build() : numbers[i]!.Build(vars[i]);
        int length = vars.Length == 0 ? 0 : columns[0].Length;
        return Tables.Make(vars.Select(v => v.Name).ToArray(), columns, length);
    }

    /// <summary>
    /// The IBM float of the bytes, or false at a missing value. Fewer than 8
    /// bytes are the first of the 8, the rest zero.
    /// </summary>
    internal static bool IbmDouble(ReadOnlySpan<byte> s, out double value)
    {
        ulong fraction = 0;
        for (int i = 1; i < s.Length; i++) fraction |= (ulong)s[i] << (56 - 8 * i);
        byte first = s[0];
        if (fraction == 0)
        {
            value = (first & 0x80) != 0 ? -0.0 : 0.0;
            // '.', '_' and 'A' to 'Z' are SAS's missing values.
            return !(first == (byte)'.' || first == (byte)'_' || (first >= (byte)'A' && first <= (byte)'Z'));
        }
        int exponent = (first & 0x7F) - 64;
        value = Math.ScaleB((double)fraction, 4 * exponent - 56);
        if ((first & 0x80) != 0) value = -value;
        return true;
    }

    // ---------------------------------------------------------------------
    // Columns

    private sealed class NumberColumn
    {
        private double[] _values = new double[1024];
        private byte[] _valid = new byte[128];
        private int _count;
        private int _nulls;

        public void Add(ReadOnlySpan<byte> cell)
        {
            if (_count == _values.Length)
            {
                System.Array.Resize(ref _values, _values.Length * 2);
                System.Array.Resize(ref _valid, _values.Length / 8);
            }
            if (IbmDouble(cell, out var v))
            {
                _values[_count] = v;
                _valid[_count >> 3] |= (byte)(1 << (_count & 7));
            }
            else _nulls++;
            _count++;
        }

        public IArrowArray Build(Variable v)
        {
            var bitmap = _nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(_valid.AsSpan(0, (_count + 7) / 8).ToArray());
            byte[] bytes;
            IArrowType type;
            switch (v.Kind)
            {
                case Date:
                {
                    var days = new int[_count];
                    for (int i = 0; i < _count; i++)
                    {
                        if (!Valid(i)) continue;
                        double d = Math.Floor(_values[i]) - DaysTo1970;
                        if (!(d >= int.MinValue && d <= int.MaxValue))
                            throw new TableProblem($"'{v.Name}' has the date {_values[i]} in row {i}, "
                                                   + "which is not a day a date column holds.");
                        days[i] = (int)d;
                    }
                    bytes = MemoryMarshal.AsBytes(days.AsSpan()).ToArray();
                    type = TimeKind.ArrowType(TimeKind.Date);
                    break;
                }
                case DateTime:
                {
                    var micros = new long[_count];
                    for (int i = 0; i < _count; i++)
                    {
                        if (!Valid(i)) continue;
                        double us = Math.Round((_values[i] - SecondsTo1970) * 1e6);
                        if (!(us >= long.MinValue && us <= long.MaxValue))
                            throw new TableProblem($"'{v.Name}' has the datetime {_values[i]} in row {i}, "
                                                   + "which is not one a timestamp column holds.");
                        micros[i] = (long)us;
                    }
                    bytes = MemoryMarshal.AsBytes(micros.AsSpan()).ToArray();
                    type = TimeKind.ArrowType(TimeKind.DateTime);
                    break;
                }
                default:
                    bytes = MemoryMarshal.AsBytes(_values.AsSpan(0, _count)).ToArray();
                    type = DoubleType.Default;
                    break;
            }
            return ArrowArrayFactory.BuildArray(new ArrayData(type, _count, _nulls, 0,
                new[] { bitmap, new ArrowBuffer(bytes) }));
        }

        private bool Valid(int i) => (_valid[i >> 3] & (1 << (i & 7))) != 0;
    }

    private sealed class TextColumn
    {
        private byte[] _bytes = new byte[4096];
        private int[] _offsets = new int[1025];
        private byte[] _valid = new byte[128];
        private int _byteCount;
        private int _count;
        private int _nulls;

        /// <summary>A cell of Latin-1, blanks after it dropped; a null when nothing is left.</summary>
        public void AddLatin1(ReadOnlySpan<byte> cell)
        {
            int end = cell.LastIndexOfAnyExcept((byte)' ', (byte)0);
            cell = cell[..(end + 1)];
            Reserve(cell.Length * 2);
            if (cell.IsEmpty) _nulls++;
            else
            {
                _valid[_count >> 3] |= (byte)(1 << (_count & 7));
                foreach (byte b in cell)
                {
                    if (b < 0x80) _bytes[_byteCount++] = b;
                    else
                    {
                        _bytes[_byteCount++] = (byte)(0xC0 | (b >> 6));
                        _bytes[_byteCount++] = (byte)(0x80 | (b & 0x3F));
                    }
                }
            }
            _offsets[++_count] = _byteCount;
        }

        /// <summary>A string, a null when it is empty.</summary>
        public void Add(string s)
        {
            var utf8 = Encoding.UTF8.GetBytes(s);
            Reserve(utf8.Length);
            if (utf8.Length == 0) _nulls++;
            else _valid[_count >> 3] |= (byte)(1 << (_count & 7));
            utf8.CopyTo(_bytes.AsSpan(_byteCount));
            _byteCount += utf8.Length;
            _offsets[++_count] = _byteCount;
        }

        private void Reserve(int bytes)
        {
            if (_count + 1 == _offsets.Length)
            {
                System.Array.Resize(ref _offsets, (_offsets.Length - 1) * 2 + 1);
                System.Array.Resize(ref _valid, (_offsets.Length + 7) / 8);
            }
            if (_byteCount + bytes > _bytes.Length)
                System.Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _byteCount + bytes));
        }

        public IArrowArray Build()
        {
            var bitmap = _nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(_valid.AsSpan(0, (_count + 7) / 8).ToArray());
            var offsets = MemoryMarshal.AsBytes(_offsets.AsSpan(0, _count + 1)).ToArray();
            return ArrowArrayFactory.BuildArray(new ArrayData(StringType.Default, _count, _nulls, 0,
                new[] { bitmap, new ArrowBuffer(offsets), new ArrowBuffer(_bytes.AsSpan(0, _byteCount).ToArray()) }));
        }
    }

    // ---------------------------------------------------------------------
    // Records of 80 bytes through a buffer. The last may be shorter, in a
    // file whose end was cut off.

    private sealed class Records(Stream stream)
    {
        private readonly byte[] _buffer = new byte[RecordLength * 4096];
        private int _at;
        private int _end;
        private int _last = -1;
        private int _lastLength;
        private bool _unread;

        public bool Next(out ReadOnlySpan<byte> record)
        {
            if (_unread)
            {
                _unread = false;
                record = _buffer.AsSpan(_last, _lastLength);
                return true;
            }
            if (_end - _at < RecordLength && !Fill())
            {
                record = default;
                return false;
            }
            int n = Math.Min(RecordLength, _end - _at);
            _last = _at;
            _lastLength = n;
            _at += n;
            record = _buffer.AsSpan(_last, n);
            return true;
        }

        /// <summary>The last record is given again by the next Next.</summary>
        public void Unread() => _unread = true;

        // Moves what is left to the start and reads after it; false at the end.
        private bool Fill()
        {
            int left = _end - _at;
            _buffer.AsSpan(_at, left).CopyTo(_buffer);
            _at = 0;
            _end = left;
            int read;
            while (_end < _buffer.Length && (read = stream.Read(_buffer, _end, _buffer.Length - _end)) > 0)
                _end += read;
            return _end > 0;
        }
    }

    // ---------------------------------------------------------------------
    // Bytes

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static string Latin1(ReadOnlySpan<byte> s) => Encoding.Latin1.GetString(s).TrimEnd(' ', '\0');

    private static int Short(ReadOnlySpan<byte> s, int at) => (short)((s[at] << 8) | s[at + 1]);

    private static int Int(ReadOnlySpan<byte> s, int at) =>
        (s[at] << 24) | (s[at + 1] << 16) | (s[at + 2] << 8) | s[at + 3];

    private static int Digits(ReadOnlySpan<byte> s)
    {
        int n = 0;
        foreach (byte b in s)
        {
            if (b < (byte)'0' || b > (byte)'9') return -1;
            n = n * 10 + (b - '0');
        }
        return n;
    }
}
