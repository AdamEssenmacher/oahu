using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Unicode;

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

    // DateTimeOffset.MinValue and MaxValue, in whole milliseconds since the Unix epoch.
    private const long MinInstantMilliseconds = -62_135_596_800_000;
    private const long MaxInstantMilliseconds = 253_402_300_799_999;

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

    /// <summary>Reads a bool: one byte, 0 for <see langword="false"/> or 1 for <see langword="true"/>.</summary>
    /// <returns>The value read.</returns>
    /// <exception cref="OahuFormatException">The input has ended, or the byte is neither 0 nor 1.</exception>
    public bool ReadBool()
    {
        var start = _offset;
        if (start >= _input.Length)
        {
            throw new OahuFormatException($"The input ends before the bool at byte offset {start}.", start);
        }

        var b = _input[start];
        if (b > 1)
        {
            throw new OahuFormatException(
                $"The bool at byte offset {start} is 0x{b:x2}, which is neither 0x00 nor 0x01.", start);
        }

        _offset = start + 1;
        return b != 0;
    }

    /// <summary>Reads an <see cref="int"/>, zigzag-encoded and written as an unsigned variable-length integer.</summary>
    /// <remarks>
    /// The integer's value, before the zigzag step is undone, must be at most <see cref="uint.MaxValue"/>. An encoding
    /// longer than the shortest form is accepted when its value is in that range.
    /// </remarks>
    /// <returns>The value read.</returns>
    /// <exception cref="OahuFormatException">
    /// The integer is malformed, as for <see cref="ReadVarUInt"/>, or its value is larger than <see cref="uint.MaxValue"/>,
    /// so it does not fit in 32 bits.
    /// </exception>
    public int ReadInt()
    {
        var start = _offset;
        var value = ReadVarUInt();
        if (value > uint.MaxValue)
        {
            _offset = start;
            throw new OahuFormatException(
                $"The int at byte offset {start} does not fit in 32 bits: its zigzag-encoded value {value} is larger " +
                $"than {uint.MaxValue}.",
                start);
        }

        var zigzag = (uint)value;
        return (int)(zigzag >> 1) ^ -(int)(zigzag & 1);
    }

    /// <summary>Reads a <see cref="long"/>, zigzag-encoded and written as an unsigned variable-length integer.</summary>
    /// <returns>The value read.</returns>
    /// <exception cref="OahuFormatException">The integer is malformed, as for <see cref="ReadVarUInt"/>.</exception>
    public long ReadLong()
    {
        var zigzag = ReadVarUInt();
        return (long)(zigzag >> 1) ^ -(long)(zigzag & 1);
    }

    /// <summary>Reads a double: eight bytes, its IEEE 754 bit pattern, little-endian.</summary>
    /// <remarks>
    /// Every bit pattern is accepted, and the value returned has exactly the bits read, including negative zero and a
    /// NaN's sign and payload. The format does not promise that a NaN's payload survives from the writer's side, because a
    /// runtime may quieten a signalling NaN before it is written.
    /// </remarks>
    /// <returns>The value read.</returns>
    /// <exception cref="OahuFormatException">Fewer than eight bytes remain.</exception>
    public double ReadDouble()
    {
        var start = _offset;
        if (_input.Length - start < sizeof(double))
        {
            throw new OahuFormatException(
                $"The input ends inside a double that starts at byte offset {start}.", start);
        }

        var bits = BinaryPrimitives.ReadInt64LittleEndian(_input.Slice(start, sizeof(double)));
        _offset = start + sizeof(double);
        return BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>
    /// Reads an instant: milliseconds since the Unix epoch, 1970-01-01T00:00:00Z, written as a <see cref="long"/>.
    /// </summary>
    /// <remarks>
    /// The value must be in the range a <see cref="DateTimeOffset"/> holds, from 0001-01-01T00:00:00Z to
    /// 9999-12-31T23:59:59.999Z. The format does not carry an offset, so the value returned is always in UTC, with an
    /// offset of zero.
    /// </remarks>
    /// <returns>The instant read, with an offset of zero.</returns>
    /// <exception cref="OahuFormatException">
    /// The integer is malformed, as for <see cref="ReadLong"/>, or the instant is outside the range of
    /// <see cref="DateTimeOffset"/>.
    /// </exception>
    public DateTimeOffset ReadInstant()
    {
        var start = _offset;
        var milliseconds = ReadLong();
        if (milliseconds is < MinInstantMilliseconds or > MaxInstantMilliseconds)
        {
            _offset = start;
            throw new OahuFormatException(
                $"The instant at byte offset {start} is {milliseconds.ToString(CultureInfo.InvariantCulture)} " +
                "milliseconds from the Unix epoch, outside the range from " +
                $"{MinInstantMilliseconds.ToString(CultureInfo.InvariantCulture)} (0001-01-01T00:00:00Z) to " +
                $"{MaxInstantMilliseconds.ToString(CultureInfo.InvariantCulture)} (9999-12-31T23:59:59.999Z).",
                start);
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    /// <summary>Reads a duration: a count of 100-nanosecond ticks, written as a <see cref="long"/>.</summary>
    /// <remarks>
    /// Every <see cref="long"/> is a valid duration, so every <see cref="TimeSpan"/> from <see cref="TimeSpan.MinValue"/>
    /// to <see cref="TimeSpan.MaxValue"/> can be read.
    /// </remarks>
    /// <returns>The duration read.</returns>
    /// <exception cref="OahuFormatException">The integer is malformed, as for <see cref="ReadLong"/>.</exception>
    public TimeSpan ReadDuration() => new(ReadLong());

    /// <summary>
    /// Reads a string: its UTF-8 byte length, written as a count, followed by that many bytes of strict UTF-8.
    /// </summary>
    /// <remarks>
    /// The bytes must be well-formed UTF-8. Overlong forms, encoded surrogates, code points above U+10FFFF, truncated
    /// sequences and stray continuation bytes are rejected, never replaced with U+FFFD. U+0000 and U+FEFF are read as
    /// ordinary characters. Only the string returned is allocated.
    /// </remarks>
    /// <returns>The string read.</returns>
    /// <exception cref="OahuFormatException">
    /// The length is malformed, as for <see cref="ReadCount"/>, the length is larger than the bytes that remain after
    /// it, or the bytes are not well-formed UTF-8.
    /// </exception>
    public string ReadString()
    {
        var start = _offset;
        var length = ReadCount();
        var bytesStart = _offset;
        var available = _input.Length - bytesStart;
        if (length > available)
        {
            _offset = start;
            throw new OahuFormatException(
                $"The string at byte offset {start} is {length} byte(s) long, but only {available} byte(s) remain " +
                "after its length.",
                start);
        }

        var bytes = _input.Slice(bytesStart, length);
        if (!Utf8.IsValid(bytes))
        {
            _offset = start;
            throw IllFormedUtf8(start, bytesStart, bytes);
        }

        _offset = bytesStart + length;
        return Encoding.UTF8.GetString(bytes);
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

    private static OahuFormatException IllFormedUtf8(int start, int bytesStart, ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        while (true)
        {
            var status = Rune.DecodeFromUtf8(bytes[index..], out _, out var consumed);
            if (status == OperationStatus.Done)
            {
                index += consumed;
                continue;
            }

            var problem = status == OperationStatus.NeedMoreData
                ? "is cut short by the end of the string"
                : "is ill-formed";
            return new OahuFormatException(
                $"The string at byte offset {start} is not well-formed UTF-8: the sequence at byte offset " +
                $"{bytesStart + index} {problem}.",
                start);
        }
    }
}
