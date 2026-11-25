namespace AdamE.Oahu.Wire;

/// <summary>Constants of Oahu's wire format, the same in every implementation of it.</summary>
public static class OahuFormat
{
    /// <summary>The largest nesting depth of a value: 64.</summary>
    /// <remarks>
    /// <para>
    /// A value's depth is the number of record and union values that enclose it, counting itself if it is one. A
    /// message whose value is a record is at depth 1, and a record in one of its fields, directly or inside a list or a
    /// nullable value, is at depth 2. Lists, nullable values, enums, strings and primitive values do not add depth.
    /// </para>
    /// <para>
    /// Records and unions count because they are the only kinds of value that can contain a value of their own type.
    /// Every record and union value counts, including a value of a record type that never contains itself.
    /// </para>
    /// <para>
    /// A union value and the case it holds are one level, not two: the union value counts once, the record of its case
    /// is not counted again, and the case's fields are at the union's depth. A record type that is also a union case
    /// counts as a record when it is a field's value, and not again when it is the case of a union value.
    /// </para>
    /// <para>
    /// A value at a depth greater than this is not valid in the format. <see cref="OahuWriter.EnterNested"/> refuses to
    /// write it, and <see cref="OahuReader.EnterNested"/> rejects it as malformed.
    /// </para>
    /// </remarks>
    public const int MaxNestingDepth = 64;
}
