using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AdamE.Oahu.Wire;

/// <summary>
/// Reads values in Oahu's wire format from a span of bytes, in order from the start.
/// </summary>
/// <remarks>
/// A reader holds the input, its position in it and the nesting depth of the value being read, and nothing else. A
/// single read call either returns a value and moves past its bytes, or throws <see cref="OahuFormatException"/> and
/// leaves the position where it was. A value made of several parts is read with several calls, so a failure can come
/// after earlier parts have been read. A reader that has thrown is not used again: the message is rejected as a whole.
/// The codec of a record or a union calls <see cref="EnterNested"/> and <see cref="ExitNested"/> around the value's
/// parts. After reading a message's value, call <see cref="EnsureEnd"/> to reject bytes left over.
/// </remarks>
public ref struct OahuReader
{
    private const int MaxVarUIntBytes = 10;

    // DateTimeOffset.MinValue and MaxValue, in whole milliseconds since the Unix epoch.
    private const long MinInstantMilliseconds = -62_135_596_800_000;
    private const long MaxInstantMilliseconds = 253_402_300_799_999;

    // Checks a string's bytes as it decodes them, so they are not scanned once more beforehand, and throws instead of
    // writing U+FFFD. On a throw, ReadString finds the first ill-formed sequence again to name it in its error.
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ReadOnlySpan<byte> _input;
    private int _offset;

    // The number of record and union values entered and not yet exited.
    private int _depth;

    /// <summary>Creates a reader positioned at the start of <paramref name="input"/>.</summary>
    /// <param name="input">The bytes to read.</param>
    public OahuReader(ReadOnlySpan<byte> input)
    {
        _input = input;
        _offset = 0;
        _depth = 0;
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

    /// <summary>
    /// Reads the index of an enum's value or a union's case, written as an unsigned variable-length integer, and
    /// rejects an index the type does not have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The codec of an enum reads its value as this index alone. The codec of a union calls
    /// <see cref="EnterNested"/>, reads the index of its case with this method, reads that case's fields, then calls
    /// <see cref="ExitNested"/>.
    /// </para>
    /// <para>
    /// The index is the position of the value among the enum's declared values, or of the case among the union's
    /// cases, in declaration order, counting from 0. It is not an enum value's numeric value. The codec maps the index
    /// back to the value or case. An index that is not below <paramref name="count"/> is rejected here, so the codec
    /// never sees one. When the index is rejected, the reader stays where it was, at the start of the index. The call
    /// allocates nothing.
    /// </para>
    /// </remarks>
    /// <param name="count">The number of values of the enum or cases of the union. It is at least 1.</param>
    /// <returns>The index read, from 0 to <paramref name="count"/> - 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 1.</exception>
    /// <exception cref="OahuFormatException">
    /// The integer is malformed, as for <see cref="ReadVarUInt"/>, or the index is not below <paramref name="count"/>.
    /// </exception>
    public int ReadIndex(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var start = _offset;
        var value = ReadVarUInt();
        if (value >= (ulong)count)
        {
            _offset = start;
            throw new OahuFormatException(
                $"The index {value} at byte offset {start} is not below {count}, the number of values or cases of " +
                "its type.",
                start);
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
        string value;
        try
        {
            value = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            _offset = start;
            throw IllFormedUtf8(start, bytesStart, bytes);
        }

        _offset = bytesStart + length;
        return value;
    }

    /// <summary>
    /// Reads the presence byte of a nullable value: 0 when the value is absent, 1 when it is present.
    /// </summary>
    /// <remarks>
    /// A nullable value is its presence byte, followed by the value only when it is present. After
    /// <see langword="true"/>, read the value itself; after <see langword="false"/>, the value is absent and has no
    /// more bytes.
    /// </remarks>
    /// <returns><see langword="true"/> when the value is present.</returns>
    /// <exception cref="OahuFormatException">The input has ended, or the byte is neither 0 nor 1.</exception>
    public bool ReadPresence()
    {
        var start = _offset;
        if (start >= _input.Length)
        {
            throw new OahuFormatException($"The input ends before the presence byte at byte offset {start}.", start);
        }

        var b = _input[start];
        if (b > 1)
        {
            throw new OahuFormatException(
                $"The presence byte at byte offset {start} is 0x{b:x2}, which is neither 0x00 nor 0x01.", start);
        }

        _offset = start + 1;
        return b != 0;
    }

    /// <summary>
    /// Reads a list: the number of items, written as a count, followed by each item in order, read by
    /// <paramref name="readItem"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every item takes at least one byte, so a count larger than the bytes that remain after it is rejected before
    /// anything is allocated. This bounds this list's array by the bytes that remain. A list does not add nesting depth
    /// and does not call <see cref="EnterNested"/>: the codec of a record or union item does that itself.
    /// </para>
    /// <para>
    /// After each item, the method throws <see cref="InvalidOperationException"/> if the item left the depth different
    /// from what it was before that item, because it called <see cref="EnterNested"/> and <see cref="ExitNested"/> a
    /// different number of times. That is a fault in the item's codec, not in the input. The check is made after each
    /// item, so an item codec that enters without exiting is reported as such and not as a value nested too deep. An item
    /// that is nested too deep throws <see cref="OahuFormatException"/> from inside <paramref name="readItem"/>,
    /// before the check.
    /// </para>
    /// <para>
    /// The items are returned as an <see cref="OahuList{T}"/> over an array of exactly the count's length, which the
    /// list owns: the array is not copied, and nothing else can reach it. Apart from what <paramref name="readItem"/>
    /// allocates for the items, that array is the only allocation. An empty list allocates nothing: it is the empty
    /// <see cref="OahuList{T}"/>, <see cref="OahuList{T}.Empty"/>. Pass a method group of a static method or a
    /// <see langword="static"/> lambda for <paramref name="readItem"/>, so that the delegate is created once and
    /// cached.
    /// </para>
    /// <para>
    /// When the count is rejected, the reader stays where it was, at the start of the list. If
    /// <paramref name="readItem"/> throws, the exception propagates, the items before it have been read, and the reader
    /// is not used again.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="readItem">Reads one item from the reader it is given.</param>
    /// <returns>The items read, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="readItem"/> is <see langword="null"/>.</exception>
    /// <exception cref="OahuFormatException">
    /// The count is malformed, as for <see cref="ReadCount"/>, or it is larger than the bytes that remain after it.
    /// <paramref name="readItem"/> may throw it too, for a malformed item.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An item left the nesting depth different from what it was before it.
    /// </exception>
    public OahuList<T> ReadList<T>(OahuReadFunc<T> readItem)
    {
        ArgumentNullException.ThrowIfNull(readItem);

        var start = _offset;
        var count = ReadCount();
        if (count == 0)
            return OahuList<T>.Empty;

        var available = _input.Length - _offset;
        if (count > available)
        {
            _offset = start;
            throw new OahuFormatException(
                $"The list at byte offset {start} has {count} item(s), but only {available} byte(s) remain after its " +
                "count, and every item takes at least one byte.",
                start);
        }

        // Checked after each item, not once after all of them: an item that enters without exiting would otherwise
        // reach the depth limit within the list and be reported as too-deep input. A too-deep item throws
        // OahuFormatException from inside readItem, so it never reaches this check.
        var depth = _depth;
        var items = new T[count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = readItem(ref this);
            if (_depth != depth)
            {
                throw new InvalidOperationException(
                    $"An item of the list at byte offset {start} left the nesting depth at {_depth}, not {depth} as " +
                    "before it. Every item must call ExitNested once for each EnterNested.");
            }
        }

        return OahuList<T>.Own(items);
    }

    /// <summary>
    /// Reads a keyed list's batch: the number of operations, written as a count, followed by each operation in order,
    /// its tag and then its payload. A row is read by <paramref name="readRow"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tag is read as a variable-length integer, as an enum's index is, so a longer form than the shortest is
    /// accepted: 0 is an insert, 1 a remove, 2 a move, 3 a replace and 4 a clear. An insert or a replace is its index,
    /// then its row; a remove, its index; a move, its from index, then its to index; a clear, nothing. Each index is read
    /// as a count.
    /// </para>
    /// <para>
    /// A batch has at least one operation, so a count of 0 is rejected. Every operation takes at least one byte, its
    /// tag, so a count larger than the bytes that remain after it is rejected before anything is allocated.
    /// </para>
    /// <para>
    /// The method does not check whether an index is in range for the list, or whether keys stay unique: that depends
    /// on the list the batch applies to, and whoever applies the batch checks it there.
    /// </para>
    /// <para>
    /// A batch and its operations do not add nesting depth and do not call <see cref="EnterNested"/>: a row is a record
    /// or a union, and its codec does that itself. After each row, the method throws
    /// <see cref="InvalidOperationException"/> if the row left the depth different from what it was before it. That is a
    /// fault in the row's codec, not in the input.
    /// </para>
    /// <para>
    /// The operations are returned in an array of exactly the count's length. Apart from what
    /// <paramref name="readRow"/> allocates for the rows, that array and one <see cref="ListOp{T}"/> for each operation
    /// are the only allocations. Pass a method group of a static method or a <see langword="static"/> lambda for
    /// <paramref name="readRow"/>, so that the delegate is created once and cached.
    /// </para>
    /// <para>
    /// When the count is rejected, the reader stays where it was, at the start of the batch. When an operation is
    /// malformed, the operations before it have been read and the reader is not used again; when its tag is rejected,
    /// the reader is at the start of that tag. If <paramref name="readRow"/> throws, the exception propagates.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type of a row.</typeparam>
    /// <param name="readRow">Reads one row from the reader it is given.</param>
    /// <returns>The operations read, in order, at least one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="readRow"/> is <see langword="null"/>.</exception>
    /// <exception cref="OahuFormatException">
    /// The count is malformed, as for <see cref="ReadCount"/>, it is 0, or it is larger than the bytes that remain after
    /// it; a tag is malformed, as for <see cref="ReadVarUInt"/>, or larger than 4; or an index is malformed, as for
    /// <see cref="ReadCount"/>. <paramref name="readRow"/> may throw it too, for a malformed row.
    /// </exception>
    /// <exception cref="InvalidOperationException">A row left the nesting depth different from what it was before it.</exception>
    public IReadOnlyList<ListOp<T>> ReadBatch<T>(OahuReadFunc<T> readRow)
        where T : IOahuKeyed
    {
        ArgumentNullException.ThrowIfNull(readRow);

        var start = _offset;
        var count = ReadCount();
        if (count == 0)
        {
            _offset = start;
            throw new OahuFormatException(
                $"The batch at byte offset {start} has no operations, and every batch has at least one.", start);
        }

        var available = _input.Length - _offset;
        if (count > available)
        {
            _offset = start;
            throw new OahuFormatException(
                $"The batch at byte offset {start} has {count} operation(s), but only {available} byte(s) remain " +
                "after its count, and every operation takes at least one byte.",
                start);
        }

        var depth = _depth;
        var operations = new ListOp<T>[count];
        for (var i = 0; i < operations.Length; i++)
        {
            var tagStart = _offset;
            var tag = ReadVarUInt();

            // C# evaluates arguments from left to right, so an index is read before the row that follows it, and a
            // move's From before its To.
            operations[i] = tag switch
            {
                OahuFormat.InsertTag => new ListOp<T>.Insert(ReadCount(), ReadRow(readRow, start, depth)),
                OahuFormat.RemoveTag => new ListOp<T>.Remove(ReadCount()),
                OahuFormat.MoveTag => new ListOp<T>.Move(ReadCount(), ReadCount()),
                OahuFormat.ReplaceTag => new ListOp<T>.Replace(ReadCount(), ReadRow(readRow, start, depth)),
                OahuFormat.ClearTag => new ListOp<T>.Clear(),
                _ => throw UnknownTag(tag, tagStart),
            };
        }

        return operations;
    }

    // Puts the reader back at the start of a tag it rejects, and returns the error to throw.
    private OahuFormatException UnknownTag(ulong tag, int tagStart)
    {
        _offset = tagStart;
        return new OahuFormatException(
            $"The operation tag {tag} at byte offset {tagStart} is not one of the tags of the five operations, 0 to " +
            $"{OahuFormat.ClearTag}.",
            tagStart);
    }

    // Reads the row of an insert or a replace, and checks that it left the depth as it was, as ReadList checks an item.
    private T ReadRow<T>(OahuReadFunc<T> readRow, int batchStart, int depth)
    {
        var row = readRow(ref this);
        if (_depth != depth)
        {
            throw new InvalidOperationException(
                $"A row of the batch at byte offset {batchStart} left the nesting depth at {_depth}, not {depth} as " +
                "before it. Every row must call ExitNested once for each EnterNested.");
        }

        return row;
    }

    /// <summary>
    /// Enters a record or union value: raises the nesting depth by one, and rejects the value if that takes the depth
    /// past <see cref="OahuFormat.MaxNestingDepth"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The codec of a record or a union, generated or written by hand, calls it once for each value it reads: before
    /// reading anything of the value, so before a record's first field or a union's case index. It calls
    /// <see cref="ExitNested"/> after the value's last field. A record with no fields calls both too. No other codec
    /// calls them: lists, nullable values, enums and other values do not add depth.
    /// </para>
    /// <para>
    /// Every record value counts, including a value of a record type that never contains itself. A union value and the
    /// case it holds are one level: a union's codec calls this method once, reads the case index and that case's
    /// fields, then calls <see cref="ExitNested"/>, and does not call them again for the case. A record type that is
    /// also a union case calls them as a record when it is a field's value, and not when it is the case of a union
    /// value.
    /// </para>
    /// <para>
    /// When the value is rejected, the reader stays where it was, at the start of the value, with its depth unchanged.
    /// If a part of the value is malformed, the codec does not call <see cref="ExitNested"/>: the reader is not used
    /// again. The call allocates nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="OahuFormatException">
    /// The value would be nested deeper than <see cref="OahuFormat.MaxNestingDepth"/>.
    /// </exception>
    public void EnterNested()
    {
        if (_depth >= OahuFormat.MaxNestingDepth)
        {
            throw new OahuFormatException(
                $"The value at byte offset {_offset} is at nesting depth {_depth + 1}, deeper than the largest " +
                $"nesting depth, {OahuFormat.MaxNestingDepth}.",
                _offset);
        }

        _depth++;
    }

    /// <summary>
    /// Exits the record or union value entered last with <see cref="EnterNested"/>: lowers the nesting depth by one.
    /// </summary>
    /// <remarks>Call it after the value's last field. The call allocates nothing.</remarks>
    /// <exception cref="InvalidOperationException">No value is entered: the depth is already zero.</exception>
    public void ExitNested()
    {
        if (_depth == 0)
            throw new InvalidOperationException("ExitNested was called with no nested value entered.");

        _depth--;
    }

    /// <summary>Checks that the whole input has been read and that every value entered has been exited.</summary>
    /// <exception cref="InvalidOperationException">
    /// A value entered with <see cref="EnterNested"/> has not been exited with <see cref="ExitNested"/>. That is a
    /// fault in a codec, not in the input.
    /// </exception>
    /// <exception cref="OahuFormatException">Bytes remain after the last value read.</exception>
    public readonly void EnsureEnd()
    {
        if (_depth != 0)
        {
            throw new InvalidOperationException(
                $"{_depth} nested value(s) entered with EnterNested have not been exited with ExitNested.");
        }

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
