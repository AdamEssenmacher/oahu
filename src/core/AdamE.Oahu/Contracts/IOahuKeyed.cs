namespace AdamE.Oahu;

/// <summary>A row of a keyed list, with the key that is its identity.</summary>
/// <remarks>
/// <para>
/// These rules are requirements on every row of an <see cref="IOahuKeyedList{T}"/> and on every implementation of that
/// stream, because the exporter and the native lists rely on them.
/// </para>
/// <para>
/// A row's type is a record or a union that implements this interface through a positional <c>string Key</c>
/// parameter, on the record or on every case of the union, such as
/// <c>public sealed record TaskRow(string Key, string Text, bool Done) : IOahuKeyed;</c>. So the key is one of the
/// row's fields, and it crosses the seam like any other field. In a union, each case passes its own <c>Key</c>
/// parameter, unchanged, to the union's base record, as in <c>Card(string Key, string Text) : BoardRow(Key)</c>, so
/// that the interface's <see cref="Key"/> is the case's <c>Key</c> field. <c>Card(string Key, string Text) :
/// BoardRow(Text)</c> is a mistake that the compiler reports only as warning CS8907, an unread parameter. Where warnings
/// are not errors it builds, and then <see cref="Key"/> returns the text. A row has no field named <c>Id</c>: a
/// native row can be identified by its key under the name <c>id</c>, as a Swift <c>Identifiable</c> is, and a field
/// named <c>Id</c> would take that name too. The analyzer, which comes in a later milestone, will enforce these rules.
/// Nothing enforces them yet.
/// </para>
/// <para>
/// The key is the row's identity: a later operation that names a row with this key names the same row, which a native
/// list keeps on screen, animates and keeps the state of. A row that changes its value but keeps its key is replaced
/// with <see cref="ListOp{T}.Replace"/>; a row with another key is another row.
/// </para>
/// <para>
/// A key is never <see langword="null"/>. It is well-formed UTF-16, with no lone surrogate: the wire format writes a
/// lone surrogate as U+FFFD, so two different keys could otherwise arrive as one. Keys are compared ordinally, by their
/// UTF-16 code units, with no culture and no Unicode normalization, so two strings that differ only in normalization
/// are two keys.
/// </para>
/// <para>
/// Keys are unique within a list at every moment: no two rows of a list have the same key, before or after any
/// operation, including between the operations of one batch. A key can be used again once the row that had it is
/// removed, even later in the same batch.
/// </para>
/// <para>
/// What the native runtimes do, which is not the row's work: they compare keys the way C# does, by their UTF-16 code
/// units, so a native list has exactly the rows and the identities the C# list has. A native runtime must not compare
/// keys with a string equality that does more. Swift's <c>String</c> equality treats canonically equivalent strings as
/// equal, so a Swift runtime cannot compare keys as <c>String</c> values.
/// </para>
/// </remarks>
public interface IOahuKeyed
{
    /// <summary>The row's key: its identity, unique within its list.</summary>
    /// <remarks>Never <see langword="null"/>, and well-formed UTF-16. Keys are compared ordinally.</remarks>
    string Key { get; }
}
