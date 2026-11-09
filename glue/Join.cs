/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Join: what is left in C# of the joins, which are Bjolang in src/table.bjo
// ("Joins"). Reshape.cs asks which types can be keys.

using Apache.Arrow.Types;

namespace Bjostat;

public static class Joins
{
    /// <summary>Whether a column of the type can be a key of a join, a distinct or a count.</summary>
    public static bool KeyType(IArrowType t) =>
        t is DoubleType or FloatType or Int64Type or Int32Type or Int16Type or UInt8Type
            or UInt16Type or UInt32Type or UInt64Type
            or StringType or BooleanType or Date32Type or TimestampType
        || Categories.IsType(t);
}
