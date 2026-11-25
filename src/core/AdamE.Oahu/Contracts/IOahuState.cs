namespace AdamE.Oahu;

/// <summary>A stream of a contract that always has a current value, such as a title or the status of a download.</summary>
/// <remarks>
/// <para>
/// These rules are requirements on every implementation. Oahu's own sources keep them, and an implementation of your
/// own, such as an adapter or a hand-written state, must keep them too, because the exporter and other observers rely
/// on them.
/// </para>
/// <para>
/// <see cref="Current"/> is the value now. When the view model changes it, <see cref="Current"/> changes at once.
/// </para>
/// <para>
/// A new observer receives the current value at once, during <see cref="IObservable{T}.Subscribe"/>, before that call
/// returns. Then, in later delivery turns, it receives the value current at that time, when it differs from the last
/// one that observer received. Contract values compare by structure. For a list, that takes Oahu's own comparison,
/// which arrives with structural equality in a later part of the vocabulary: the default comparer compares two lists
/// by reference, so an implementation that uses it delivers an equal list again.
/// </para>
/// <para>
/// Observers hear of a change in a later delivery turn, never during the call that changed the value: no observer
/// runs while that call is on the stack. A turn can run before the method that made the change has finished, when
/// that method is waiting at an <see langword="await"/>.
/// So several changes before a turn can reach an observer as one value, and a change and its reversal as none.
/// </para>
/// <para>
/// Disposing a subscription stops all delivery to it at once, even later in a delivery turn that is already running,
/// and even when the observer disposes it during its own callback. Disposing it again does nothing.
/// </para>
/// <para>
/// A state never completes and never fails: it never calls <see cref="IObserver{T}.OnCompleted"/> or
/// <see cref="IObserver{T}.OnError"/>.
/// </para>
/// <para>
/// A state is used only on the UI thread: <see cref="Current"/> is read there, observers subscribe and dispose their
/// subscriptions there, and values are delivered there. Changing a state's value on another
/// thread is a fault. Reading <see cref="Current"/>, subscribing or disposing a subscription on another thread is a
/// mistake too; whether Oahu reports it as a fault is specified with the sources, so an implementation must not
/// rely on it being allowed.
/// </para>
/// <para>
/// An exception thrown by an observer is a fault. The details arrive with the sources.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of the value: a type a contract value may have.</typeparam>
public interface IOahuState<out T> : IObservable<T>
{
    /// <summary>The value now.</summary>
    /// <remarks>It changes as soon as the view model changes the value, before any observer hears of the change.</remarks>
    T Current { get; }
}
