using AdamE.Oahu.Hosting;
using Xunit;

namespace AdamE.Oahu.Tests.Loop;

public sealed class LoopThreadTests
{
    [Fact]
    public void A_loop_is_the_loop_of_the_thread_it_is_created_on()
    {
        Assert.Null(OahuLoop.ForCurrentThread);

        using var host = new LoopHarness();

        Assert.Same(host.Loop, OahuLoop.ForCurrentThread);
        Assert.True(host.Loop.IsOnThread);
    }

    [Fact]
    public void A_second_loop_on_a_thread_throws_while_the_first_is_alive()
    {
        using var host = new LoopHarness();

        var thrown = Assert.Throws<InvalidOperationException>(() => new OahuLoop(new FakeDispatcher(), new RecordingFaultSink()));

        Assert.Contains("already has an Oahu loop", thrown.Message);
        Assert.Same(host.Loop, OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void Disposing_a_loop_frees_its_thread_for_another()
    {
        var first = new LoopHarness();
        first.Dispose();

        Assert.Null(OahuLoop.ForCurrentThread);
        using var second = new LoopHarness();
        Assert.Same(second.Loop, OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void A_thread_can_create_and_discard_many_loops()
    {
        for (var i = 0; i < 100; i++)
        {
            using var host = new LoopHarness();
            Assert.Same(host.Loop, OahuLoop.ForCurrentThread);
        }

        Assert.Null(OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void Disposing_a_loop_again_does_nothing()
    {
        var host = new LoopHarness();
        host.Dispose();
        using var next = new LoopHarness();

        host.Dispose();

        Assert.Same(next.Loop, OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void Two_threads_each_have_their_own_loop()
    {
        using var host = new LoopHarness();
        OahuLoop? other = null;
        OahuLoop? foundThere = null;

        OtherThread.Run(() =>
        {
            Assert.Null(OahuLoop.ForCurrentThread);
            using var there = new LoopHarness();
            other = there.Loop;
            foundThere = OahuLoop.ForCurrentThread;
        });

        Assert.NotNull(other);
        Assert.Same(other, foundThere);
        Assert.NotSame(host.Loop, other);
        Assert.Same(host.Loop, OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void A_loop_is_not_found_on_another_thread()
    {
        using var host = new LoopHarness();
        OahuLoop? found = host.Loop;
        var onThread = true;

        OtherThread.Run(() =>
        {
            found = OahuLoop.ForCurrentThread;
            onThread = host.Loop.IsOnThread;
        });

        Assert.Null(found);
        Assert.False(onThread);
    }

    [Fact]
    public void A_loop_must_be_created_on_the_thread_its_dispatcher_serves()
    {
        FakeDispatcher? elsewhere = null;
        OtherThread.Run(() => elsewhere = new FakeDispatcher());

        var thrown = Assert.Throws<InvalidOperationException>(() => new OahuLoop(elsewhere!, new RecordingFaultSink()));

        Assert.Contains("thread its dispatcher serves", thrown.Message);
        Assert.Null(OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void A_loop_needs_a_dispatcher_and_a_fault_sink()
    {
        Assert.Throws<ArgumentNullException>("dispatcher", () => new OahuLoop(null!, new RecordingFaultSink()));
        Assert.Throws<ArgumentNullException>("faultSink", () => new OahuLoop(new FakeDispatcher(), null!));
        Assert.Null(OahuLoop.ForCurrentThread);
    }

    [Fact]
    public void A_loop_disposed_off_its_thread_is_no_longer_the_threads_loop()
    {
        var host = new LoopHarness();

        OtherThread.Run(host.Loop.Dispose);

        Assert.Null(OahuLoop.ForCurrentThread);
        host.Dispose();
        Assert.Null(OahuLoop.ForCurrentThread);
    }
}
