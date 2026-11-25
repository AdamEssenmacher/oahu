using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text.Unicode;

namespace AdamE.Oahu.Wire;

/// <summary>
/// Writes values in Oahu's wire format to a growable byte buffer that the writer owns.
/// </summary>
/// <remarks>
/// A writer holds bytes and nothing else. It is not thread-safe. Call <see cref="Reset"/> to reuse it for another message.
/// </remarks>
public sealed class OahuWriter
{
    private const int DefaultCapacity = 256;

    private static readonly SearchValues<char> AsciiChars =
        SearchValues.Create(string.Create(128, 0, static (chars, _) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)i;
        }));

    private byte[] _buffer;
    private int _count;

    /// <summary>Creates a writer with a default initial capacity.</summary>
    public OahuWriter()
        : this(DefaultCapacity)
    {
    }

    /// <summary>Creates a writer whose buffer starts with room for <paramref name="initialCapacity"/> bytes.</summary>
    /// <param name="initialCapacity">The number of bytes the buffer holds before it first grows. It may be 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is negative.</exception>
    public OahuWriter(int initialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _buffer = initialCapacity == 0 ? [] : new byte[initialCapacity];
    }

    /// <summary>The bytes written since the writer was created or last reset.</summary>
    /// <remarks>The span is valid until the next write or <see cref="Reset"/>.</remarks>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _count);

    /// <summary>The number of bytes written since the writer was created or last reset.</summary>
    public int WrittenCount => _count;

    /// <summary>Empties the writer so that it can write another message.</summary>
    /// <remarks>
    /// The buffer is kept and never shrinks, so it keeps the size that the largest message written so far needed.
    /// </remarks>
    public void Reset() => _count = 0;

    /// <summary>
    /// Writes <paramref name="value"/> as an unsigned variable-length integer, in the shortest form: 1 to 10 bytes.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteVarUInt(ulong value)
    {
        var size = VarUIntSize(value);
        WriteVarUInt(Reserve(size), value);
    }

    /// <summary>Writes a count, such as a length or a number of items, as an unsigned variable-length integer.</summary>
    /// <param name="count">The count to write.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        WriteVarUInt((ulong)count);
    }

    /// <summary>Writes <paramref name="value"/> as one byte: 0 for <see langword="false"/>, 1 for <see langword="true"/>.</summary>
    /// <param name="value">The value to write.</param>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteBool(bool value) => Reserve(1)[0] = value ? (byte)1 : (byte)0;

    /// <summary>
    /// Writes <paramref name="value"/> zigzag-encoded, as an unsigned variable-length integer in the shortest form: 1 to
    /// 5 bytes.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteInt(int value) => WriteVarUInt((uint)((value << 1) ^ (value >> 31)));

    /// <summary>
    /// Writes <paramref name="value"/> zigzag-encoded, as an unsigned variable-length integer in the shortest form: 1 to
    /// 10 bytes.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteLong(long value) => WriteVarUInt((ulong)((value << 1) ^ (value >> 63)));

    /// <summary>Writes <paramref name="value"/> as eight bytes: its IEEE 754 bit pattern, little-endian.</summary>
    /// <remarks>
    /// The bits of <paramref name="value"/> are written unchanged, including negative zero and a NaN's sign and payload.
    /// The format does not promise that a NaN's payload survives to the reader's side, because a runtime may quieten a
    /// signalling NaN before it reaches a writer.
    /// </remarks>
    /// <param name="value">The value to write.</param>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteDouble(double value) =>
        BinaryPrimitives.WriteInt64LittleEndian(Reserve(sizeof(double)), BitConverter.DoubleToInt64Bits(value));

    /// <summary>
    /// Writes <paramref name="value"/> as its UTF-8 byte length, written as a count, followed by its UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// The length is in bytes, not characters. No terminator or byte-order mark is added, and U+0000 and U+FEFF are
    /// written as ordinary characters. A string that is not well-formed UTF-16 is still written: each lone surrogate,
    /// a high surrogate that no low surrogate follows or a low surrogate that no high surrogate comes before, is
    /// written as U+FFFD, the bytes <c>ef bf bd</c>. So the bytes are always well-formed UTF-8, and the length counts
    /// three bytes for each replaced surrogate. A string cut in the middle of a character then shows one replacement
    /// character instead of ending the app. The string's UTF-8 bytes, with its length and the bytes written before it,
    /// must fit in <see cref="Array.MaxLength"/> bytes, which also keeps the length within the largest count; a write
    /// that does not fit throws and leaves the writer as it was.
    /// </remarks>
    /// <param name="value">The string to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Each UTF-16 char takes 1 to 3 UTF-8 bytes: a char below U+0800 takes 1 or 2, any other char outside a pair
        // takes 3, a lone surrogate included, since it becomes the three bytes of U+FFFD, and a pair's two chars take
        // 4. So the byte length is from value.Length to three times that. When both bounds take the same number of
        // length bytes and the larger fits in the buffer, the string is encoded once, straight after the length.
        // Otherwise its exact byte length is counted first.
        var maxByteCount = (long)value.Length * 3;
        var prefixSize = VarUIntSize((ulong)value.Length);
        long byteCapacity;
        if (prefixSize == VarUIntSize((ulong)maxByteCount) && _count + prefixSize + maxByteCount <= _buffer.Length)
        {
            byteCapacity = maxByteCount;
        }
        else
        {
            byteCapacity = Utf8ByteCount(value);
            prefixSize = VarUIntSize((ulong)byteCapacity);
            EnsureCapacity(_count + prefixSize + byteCapacity);
        }

        var destination = _buffer.AsSpan(_count + prefixSize, (int)byteCapacity);
        var status = Utf8.FromUtf16(value, destination, out _, out var bytesWritten, replaceInvalidSequences: true);

        Debug.Assert(status == OperationStatus.Done);
        Debug.Assert(VarUIntSize((ulong)bytesWritten) == prefixSize);
        WriteVarUInt(_buffer.AsSpan(_count, prefixSize), (ulong)bytesWritten);
        _count += prefixSize + bytesWritten;
    }

    // Seven value bits per byte; zero still takes one byte.
    private static int VarUIntSize(ulong value) => (70 - BitOperations.LeadingZeroCount(value | 1)) / 7;

    private static void WriteVarUInt(Span<byte> span, ulong value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            span[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[i] = (byte)value;
    }

    // The number of UTF-8 bytes WriteString writes for value: 1, 2 or 3 for each char outside a pair, a lone surrogate
    // counted as the three bytes of U+FFFD, and 4 for each well-formed pair. Counted as a long because the bytes of a
    // string can exceed int range. A vectorized search skips the leading ASCII chars, then the rest is counted a char
    // at a time. Nothing is allocated. The search uses SearchValues because IndexOfAnyExceptInRange allocates before
    // the JIT optimizes it.
    internal static long Utf8ByteCount(ReadOnlySpan<char> value)
    {
        var firstNonAscii = value.IndexOfAnyExcept(AsciiChars);
        if (firstNonAscii < 0)
            return value.Length;

        long count = firstNonAscii;
        for (var i = firstNonAscii; i < value.Length; i++)
        {
            var c = value[i];
            if (c < 0x80)
            {
                count += 1;
            }
            else if (c < 0x800)
            {
                count += 2;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                count += 4;
                i++;
            }
            else
            {
                count += 3;
            }
        }

        return count;
    }

    private Span<byte> Reserve(int size)
    {
        EnsureCapacity((long)_count + size);
        var span = _buffer.AsSpan(_count, size);
        _count += size;
        return span;
    }

    private void EnsureCapacity(long required)
    {
        if (required > _buffer.Length)
            Grow(required);
    }

    private void Grow(long required)
    {
        if (required > Array.MaxLength)
            throw new InvalidOperationException($"The writer cannot hold more than {Array.MaxLength} bytes.");

        var doubled = Math.Min((long)_buffer.Length * 2, Array.MaxLength);
        Array.Resize(ref _buffer, (int)Math.Max(doubled, required));
    }
}
