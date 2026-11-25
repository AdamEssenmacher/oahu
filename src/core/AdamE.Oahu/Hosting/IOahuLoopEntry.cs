namespace AdamE.Oahu.Hosting;

/// <summary>An entry of a loop's queue: work a delivery turn runs, such as one source's pending delivery.</summary>
/// <remarks>
/// The loop holds the entry itself, so queuing an object that is already allocated allocates nothing. An exception
/// <see cref="Run"/> throws is a fault; the turn goes on with the next entry.
/// </remarks>
internal interface IOahuLoopEntry
{
    /// <summary>Runs the entry, on the loop's thread, during a delivery turn.</summary>
    void Run();
}
