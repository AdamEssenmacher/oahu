using AdamE.Oahu.Hosting;
using Xunit;

namespace AdamE.Oahu.Tests.Loop;

public sealed class LoopDisposalTests
{
    private readonly List<string> _log = [];

    [Fact]
    public void Disposing_a_loop_drops_what_is_queued_and_the_posted_turn_then_does_nothing()
    {
        var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));

        host.Dispose();
        host.Dispatcher.RunPosted();

        Assert.Empty(_log);
        Assert.Empty(host.Sink.Faults);
    }

    [Fact]
    public void A_disposed_loop_drops_work_it_is_given_without_throwing()
    {
        var host = new LoopHarness();
        host.Dispose();

        host.Loop.Enqueue(new LoggingEntry("a", _log));
        host.Loop.Drain();

        Assert.Empty(_log);
        Assert.Equal(0, host.Dispatcher.PostedCount);
        Assert.Empty(host.Sink.Faults);
    }

    [Fact]
    public void Draining_a_disposed_loop_does_nothing()
    {
        var host = new LoopHarness();
        host.Dispose();

        host.Loop.Drain();

        Assert.Empty(host.Sink.Faults);
    }

    [Fact]
    public void A_turn_stops_after_the_entry_that_disposes_its_loop()
    {
        var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log, host.Dispose));
        host.Loop.Enqueue(new LoggingEntry("b", _log));

        host.Dispatcher.RunPosted();

        Assert.Equal(["a"], _log);
        Assert.Null(OahuLoop.ForCurrentThread);
        Assert.Empty(host.Sink.Faults);
    }

    [Fact]
    public void A_fault_raised_after_a_loop_is_disposed_still_ends_the_app()
    {
        var host = new LoopHarness();
        host.Dispose();
        var fault = new InvalidOperationException("Late.");

        OtherThread.Run(() => host.Loop.RaiseFault(fault));

        Assert.Same(fault, Assert.Single(host.Sink.Faults));
        Assert.Same(fault, Assert.Throws<OahuFaultException>(host.Dispatcher.RunNext).InnerException);
    }

    [Fact]
    public void A_loop_disposed_on_another_thread_frees_its_own_thread_for_a_new_loop()
    {
        var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));

        OtherThread.Run(host.Dispose);

        Assert.Null(OahuLoop.ForCurrentThread);
        host.Dispatcher.RunPosted();
        Assert.Empty(_log);
        host.Loop.Enqueue(new LoggingEntry("b", _log));
        Assert.Equal(0, host.Dispatcher.PostedCount);

        using var next = new LoopHarness();
        Assert.Same(next.Loop, OahuLoop.ForCurrentThread);
        Assert.Empty(host.Sink.Faults);
    }

    [Fact]
    public void A_new_loop_can_be_created_at_once_on_a_thread_whose_loop_was_disposed_elsewhere()
    {
        var host = new LoopHarness();
        OtherThread.Run(host.Dispose);

        using var next = new LoopHarness();

        Assert.Same(next.Loop, OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void A_turn_stops_after_the_entry_during_which_another_thread_disposes_the_loop()
    {
        var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log, () => OtherThread.Run(host.Dispose)));
        host.Loop.Enqueue(new LoggingEntry("b", _log));

        host.Dispatcher.RunPosted();

        Assert.Equal(["a"], _log);
        Assert.Null(OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void A_faulted_loop_can_be_disposed_and_its_thread_reused()
    {
        var host = new LoopHarness();
        host.Loop.RaiseFault(new InvalidOperationException("Broken."));
        Assert.Same(host.Loop, OahuLoop.ForCurrentThread);

        host.Dispose();

        using var next = new LoopHarness();
        Assert.Same(next.Loop, OahuLoop.ForCurrentThread);
        Assert.False(next.Loop.IsFaulted);
    }
}
