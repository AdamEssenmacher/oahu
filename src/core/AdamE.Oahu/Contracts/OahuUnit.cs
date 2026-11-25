namespace AdamE.Oahu;

/// <summary>The no-value type: the type of events that carry nothing but the fact that they happened.</summary>
/// <remarks>
/// <para>
/// It has one value, <see cref="Value"/>. <see langword="default"/> and <c>new OahuUnit()</c> are the same value, and
/// all of them are equal.
/// </para>
/// <para>
/// A contract uses it only as the whole type of an events stream, such as <c>IOahuEvents&lt;OahuUnit&gt; Saved { get; }</c>.
/// It is never the type of a state, a nullable value, a list's items, a record's field or a method's argument: a
/// method with no argument declares no parameter, and a state always has a value worth a real type.
/// </para>
/// <para>
/// On the wire it is no bytes, so an events message of it is an empty message.
/// </para>
/// </remarks>
public readonly record struct OahuUnit
{
    /// <summary>The one value, for example to publish an occurrence: <c>events.Publish(OahuUnit.Value)</c>.</summary>
    public static OahuUnit Value => default;

    /// <summary>Returns <c>()</c>, the text of the one value.</summary>
    public override string ToString() => "()";
}
