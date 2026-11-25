namespace AdamE.Oahu.Tests.Wire;

/// <summary>Measures how many bytes an action allocates on the calling thread.</summary>
/// <remarks>
/// The runtime can allocate on the calling thread on its own, for example when tiered compilation recompiles a
/// method during a loop. So the action runs for several rounds, and the smallest round counts. A one-off runtime
/// allocation shows in one round only; an allocation the action makes on every call shows in every round.
/// </remarks>
internal static class Allocations
{
    private const int WarmUpCalls = 1_000;
    private const int Rounds = 5;
    private const int CallsPerRound = 10_000;

    /// <summary>Returns the bytes allocated by the round of calls that allocated least.</summary>
    public static long Smallest(Action action)
    {
        for (var i = 0; i < WarmUpCalls; i++)
            action();

        var smallest = long.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < CallsPerRound; i++)
                action();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            smallest = Math.Min(smallest, allocated);
        }

        return smallest;
    }
}
