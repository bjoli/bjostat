/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Tables.IndexOf: used by the other files of the glue.
//
// The rest of what was here is Bjolang now, in src/table.bjo ("Columns and
// tables in Arrow", "Making tables").

using Apache.Arrow;

namespace Bjostat;

public static class Tables
{
    /// <summary>
    /// The index of the first column of the name, or -1. Names are compared
    /// ordinally, character for character. Arrow's Schema.GetFieldIndex
    /// compares them in the current culture, which ignores characters such
    /// as a soft hyphen, so that "pri\u00ADce" found the column "price".
    /// </summary>
    public static int IndexOf(RecordBatch t, string name)
    {
        var fields = t.Schema.FieldsList;
        for (int i = 0; i < fields.Count; i++)
            if (string.Equals(fields[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }
}
