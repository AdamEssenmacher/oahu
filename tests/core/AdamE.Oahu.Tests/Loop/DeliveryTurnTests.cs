using AdamE.Oahu.Tests.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Loop;

public sealed class DeliveryTurnTests
{
    private readonly List<string> _log = [];

    [Fact]
    public void An_entry_runs_in_a_later_turn_never_during_the_call_that_queued_it()
    {
        using var host = new LoopHarness();

        host.Loop.Enqueue(new LoggingEntry("a", _log));

        Assert.Empty(_log);
        host.Dispatcher.RunPosted();
        Assert.Equal(["a"], _log);
    }

    [Fact]
    public void One_turn_is_posted_for_many_entries()
    {
        using var host = new LoopHarness();

        host.Loop.Enqueue(new LoggingEntry("a", _log));
        host.Loop.Enqueue(new LoggingEntry("b", _log));
        host.Loop.Enqueue(new LoggingEntry("c", _log));

        Assert.Equal(1, host.Dispatcher.PostedCount);
        host.Dispatcher.RunNext();
        Assert.Equal(["a", "b", "c"], _log);
        Assert.Equal(0, host.Dispatcher.PostedCount);
    }

    [Fact]
    public void A_new_turn_is_posted_once_the_queue_has_emptied()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));
        host.Dispatcher.RunPosted();

        host.Loop.Enqueue(new LoggingEntry("b", _log));

        Assert.Equal(1, host.Dispatcher.PostedCount);
        host.Dispatcher.RunPosted();
        Assert.Equal(["a", "b"], _log);
    }

    [Fact]
    public void Entries_run_first_in_first_out_and_an_entry_queued_twice_runs_twice()
    {
        using var host = new LoopHarness();
        var a = new LoggingEntry("a", _log);
        var b = new LoggingEntry("b", _log);

        host.Loop.Enqueue(b);
        host.Loop.Enqueue(a);
        host.Loop.Enqueue(b);
        host.Dispatcher.RunPosted();

        Assert.Equal(["b", "a", "b"], _log);
    }

    [Fact]
    public void A_turn_runs_until_the_queue_is_empty_including_entries_queued_during_it()
    {
        using var host = new LoopHarness();
        var late = new LoggingEntry("late", _log, () => host.Loop.Enqueue(new LoggingEntry("later", _log)));
        host.Loop.Enqueue(new LoggingEntry("a", _log, () => host.Loop.Enqueue(late)));
        host.Loop.Enqueue(new LoggingEntry("b", _log));

        host.Dispatcher.RunNext();

        Assert.Equal(["a", "b", "late", "later"], _log);
        Assert.Equal(0, host.Dispatcher.PostedCount);
    }

    [Fact]
    public void A_turn_reports_that_it_is_running_only_while_it_runs()
    {
        using var host = new LoopHarness();
        var running = false;
        host.Loop.Enqueue(new LoggingEntry("a", _log, () => running = host.Loop.IsTurnRunning));

        Assert.False(host.Loop.IsTurnRunning);
        host.Dispatcher.RunPosted();

        Assert.True(running);
        Assert.False(host.Loop.IsTurnRunning);
    }

    [Fact]
    public void A_drain_during_a_turn_does_nothing_more_than_let_the_turn_finish_the_queue()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log, () =>
        {
            host.Loop.Enqueue(new LoggingEntry("b", _log));
            host.Loop.Drain();
            _log.Add("drain returned");
        }));

        host.Dispatcher.RunPosted();

        Assert.Equal(["a", "drain returned", "b"], _log);
    }

    [Fact]
    public void A_posted_turn_run_during_a_turn_does_nothing_and_the_running_turn_finishes_the_queue()
    {
        // A nested run loop, such as a modal pumped from an entry, runs the turn posted for "a" inside the turn the
        // drain runs.
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log, () =>
        {
            host.Loop.Enqueue(new LoggingEntry("c", _log));
            host.Dispatcher.RunPosted();
            _log.Add("pump returned");
        }));
        host.Loop.Enqueue(new LoggingEntry("b", _log));

        host.Loop.Drain();

        Assert.Equal(["a", "pump returned", "b", "c"], _log);
        Assert.Equal(0, host.Dispatcher.PostedCount);
        Assert.Empty(host.Sink.Faults);
    }

    [Fact]
    public void A_drain_runs_a_turn_at_once_and_the_posted_turn_then_does_nothing()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));
        host.Loop.Enqueue(new LoggingEntry("b", _log));

        host.Loop.Drain();

        Assert.Equal(["a", "b"], _log);
        Assert.Equal(1, host.Dispatcher.PostedCount);
        host.Dispatcher.RunPosted();
        Assert.Equal(["a", "b"], _log);
    }

    [Fact]
    public void An_entry_queued_after_a_drain_waits_for_the_turn_already_posted()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));
        host.Loop.Drain();

        host.Loop.Enqueue(new LoggingEntry("b", _log));

        Assert.Equal(1, host.Dispatcher.PostedCount);
        Assert.Equal(["a"], _log);
        host.Dispatcher.RunPosted();
        Assert.Equal(["a", "b"], _log);
    }

    [Fact]
    public void A_drain_with_nothing_queued_does_nothing()
    {
        using var host = new LoopHarness();

        host.Loop.Drain();

        Assert.Equal(0, host.Dispatcher.PostedCount);
        Assert.Empty(host.Sink.Faults);
    }

    [Fact]
    public void A_drain_off_the_loops_thread_is_a_fault_thrown_to_the_caller_and_runs_nothing()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));

        var thrown = OtherThread.Catch(host.Loop.Drain);

        var caught = Assert.IsType<OahuFaultException>(thrown);
        Assert.Contains("off its thread", caught.Message);
        var fault = Assert.Single(host.Sink.Faults);
        Assert.Equal(caught.Message, fault.Message);
        Assert.Empty(_log);

        // The turn posted for "a" runs nothing on the faulted loop, then the end of the app.
        host.Dispatcher.RunNext();
        Assert.Empty(_log);
        var end = Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext);
        Assert.Same(fault, end.InnerException);
    }

    [Fact]
    public void A_turn_in_steady_state_allocates_nothing()
    {
        using var host = new LoopHarness();
        var a = new CountingEntry();
        var b = new CountingEntry();

        var allocated = Allocations.Smallest(() =>
        {
            host.Loop.Enqueue(a);
            host.Loop.Enqueue(b);
            host.Loop.Enqueue(a);
            host.Dispatcher.RunPosted();
        });

        Assert.Equal(0, allocated);
        Assert.Equal(2 * b.Runs, a.Runs);
    }

    [Fact]
    public void A_drained_turn_in_steady_state_allocates_nothing()
    {
        using var host = new LoopHarness();
        var a = new CountingEntry();

        var allocated = Allocations.Smallest(() =>
        {
            host.Loop.Enqueue(a);
            host.Loop.Drain();
            host.Dispatcher.RunPosted();
        });

        Assert.Equal(0, allocated);
    }
}
