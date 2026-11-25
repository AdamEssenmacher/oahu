namespace AdamE.Oahu.Wire;

/// <summary>
/// Reads values in Oahu's wire format from a span of bytes, in order from the start.
/// </summary>
/// <remarks>
/// A reader holds the input and its position in it, and nothing else. A single read call either returns a value and
/// moves past its bytes, or throws <see cref="OahuFormatException"/> and leaves the position where it was. A value
/// made of several parts is read with several calls, so a failure can come after earlier parts have been read. A
/// reader that has thrown is not used again: the message is rejected as a whole. After reading a message's value, call
/// <see cref="EnsureEnd"/> to reject bytes left over.
/// </remarks>
public ref struct OahuReader
{
    private const int MaxVarUIntBytes = 10;

    private readonly ReadOnlySpan<byte> _input;
    private int _offset;

    /// <summary>Creates a reader positioned at the start of <paramref name="input"/>.</summary>
    /// <param name="input">The bytes to read.</param>
    public OahuReader(ReadOnlySpan<byte> input)
    {
        _input = input;
        _offset = 0;
    }

    /// <summary>The number of bytes not read yet.</summary>
    public readonly int Remaining => _input.Length - _offset;

    /// <summary>Reads an unsigned variable-length integer.</summary>
    /// <remarks>
    /// An encoding longer than the shortest form is accepted, up to 10 bytes. In a 10-byte encoding the last byte must be
    /// 0 or 1.
    /// </remarks>
    /// <returns>The value read.</returns>
    /// <exception cref="OahuFormatException">
    /// The input ends inside the integer, the integer is longer than 10 bytes, or its value does not fit in 64 bits.
    /// </exception>
    public ulong ReadVarUInt()
    {
        var start = _offset;

        // Most integers are one byte.
        if ((uint)start < (uint)_input.Length)
        {
            var first = _input[start];
            if (first < 0x80)
            {
                _offset = start + 1;
                return first;
            }
        }

        ulong value = 0;

        // The first nine bytes carry 7 bits each.
        for (var i = 0; i < MaxVarUIntBytes - 1; i++)
        {
            var b = ByteOfVarUInt(start, i);
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                _offset = start + i + 1;
                return value;
            }
        }

        // The tenth byte carries only bit 63 and must end the integer.
        var last = ByteOfVarUInt(start, MaxVarUIntBytes - 1);
        if ((last & 0x80) != 0)
        {
            throw new OahuFormatException(
                $"The variable-length integer at byte offset {start} is longer than {MaxVarUIntBytes} bytes.", start);
        }

        if (last > 1)
        {
            throw new OahuFormatException(
                $"The variable-length integer at byte offset {start} does not fit in 64 bits.", start);
        }

        _offset = start + MaxVarUIntBytes;
        return value | ((ulong)last << 63);
    }

    /// <summary>Reads a count, such as a length or a number of items, written as an unsigned variable-length integer.</summary>
    /// <returns>The count read, from 0 to <see cref="int.MaxValue"/>.</returns>
    /// <exception cref="OahuFormatException">
    /// The integer is malformed, as for <see cref="ReadVarUInt"/>, or the count is larger than <see cref="int.MaxValue"/>.
    /// </exception>
    public int ReadCount()
    {
        var start = _offset;
        var value = ReadVarUInt();
        if (value > int.MaxValue)
        {
            _offset = start;
            throw new OahuFormatException(
                $"The count {value} at byte offset {start} is larger than the largest count, {int.MaxValue}.", start);
        }

        return (int)value;
    }

    /// <summary>Checks that the whole input has been read.</summary>
    /// <exception cref="OahuFormatException">Bytes remain after the last value read.</exception>
    public readonly void EnsureEnd()
    {
        if (_offset != _input.Length)
        {
            throw new OahuFormatException(
                $"{Remaining} unread byte(s) remain at the end of the input, from byte offset {_offset}.", _offset);
        }
    }

    private readonly byte ByteOfVarUInt(int start, int index)
    {
        var position = start + index;
        if (position >= _input.Length)
        {
            throw new OahuFormatException(
                $"The input ends inside a variable-length integer that starts at byte offset {start}.", start);
        }

        return _input[position];
    }
}
