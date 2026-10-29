// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Bjostat;

/// <summary>
/// Threads for work that uses a core: one for each core, made once, and not
/// threads of .NET's pool. The pumps of Bjolang's run queues run on the
/// pool's threads, so a part of a loop that ran there could hold a thread a
/// pump waits for; here it holds none, and the operating system shares the
/// cores between the parts and the pumps.
///
/// A part is a function that keeps its value itself; <see cref="Run"/> gives
/// a Task of its end, which Bjolang makes an event (task->event).
/// </summary>
public static class Workers
{
    private static readonly BlockingCollection<Action> Queue = new();

    static Workers()
    {
        // For measuring (bench/latency.bjo): BJOSTAT_WORKERS threads, and
        // BJOSTAT_WORKER_PRIORITY, which made no difference on Linux.
        int n = int.TryParse(Environment.GetEnvironmentVariable("BJOSTAT_WORKERS"), out var w) && w > 0
            ? w : Environment.ProcessorCount;
        var priority = Enum.TryParse<ThreadPriority>(Environment.GetEnvironmentVariable("BJOSTAT_WORKER_PRIORITY"), out var p)
            ? p : ThreadPriority.Normal;
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(Work) { IsBackground = true, Name = "bjostat worker " + i, Priority = priority };
            t.Start();
        }
    }

    private static void Work()
    {
        foreach (var job in Queue.GetConsumingEnumerable()) job();
    }

    /// <summary>Runs <paramref name="work"/> on a worker; the Task ends when it does.</summary>
    public static Task Run(Func<Bjoml.Unit> work, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (token.IsCancellationRequested)
        {
            done.SetCanceled(token);
            return done.Task;
        }
        Queue.Add(() =>
        {
            if (token.IsCancellationRequested)
            {
                done.TrySetCanceled(token);
                return;
            }
            try
            {
                work();
                done.TrySetResult();
            }
            catch (Exception e) { done.TrySetException(e); }
        });
        return done.Task;
    }
}
