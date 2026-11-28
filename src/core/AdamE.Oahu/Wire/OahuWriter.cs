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
/// A writer holds bytes and the nesting depth of the value being written, and nothing else. It is not thread-safe. The
/// codec of a record or a union calls <see cref="EnterNested"/> and <see cref="ExitNested"/> around the value's parts.
/// Call <see cref="Reset"/> to reuse the writer for another message.
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

    // Counts calls to Reset, so that WriteList and WriteBatch can tell that an item or row writer reset the writer.
    private int _resets;

    // The number of record and union values entered and not yet exited.
    private int _depth;

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
    /// <remarks>
    /// The span is valid until the next write or <see cref="Reset"/>. Use <see cref="GetCompletedSpan"/> to get the
    /// bytes for sending a message.
    /// </remarks>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _count);

    /// <summary>The number of bytes written since the writer was created or last reset.</summary>
    public int WrittenCount => _count;

    /// <summary>Returns the written bytes after checking that every value entered has been exited.</summary>
    /// <returns>The span of written bytes, valid until the next write or <see cref="Reset"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// A value entered with <see cref="EnterNested"/> has not been exited with <see cref="ExitNested"/>.
    /// </exception>
    public ReadOnlySpan<byte> GetCompletedSpan()
    {
        if (_depth != 0)
        {
            throw new InvalidOperationException(
                $"{_depth} nested value(s) entered with EnterNested have not been exited with ExitNested.");
        }

        return WrittenSpan;
    }

    /// <summary>Empties the writer so that it can write another message.</summary>
    /// <remarks>
    /// The nesting depth returns to zero. The buffer is kept and never shrinks, so it keeps the size that the largest
    /// message written so far needed. Do not call it from an item writer of <see cref="WriteList{T}"/> or a row writer
    /// of <see cref="WriteBatch{T}"/>: the list or batch then throws.
    /// </remarks>
    public void Reset()
    {
        _count = 0;
        _depth = 0;
        _resets++;
    }

    /// <summary>
    /// Enters a record or union value: raises the nesting depth by one, and refuses the value if that takes the depth
    /// past <see cref="OahuFormat.MaxNestingDepth"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The codec of a record or a union, generated or written by hand, calls it once for each value it writes: before
    /// writing anything of the value, so before a record's first field or a union's case index. It calls
    /// <see cref="ExitNested"/> after the value's last field. A record with no fields calls both too. No other codec
    /// calls them: lists, nullable values, enums and other values do not add depth.
    /// </para>
    /// <para>
    /// Every record value counts, including a value of a record type that never contains itself. A union value and the
    /// case it holds are one level: a union's codec calls this method once, writes the case index and that case's
    /// fields, then calls <see cref="ExitNested"/>, and does not call them again for the case. A record type that is
    /// also a union case calls them as a record when it is a field's value, and not when it is the case of a union
    /// value.
    /// </para>
    /// <para>
    /// When the value is refused, nothing of it has been written and the depth is unchanged. The exception propagates
    /// through the codecs of the values that enclose it, which do not call <see cref="ExitNested"/>, so the writer's
    /// contents and depth are then unspecified: call <see cref="Reset"/> before writing another message. The call
    /// allocates nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The value would be nested deeper than <see cref="OahuFormat.MaxNestingDepth"/>.
    /// </exception>
    public void EnterNested()
    {
        if (_depth >= OahuFormat.MaxNestingDepth)
        {
            throw new InvalidOperationException(
                $"The value at byte offset {_count} is at nesting depth {_depth + 1}, deeper than the largest " +
                $"nesting depth, {OahuFormat.MaxNestingDepth}.");
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

    /// <summary>
    /// Writes the index of an enum's value or a union's case, as an unsigned variable-length integer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The codec of an enum writes its value as this index alone. The codec of a union calls
    /// <see cref="EnterNested"/>, writes the index of its case with this method, writes that case's fields, then calls
    /// <see cref="ExitNested"/>.
    /// </para>
    /// <para>
    /// The index is the position of the value among the enum's declared values, or of the case among the union's
    /// cases, in declaration order, counting from 0. It is not an enum value's numeric value, and an enum's numeric
    /// values and names are never written. <paramref name="count"/> is the number of values or cases the type has, so
    /// that the writer never writes an index that a reader of the type rejects. The call allocates nothing.
    /// </para>
    /// </remarks>
    /// <param name="index">The index to write, from 0 to <paramref name="count"/> - 1.</param>
    /// <param name="count">The number of values of the enum or cases of the union. It is at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is less than 1, or <paramref name="index"/> is negative or not less than
    /// <paramref name="count"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteIndex(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        WriteVarUInt((ulong)index);
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
    /// Writes <paramref name="value"/> as the number of milliseconds since the Unix epoch, 1970-01-01T00:00:00Z,
    /// written as for <see cref="WriteLong"/>.
    /// </summary>
    /// <remarks>
    /// A fraction of a millisecond is rounded down, towards the earlier time, also before 1970: 0.9999 ms after the epoch
    /// is written as 0, and 0.5 ms before it as -1. The offset is not written, so two values for the same instant with
    /// different offsets are written the same, and the reader gets the instant in UTC.
    /// </remarks>
    /// <param name="value">The instant to write.</param>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteInstant(DateTimeOffset value) => WriteLong(value.ToUnixTimeMilliseconds());

    /// <summary>
    /// Writes <paramref name="value"/> as its count of 100-nanosecond ticks, written as for <see cref="WriteLong"/>.
    /// </summary>
    /// <remarks>Every <see cref="TimeSpan"/> is written exactly.</remarks>
    /// <param name="value">The duration to write.</param>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WriteDuration(TimeSpan value) => WriteLong(value.Ticks);

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

    /// <summary>
    /// Writes the presence byte of a nullable value: 0 when the value is absent, 1 when it is present.
    /// </summary>
    /// <remarks>
    /// A nullable value is its presence byte, followed by the value only when it is present. After writing
    /// <see langword="true"/>, write the value itself; after <see langword="false"/>, write nothing more for it.
    /// </remarks>
    /// <param name="present"><see langword="true"/> when the value is present.</param>
    /// <exception cref="InvalidOperationException">The message would be longer than <see cref="Array.MaxLength"/> bytes.</exception>
    public void WritePresence(bool present) => Reserve(1)[0] = present ? (byte)1 : (byte)0;

    /// <summary>
    /// Writes a list: the number of items, written as a count, followed by each item in order, written by
    /// <paramref name="writeItem"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is an <see cref="OahuList{T}"/>, the list of a contract value, and it is passed without boxing. It is
    /// immutable, so its count and items cannot change while it is written. Every item must take at least one byte,
    /// because a reader rejects a count larger than the bytes that remain after it. Items can be of any kind, including
    /// lists and nullable values.
    /// </para>
    /// <para>
    /// Pass a method group of a static method or a <see langword="static"/> lambda for <paramref name="writeItem"/>, so
    /// that the delegate is created once and cached. The method itself allocates nothing.
    /// </para>
    /// <para>
    /// After the items, the method checks that they wrote at least as many bytes as there are items, so that a reader
    /// accepts the count. It does not check each item on its own.
    /// </para>
    /// <para>
    /// <paramref name="writeItem"/> must not call <see cref="Reset"/>, because that would remove the list's count and
    /// everything written before it. After each item, the method throws if that item reset the writer.
    /// </para>
    /// <para>
    /// A list does not add nesting depth and does not call <see cref="EnterNested"/>: the codec of a record or union
    /// item does that itself. After each item, the method throws if that item left the depth different from what it
    /// was before it, because the item called <see cref="EnterNested"/> and <see cref="ExitNested"/> a different
    /// number of times. So an item that enters without exiting is reported as that fault, not as a value nested too
    /// deep.
    /// </para>
    /// <para>
    /// A <see langword="null"/> <paramref name="writeItem"/> throws before anything is written. If
    /// <paramref name="writeItem"/> throws, the writer was reset, an item changed the depth, or the items wrote fewer
    /// bytes than there are items, the exception propagates and the writer's contents are unspecified: call
    /// <see cref="Reset"/> before writing another message.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="items">The items to write.</param>
    /// <param name="writeItem">Writes one item to the writer it is given.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writeItem"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The message would be longer than <see cref="Array.MaxLength"/> bytes, <paramref name="writeItem"/> reset the
    /// writer, an item left the nesting depth different from what it was before it, or the items wrote fewer bytes than
    /// there are items.
    /// </exception>
    public void WriteList<T>(OahuList<T> items, Action<OahuWriter, T> writeItem)
    {
        ArgumentNullException.ThrowIfNull(writeItem);

        var span = items.AsSpan();
        WriteCount(span.Length);
        var itemsStart = _count;
        var resets = _resets;
        var depth = _depth;
        for (var i = 0; i < span.Length; i++)
        {
            writeItem(this, span[i]);

            // Both checks run after each item, so that an item that enters without exiting is reported here rather
            // than reaching the depth limit within the list and being reported as a too-deep value. A reset followed
            // by enough writes can leave _count above itemsStart, so the byte check alone misses it. A reset also
            // zeroes the depth, so it is checked first.
            if (_resets != resets)
                throw new InvalidOperationException("An item writer reset the writer while its list was being written.");

            if (_depth != depth)
            {
                throw new InvalidOperationException(
                    $"An item of the list left the nesting depth at {_depth}, not {depth} as before it. Every item " +
                    "must call ExitNested once for each EnterNested.");
            }
        }

        // One check per list. It guarantees that a reader's count check passes, but it does not prove that each item
        // wrote a byte: an item that wrote nothing goes undetected when other items wrote enough bytes to make up for it.
        var itemBytes = _count - itemsStart;
        if (itemBytes < span.Length)
        {
            throw new InvalidOperationException(
                $"The list's {span.Length} item(s) wrote only {itemBytes} byte(s), fewer than there are items. " +
                "Every item must write at least one byte.");
        }
    }

    /// <summary>
    /// Writes a keyed list's batch: the number of operations, written as a count, followed by each operation in order,
    /// its tag and then its payload. A row is written by <paramref name="writeRow"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tag is written as a variable-length integer, as an enum's index is: 0 for
    /// <see cref="ListOp{T}.Insert"/>, 1 for <see cref="ListOp{T}.Remove"/>, 2 for <see cref="ListOp{T}.Move"/>, 3 for
    /// <see cref="ListOp{T}.Replace"/> and 4 for <see cref="ListOp{T}.Clear"/>, so it is always one byte. The payload of
    /// an insert or a replace is its index, then its row; of a remove, its index; of a move, its from index, then its to
    /// index; of a clear, nothing. Each index is written as a count.
    /// </para>
    /// <para>
    /// A batch has at least one operation. An empty list is replayed as one <see cref="ListOp{T}.Clear"/>, and a
    /// delivered batch is never empty, so the format gives an empty batch no bytes and a reader rejects one.
    /// </para>
    /// <para>
    /// <see cref="IReadOnlyCollection{T}.Count"/> is read once, before the count is written, and the batch must not
    /// change while it is written. The method reads each operation once, just before it writes it, and checks it then:
    /// that it is not <see langword="null"/>, that its indexes are not negative, and that the row of an insert or a
    /// replace is not <see langword="null"/>. So a batch that fails this check at an operation after the first has
    /// already written the operations before it. The method does not check whether an index is in range for the list
    /// or whether keys stay unique; whoever applies the batch does.
    /// </para>
    /// <para>
    /// Pass a method group of a static method or a <see langword="static"/> lambda for <paramref name="writeRow"/>, so
    /// that the delegate is created once and cached. The method itself allocates nothing.
    /// </para>
    /// <para>
    /// A batch and its operations do not add nesting depth and do not call <see cref="EnterNested"/>: a row is a record
    /// or a union, and its codec does that itself, so a row of a batch is at depth 1. After each row, the method throws
    /// if <paramref name="writeRow"/> reset the writer, or left the depth different from what it was before that row.
    /// </para>
    /// <para>
    /// A <see langword="null"/> <paramref name="batch"/> or <paramref name="writeRow"/>, or an empty batch, throws before
    /// anything is written. If an operation fails its check, <paramref name="writeRow"/> throws, the writer was reset,
    /// or a row changed the depth, the exception propagates and the writer's contents are unspecified: call
    /// <see cref="Reset"/> before writing another message.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type of a row.</typeparam>
    /// <param name="batch">The operations to write, at least one.</param>
    /// <param name="writeRow">Writes one row to the writer it is given.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="batch"/> or <paramref name="writeRow"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An operation has a negative index.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="batch"/> is empty, an operation is <see langword="null"/>, or the row of an insert or a replace is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The message would be longer than <see cref="Array.MaxLength"/> bytes, <paramref name="writeRow"/> reset the
    /// writer, or a row left the nesting depth different from what it was before it.
    /// </exception>
    public void WriteBatch<T>(IReadOnlyList<ListOp<T>> batch, Action<OahuWriter, T> writeRow)
        where T : IOahuKeyed
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(writeRow);

        var count = batch.Count;
        if (count == 0)
        {
            throw new ArgumentException(
                "A batch has at least one operation. An empty list is replayed as one Clear.", nameof(batch));
        }

        WriteCount(count);
        var resets = _resets;
        var depth = _depth;
        for (var i = 0; i < count; i++)
        {
            // Each operation is read once and checked just before it is written, as WriteList writes each item: a
            // failure leaves the operations before it written, and the writer must then be reset.
            var operation = BatchOperation<T>.Of(batch[i], i);
            CheckOperation(operation, i);
            WriteVarUInt(operation.Tag);
            if (operation.IndexCount > 0)
                WriteCount(operation.First);
            if (operation.IndexCount > 1)
                WriteCount(operation.Second);
            if (!operation.HasRow)
                continue;

            writeRow(this, operation.Row);

            // As in WriteList: a reset also zeroes the depth, so it is checked first.
            if (_resets != resets)
                throw new InvalidOperationException("A row writer reset the writer while its batch was being written.");

            if (_depth != depth)
            {
                throw new InvalidOperationException(
                    $"A row of the batch left the nesting depth at {_depth}, not {depth} as before it. Every row " +
                    "must call ExitNested once for each EnterNested.");
            }
        }
    }

    private static void CheckOperation<T>(BatchOperation<T> operation, int position)
        where T : IOahuKeyed
    {
        if ((operation.IndexCount > 0 && operation.First < 0) || (operation.IndexCount > 1 && operation.Second < 0))
        {
            throw new ArgumentOutOfRangeException(
                "batch", $"The operation at position {position} of the batch has a negative index.");
        }

        if (operation.HasRow && operation.Row is null)
            throw new ArgumentException($"The operation at position {position} of the batch has a null row.", "batch");
    }

    // The parts of one operation that the format writes. Of names each of the five cases and has no discard arm, so
    // the compiler checks that it handles every case of the closed ListOp<T>.
    private readonly record struct BatchOperation<T>(byte Tag, int IndexCount, int First, int Second, bool HasRow, T Row)
        where T : IOahuKeyed
    {
        public static BatchOperation<T> Of(ListOp<T>? operation, int position) => operation switch
        {
            null => throw new ArgumentException($"The operation at position {position} of the batch is null.", "batch"),
            ListOp<T>.Insert insert => new(OahuFormat.InsertTag, 1, insert.Index, 0, true, insert.Item),
            ListOp<T>.Remove remove => new(OahuFormat.RemoveTag, 1, remove.Index, 0, false, default!),
            ListOp<T>.Move move => new(OahuFormat.MoveTag, 2, move.From, move.To, false, default!),
            ListOp<T>.Replace replace => new(OahuFormat.ReplaceTag, 1, replace.Index, 0, true, replace.Item),
            ListOp<T>.Clear => new(OahuFormat.ClearTag, 0, 0, 0, false, default!),
        };
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
