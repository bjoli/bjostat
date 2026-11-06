// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;

namespace Bjostat;

/// <summary>
/// Where the glue's work runs on several cores: the compute threads of
/// Bjolang's (std parallel), so that a program has one pool of threads for
/// work that uses a core, not two.
///
/// The pool is Bjolang code, so the glue reaches it through a function that
/// a module of bjostat installs when it is initialised: (bjostat table),
/// (bjostat query) and (bjostat csv), the modules that call glue that runs
/// in parts. A module is initialised before its first function runs, so the
/// function is in place before the glue of that module is called.
/// BJOLANG_COMPUTE_THREADS sets the number of threads.
/// </summary>
public static class Workers
{
    private static Func<int, Func<int, Bjoml.Unit>, Bjoml.Unit>? _run;
    private static int _count = Environment.ProcessorCount;

    /// <summary>
    /// The number of compute threads, for the glue to choose a number of
    /// parts. The logical cores until a module has installed the pool, and
    /// then what (std parallel) has: one less than the logical cores by
    /// default.
    /// </summary>
    public static int Count => _count;

    /// <summary>
    /// Sets the function that runs the parts, (std parallel)'s
    /// <c>computing-each</c>, and the number of compute threads.
    /// </summary>
    public static void Install(Func<int, Func<int, Bjoml.Unit>, Bjoml.Unit> run, int count)
    {
        _count = Math.Max(1, count);
        _run = run;
    }

    /// <summary>
    /// Runs <paramref name="work"/> of 0 to count - 1 on the compute threads
    /// and on the calling thread, and returns when every part has. The
    /// calling thread takes parts too, so a call from a part cannot wait for
    /// threads that are all busy. The first exception of a part is raised
    /// here, and the parts not started then do not run. Before the pool is
    /// installed, or for one part, the parts run on the calling thread, in
    /// order.
    /// </summary>
    public static void For(int count, Action<int> work)
    {
        if (count <= 0) return;
        var run = _run;
        if (run is null || count == 1)
        {
            for (var i = 0; i < count; i++) work(i);
            return;
        }

        run(count, k =>
        {
            work(k);
            return Bjoml.Unit.Value;
        });
    }
}
