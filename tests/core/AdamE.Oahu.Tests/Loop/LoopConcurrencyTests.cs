using System.Collections.Concurrent;
using AdamE.Oahu.Hosting;
using Xunit;

namespace AdamE.Oahu.Tests.Loop;

public sealed class LoopConcurrencyTests
{
    private readonly List<string> _log = [];

    [Fact]
    public void A_fault_from_another_thread_during_a_turn_lets_it_finish_the_queue_and_drops_what_comes_after()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("first", _log, () =>
        {
            OtherThread.Run(() => host.Loop.RaiseFault(new InvalidOperationException("Elsewhere.")));
            host.Loop.Enqueue(new LoggingEntry("after the fault", _log));
        }));
        host.Loop.Enqueue(new LoggingEntry("second", _log));

        host.Dispatcher.RunNext();

        Assert.Equal(["first", "second"], _log);
        Assert.Single(host.Sink.Faults);
        Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext);
        Assert.Equal(0, host.Dispatcher.PostedCount);
    }

    [Fact]
    public void A_fault_from_another_thread_while_a_turn_is_posted_drops_the_queue_and_the_turn_runs_nothing()
    {
        var dispatcher = new HookDispatcher();
        var sink = new HookSink();
        using var loop = CreateLoop(dispatcher, sink);
        dispatcher.BeforePost = () => OtherThread.Run(() => loop.RaiseFault(new InvalidOperationException("During.")));

        loop.Enqueue(new LoggingEntry("a", _log));

        Assert.True(loop.IsFaulted);
        Assert.Single(sink.Faults);

        // The end of the app, posted from the other thread, then the turn.
        Assert.Equal(2, dispatcher.Inner.PostedCount);
        Assert.Throws<OahuFaultException>(dispatcher.Inner.RunNext);
        dispatcher.Inner.RunNext();
        Assert.Empty(_log);

        loop.Enqueue(new LoggingEntry("b", _log));
        loop.Drain();
        Assert.Empty(_log);
        Assert.Equal(0, dispatcher.Inner.PostedCount);
    }

    [Fact]
    public void A_sink_that_raises_faults_while_reporting_gets_each_once()
    {
        var dispatcher = new FakeDispatcher();
        var sink = new HookSink();
        using var loop = CreateLoop(dispatcher, sink);
        var first = new InvalidOperationException("First.");
        var second = new InvalidOperationException("Second.");
        sink.OnReport = fault =>
        {
            if (!ReferenceEquals(fault, first))
                return;
            loop.RaiseFault(first);
            loop.RaiseFault(second);
            loop.RaiseFault(first);
        };

        loop.RaiseFault(first);

        Assert.Equal([first, second], sink.Faults.ToArray());
        Assert.Equal(2, dispatcher.PostedCount);
    }

    [Fact]
    public void Faults_and_disposal_from_other_threads_during_turns_report_each_fault_once_and_lose_none()
    {
        for (var iteration = 0; iteration < 300; iteration++)
            RunStressIteration(iteration);
    }

    private static OahuLoop CreateLoop(
        IOahuDispatcher dispatcher, IOahuFaultSink sink, ConcurrentQueue<OahuFaultException>? lastResorts = null)
    {
        var loop = new OahuLoop(dispatcher, sink);
        loop.EndOnNewThread = end => lastResorts?.Enqueue(end);
        loop.FailFast = (_, _) => { };
        return loop;
    }

    // Four threads each raise two faults of their own and one they share, and drain off the loop's thread, which is a
    // fault too; one of them disposes the loop. Meanwhile the loop's thread queues work, drains and pumps.
    private static void RunStressIteration(int iteration)
    {
        var dispatcher = new FakeDispatcher();
        var sink = new HookSink();
        var lastResorts = new ConcurrentQueue<OahuFaultException>();
        var loop = CreateLoop(dispatcher, sink, lastResorts);

        var faultRaised = 0;
        var disposed = 0;
        var ranAfterDisposal = 0;
        var violations = new ConcurrentQueue<string>();
        var own = Enumerable.Range(0, 8).Select(index => new InvalidOperationException($"Fault {index}.")).ToArray();
        var shared = new InvalidOperationException("Shared.");

        using var start = new Barrier(5);
        var threads = new List<Thread>();
        for (var index = 0; index < 4; index++)
        {
            var id = index;
            var thread = new Thread(() =>
            {
                start.SignalAndWait();
                for (var round = 0; round < 2; round++)
                {
                    loop.RaiseFault(own[(id * 2) + round]);
                    loop.RaiseFault(shared);
                    Volatile.Write(ref faultRaised, 1);
                    try
                    {
                        loop.Drain();
                        violations.Enqueue("A drain off the loop's thread did not throw.");
                    }
                    catch (OahuFaultException)
                    {
                    }
                }

                if (id == (iteration & 3))
                {
                    loop.Dispose();
                    Volatile.Write(ref disposed, 1);
                }
            });
            thread.Start();
            threads.Add(thread);
        }

        start.SignalAndWait();
        for (var index = 0; index < 200; index++)
        {
            var queuedAfterFault = Volatile.Read(ref faultRaised) == 1;
            var name = index;
            loop.Enqueue(new ActionEntry(() =>
            {
                if (queuedAfterFault)
                    violations.Enqueue($"Entry {name}, queued after a fault, ran.");
                if (Volatile.Read(ref disposed) == 1 && Interlocked.Increment(ref ranAfterDisposal) > 1)
                    violations.Enqueue($"Entry {name} ran after the loop was disposed, and not as the entry running then.");
            }));
            if ((index & 7) == 0)
                loop.Drain();
            if ((index & 3) == 0)
                Pump(dispatcher);
        }

        foreach (var thread in threads)
            thread.Join();
        Pump(dispatcher);
        loop.Dispose();
        Pump(dispatcher);

        var reported = sink.Faults.ToArray();
        foreach (var fault in own.Append(shared))
        {
            var reports = reported.Count(report => ReferenceEquals(report, fault));
            if (reports != 1)
                violations.Enqueue($"{fault.Message} was reported {reports} times.");
        }

        // Nine faults raised, and one more for each of the eight drains off the loop's thread.
        if (reported.Length != 9 + 8)
            violations.Enqueue($"{reported.Length} faults were reported.");
        if (!lastResorts.IsEmpty)
            violations.Enqueue($"{lastResorts.Count} ends of the app were not posted.");
        Assert.True(violations.IsEmpty, $"Iteration {iteration}: " + string.Join(" ", violations));
    }

    private static void Pump(FakeDispatcher dispatcher)
    {
        while (dispatcher.PostedCount > 0)
        {
            try
            {
                dispatcher.RunNext();
            }
            catch (OahuFaultException)
            {
            }
        }
    }

    /// <summary>A <see cref="FakeDispatcher"/> that runs an action once, on its thread, before the next post.</summary>
    private sealed class HookDispatcher : IOahuDispatcher
    {
        public FakeDispatcher Inner { get; } = new();

        public Action? BeforePost { get; set; }

        public bool IsOnThread => Inner.IsOnThread;

        public bool TryPost(Action action)
        {
            if (IsOnThread)
            {
                var before = BeforePost;
                BeforePost = null;
                before?.Invoke();
            }

            return Inner.TryPost(action);
        }
    }

    /// <summary>A fault sink that records each fault and then does what the test gives it.</summary>
    private sealed class HookSink : IOahuFaultSink
    {
        public ConcurrentQueue<Exception> Faults { get; } = new();

        public Action<Exception>? OnReport { get; set; }

        public void ReportFault(Exception exception)
        {
            Faults.Enqueue(exception);
            OnReport?.Invoke(exception);
        }
    }

    private sealed class ActionEntry(Action action) : IOahuLoopEntry
    {
        public void Run() => action();
    }
}
