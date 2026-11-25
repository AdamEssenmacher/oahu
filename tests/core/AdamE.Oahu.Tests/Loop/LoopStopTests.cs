using AdamE.Oahu.Hosting;
using Xunit;

namespace AdamE.Oahu.Tests.Loop;

/// <summary>A dispatcher that refuses a delivery turn stops the loop, which is not a fault.</summary>
public sealed class LoopStopTests
{
    private readonly List<string> _log = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_refused_first_turn_stops_the_loop_without_a_fault(bool dispatcherThrows)
    {
        using var host = new LoopHarness();
        host.Dispatcher.Mode = dispatcherThrows ? PostMode.Throws : PostMode.Refuses;

        host.Loop.Enqueue(new LoggingEntry("a", _log));

        Assert.True(host.Loop.IsStopped);
        Assert.False(host.Loop.IsFaulted);
        Assert.Empty(host.Sink.Faults);
        Assert.Empty(host.LastResorts);
        Assert.Empty(host.FailFasts);
        Assert.Equal(1, host.Dispatcher.Refusals);

        // The queue was dropped: a dispatcher that takes work again finds nothing to run.
        host.Dispatcher.Mode = PostMode.Queues;
        host.Loop.Drain();
        host.Loop.Enqueue(new LoggingEntry("b", _log));
        host.Dispatcher.RunPosted();
        Assert.Empty(_log);
        Assert.Equal(0, host.Dispatcher.PostedCount);
        Assert.Equal(1, host.Dispatcher.Refusals);
    }

    [Fact]
    public void A_refused_later_turn_stops_the_loop_after_the_turns_already_delivered()
    {
        using var host = new LoopHarness();
        host.Loop.Enqueue(new LoggingEntry("a", _log));
        host.Dispatcher.RunPosted();

        host.Dispatcher.Mode = PostMode.Refuses;
        host.Loop.Enqueue(new LoggingEntry("b", _log));
        host.Loop.Enqueue(new LoggingEntry("c", _log));

        Assert.Equal(["a"], _log);
        Assert.True(host.Loop.IsStopped);
        Assert.Empty(host.Sink.Faults);
        Assert.Equal(1, host.Dispatcher.Refusals);
    }

    [Fact]
    public void A_stopped_loop_stays_its_threads_loop_until_it_is_disposed()
    {
        var host = new LoopHarness();
        host.Dispatcher.Mode = PostMode.Refuses;
        host.Loop.Enqueue(new LoggingEntry("a", _log));

        Assert.Same(host.Loop, OahuLoop.ForCurrentThread);
        Assert.Throws<InvalidOperationException>(() => new OahuLoop(new FakeDispatcher(), new RecordingFaultSink()));

        host.Dispose();
        Assert.Null(OahuLoop.ForCurrentThread);
        using var next = new LoopHarness();
        Assert.Same(next.Loop, OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void A_fault_on_a_stopped_loop_is_still_reported_and_ends_the_app_on_a_new_thread()
    {
        using var host = new LoopHarness();
        host.Dispatcher.Mode = PostMode.Refuses;
        host.Loop.Enqueue(new LoggingEntry("a", _log));
        var fault = new InvalidOperationException("During shutdown.");

        OtherThread.Run(() => host.Loop.RaiseFault(fault));

        Assert.Same(fault, Assert.Single(host.Sink.Faults));
        Assert.Same(fault, Assert.Single(host.LastResorts).InnerException);
    }
}
