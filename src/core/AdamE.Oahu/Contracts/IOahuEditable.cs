namespace AdamE.Oahu;

/// <summary>
/// A stream of a contract that is a state the screen can also change, such as the text of a field, the position of a
/// slider or a selected option.
/// </summary>
/// <remarks>
/// <para>
/// The rules of this interface and of <see cref="Edit"/> are requirements on every implementation, except where they
/// say what the native model or the exporter does. Oahu's own sources keep them, and an implementation of your own,
/// such as an adapter or a hand-written editable, must keep them too, because the exporter and other observers rely on
/// them.
/// </para>
/// <para>
/// An editable is a state, and every rule of <see cref="IOahuState{T}"/> applies to it: <see cref="IOahuState{T}.Current"/>
/// is the value now, a new observer receives it during <see cref="IObservable{T}.Subscribe"/>, later changes reach
/// observers in later delivery turns and only when they differ from the last value that observer received, disposing
/// a subscription stops delivery at once, it never completes and never fails, and it is used only on the UI thread.
/// </para>
/// <para>
/// The view model owns the value. The screen proposes a new one by calling <see cref="Edit"/>, and the view model
/// decides what the value becomes. The view model also has its own way to set the value, and a change made that way
/// follows the same rules as any change to a state.
/// </para>
/// <para>
/// What the native model and the exporter do, which is not the implementation's work: the screen does not take an
/// edited value for itself, and shows only what the view model publishes. The exporter delivers the result of an edit
/// before the outermost native call returns. An edit the screen makes while it is receiving a message is delivered
/// later in the delivery turn already running. An edit that changes nothing leaves the screen showing the value it
/// already had.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// The type of the value: a type a contract value may have, except <see cref="OahuUnit"/>. It is invariant, because the
/// value is both read and proposed. When it is a reference type, an editable can still be used as a state of a wider
/// type, through the covariance of <see cref="IOahuState{T}"/>, but never as an editable of a wider type. Covariance
/// does not apply to a value type, so an editable of <see cref="int"/> is not a state of <see cref="object"/>.
/// </typeparam>
public interface IOahuEditable<T> : IOahuState<T>
{
    /// <summary>Proposes a new value for the editable.</summary>
    /// <remarks>
    /// <para>
    /// An edit is a proposal. The implementation may accept the value, change it, as when it trims text or clamps a
    /// number to a range, or refuse it. It must settle on one of these before <see cref="Edit"/> returns: when
    /// <see cref="Edit"/> returns, <see cref="IOahuState{T}.Current"/> is the result.
    /// </para>
    /// <para>
    /// If the settled value differs from the value held before the call, the edit is a change to the state and is
    /// delivered under the state's rules, merged with any other changes before the next delivery turn. If it is the
    /// same, the edit changes nothing, whatever value was proposed. That is what a refusal looks like, and also a change
    /// that lands back on the value held before, such as text cut down to a length it already had.
    /// </para>
    /// <para>
    /// "Differs" and "the same" follow the state's rule: contract values compare by structure. How lists and doubles
    /// compare, such as 0.0 and -0.0, or NaN, is defined with structural equality, a later part of the vocabulary.
    /// </para>
    /// <para>
    /// Settling is not delivering. No observer runs while <see cref="Edit"/> is on the stack. The exporter delivers the
    /// result before the outermost native call returns, and that is not the implementation's work.
    /// </para>
    /// <para>
    /// The result is what <see cref="IOahuState{T}.Current"/> holds when <see cref="Edit"/> returns, so an edit cannot
    /// settle later, after an <see langword="await"/>, a timer or a posted callback. Work that must be asynchronous,
    /// such as checking a value with a server, is done some other way: settle now on what is known, and report the
    /// outcome of the work through another stream. A later change the view model makes in its own way is a new change,
    /// not the edit's result.
    /// </para>
    /// <para>
    /// <see cref="Edit"/> is called on the UI thread. Calling it on another thread is a fault, even if the edit would
    /// be refused.
    /// </para>
    /// <para>
    /// Whether an exception thrown by <see cref="Edit"/> is a failure or a fault is not decided yet: it is specified
    /// with the exporter, and an implementation must not rely on either. After it throws,
    /// <see cref="IOahuState{T}.Current"/> must still be a value the implementation could have settled on.
    /// </para>
    /// </remarks>
    /// <param name="value">The proposed value.</param>
    void Edit(T value);
}
