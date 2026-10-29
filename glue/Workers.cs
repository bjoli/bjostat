// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bjostat;

/// <summary>
/// The threads that do bjostat's work on several cores: one for each logical
/// core by default, made once, and not threads of .NET's pool. The pumps of
/// Bjolang's run queues run on the pool's threads, and a part that ran there
/// could hold a thread a pump waits for; here it holds none, and the
/// operating system shares the cores between the parts and the pumps.
/// BJOSTAT_WORKERS sets another number: fewer threads than cores keep fibers
/// on time while a loop runs, at less throughput (bench/latency.bjo).
///
/// A part runs in the dynamic environment of the code that started it, as
/// Bjolang's <c>blocking</c> runs its thunk. A part keeps its value itself;
/// <see cref="Run"/> and <see cref="RunAll"/> give a Task of the end of the
/// work, which Bjolang makes an event (task->event). A Task&lt;T&gt; of the
/// glue cannot be imported #:async yet.
/// </summary>
public static class Workers
{
    private static readonly BlockingCollection<Action> Queue = new();

    /// <summary>The number of worker threads.</summary>
    public static readonly int Count =
        int.TryParse(Environment.GetEnvironmentVariable("BJOSTAT_WORKERS"), out var n) && n > 0
            ? n
            : Environment.ProcessorCount;

    /// <summary>The number of worker threads, for Bjolang.</summary>
    public static int CountOf() => Count;

    static Workers()
    {
        for (int i = 0; i < Count; i++)
        {
            var t = new Thread(Work) { IsBackground = true, Name = "bjostat worker " + i };
            t.Start();
        }
    }

    private static void Work()
    {
        foreach (var job in Queue.GetConsumingEnumerable()) job();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a worker; the Task ends when it does.
    /// A part whose token was cancelled before it started does not run.
    /// </summary>
    public static Task Run(Func<Bjoml.Unit> work, CancellationToken token) =>
        RunAll(1, _ => work(), token);

    /// <summary>
    /// Runs <paramref name="work"/> of 0 to count - 1 on the workers, at most
    /// one part on each, and the parts in the order of their numbers; the
    /// Task ends when every part has. The first exception of a part is the
    /// Task's, and the parts not started then do not run, nor do those not
    /// started when the token is cancelled.
    /// </summary>
    public static Task RunAll(int count, Func<int, Bjoml.Unit> work, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (count <= 0)
        {
            done.SetResult();
            return done.Task;
        }
        var env = Bjoml.FiberContext.ForChild();
        var parts = new Parts(count, part => work(part), token);
        int runners = Math.Min(count, Count);
        int left = runners;
        for (int r = 0; r < runners; r++)
        {
            Queue.Add(() =>
            {
                using (Bjoml.FiberContext.Push(env)) parts.RunSome();
                if (Interlocked.Decrement(ref left) == 0) parts.Finish(done);
            });
        }
        return done.Task;
    }

    /// <summary>
    /// Runs <paramref name="work"/> of 0 to count - 1 on the workers and on
    /// the calling thread, and returns when every part has. For the glue's
    /// own work, which a caller waits for as for any call: the calling
    /// thread takes parts too, so a call from a worker cannot wait for
    /// workers that are all busy.
    /// </summary>
    public static void For(int count, Action<int> work)
    {
        if (count <= 0) return;
        var parts = new Parts(count, work, CancellationToken.None);
        int helpers = Math.Min(count - 1, Count);
        using var left = new CountdownEvent(helpers + 1);
        for (int r = 0; r < helpers; r++)
        {
            Queue.Add(() =>
            {
                parts.RunSome();
                left.Signal();
            });
        }
        parts.RunSome();
        left.Signal();
        left.Wait();
        parts.Rethrow();
    }

    /// <summary>The parts of one call, taken by number by whoever is free.</summary>
    private sealed class Parts(int count, Action<int> work, CancellationToken token)
    {
        private int _next = -1;
        private int _completed;
        private ExceptionDispatchInfo? _failure;

        public void RunSome()
        {
            while (_failure is null && !token.IsCancellationRequested)
            {
                int part = Interlocked.Increment(ref _next);
                if (part >= count) return;
                try
                {
                    work(part);
                    Interlocked.Increment(ref _completed);
                }
                catch (Exception e) { Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(e), null); }
            }
        }

        public void Finish(TaskCompletionSource done)
        {
            if (_failure is not null) done.TrySetException(_failure.SourceException);
            else if (Volatile.Read(ref _completed) < count) done.TrySetCanceled(token);
            else done.TrySetResult();
        }

        public void Rethrow() => _failure?.Throw();
    }
}
