// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using BjoString;

namespace Bjostat;

/// <summary>
/// A CSV file cut into chunks of whole rows, so that read-csv can read them
/// on bjostat's workers. Each chunk starts with the file's header line.
/// </summary>
public static class CsvChunks
{
    /// <summary>
    /// The file in <paramref name="parts"/> chunks of about one size, each the
    /// header line and then whole rows, or no chunks: when the file is smaller
    /// than <paramref name="minBytes"/>, when it cannot be read, when it starts
    /// with a byte order mark, when it has no header line, when a quote is not
    /// closed, or when its bytes are not UTF-8. Then read-csv reads it on one
    /// thread, which says what is wrong with it.
    ///
    /// A row ends at a line feed outside quotes: a quoted field may hold a
    /// line feed, and a quote in a quoted field is two quotes, which keeps the
    /// count of quotes even, so a line feed is outside quotes when the quotes
    /// before it are even.
    /// </summary>
    public static Utf8String[] Split(string path, int parts, long minBytes)
    {
        try
        {
            if (parts < 2 || new FileInfo(path).Length < minBytes) return [];
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return [];

            // The ends of the rows where the chunks are cut, after the header.
            var cuts = new int[parts + 1];
            int found = 0;
            int headerEnd = -1;
            long target = 0;
            bool quoted = false;
            var span = bytes.AsSpan();
            int at = 0;
            while (at < span.Length)
            {
                int i = span.Slice(at).IndexOfAny((byte)'"', (byte)'\n');
                if (i < 0) break;
                at += i;
                if (span[at] == (byte)'"') quoted = !quoted;
                else if (!quoted)
                {
                    if (headerEnd < 0)
                    {
                        headerEnd = at + 1;
                        cuts[0] = headerEnd;
                        target = headerEnd + (long)(bytes.Length - headerEnd) / parts;
                    }
                    else if (at + 1 >= target && found < parts - 1)
                    {
                        cuts[++found] = at + 1;
                        target = headerEnd + (long)(bytes.Length - headerEnd) * (found + 1) / parts;
                    }
                }
                at++;
            }
            if (quoted || headerEnd < 0) return [];
            cuts[++found] = bytes.Length;
            if (found < 2) return [];

            var chunks = new Utf8String[found];
            for (int c = 0; c < found; c++)
            {
                int from = cuts[c], to = cuts[c + 1];
                var chunk = new byte[headerEnd + (to - from)];
                span.Slice(0, headerEnd).CopyTo(chunk);
                span.Slice(from, to - from).CopyTo(chunk.AsSpan(headerEnd));
                chunks[c] = Utf8String.FromUtf8(chunk);
            }
            return chunks;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
