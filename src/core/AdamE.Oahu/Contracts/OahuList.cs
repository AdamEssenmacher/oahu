using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AdamE.Oahu;

/// <summary>
/// The list of a contract value: an immutable list that is equal to another when their items are equal, in order.
/// </summary>
/// <remarks>
/// <para>
/// Every list in a contract value is an <see cref="OahuList{T}"/>: a list field of a record or a union case, a list
/// stream such as <c>IOahuState&lt;OahuList&lt;string&gt;&gt;</c>, and a list inside another list or a nullable value.
/// Build one with a collection expression, such as <c>OahuList&lt;string&gt; tags = ["home", "work"];</c> or
/// <c>[]</c>, with <see cref="OahuList.Create{T}(ReadOnlySpan{T})"/>, or with
/// <see cref="OahuList.ToOahuList{T}(IEnumerable{T})"/>.
/// </para>
/// <para>
/// <b>Immutable.</b> Nothing changes a list after it is built: it has no member that changes it, and no interface it
/// implements has one. A list is built from a copy of the items it is given, so changing the array, list or span it
/// was built from afterwards does not change it. Its items are the values they were when it was built; a contract
/// value's items are immutable too.
/// </para>
/// <para>
/// <b>Equal by content.</b> Two lists are equal when they have the same count and each pair of items at the same index
/// is equal by <see cref="EqualityComparer{T}.Default"/>. Order matters. <see cref="Equals(OahuList{T})"/>,
/// <see cref="Equals(object)"/>, <c>==</c> and <c>!=</c> all make that comparison. So a record with a list field
/// compares by the list's items, through the record's own compiler-generated equality, and a list of lists or of
/// records compares all the way down. An item compares as C# compares it: a <see cref="double"/> NaN equals any other
/// NaN, and 0.0 equals -0.0; a <see cref="DateTimeOffset"/> compares by instant, whatever its offset; a string compares
/// ordinally. Comparing takes time linear in the size of the lists. Two copies of one list, such as an unchanged list
/// field of a record and of a record copied from it with <see langword="with"/>, compare at once, without looking at
/// their items.
/// </para>
/// <para>
/// <b>Never null.</b> It is a struct, so a list field of a record cannot be <see langword="null"/>.
/// <see langword="default"/>, <c>new OahuList&lt;T&gt;()</c>, <see cref="Empty"/> and <c>[]</c> are the same value, the
/// empty list, and each behaves as an empty list everywhere. A list that may be absent is an
/// <c>OahuList&lt;T&gt;?</c>, a <see cref="Nullable{T}"/>, which the wire format writes with a presence byte; an absent
/// list and an empty one are different values.
/// </para>
/// <para>
/// <b>Cost.</b> The list is one reference to an array, which it shares with every copy of it, so copying it copies no
/// items. Indexing it, enumerating it with <see langword="foreach"/>, reading <see cref="AsSpan"/> and comparing it
/// allocate nothing. Using it as an <see cref="IReadOnlyList{T}"/> or an <see cref="IEnumerable{T}"/>, as LINQ does,
/// boxes it, and enumerating it that way allocates an enumerator, so code that cares, such as a codec, uses the list
/// itself. <see cref="Wire.OahuWriter.WriteList{T}"/> takes it without boxing. It implements no collection interface
/// that could change it, such as <see cref="ICollection{T}"/>, so LINQ's <c>Count()</c> and <c>ToList()</c> enumerate
/// it item by item; use <see cref="Count"/> and <see cref="AsSpan"/> instead. <see cref="ToString"/> writes every item.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of the items: a type a contract value may have.</typeparam>
[CollectionBuilder(typeof(OahuList), nameof(OahuList.Create))]
public readonly struct OahuList<T> : IReadOnlyList<T>, IEquatable<OahuList<T>>
{
    // Null for the empty list, so that default is the empty list. Never an empty array, so that two empty lists hold
    // the same reference. Never changed, and never exposed in a way that lets it change.
    private readonly T[]? _items;

    private OahuList(T[] items) => _items = items.Length == 0 ? null : items;

    /// <summary>The empty list, the same value as <see langword="default"/> and <c>[]</c>.</summary>
    public static OahuList<T> Empty => default;

    /// <summary>The number of items.</summary>
    public int Count => _items?.Length ?? 0;

    /// <summary>Whether the list has no items.</summary>
    public bool IsEmpty => _items is null;

    /// <summary>The item at <paramref name="index"/>.</summary>
    /// <param name="index">The index of the item, from 0 to <see cref="Count"/> - 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is negative or not less than <see cref="Count"/>.
    /// </exception>
    public T this[int index]
    {
        get
        {
            var items = _items;
            if (items is null || (uint)index >= (uint)items.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, $"The index is not from 0 to the list's count, {Count}, exclusive.");
            }

            return items[index];
        }
    }

    /// <summary>Two lists are equal when they have the same count and their items are equal, in order.</summary>
    /// <param name="left">A list.</param>
    /// <param name="right">Another list.</param>
    /// <returns>Whether the two lists are equal, as <see cref="Equals(OahuList{T})"/> says.</returns>
    public static bool operator ==(OahuList<T> left, OahuList<T> right) => left.Equals(right);

    /// <summary>Two lists differ when their counts differ or a pair of their items is not equal.</summary>
    /// <param name="left">A list.</param>
    /// <param name="right">Another list.</param>
    /// <returns>Whether the two lists are not equal, as <see cref="Equals(OahuList{T})"/> says.</returns>
    public static bool operator !=(OahuList<T> left, OahuList<T> right) => !left.Equals(right);

    // Takes the array it is given, which the caller must never change or hand out afterwards. OahuReader.ReadList
    // uses it for the array it has just filled; everything else copies.
    internal static OahuList<T> Own(T[] items) => new(items);

    /// <summary>The items, as a read-only span.</summary>
    /// <remarks>The span is empty for the empty list. Reading it allocates nothing.</remarks>
    /// <returns>A span over the items.</returns>
    public ReadOnlySpan<T> AsSpan() => new(_items);

    /// <summary>Returns an enumerator of the items, which <see langword="foreach"/> uses without allocating.</summary>
    /// <returns>An enumerator of the items, in order.</returns>
    public Enumerator GetEnumerator() => new(_items);

    /// <inheritdoc/>
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => (_items ?? []).GetEnumerator();

    /// <summary>
    /// Whether this list and <paramref name="other"/> have the same count and each pair of items at the same index is
    /// equal by <see cref="EqualityComparer{T}.Default"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An item compares as C# compares it. A <see cref="double"/> NaN equals any other NaN, whatever its bits, and 0.0
    /// equals -0.0, though each pair has different bytes on the wire. A <see cref="DateTimeOffset"/> compares by
    /// instant, whatever its offset. A string compares ordinally, by its UTF-16 code units, so two strings that differ
    /// only in Unicode normalization are not equal. A list or a record item compares by its own equality, so nested
    /// lists compare by their items too.
    /// </para>
    /// <para>
    /// The comparison takes time linear in the size of the lists and allocates nothing. Two copies of one list, such
    /// as an unchanged list field of a record and of a record copied from it with <see langword="with"/>, are equal at
    /// once, without looking at their items. Every empty list equals every other, including <see langword="default"/>.
    /// </para>
    /// </remarks>
    /// <param name="other">The list to compare with.</param>
    /// <returns>Whether the two lists are equal.</returns>
    public bool Equals(OahuList<T> other)
    {
        var items = _items;
        var others = other._items;
        if (items == others)
            return true;

        if (items is null || others is null)
            return false;

        // For a value type with the default comparer, this compares each pair with EqualityComparer<T>.Default, or
        // compares bits where the runtime knows that is the same thing, as it is for an int but not for a double.
        return new ReadOnlySpan<T>(items).SequenceEqual(others, EqualityComparer<T>.Default);
    }

    /// <summary>Whether <paramref name="obj"/> is an <see cref="OahuList{T}"/> equal to this one.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns>
    /// Whether <paramref name="obj"/> is an <see cref="OahuList{T}"/> of the same item type, equal to this list as
    /// <see cref="Equals(OahuList{T})"/> says.
    /// </returns>
    public override bool Equals(object? obj) => obj is OahuList<T> other && Equals(other);

    /// <summary>Returns a hash code of the items, consistent with <see cref="Equals(OahuList{T})"/>.</summary>
    /// <remarks>
    /// The hash code combines the hash code of each item by <see cref="EqualityComparer{T}.Default"/>, in order, so
    /// two equal lists have the same hash code. It is computed again on each call, in time linear in the size of the
    /// list, and is not cached. It allocates nothing. Like a string's, it differs from one run of the process to the
    /// next.
    /// </remarks>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var item in AsSpan())
            hash.Add(item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item));

        return hash.ToHashCode();
    }

    /// <summary>Returns the items in brackets, as a collection expression writes them, such as <c>[a, b]</c>.</summary>
    /// <remarks>
    /// Every item is written, with its own <see cref="object.ToString"/>, and a null item as <c>null</c>, so the text of
    /// a large list is large.
    /// </remarks>
    /// <returns>The text of the list.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder("[");
        var items = AsSpan();
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(items[i]?.ToString() ?? "null");
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Enumerates the items of an <see cref="OahuList{T}"/>, in order, without allocating.</summary>
    /// <remarks>
    /// <see langword="foreach"/> uses it. <see cref="Current"/> is valid only after <see cref="MoveNext"/> has
    /// returned <see langword="true"/>.
    /// </remarks>
    public struct Enumerator
    {
        private readonly T[]? _items;
        private int _index;

        internal Enumerator(T[]? items)
        {
            _items = items;
            _index = -1;
        }

        /// <summary>The item at the enumerator's position.</summary>
        public readonly T Current => _items![_index];

        /// <summary>Moves to the next item.</summary>
        /// <returns><see langword="true"/> if there is a next item; <see langword="false"/> at the end.</returns>
        public bool MoveNext()
        {
            var count = _items?.Length ?? 0;
            if (_index < count)
                _index++;

            return _index < count;
        }
    }
}

/// <summary>Builds <see cref="OahuList{T}"/> values.</summary>
/// <remarks>
/// Each method copies the items it is given, so the list it returns never changes, whatever happens to its source
/// afterwards. A collection expression, such as <c>OahuList&lt;string&gt; tags = ["home", "work"];</c>, builds a list
/// through <see cref="Create{T}(ReadOnlySpan{T})"/>.
/// </remarks>
public static class OahuList
{
    /// <summary>Returns a list of a copy of <paramref name="items"/>.</summary>
    /// <remarks>
    /// It takes an array or a span, or items one by one, such as <c>OahuList.Create(1, 2, 3)</c>, and a collection
    /// expression calls it. Changing the array or span afterwards does not change the list. No items give the empty
    /// list, with no allocation. A collection expression of items copies them once. One with a spread, such as
    /// <c>[.. source, 5]</c>, copies them twice: the compiler gathers them into a span, and this method copies the
    /// span. For a large array or <see cref="List{T}"/>, <see cref="ToOahuList{T}(IEnumerable{T})"/> copies once.
    /// </remarks>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="items">The items, in order.</param>
    /// <returns>A list of the items.</returns>
    public static OahuList<T> Create<T>(params ReadOnlySpan<T> items) =>
        items.IsEmpty ? default : OahuList<T>.Own(items.ToArray());

    /// <summary>Returns a list of the items of <paramref name="items"/>, enumerated once, in order.</summary>
    /// <remarks>
    /// <para>
    /// The items are copied, so changing the source afterwards does not change the list. An
    /// <see cref="OahuList{T}"/> passed as an <see cref="IEnumerable{T}"/> is returned as it is, since it never
    /// changes.
    /// </para>
    /// <para>
    /// An array or a <see cref="List{T}"/> is copied once, as a span. A type derived from <see cref="List{T}"/> is
    /// not: it can enumerate other items than the ones it stores, so it is enumerated like any other source. Any other
    /// source is enumerated, once, into an array that the list then keeps. That array is never passed to the source's
    /// own code, so a source cannot keep it and change the list afterwards: <see cref="ICollection{T}.CopyTo"/> is not
    /// called, and neither is <see cref="Enumerable.ToArray{TSource}(IEnumerable{TSource})"/>, which calls it. When the
    /// source tells its count without being enumerated, as every <see cref="ICollection{T}"/> does, a count of up to
    /// 4,096 items sizes the array exactly. A larger count is not trusted for the first array, which holds 4,096
    /// items and then doubles as items arrive, up to the count; with no count, the array grows as a
    /// <see cref="List{T}"/> does. The array is cut to the number of items at the end. A count that turns out to be
    /// wrong changes only the cost: the list has the items the enumeration gave.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="items">The items, in order.</param>
    /// <returns>A list of the items.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public static OahuList<T> CreateRange<T>(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Only types whose items can be read without running the source's own code are copied as a span. A type
        // derived from List<T> can reimplement IEnumerable<T>.GetEnumerator to give other items than the ones it
        // stores, so only List<T> itself is read through CollectionsMarshal. An array type cannot be derived from; an
        // array whose element type is derived from T (array covariance) enumerates the same items its span holds, and
        // ReadOnlySpan, unlike Span, accepts it.
        return items switch
        {
            OahuList<T> list => list,
            T[] array => Create(new ReadOnlySpan<T>(array)),
            List<T> list when list.GetType() == typeof(List<T>) => Create<T>(CollectionsMarshal.AsSpan(list)),
            _ => Enumerate(items),
        };
    }

    // The most items a reported count can make the first array hold before any item is read. A source can report any
    // count, so trusting a large one would let it allocate that much (int.MaxValue items throws OutOfMemoryException)
    // for a few items. 4,096 items cost at most 32 KB for references or 8-byte values, under the large object heap's
    // threshold of 85,000 bytes, and a count up to this size, the common case, still takes a single array.
    internal const int MostTrustedCount = 4096;

    // Enumerates items into an array that no code of theirs ever sees, so the list can own it.
    private static OahuList<T> Enumerate<T>(IEnumerable<T> items)
    {
        // Past the first array, each array at most doubles the items already read, so a count that is too large
        // costs at most a first array of MostTrustedCount items and then twice the items the source gives. The
        // reported count only stops the doubling at that size, so an honest large count needs no cut at the end.
        var hasCount = items.TryGetNonEnumeratedCount(out var count) && count > 0;
        var buffer = hasCount ? new T[Math.Min(count, MostTrustedCount)] : [];
        var length = 0;
        foreach (var item in items)
        {
            if (length == buffer.Length)
            {
                var size = Math.Min(Math.Max(4L, 2L * buffer.Length), Array.MaxLength);
                if (hasCount && count > length)
                    size = Math.Min(size, count);

                Array.Resize(ref buffer, (int)Math.Max(length + 1L, size));
            }

            buffer[length++] = item;
        }

        if (length == 0)
            return default;

        if (length != buffer.Length)
            Array.Resize(ref buffer, length);

        return OahuList<T>.Own(buffer);
    }

    /// <summary>Returns a list of the items of <paramref name="items"/>, as <see cref="CreateRange{T}"/> does.</summary>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="items">The items, in order.</param>
    /// <returns>A list of the items.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public static OahuList<T> ToOahuList<T>(this IEnumerable<T> items) => CreateRange(items);

    /// <summary>Returns a list of a copy of <paramref name="items"/>, as <see cref="Create{T}(ReadOnlySpan{T})"/> does.</summary>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="items">The items, in order.</param>
    /// <returns>A list of the items.</returns>
    public static OahuList<T> ToOahuList<T>(this ReadOnlySpan<T> items) => Create(items);
}
