namespace AdamE.Oahu.Hosting;

/// <summary>
/// The thread an <see cref="OahuLoop"/> serves, as its host provides it: a check for whether the caller is on that
/// thread, and a way to run work on it later. In an app it is the UI thread and its run loop.
/// </summary>
/// <remarks>
/// <para>
/// A host implements it for its platform's UI thread. One dispatcher serves one thread, for as long as the loops
/// created over it are alive.
/// </para>
/// <para>
/// Both members may be called on any thread, at the same time, so an implementation must be thread-safe: the loop
/// calls them on its own thread, and also on the thread a fault was raised on, which may be another one.
/// </para>
/// <para>
/// The host must also make sure the dispatcher's thread has a <see cref="SynchronizationContext"/> that posts back to
/// that thread, because the loop installs none. An app needs it: the code after an <see langword="await"/> in a view
/// model's method must resume on the loop's thread, since changing a source on another thread is a fault.
/// </para>
/// <para>
/// A fault on a loop whose dispatcher has stopped taking work still ends the process, from a new thread, as
/// <see cref="OahuLoop"/> describes. So while an app shuts down, nothing may change a source off the UI thread.
/// </para>
/// </remarks>
public interface IOahuDispatcher
{
    /// <summary>Whether the caller is on the dispatcher's thread.</summary>
    /// <remarks>
    /// May be called on any thread; it must be thread-safe. It must be <see langword="true"/> exactly on the thread the
    /// loop was created on, and <see langword="false"/> on every other. It must not throw, and it must give the same
    /// answer for a thread every time it is asked.
    /// </remarks>
    bool IsOnThread { get; }

    /// <summary>
    /// Queues <paramref name="action"/> to run on the dispatcher's thread, in a later pass of its run loop, unless the
    /// dispatcher has stopped taking work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// May be called on any thread; it must be thread-safe. Actions run in the order they were posted. The loop posts
    /// its delivery turns with it, and the end of the app after a fault.
    /// </para>
    /// <para>
    /// It must never run the action before it returns, even when it is called on the dispatcher's thread: it always
    /// queues. Some platform APIs that post to the UI thread run the action at once when they are called on that
    /// thread, such as MAUI's <c>MainThread.BeginInvokeOnMainThread</c> and Android's <c>Activity.RunOnUiThread</c>,
    /// so they cannot be used for it. The loop treats a delivery turn run inside <see cref="TryPost"/> as a fault, and
    /// when the end of the app runs inside it, the loop ends the process from a new thread.
    /// </para>
    /// <para>
    /// An exception the action throws must end the process as an unhandled exception: the dispatcher must not catch it
    /// and carry on. The end of the app throws one on purpose. Where posted actions run under native frames that a
    /// managed exception must not cross, the host catches the exception at that boundary and ends the process there
    /// itself. Either way the loop has reported the fault to its <see cref="IOahuFaultSink"/> before then.
    /// </para>
    /// <para>
    /// An exception thrown by <see cref="TryPost"/> itself is treated as <see langword="false"/>. A loop whose delivery
    /// turn is refused stops, as <see cref="OahuLoop"/> describes.
    /// </para>
    /// </remarks>
    /// <param name="action">The work to run.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="action"/> will run later on the dispatcher's thread, exactly once;
    /// <see langword="false"/> when the dispatcher has stopped taking work, as a UI thread does while the app shuts
    /// down, and the action will never run. A platform API that reports whether it took the work, such as Android's
    /// <c>Handler.post</c> or WinUI's <c>DispatcherQueue.TryEnqueue</c>, gives the answer to return. A dispatcher that
    /// returns <see langword="true"/> must run the action: the loop posts no other turn while one is pending, so a turn
    /// that never runs makes the loop stop delivering, and nothing reports it.
    /// </returns>
    bool TryPost(Action action);
}
