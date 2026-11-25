using System.Buffers.Binary;
using System.Numerics;

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
        var span = Reserve(size);
        var i = 0;
        while (value >= 0x80)
        {
            span[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[i] = (byte)value;
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

    // Seven value bits per byte; zero still takes one byte.
    private static int VarUIntSize(ulong value) => (70 - BitOperations.LeadingZeroCount(value | 1)) / 7;

    private Span<byte> Reserve(int size)
    {
        var required = (long)_count + size;
        if (required > _buffer.Length)
            Grow(required);

        var span = _buffer.AsSpan(_count, size);
        _count += size;
        return span;
    }

    private void Grow(long required)
    {
        if (required > Array.MaxLength)
            throw new InvalidOperationException($"The writer cannot hold more than {Array.MaxLength} bytes.");

        var doubled = Math.Min((long)_buffer.Length * 2, Array.MaxLength);
        Array.Resize(ref _buffer, (int)Math.Max(doubled, required));
    }
}
