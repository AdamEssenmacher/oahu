using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class BatchTests
{
    // Keeps each batch read in the allocation tests reachable, so that the runtime cannot drop or stack-allocate it.
    private static object? _sink;

    private static readonly TaskRow Milk = new("a", "Milk", false);
    private static readonly TaskRow Eggs = new("b", "Eggs", true);

    // TaskRow("a", "Milk", false) and TaskRow("b", "Eggs", true).
    private const string MilkHex = "0161" + "044d696c6b" + "00";
    private const string EggsHex = "0162" + "0445676773" + "01";

    public static TheoryData<ListOp<TaskRow>, string> OperationCases => new()
    {
        { new ListOp<TaskRow>.Insert(0, Milk), "01" + "00" + "00" + MilkHex },
        { new ListOp<TaskRow>.Remove(2), "01" + "01" + "02" },
        { new ListOp<TaskRow>.Move(0, 2), "01" + "02" + "00" + "02" },
        { new ListOp<TaskRow>.Replace(1, Eggs), "01" + "03" + "01" + EggsHex },
        { new ListOp<TaskRow>.Clear(), "01" + "04" },
    };

    [Theory]
    [MemberData(nameof(OperationCases))]
    public void A_batch_of_one_operation_is_a_count_of_one_then_the_tag_and_the_payload(
        ListOp<TaskRow> operation, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteBatch([operation], TaskRow.Write);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal<ListOp<TaskRow>>([operation], reader.ReadBatch(TaskRow.Read));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_batch_of_every_operation_writes_them_in_order_with_nothing_between()
    {
        ListOp<TaskRow>[] batch =
        [
            new ListOp<TaskRow>.Clear(),
            new ListOp<TaskRow>.Insert(0, Milk),
            new ListOp<TaskRow>.Insert(1, Eggs),
            new ListOp<TaskRow>.Move(1, 0),
            new ListOp<TaskRow>.Replace(1, Milk with { Done = true }),
            new ListOp<TaskRow>.Remove(0),
        ];

        var writer = new OahuWriter();
        writer.WriteBatch(batch, TaskRow.Write);

        Assert.Equal(
            "06" + "04" + "0000" + MilkHex + "0001" + EggsHex + "020100" + "0301" + "0161044d696c6b01" + "0100",
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(batch, reader.ReadBatch(TaskRow.Read));
        reader.EnsureEnd();
    }

    [Fact]
    public void The_replay_of_two_rows_is_a_clear_then_an_insert_of_each_row()
    {
        ListOp<TaskRow>[] replay = [new ListOp<TaskRow>.Clear(), new ListOp<TaskRow>.Insert(0, Milk), new ListOp<TaskRow>.Insert(1, Eggs)];

        var writer = new OahuWriter();
        writer.WriteBatch(replay, TaskRow.Write);

        Assert.Equal("03" + "04" + "0000" + MilkHex + "0001" + EggsHex, Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void The_replay_of_an_empty_list_is_one_clear_and_two_bytes()
    {
        var writer = new OahuWriter();
        writer.WriteBatch([new ListOp<TaskRow>.Clear()], TaskRow.Write);

        Assert.Equal("0104", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void Rows_that_are_a_union_are_written_by_their_codec_with_the_case_index()
    {
        ListOp<BoardRow>[] batch =
        [
            new ListOp<BoardRow>.Insert(0, new BoardRow.Header("h", "Today")),
            new ListOp<BoardRow>.Replace(0, new BoardRow.Card("l", "")),
        ];

        var writer = new OahuWriter();
        writer.WriteBatch(batch, BoardRow.Write);

        Assert.Equal("02" + "00" + "00" + "00016805546f646179" + "03" + "00" + "01016c00", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(batch, reader.ReadBatch(BoardRow.Read));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(0, "00")]
    [InlineData(127, "7f")]
    [InlineData(128, "8001")]
    [InlineData(int.MaxValue, "ffffffff07")]
    public void An_index_is_written_as_a_count(int index, string indexHex)
    {
        ListOp<TaskRow>[] batch =
        [
            new ListOp<TaskRow>.Insert(index, Milk),
            new ListOp<TaskRow>.Remove(index),
            new ListOp<TaskRow>.Move(index, index),
            new ListOp<TaskRow>.Replace(index, Eggs),
        ];

        var writer = new OahuWriter();
        writer.WriteBatch(batch, TaskRow.Write);

        Assert.Equal(
            "04" + "00" + indexHex + MilkHex + "01" + indexHex + "02" + indexHex + indexHex + "03" + indexHex + EggsHex,
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(batch, reader.ReadBatch(TaskRow.Read));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(127, "7f")]
    [InlineData(128, "8001")]
    public void The_count_crosses_the_one_byte_prefix_boundary(int count, string prefix)
    {
        var batch = new ListOp<TaskRow>[count];
        for (var i = 0; i < count; i++)
            batch[i] = i % 2 == 0 ? new ListOp<TaskRow>.Clear() : new ListOp<TaskRow>.Remove(i);

        var writer = new OahuWriter();
        writer.WriteBatch(batch, TaskRow.Write);

        var expected = prefix + string.Concat(batch.Select((op, i) => op is ListOp<TaskRow>.Clear ? "04" : "01" + Hex.Of([(byte)i])));
        Assert.Equal(expected, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(batch, reader.ReadBatch(TaskRow.Read));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("01" + "8000" + "00" + MilkHex, "Insert")]
    [InlineData("01" + "8400", "Clear")]
    [InlineData("01" + "82808080808080808000" + "0001", "Move")]
    [InlineData("8100" + "04", "Clear")]
    public void A_longer_form_of_a_tag_or_a_count_is_accepted(string hex, string operation)
    {
        var reader = new OahuReader(Hex.Bytes(hex));
        var batch = reader.ReadBatch(TaskRow.Read);
        reader.EnsureEnd();

        Assert.Equal(operation, Assert.Single(batch).GetType().Name);
    }

    [Fact]
    public void A_reader_does_not_check_indexes_or_keys_against_any_list()
    {
        // Out of range for any list it could apply to, and the same key inserted twice.
        var reader = new OahuReader(Hex.Bytes("04" + "0105" + "0009" + MilkHex + "0009" + MilkHex + "020303"));

        Assert.Equal<ListOp<TaskRow>>(
            [
                new ListOp<TaskRow>.Remove(5),
                new ListOp<TaskRow>.Insert(9, Milk),
                new ListOp<TaskRow>.Insert(9, Milk),
                new ListOp<TaskRow>.Move(3, 3),
            ],
            reader.ReadBatch(TaskRow.Read));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("80", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("8080808008", "The count 2147483648 at byte offset 0 is larger than the largest count, 2147483647.")]
    [InlineData("00", "The batch at byte offset 0 has no operations, and every batch has at least one.")]
    [InlineData("8000" + "04", "The batch at byte offset 0 has no operations, and every batch has at least one.")]
    [InlineData("01", "The batch at byte offset 0 has 1 operation(s), but only 0 byte(s) remain after its count, and every operation takes at least one byte.")]
    [InlineData("03" + "0404", "The batch at byte offset 0 has 3 operation(s), but only 2 byte(s) remain after its count, and every operation takes at least one byte.")]
    public void A_malformed_count_is_rejected_at_the_start_of_the_batch_and_does_not_move_the_reader(string hex, string message)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);

        var exception = ReadBatchFailure(ref reader);

        Assert.Equal(0, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(input.Length, reader.Remaining);
    }

    [Theory]
    [InlineData("01" + "00", "The batch at byte offset 1 has no operations, and every batch has at least one.", 1)]
    [InlineData("01" + "0204", "The batch at byte offset 1 has 2 operation(s), but only 1 byte(s) remain after its count, and every operation takes at least one byte.", 2)]
    [InlineData("01" + "8080808008" + "00", "The count 2147483648 at byte offset 1 is larger than the largest count, 2147483647.", 6)]
    public void A_malformed_count_after_the_start_names_its_offset_and_does_not_move_the_reader(
        string hex, string message, int remaining)
    {
        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.True(reader.ReadBool());

        var exception = ReadBatchFailure(ref reader);

        Assert.Equal(1, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(remaining, reader.Remaining);
    }

    [Fact]
    public void A_huge_count_is_rejected_without_allocating_the_batch()
    {
        var input = Hex.Bytes("01" + "ffffffff07" + "0404");
        var reader = new OahuReader(input);
        Assert.True(reader.ReadBool());

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var exception = ReadBatchFailure(ref reader);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(1, exception.Offset);
        Assert.Equal(
            "The batch at byte offset 1 has 2147483647 operation(s), but only 2 byte(s) remain after its count, and " +
            "every operation takes at least one byte.",
            exception.Message);
        Assert.Equal(7, reader.Remaining);
        Assert.True(allocated < 1_000_000, $"The failed read allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData("01" + "05", 1, "5")]
    [InlineData("03" + "04" + "05" + "04", 2, "5")]
    [InlineData("03" + "04" + "04" + "05", 3, "5")]
    [InlineData("02" + "00" + "00" + MilkHex + "8001", 11, "128")]
    [InlineData("01" + "8500", 1, "5")]
    [InlineData("01" + "ffffffffffffffffff01", 1, "18446744073709551615")]
    public void An_unknown_tag_is_rejected_at_the_tag_and_leaves_the_reader_there(string hex, int offset, string tag)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);

        var exception = ReadBatchFailure(ref reader);

        Assert.Equal(offset, exception.Offset);
        Assert.Equal(
            $"The operation tag {tag} at byte offset {offset} is not one of the tags of the five operations, 0 to 4.",
            exception.Message);
        Assert.Equal(input.Length - offset, reader.Remaining);
    }

    [Theory]
    [InlineData("02" + "04" + "80", "The input ends inside a variable-length integer that starts at byte offset 2.", 2)]
    [InlineData("01" + "8080808080808080808000", "The variable-length integer at byte offset 1 is longer than 10 bytes.", 1)]
    [InlineData("01" + "ffffffffffffffffff02", "The variable-length integer at byte offset 1 does not fit in 64 bits.", 1)]
    public void A_malformed_tag_is_rejected_as_a_variable_length_integer(string hex, string message, int offset)
    {
        var reader = new OahuReader(Hex.Bytes(hex));

        var exception = ReadBatchFailure(ref reader);

        Assert.Equal(offset, exception.Offset);
        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [InlineData("01" + "00", "The input ends inside a variable-length integer that starts at byte offset 2.", 2)]
    [InlineData("02" + "0400", "The input ends inside a variable-length integer that starts at byte offset 3.", 3)]
    [InlineData("01" + "00" + "8080808008" + MilkHex, "The count 2147483648 at byte offset 2 is larger than the largest count, 2147483647.", 2)]
    [InlineData("01" + "01", "The input ends inside a variable-length integer that starts at byte offset 2.", 2)]
    [InlineData("02" + "01" + "80", "The input ends inside a variable-length integer that starts at byte offset 2.", 2)]
    [InlineData("01" + "02", "The input ends inside a variable-length integer that starts at byte offset 2.", 2)]
    [InlineData("01" + "02" + "00", "The input ends inside a variable-length integer that starts at byte offset 3.", 3)]
    [InlineData("01" + "02" + "00" + "8080808008", "The count 2147483648 at byte offset 3 is larger than the largest count, 2147483647.", 3)]
    [InlineData("01" + "03", "The input ends inside a variable-length integer that starts at byte offset 2.", 2)]
    [InlineData("02" + "0403", "The input ends inside a variable-length integer that starts at byte offset 3.", 3)]
    public void A_truncated_or_malformed_payload_is_rejected_by_the_read_of_its_part(
        string hex, string message, int offset)
    {
        var reader = new OahuReader(Hex.Bytes(hex));

        var exception = ReadBatchFailure(ref reader);

        Assert.Equal(offset, exception.Offset);
        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [InlineData("01" + "00" + "00" + "0161044d696c6b02", "The bool at byte offset 10 is 0x02, which is neither 0x00 nor 0x01.", 10)]
    [InlineData("01" + "03" + "00" + "0161" + "01ff" + "00", "The string at byte offset 5 is not well-formed UTF-8: the sequence at byte offset 6 is ill-formed.", 5)]
    public void A_malformed_row_inside_an_insert_or_a_replace_is_rejected_by_the_row_read(
        string hex, string message, int offset)
    {
        var reader = new OahuReader(Hex.Bytes(hex));

        var exception = ReadBatchFailure(ref reader);

        Assert.Equal(offset, exception.Offset);
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void A_malformed_union_row_is_rejected_by_its_case_index()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("01" + "03" + "00" + "02"));
            reader.ReadBatch(BoardRow.Read);
        });

        Assert.Equal(3, exception.Offset);
        Assert.Equal(
            "The index 2 at byte offset 3 is not below 2, the number of values or cases of its type.", exception.Message);
    }

    [Fact]
    public void The_end_check_rejects_bytes_left_over_after_a_batch()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("01" + "04" + "04"));
            reader.ReadBatch(TaskRow.Read);
            reader.EnsureEnd();
        });

        Assert.Equal(2, exception.Offset);
        Assert.Equal("1 unread byte(s) remain at the end of the input, from byte offset 2.", exception.Message);
    }

    [Fact]
    public void Writing_a_null_batch_or_a_null_callback_is_an_argument_error_and_writes_nothing()
    {
        var writer = new OahuWriter();
        writer.WriteInt(5);

        Assert.Throws<ArgumentNullException>(() => writer.WriteBatch<TaskRow>(null!, TaskRow.Write));
        Assert.Throws<ArgumentNullException>(() => writer.WriteBatch([new ListOp<TaskRow>.Clear()], null!));
        Assert.Equal("0a", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void Writing_an_empty_batch_is_refused_and_writes_nothing()
    {
        var writer = new OahuWriter();
        writer.WriteInt(5);

        var exception = Assert.Throws<ArgumentException>(() => writer.WriteBatch<TaskRow>([], TaskRow.Write));

        Assert.Equal("batch", exception.ParamName);
        Assert.StartsWith("A batch has at least one operation. An empty list is replayed as one Clear.", exception.Message, StringComparison.Ordinal);
        Assert.Equal("0a", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void A_null_operation_anywhere_in_the_batch_is_an_argument_error_and_a_reset_writer_writes_again()
    {
        var writer = new OahuWriter();
        writer.WriteInt(5);

        var exception = Assert.Throws<ArgumentException>(() =>
            writer.WriteBatch([new ListOp<TaskRow>.Clear(), new ListOp<TaskRow>.Remove(0), null!], TaskRow.Write));

        Assert.Equal("batch", exception.ParamName);
        Assert.StartsWith("The operation at position 2 of the batch is null.", exception.Message, StringComparison.Ordinal);
        AssertWritesAgainAfterReset(writer);
    }

    public static TheoryData<ListOp<TaskRow>> NullRowCases => new()
    {
        new ListOp<TaskRow>.Insert(0, null!),
        new ListOp<TaskRow>.Replace(0, null!),
    };

    [Theory]
    [MemberData(nameof(NullRowCases))]
    public void A_null_row_is_an_argument_error_and_a_reset_writer_writes_again(ListOp<TaskRow> operation)
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<ArgumentException>(() =>
            writer.WriteBatch([new ListOp<TaskRow>.Clear(), operation], TaskRow.Write));

        Assert.Equal("batch", exception.ParamName);
        Assert.StartsWith("The operation at position 1 of the batch has a null row.", exception.Message, StringComparison.Ordinal);
        AssertWritesAgainAfterReset(writer);
    }

    public static TheoryData<ListOp<TaskRow>> NegativeIndexCases => new()
    {
        new ListOp<TaskRow>.Insert(-1, Milk),
        new ListOp<TaskRow>.Remove(-1),
        new ListOp<TaskRow>.Move(-1, 0),
        new ListOp<TaskRow>.Move(0, int.MinValue),
        new ListOp<TaskRow>.Replace(-1, Milk),
    };

    [Theory]
    [MemberData(nameof(NegativeIndexCases))]
    public void A_negative_index_is_refused_and_a_reset_writer_writes_again(ListOp<TaskRow> operation)
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.WriteBatch([new ListOp<TaskRow>.Insert(0, Milk), operation], TaskRow.Write));

        Assert.Equal("batch", exception.ParamName);
        Assert.StartsWith("The operation at position 1 of the batch has a negative index.", exception.Message, StringComparison.Ordinal);
        AssertWritesAgainAfterReset(writer);
    }

    [Fact]
    public void Each_operation_is_read_once_and_checked_just_before_it_is_written()
    {
        // The insert is read and written before the remove is read and refused.
        var writer = new OahuWriter();
        var batch = new RecordingBatch([new ListOp<TaskRow>.Insert(0, Milk), new ListOp<TaskRow>.Remove(-1)]);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBatch(batch, TaskRow.Write));

        Assert.Equal("batch", exception.ParamName);
        Assert.StartsWith("The operation at position 1 of the batch has a negative index.", exception.Message, StringComparison.Ordinal);
        Assert.Equal([0, 1], batch.Reads);
        Assert.StartsWith("02" + "00" + "00" + MilkHex, Hex.Of(writer.WrittenSpan), StringComparison.Ordinal);
        AssertWritesAgainAfterReset(writer);
    }

    // After a failed write, a reset writer writes a batch as a new writer does.
    private static void AssertWritesAgainAfterReset(OahuWriter writer)
    {
        writer.Reset();
        writer.WriteBatch([new ListOp<TaskRow>.Insert(0, Milk)], TaskRow.Write);
        Assert.Equal("01" + "00" + "00" + MilkHex, Hex.Of(writer.WrittenSpan));
    }

    // A batch that records which positions are read, to show that each operation is read once.
    private sealed class RecordingBatch(ListOp<TaskRow>[] operations) : IReadOnlyList<ListOp<TaskRow>>
    {
        public List<int> Reads { get; } = [];

        public int Count => operations.Length;

        public ListOp<TaskRow> this[int index]
        {
            get
            {
                Reads.Add(index);
                return operations[index];
            }
        }

        public IEnumerator<ListOp<TaskRow>> GetEnumerator() => ((IEnumerable<ListOp<TaskRow>>)operations).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Reading_with_a_null_callback_is_an_argument_error_and_does_not_move_the_reader()
    {
        var reader = new OahuReader(Hex.Bytes("01" + "04"));

        ArgumentNullException? exception = null;
        try
        {
            reader.ReadBatch<TaskRow>(null!);
        }
        catch (ArgumentNullException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(2, reader.Remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_row_writer_that_resets_the_writer_is_rejected(int writesAfterReset)
    {
        var writer = new OahuWriter();
        writer.WriteString("Milk");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteBatch([new ListOp<TaskRow>.Insert(0, Milk)], (w, row) =>
            {
                w.Reset();
                for (var i = 0; i < writesAfterReset; i++)
                    TaskRow.Write(w, row);
            }));

        Assert.Equal("A row writer reset the writer while its batch was being written.", exception.Message);
    }

    [Fact]
    public void A_row_writer_that_enters_without_exiting_is_rejected_after_that_row()
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteBatch(
                [new ListOp<TaskRow>.Clear(), new ListOp<TaskRow>.Replace(0, Milk), new ListOp<TaskRow>.Insert(0, Eggs)],
                static (w, row) =>
                {
                    w.EnterNested();
                    w.WriteString(row.Key);
                }));

        Assert.Equal(
            "A row of the batch left the nesting depth at 1, not 0 as before it. Every row must call ExitNested once " +
            "for each EnterNested.",
            exception.Message);
    }

    [Fact]
    public void A_row_reader_that_enters_without_exiting_is_rejected_after_that_row()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("01" + "01" + "00" + "00" + MilkHex));
            Assert.True(reader.ReadBool());
            reader.ReadBatch(static (ref OahuReader r) =>
            {
                r.EnterNested();
                return new TaskRow(r.ReadString(), r.ReadString(), r.ReadBool());
            });
        });

        Assert.Equal(
            "A row of the batch at byte offset 1 left the nesting depth at 1, not 0 as before it. Every row must call " +
            "ExitNested once for each EnterNested.",
            exception.Message);
    }

    [Fact]
    public void An_exception_from_the_row_writer_propagates_and_a_reset_writer_writes_again()
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteBatch([new ListOp<TaskRow>.Insert(0, Milk)], static (_, _) =>
                throw new InvalidOperationException("The row cannot be written.")));

        Assert.Equal("The row cannot be written.", exception.Message);

        writer.Reset();
        writer.WriteBatch([new ListOp<TaskRow>.Insert(0, Milk)], TaskRow.Write);
        Assert.Equal("01" + "00" + "00" + MilkHex, Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void A_batch_adds_no_nesting_depth_so_its_rows_nest_64_deep()
    {
        var deepest = NodeRow.Chain(64);
        var writer = new OahuWriter();
        writer.WriteBatch([new ListOp<NodeRow>.Insert(0, deepest)], NodeRow.Write);

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Assert.IsType<ListOp<NodeRow>.Insert>(Assert.Single(reader.ReadBatch(NodeRow.Read)));
        reader.EnsureEnd();
        Assert.Equal(64, read.Item.Depth);

        writer.Reset();
        Assert.Throws<InvalidOperationException>(() =>
            writer.WriteBatch([new ListOp<NodeRow>.Insert(0, NodeRow.Chain(65))], NodeRow.Write));
    }

    [Fact]
    public void A_row_at_depth_65_is_rejected_as_nested_too_deep()
    {
        // A batch of one insert at 0, then 65 rows, each the only child of the one before: its key, the empty string,
        // then its children's count, 01, or 00 for the last.
        var hex = "01" + "00" + "00" + string.Concat(Enumerable.Repeat("0001", 64)) + "0000";

        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes(hex));
            reader.ReadBatch(NodeRow.Read);
        });

        Assert.Equal(3 + (64 * 2), exception.Offset);
        Assert.Equal(
            $"The value at byte offset {3 + (64 * 2)} is at nesting depth 65, deeper than the largest nesting depth, 64.",
            exception.Message);
    }

    [Fact]
    public void Writing_a_batch_with_a_cached_delegate_allocates_nothing()
    {
        ListOp<TaskRow>[] batch =
        [
            new ListOp<TaskRow>.Clear(),
            new ListOp<TaskRow>.Insert(0, Milk),
            new ListOp<TaskRow>.Move(0, 0),
            new ListOp<TaskRow>.Replace(0, Eggs),
            new ListOp<TaskRow>.Remove(0),
        ];
        var writer = new OahuWriter();

        Assert.Equal(0, Allocations.Smallest(() =>
        {
            writer.Reset();
            writer.WriteBatch(batch, TaskRow.Write);
        }));
    }

    [Fact]
    public void Reading_a_batch_allocates_only_its_array_and_its_operations()
    {
        var input = Hex.Bytes("03" + "0101" + "020001" + "04");

        // The same work, less the batch: the array of three operations and the operations, kept reachable the same way.
        var arrayAndOperations = Allocations.Smallest(() => _sink = new ListOp<TaskRow>[]
        {
            new ListOp<TaskRow>.Remove(1),
            new ListOp<TaskRow>.Move(0, 1),
            new ListOp<TaskRow>.Clear(),
        });
        var read = Allocations.Smallest(() =>
        {
            var reader = new OahuReader(input);
            _sink = reader.ReadBatch(TaskRow.Read);
        });

        Assert.True(arrayAndOperations > 0);
        Assert.Equal(arrayAndOperations, read);
    }

    [Fact]
    public void Batches_mixed_with_other_values_are_read_in_the_order_written()
    {
        var writer = new OahuWriter(1);
        writer.WriteString("Milk");
        writer.WriteBatch([new ListOp<TaskRow>.Clear()], TaskRow.Write);
        writer.WriteList([1, 2], static (w, item) => w.WriteInt(item));
        writer.WriteBatch([new ListOp<TaskRow>.Insert(1, Eggs), new ListOp<TaskRow>.Remove(0)], TaskRow.Write);
        writer.WritePresence(false);
        writer.WriteBatch([new ListOp<BoardRow>.Insert(0, new BoardRow.Card("c", "x"))], BoardRow.Write);
        writer.WriteBool(true);

        Assert.Equal(
            "044d696c6b" + "0104" + "020204" + "02" + "0001" + EggsHex + "0100" + "00" + "01" + "0000" + "0101630178" +
            "01",
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal("Milk", reader.ReadString());
        Assert.Equal<ListOp<TaskRow>>([new ListOp<TaskRow>.Clear()], reader.ReadBatch(TaskRow.Read));
        Assert.Equal<int>([1, 2], reader.ReadList(static (ref OahuReader r) => r.ReadInt()));
        Assert.Equal<ListOp<TaskRow>>(
            [new ListOp<TaskRow>.Insert(1, Eggs), new ListOp<TaskRow>.Remove(0)], reader.ReadBatch(TaskRow.Read));
        Assert.False(reader.ReadPresence());
        Assert.Equal<ListOp<BoardRow>>(
            [new ListOp<BoardRow>.Insert(0, new BoardRow.Card("c", "x"))], reader.ReadBatch(BoardRow.Read));
        Assert.True(reader.ReadBool());
        reader.EnsureEnd();
    }

    private static OahuFormatException ReadBatchFailure(ref OahuReader reader)
    {
        try
        {
            reader.ReadBatch(TaskRow.Read);
        }
        catch (OahuFormatException caught)
        {
            return caught;
        }

        throw new InvalidOperationException("ReadBatch did not throw.");
    }

    // A row, with the codec a generator would write for it: EnterNested, its fields in order, ExitNested.
    public sealed record TaskRow(string Key, string Text, bool Done) : IOahuKeyed
    {
        public static void Write(OahuWriter writer, TaskRow row)
        {
            writer.EnterNested();
            writer.WriteString(row.Key);
            writer.WriteString(row.Text);
            writer.WriteBool(row.Done);
            writer.ExitNested();
        }

        public static TaskRow Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var key = reader.ReadString();
            var text = reader.ReadString();
            var done = reader.ReadBool();
            reader.ExitNested();
            return new TaskRow(key, text, done);
        }
    }

    // A row that is a union, with the codec a generator would write for it: EnterNested once, the case index, the
    // case's fields, ExitNested.
    public abstract record BoardRow(string Key) : IOahuKeyed
    {
        public static void Write(OahuWriter writer, BoardRow row)
        {
            writer.EnterNested();
            switch (row)
            {
                case Header header:
                    writer.WriteIndex(0, 2);
                    writer.WriteString(header.Key);
                    writer.WriteString(header.Title);
                    break;
                case Card card:
                    writer.WriteIndex(1, 2);
                    writer.WriteString(card.Key);
                    writer.WriteString(card.Text);
                    break;
                default:
                    throw new InvalidOperationException("Unknown case.");
            }

            writer.ExitNested();
        }

        public static BoardRow Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var index = reader.ReadIndex(2);
            var key = reader.ReadString();
            var text = reader.ReadString();
            reader.ExitNested();
            return index == 0 ? new Header(key, text) : new Card(key, text);
        }

        public sealed record Header(string Key, string Title) : BoardRow(Key);

        public sealed record Card(string Key, string Text) : BoardRow(Key);
    }

    // A row that holds rows of its own type, so that a value of it can nest as deep as the input says.
    public sealed record NodeRow(string Key, OahuList<NodeRow> Children) : IOahuKeyed
    {
        public int Depth => 1 + (Children.Count == 0 ? 0 : Children.Max(child => child.Depth));

        public static NodeRow Chain(int depth)
        {
            var row = new NodeRow("", []);
            for (var i = 1; i < depth; i++)
                row = new NodeRow("", [row]);
            return row;
        }

        public static void Write(OahuWriter writer, NodeRow row)
        {
            writer.EnterNested();
            writer.WriteString(row.Key);
            writer.WriteList(row.Children, Write);
            writer.ExitNested();
        }

        public static NodeRow Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var key = reader.ReadString();
            var children = reader.ReadList(Read);
            reader.ExitNested();
            return new NodeRow(key, children);
        }
    }
}
