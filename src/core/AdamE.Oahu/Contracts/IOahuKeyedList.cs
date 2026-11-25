namespace AdamE.Oahu;

/// <summary>
/// A stream of a contract that is a list of rows with stable keys, such as the rows of a table, sent to the screen as
/// batches of operations.
/// </summary>
/// <remarks>
/// <para>
/// These rules are requirements on every implementation, except where they say what the exporter or the native model
/// does. Oahu's own sources keep them, and an implementation of your own, such as an adapter, must keep them too,
/// because the exporter and other observers rely on them.
/// </para>
/// <para>
/// <see cref="Items"/> is the rows now, in order. Each row is an <see cref="IOahuKeyed"/>, and no two rows have the same
/// key, as <see cref="IOahuKeyed"/> says. Each notification is one batch: a list of <see cref="ListOp{T}"/> that
/// changes the list. Applying every batch an observer has received, in order, to an empty list gives the rows that
/// <see cref="Items"/> had after the last operation that observer received.
/// </para>
/// <para>
/// The operations of a batch apply one after another, in order. Each index refers to the list as it is when that
/// operation applies: after every operation before it in the batch. An operation is valid only if its indexes are in
/// range for that list, as <see cref="ListOp{T}"/> says for each case, an insert adds a key the list does not have
/// then, a replace keeps the key of the row it replaces, and every row is not <see langword="null"/> and has a key
/// that follows the rules of <see cref="IOahuKeyed"/>.
/// </para>
/// <para>
/// A batch is applied all together or not at all. When the view model applies a batch, <see cref="Items"/> changes at
/// once by all of it. A batch with an operation that is not valid changes nothing: <see cref="Items"/> stays as it
/// was, and nothing is delivered. Whether such a batch is thrown to the caller or reported as a fault is specified
/// with the sources. A batch with no operations changes nothing and delivers nothing.
/// </para>
/// <para>
/// A new observer receives the current rows at once, during <see cref="IObservable{T}.Subscribe"/>, before that call
/// returns, as one batch: a <see cref="ListOp{T}.Clear"/>, then an <see cref="ListOp{T}.Insert"/> of each row of
/// <see cref="Items"/>, in order, at indexes 0, 1, 2 and on. The batch for an empty list is one
/// <see cref="ListOp{T}.Clear"/>.
/// </para>
/// <para>
/// Then, in later delivery turns, it receives every operation applied since its last delivery, or since its replay,
/// in order, as one batch. No operation is dropped, merged with another or reordered, and none is delivered to it
/// twice: an operation applied before it subscribed is part of its replay, not of a later batch. Several batches
/// applied before a turn reach an observer as one batch, the operations of the first followed by those of the next.
/// A delivered batch is never empty: an observer that has nothing new receives nothing. An observer can receive more
/// than one batch in one delivery turn: a turn runs until its queue is empty, so when the list changes during the turn,
/// after an observer's batch, the observer receives those operations as another batch later in the same turn.
/// </para>
/// <para>
/// A delivered batch never changes afterwards, contains no <see langword="null"/> operation, and may be one list
/// instance shared by several observers, so an observer must not try to change it. A batch is an
/// <see cref="IReadOnlyList{T}"/>, not an <see cref="OahuList{T}"/>, because, like the keyed list, it is not a contract
/// value: the implementation chooses its storage, and keeping it unchanged is the implementation's duty.
/// </para>
/// <para>
/// Observers hear of a change in a later delivery turn, never during the call that changed the list: no observer runs
/// while that call is on the stack. A turn can run before the method that made the change has finished, when that
/// method is waiting at an <see langword="await"/>.
/// </para>
/// <para>
/// Disposing a subscription stops all delivery to it at once, even later in a delivery turn that is already running,
/// and even when the observer disposes it during its own callback. Disposing it again does nothing.
/// </para>
/// <para>
/// A keyed list never completes and never fails: it never calls <see cref="IObserver{T}.OnCompleted"/> or
/// <see cref="IObserver{T}.OnError"/>.
/// </para>
/// <para>
/// A keyed list is used only on the UI thread: <see cref="Items"/> is read there, observers subscribe and dispose
/// their subscriptions there, and batches are delivered there. Changing a keyed list on another thread is a fault.
/// Reading <see cref="Items"/>, subscribing or disposing a subscription on another thread is a mistake too; whether
/// Oahu reports it as a fault is specified with the sources, so an implementation must not rely on it being allowed.
/// </para>
/// <para>
/// An exception thrown by an observer is a fault. The details arrive with the sources.
/// </para>
/// <para>
/// What the exporter and the native model do, which is not the implementation's work: the exporter writes each batch,
/// the replay included, as one message, and the native model applies the message all together or not at all, so the
/// screen's list is never left half-changed. The wire format's keyed-list batches give a batch's bytes;
/// <see cref="Wire.OahuWriter.WriteBatch{T}"/> writes them and <see cref="Wire.OahuReader.ReadBatch{T}"/> reads them.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// The type of a row: a record or a union that implements <see cref="IOahuKeyed"/> through a positional
/// <c>string Key</c> parameter. It is invariant, because a batch is a list of <see cref="ListOp{T}"/>, a record, and a
/// class cannot vary in its type argument. So a keyed list of one row type is never a keyed list of another, though it
/// is still an <see cref="IObservable{T}"/> of its batches, and of <see cref="object"/>.
/// </typeparam>
public interface IOahuKeyedList<T> : IObservable<IReadOnlyList<ListOp<T>>>
    where T : IOahuKeyed
{
    /// <summary>The rows now, in order.</summary>
    /// <remarks>
    /// <para>
    /// It changes as soon as the view model applies a batch, by the whole batch, before any observer hears of the
    /// change. No two of its rows have the same key.
    /// </para>
    /// <para>
    /// A list it returned does not change afterwards: a later change gives a new list.
    /// </para>
    /// <para>
    /// It is an <see cref="IReadOnlyList{T}"/>, not an <see cref="OahuList{T}"/>, because a keyed list is not a contract
    /// value: it changes by operations, and this is a read-only view of its content now. The implementation chooses
    /// the storage behind it, so a source of many rows can keep a persistent list and not copy every row on every
    /// change. So the rule that a list it returned never changes is the implementation's duty, not something the type
    /// enforces. The rows themselves, and any list inside a row, are contract values, and such a list is an
    /// <see cref="OahuList{T}"/>.
    /// </para>
    /// </remarks>
    IReadOnlyList<T> Items { get; }
}
