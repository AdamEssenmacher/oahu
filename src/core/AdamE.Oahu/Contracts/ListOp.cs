namespace AdamE.Oahu;

/// <summary>
/// One operation on a keyed list: <see cref="Insert"/>, <see cref="Remove"/>, <see cref="Move"/>,
/// <see cref="Replace"/> or <see cref="Clear"/>.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IOahuKeyedList{T}"/> sends its changes as batches of these operations, so that a native list can
/// animate each change and keep each row's identity. The rules of a batch, and the rules an operation must keep to be
/// applied, are on <see cref="IOahuKeyedList{T}"/>.
/// </para>
/// <para>
/// An index refers to the list as it is when that operation applies: after every operation before it in the same
/// batch, and after every batch before it. So an operation can only be checked against the list it applies to. An
/// operation checks nothing when it is created, not even that an index is not negative: whoever applies it checks it,
/// and refuses the whole batch if any operation in it is not valid there.
/// </para>
/// <para>
/// These five cases are the only ones. <see cref="ListOp{T}"/> is a <see langword="closed"/> record: no type in
/// another assembly can derive from it, and a <see langword="switch"/> that handles the five is complete, so the
/// compiler needs no discard arm.
/// </para>
/// <para>
/// Two operations are equal when they are the same case with equal arguments, by C# record equality: a row compares
/// with its type's <see cref="object.Equals(object)"/>. A row's list field is an <see cref="OahuList{T}"/>, which
/// compares by its items, so two operations whose rows have equal content are equal, whichever list instances they
/// hold.
/// </para>
/// <para>
/// It is shaped like a contract union, but it is not a contract value: a contract never declares it as the type of a
/// stream, a field or an argument. A keyed list carries it, and the wire format writes a batch of it in its own way,
/// with <see cref="Wire.OahuWriter.WriteBatch{T}"/>.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of a row, which is an <see cref="IOahuKeyed"/>.</typeparam>
public closed record ListOp<T>
    where T : IOahuKeyed
{
    private ListOp()
    {
    }

    /// <summary>Inserts a row at an index.</summary>
    /// <remarks>
    /// The index is from 0 to the list's count, inclusive: the count appends the row. The row ends up at the index, and
    /// the rows from that index on move one place later. The list must not already have a row with the new row's key.
    /// </remarks>
    /// <param name="Index">Where the row ends up, from 0 to the list's count.</param>
    /// <param name="Item">The row to insert.</param>
    public sealed record Insert(int Index, T Item) : ListOp<T>;

    /// <summary>Removes the row at an index.</summary>
    /// <remarks>
    /// The index is from 0 to one less than the list's count. The rows after it move one place earlier. Its key can be
    /// used again from the next operation on.
    /// </remarks>
    /// <param name="Index">The index of the row to remove.</param>
    public sealed record Remove(int Index) : ListOp<T>;

    /// <summary>Moves the row at one index so that it ends up at another.</summary>
    /// <remarks>
    /// <para>
    /// The move removes the row at <paramref name="From"/>, then inserts it at <paramref name="To"/> in the list without
    /// it, so <paramref name="To"/> is the row's index afterwards. On [a, b, c, d], <c>Move(0, 2)</c> gives
    /// [b, c, a, d], and <c>Move(2, 0)</c> gives [c, a, b, d]. Both indexes are from 0 to one less than the list's
    /// count: <paramref name="From"/> in the list before the move, <paramref name="To"/> in the list after it, which
    /// has the same count. It is the same row, with the same key and value. A move to its own index changes nothing,
    /// and is still an operation.
    /// </para>
    /// <para>
    /// A note for native runtimes: SwiftUI's <c>move(fromOffsets:toOffset:)</c> takes the destination as an offset in
    /// the list before the removal, which is <c>To + 1</c> when <paramref name="To"/> is greater than
    /// <paramref name="From"/>, and <paramref name="To"/> otherwise. For <c>Move(0, 2)</c> it is offset 3.
    /// </para>
    /// </remarks>
    /// <param name="From">The index of the row to move, in the list before the move.</param>
    /// <param name="To">The row's index after the move.</param>
    public sealed record Move(int From, int To) : ListOp<T>;

    /// <summary>Replaces the row at an index with a new value of the same row.</summary>
    /// <remarks>
    /// The index is from 0 to one less than the list's count. The new value has the same key as the row it replaces:
    /// it is the same row, with a new value. A row whose key changes is another row, removed and inserted. A replace
    /// with a value equal to the one it replaces, by C# record equality, is still an operation.
    /// </remarks>
    /// <param name="Index">The index of the row to replace.</param>
    /// <param name="Item">The row's new value, with the same key.</param>
    public sealed record Replace(int Index, T Item) : ListOp<T>;

    /// <summary>Removes every row.</summary>
    /// <remarks>It applies to any list, including an empty one. Every key can be used again from the next operation on.</remarks>
    public sealed record Clear() : ListOp<T>;
}
