namespace AdamE.Oahu.Wire;

/// <summary>
/// The exception that <see cref="OahuReader"/> throws when its input is not valid in Oahu's wire format.
/// </summary>
/// <remarks>The message says what was wrong and at which byte offset.</remarks>
public sealed class OahuFormatException : FormatException
{
    internal OahuFormatException(string message, int offset)
        : base(message)
    {
        Offset = offset;
    }

    /// <summary>The offset, from the start of the input, of the byte where the malformed part begins.</summary>
    public int Offset { get; }
}
