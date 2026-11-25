using System.Runtime.CompilerServices;
using AdamE.Oahu.Hosting;
using AdamE.Oahu.Tests.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Loop;

public sealed class LoopFaultTests
{
    private readonly List<string> _log = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromAnObserver() => throw new InvalidOperationException("The observer broke.");

    [Fact]
    public void The_end_of_the_app_run_by_a_nested_run_loop_in_an_entry_goes_out_of_the_turn_and_is_not_a_second_fault()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("throws", _log, ThrowFromAnObserver));
        host.Loop.Enqueue(new LoggingEntry("modal", _log, host.Dispatcher.RunPosted));
        host.Loop.Enqueue(new LoggingEntry("after", _log));

        // The turn: the modal entry pumps the run loop, which runs the end of the app posted for the first entry.
        var end = Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext);

        var fault = Assert.Single(host.Sink.Faults);
        Assert.Same(fault, end.InnerException);
        Assert.Contains(nameof(FakeDispatcher.RunPosted), end.StackTrace);
        Assert.Equal(["throws", "modal"], _log);
        Assert.False(host.Loop.IsTurnRunning);
        Assert.True(host.Loop.IsFaulted);
        Assert.Equal(0, host.Dispatcher.PostedCount);

        // Raised again, it is not reported either.
        host.Loop.RaiseFault(end);
        host.Loop.Drain();
        Assert.Single(host.Sink.Faults);
        Assert.Equal(["throws", "modal"], _log);
        Assert.Empty(host.LastResorts);
    }

    [Fact]
    public void A_fault_whose_Message_throws_is_reported_and_named_by_its_type()
    {
        using var host = new LoopHarness();
        var fault = new MessageThrowsException();
        host.Loop.Enqueue(new LoggingEntry("throws", _log, () => throw fault));

        var escaped = Record.Exception(host.Dispatcher.RunNext);

        Assert.Null(escaped);
        Assert.Same(fault, Assert.Single(host.Sink.Faults));
        var end = Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext);
        Assert.Equal("An Oahu fault ended the app: " + typeof(MessageThrowsException).FullName, end.Message);
        Assert.Same(fault, end.InnerException);
        Assert.Empty(host.FailFasts);
    }

    [Fact]
    public void A_fault_whose_ToString_throws_is_reported_and_ends_the_app()
    {
        using var host = new LoopHarness();
        var fault = new ToStringThrowsException();

        var escaped = Record.Exception(() => host.Loop.RaiseFault(fault));

        Assert.Null(escaped);
        Assert.Same(fault, Assert.Single(host.Sink.Faults));
        var end = Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext);
        Assert.Equal(
            "An Oahu fault ended the app: " + typeof(ToStringThrowsException).FullName + ": No string for you.",
            end.Message);
        Assert.Empty(host.FailFasts);
    }

    [Fact]
    public void An_entry_that_throws_is_reported_once_and_the_turn_goes_on()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));
        host.Loop.Enqueue(new LoggingEntry("throws", _log, ThrowFromAnObserver));
        host.Loop.Enqueue(new LoggingEntry("b", _log));

        host.Dispatcher.RunNext();

        Assert.Equal(["a", "throws", "b"], _log);
        var fault = Assert.IsType<InvalidOperationException>(Assert.Single(host.Sink.Faults));
        Assert.Equal("The observer broke.", fault.Message);
    }

    [Fact]
    public void The_end_of_the_app_is_posted_and_throws_with_the_fault_and_its_own_stack_trace()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("throws", _log, ThrowFromAnObserver));
        host.Dispatcher.RunNext();
        var fault = Assert.Single(host.Sink.Faults);

        Assert.Equal(1, host.Dispatcher.PostedCount);
        var end = Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext);

        Assert.Same(fault, end.InnerException);
        Assert.Equal(
            "An Oahu fault ended the app: System.InvalidOperationException: The observer broke.", end.Message);
        Assert.Contains(nameof(ThrowFromAnObserver), fault.StackTrace);
        Assert.Equal(host.Sink.TraceWhenReported(fault), fault.StackTrace);
    }

    [Fact]
    public void Each_fault_ends_the_app_and_the_same_exception_raised_twice_is_one_fault()
    {
        using var host = new LoopHarness();
        var shared = new InvalidOperationException("Shared.");
        host.Loop.Enqueue(new LoggingEntry("first", _log, () => throw shared));
        host.Loop.Enqueue(new LoggingEntry("again", _log, () => throw shared));
        host.Loop.Enqueue(new LoggingEntry("other", _log, ThrowFromAnObserver));

        host.Dispatcher.RunNext();

        Assert.Equal(["first", "again", "other"], _log);
        Assert.Equal(2, host.Sink.Faults.Count);
        Assert.Same(shared, host.Sink.Faults[0]);
        Assert.Equal(2, host.Dispatcher.PostedCount);
        Assert.Same(shared, Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext).InnerException);
        Assert.Same(host.Sink.Faults[1], Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext).InnerException);
    }

    [Fact]
    public void A_fault_raised_on_another_thread_is_reported_there_and_ends_the_app_on_the_loops_thread()
    {
        using var host = new LoopHarness();
        var fault = new InvalidOperationException("From a pool thread.");
        var raisingThread = 0;

        OtherThread.Run(() =>
        {
            raisingThread = Environment.CurrentManagedThreadId;
            host.Loop.RaiseFault(fault);
        });

        Assert.Same(fault, Assert.Single(host.Sink.Faults));
        Assert.Equal(raisingThread, host.Sink.ThreadOf(fault));
        Assert.NotEqual(Environment.CurrentManagedThreadId, raisingThread);
        Assert.Same(fault, Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext).InnerException);
    }

    [Fact]
    public void A_thread_check_on_another_thread_reports_one_exception_and_throws_another_to_the_caller()
    {
        using var host = new LoopHarness();

        var thrown = OtherThread.Catch(() => host.Loop.CheckThread("A source was changed off its loop's thread."));

        var caught = Assert.IsType<OahuFaultException>(thrown);
        var reported = Assert.IsType<OahuFaultException>(Assert.Single(host.Sink.Faults));
        Assert.NotSame(reported, caught);
        Assert.Equal("A source was changed off its loop's thread.", reported.Message);
        Assert.Equal(reported.Message, caught.Message);

        // The reported one has the trace of the call that broke the rule, and keeps it: it is never thrown.
        Assert.Contains(
            nameof(A_thread_check_on_another_thread_reports_one_exception_and_throws_another_to_the_caller),
            reported.StackTrace);
        Assert.Equal(host.Sink.TraceWhenReported(reported), reported.StackTrace);

        // The thrown one has the trace of its own unwinding, from the check.
        Assert.Contains(nameof(OahuLoop.CheckThread), caught.StackTrace);

        Assert.Same(reported, Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext).InnerException);
    }

    [Fact]
    public void A_thread_check_on_the_loops_thread_does_nothing()
    {
        using var host = new LoopHarness();

        host.Loop.CheckThread("Not raised.");

        Assert.Empty(host.Sink.Faults);
        Assert.Equal(0, host.Dispatcher.PostedCount);
        Assert.False(host.Loop.IsFaulted);
    }

    [Fact]
    public void A_thread_checks_exception_raised_again_by_its_caller_is_not_reported_twice()
    {
        using var host = new LoopHarness();
        OahuFaultException? thrown = null;
        string? reportedTrace = null;
        host.Loop.Enqueue(new LoggingEntry("checks", _log, () =>
        {
            thrown = Assert.IsType<OahuFaultException>(OtherThread.Catch(() => host.Loop.CheckThread("Off the thread.")));
            reportedTrace = Assert.Single(host.Sink.Faults).StackTrace;
            throw thrown;
        }));

        host.Loop.Drain();
        OtherThread.Run(() => host.Loop.RaiseFault(thrown!));

        var reported = Assert.Single(host.Sink.Faults);
        Assert.NotSame(thrown, reported);
        Assert.Equal(reportedTrace, reported.StackTrace);

        // The turn posted for the entry, which the drain ran first, then one end of the app.
        host.Dispatcher.RunNext();
        Assert.Same(reported, Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext).InnerException);
        Assert.Equal(0, host.Dispatcher.PostedCount);
    }

    [Fact]
    public void A_fault_already_reported_raised_again_allocates_nothing()
    {
        using var host = new LoopHarness();
        var fault = new InvalidOperationException("Once.");
        host.Loop.RaiseFault(fault);

        var allocated = Allocations.Smallest(() => host.Loop.RaiseFault(fault));

        Assert.Equal(0, allocated);
        Assert.Single(host.Sink.Faults);
    }

    [Fact]
    public void A_sink_that_throws_does_not_stop_the_end_of_the_app_which_carries_both_exceptions()
    {
        using var host = new LoopHarness();
        var fault = new InvalidOperationException("Broken.");
        host.Sink.Throws = true;

        host.Loop.RaiseFault(fault);

        Assert.Empty(host.LastResorts);
        Assert.Empty(host.FailFasts);
        var end = Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext);
        Assert.StartsWith("An Oahu fault ended the app: System.InvalidOperationException: Broken.", end.Message);
        Assert.Contains("fault sink threw", end.Message);
        var both = Assert.IsType<AggregateException>(end.InnerException);
        Assert.Same(fault, both.InnerExceptions[0]);
        Assert.Equal("The crash reporter is gone.", both.InnerExceptions[1].Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_end_of_the_app_that_cannot_be_posted_is_thrown_on_a_new_thread(bool dispatcherThrows)
    {
        using var host = new LoopHarness();
        var fault = new InvalidOperationException("Broken.");
        host.Dispatcher.Mode = dispatcherThrows ? PostMode.Throws : PostMode.Refuses;

        host.Loop.RaiseFault(fault);

        Assert.Same(fault, Assert.Single(host.Sink.Faults));
        Assert.Empty(host.FailFasts);
        var end = Assert.Single(host.LastResorts);
        Assert.StartsWith("An Oahu fault ended the app: System.InvalidOperationException: Broken.", end.Message);
        Assert.Contains("did not take the end of the app, so the loop ended the process from a new thread", end.Message);
        if (!dispatcherThrows)
        {
            Assert.Same(fault, end.InnerException);
        }
        else
        {
            var both = Assert.IsType<AggregateException>(end.InnerException);
            Assert.Same(fault, both.InnerExceptions[0]);
            Assert.Equal("The run loop has stopped.", both.InnerExceptions[1].Message);
        }
    }

    [Fact]
    public void A_sink_that_throws_and_a_refused_end_of_the_app_still_end_the_app_on_a_new_thread()
    {
        using var host = new LoopHarness();
        var fault = new InvalidOperationException("Broken.");
        host.Sink.Throws = true;
        host.Dispatcher.Mode = PostMode.Refuses;

        host.Loop.RaiseFault(fault);

        var end = Assert.Single(host.LastResorts);
        var both = Assert.IsType<AggregateException>(end.InnerException);
        Assert.Same(fault, both.InnerExceptions[0]);
        Assert.Empty(host.FailFasts);
    }

    [Fact]
    public void The_process_fails_fast_only_when_the_thread_for_the_end_of_the_app_cannot_be_started()
    {
        using var host = new LoopHarness();
        var fault = new InvalidOperationException("Broken.");
        host.Dispatcher.Mode = PostMode.Refuses;
        host.ThreadStartThrows = true;

        host.Loop.RaiseFault(fault);

        var end = Assert.Single(host.LastResorts);
        var (message, exception) = Assert.Single(host.FailFasts);
        Assert.StartsWith(end.Message, message);
        Assert.Contains("could not be started", message);
        var both = Assert.IsType<AggregateException>(exception);
        Assert.Same(end, both.InnerExceptions[0]);
        Assert.Equal("No thread for you.", both.InnerExceptions[1].Message);
    }

    [Fact]
    public void By_default_the_last_resort_is_a_foreground_thread_and_only_then_fail_fast()
    {
        var loop = new OahuLoop(new FakeDispatcher(), new RecordingFaultSink());
        try
        {
            Assert.Equal(OahuLoop.EndOnNewThreadByDefault, loop.EndOnNewThread);
            Assert.Equal(Environment.FailFast, loop.FailFast);
        }
        finally
        {
            loop.Dispose();
        }

        var end = new OahuFaultException("The end.");
        var thread = OahuLoop.CreateLastResortThread(end);
        Assert.False(thread.IsBackground);
        Assert.Equal("Oahu fault", thread.Name);
        Assert.Equal(ThreadState.Unstarted, thread.ThreadState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_last_resort_raises_the_unhandled_exception_event_then_fails_fast_even_if_raising_throws(
        bool raisingThrows)
    {
        var end = new OahuFaultException("The end.");
        var calls = new List<string>();

        OahuLoop.EndTheProcess(
            end,
            exception =>
            {
                Assert.Same(end, exception);
                calls.Add("raise");
                if (raisingThrows)
                    throw new InvalidOperationException("A handler broke.");
            },
            (message, exception) =>
            {
                Assert.Equal(end.Message, message);
                Assert.Same(end, exception);
                calls.Add("fail fast");
            });

        Assert.Equal(["raise", "fail fast"], calls);
    }

    [Fact]
    public void A_dispatcher_that_runs_the_turn_inside_TryPost_is_a_fault_and_the_turn_does_not_run_there()
    {
        using var host = new LoopHarness();
        host.Dispatcher.Mode = PostMode.RunsInline;

        host.Loop.Enqueue(new LoggingEntry("a", _log));

        Assert.Empty(_log);
        var fault = Assert.IsType<OahuFaultException>(Assert.Single(host.Sink.Faults));
        Assert.Contains("before IOahuDispatcher.TryPost returned", fault.Message);
        Assert.Contains("MainThread.BeginInvokeOnMainThread", fault.Message);
        Assert.Contains(nameof(FakeDispatcher.TryPost), fault.StackTrace);
        Assert.True(host.Loop.IsFaulted);

        // The same dispatcher runs the end of the app inline too; it throws there, so the loop says so, and ends the
        // process from a new thread.
        var end = Assert.Single(host.LastResorts);
        Assert.Same(fault, end.InnerException);
        Assert.Contains("ran the end of the app inside IOahuDispatcher.TryPost", end.Message);
        Assert.DoesNotContain("did not take", end.Message);

        // The faulted loop dropped the entry, and takes no more.
        host.Dispatcher.Mode = PostMode.Queues;
        host.Loop.Enqueue(new LoggingEntry("b", _log));
        host.Loop.Drain();
        Assert.Empty(_log);
        Assert.Equal(0, host.Dispatcher.PostedCount);
    }

    [Fact]
    public void A_faulted_loop_finishes_its_turn_then_takes_no_more_work()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("throws", _log, ThrowFromAnObserver));
        host.Loop.Enqueue(new LoggingEntry("queued before", _log, () =>
            host.Loop.Enqueue(new LoggingEntry("queued after", _log))));
        host.Loop.Enqueue(new LoggingEntry("also before", _log));

        host.Dispatcher.RunNext();

        Assert.Equal(["throws", "queued before", "also before"], _log);
        Assert.True(host.Loop.IsFaulted);

        host.Loop.Enqueue(new LoggingEntry("later", _log));
        host.Loop.Drain();

        Assert.Equal(["throws", "queued before", "also before"], _log);
        Assert.Single(host.Sink.Faults);

        // Only the end of the app is posted; no turn.
        Assert.Equal(1, host.Dispatcher.PostedCount);
        Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext);
        Assert.Same(host.Loop, OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void A_fault_from_another_thread_leaves_the_posted_turn_nothing_to_run()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));

        OtherThread.Run(() => host.Loop.RaiseFault(new InvalidOperationException("Elsewhere.")));
        host.Dispatcher.RunNext();

        Assert.Empty(_log);
        Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext);
    }

    [Fact]
    public void A_posted_turn_run_off_the_loops_thread_is_a_fault_and_runs_nothing()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));

        OtherThread.Run(host.Dispatcher.RunNext);

        Assert.Empty(_log);
        var fault = Assert.IsType<OahuFaultException>(Assert.Single(host.Sink.Faults));
        Assert.Contains("off the loop's thread", fault.Message);
        Assert.Same(fault, Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext).InnerException);
    }

    private sealed class MessageThrowsException : Exception
    {
        public override string Message => throw new InvalidOperationException("The Message getter broke.");
    }

    private sealed class ToStringThrowsException() : Exception("No string for you.")
    {
        public override string ToString() => throw new InvalidOperationException("ToString broke.");
    }
}
