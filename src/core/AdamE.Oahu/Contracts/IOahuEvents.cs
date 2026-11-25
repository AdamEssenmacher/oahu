namespace AdamE.Oahu;

/// <summary>A stream of a contract that carries occurrences with no current value, such as a message to show once.</summary>
/// <remarks>
/// <para>
/// These rules are requirements on every implementation. Oahu's own sources keep them, and an implementation of your
/// own, such as an adapter, must keep them too, because the exporter and other observers rely on them.
/// </para>
/// <para>
/// Nothing is replayed: a new observer receives only occurrences published after it subscribed. Each occurrence is
/// delivered once to each observer that was subscribed when it was published and is still subscribed when it is
/// delivered.
/// </para>
/// <para>
/// Events are never merged. Two occurrences are two deliveries, even when their values are equal.
/// </para>
/// <para>
/// Observers hear of an occurrence in a later delivery turn, never while the code that published it is still running.
/// </para>
/// <para>
/// Disposing a subscription stops all delivery to it at once, even later in a delivery turn that is already running,
/// and even when the observer disposes it during its own callback. Disposing it again does nothing.
/// </para>
/// <para>
/// A stream of events never completes and never fails: it never calls <see cref="IObserver{T}.OnCompleted"/> or
/// <see cref="IObserver{T}.OnError"/>.
/// </para>
/// <para>
/// A stream of events is used only on the UI thread: observers subscribe and dispose their subscriptions there, and
/// occurrences are delivered there. Using a stream of events on another thread is a mistake. How Oahu reports it is
/// specified with the sources; a change made off the UI thread is a fault.
/// </para>
/// <para>
/// An exception thrown by an observer is a fault. The details arrive with the sources.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of each occurrence's value: a type a contract value may have.</typeparam>
public interface IOahuEvents<out T> : IObservable<T>;
