using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class ListTests
{
    // Keeps each list read in the allocation tests reachable, so that the runtime cannot drop or stack-allocate it.
    private static object? _sink;

    public static TheoryData<int[], string> IntCases => new()
    {
        { [], "00" },
        { [5], "01" + "0a" },
        { [0, -1, 1, 64, int.MaxValue], "05" + "00" + "01" + "02" + "8001" + "feffffff0f" },
    };

    [Theory]
    [MemberData(nameof(IntCases))]
    public void A_list_of_ints_is_its_count_then_each_int(int[] items, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteList(items, WriteIntItem);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal<int>(items, reader.ReadList(ReadIntItem));
        reader.EnsureEnd();
    }

    public static TheoryData<string[], string> StringCases => new()
    {
        { [], "00" },
        { ["Milk"], "01" + "044d696c6b" },
        { ["a", "bc"], "02" + "0161" + "026263" },
        { ["", "€", ""], "03" + "00" + "03e282ac" + "00" },
    };

    [Theory]
    [MemberData(nameof(StringCases))]
    public void A_list_of_strings_is_its_count_then_each_string(string[] items, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteList(items, WriteStringItem);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal<string>(items, reader.ReadList(ReadStringItem));
        reader.EnsureEnd();
    }

    public static TheoryData<bool[], string> BoolCases => new()
    {
        { [], "00" },
        { [true], "01" + "01" },
        { [false, true, false], "03" + "00" + "01" + "00" },
    };

    [Theory]
    [MemberData(nameof(BoolCases))]
    public void A_list_of_bools_is_its_count_then_each_bool(bool[] items, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteList(items, WriteBoolItem);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal<bool>(items, reader.ReadList(ReadBoolItem));
        reader.EnsureEnd();
    }

    public static TheoryData<double[], string> DoubleCases => new()
    {
        { [], "00" },
        { [1.5], "01" + "000000000000f83f" },
        { [-0.0, 1.5, double.PositiveInfinity], "03" + "0000000000000080" + "000000000000f83f" + "000000000000f07f" },
    };

    [Theory]
    [MemberData(nameof(DoubleCases))]
    public void A_list_of_doubles_is_its_count_then_each_double(double[] items, string hex)
    {
        var writer = new OahuWriter();
        writer.WriteList(items, WriteDoubleItem);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = reader.ReadList(ReadDoubleItem);
        Assert.Equal(items.Length, read.Count);
        for (var i = 0; i < items.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(items[i]), BitConverter.DoubleToInt64Bits(read[i]));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_list_of_lists_is_its_count_then_each_inner_list()
    {
        int[][] items = [[], [1], [2, 3]];

        var writer = new OahuWriter();
        writer.WriteList(items, static (w, list) => w.WriteList(list, WriteIntItem));

        Assert.Equal("03" + "00" + "0102" + "020406", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = reader.ReadList(static (ref OahuReader r) => r.ReadList(ReadIntItem));
        Assert.Equal(items.Length, read.Count);
        for (var i = 0; i < items.Length; i++)
            Assert.Equal<int>(items[i], read[i]);
        reader.EnsureEnd();
    }

    [Fact]
    public void A_list_of_nullable_strings_has_a_presence_byte_for_each_item()
    {
        string?[] items = [null, "x", "", null];

        var writer = new OahuWriter();
        writer.WriteList(items, WriteNullableStringItem);

        Assert.Equal("04" + "00" + "010178" + "0100" + "00", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal<string?>(items, reader.ReadList(ReadNullableStringItem));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_list_of_records_is_its_count_then_the_fields_of_each_record_in_order()
    {
        Item[] items = [new("Milk", 2), new("Eggs", 12)];

        var writer = new OahuWriter();
        writer.WriteList(items, Item.Write);

        Assert.Equal("02" + "044d696c6b" + "04" + "0445676773" + "18", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal<Item>(items, reader.ReadList(Item.Read));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(127, "7f")]
    [InlineData(128, "8001")]
    public void The_count_crosses_the_one_byte_prefix_boundary(int count, string prefix)
    {
        var items = new bool[count];
        for (var i = 0; i < count; i++)
            items[i] = i % 3 == 0;

        var writer = new OahuWriter();
        writer.WriteList(items, WriteBoolItem);

        var expected = prefix + string.Concat(items.Select(item => item ? "01" : "00"));
        Assert.Equal(expected, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal<bool>(items, reader.ReadList(ReadBoolItem));
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData("01", 1, 0)]
    [InlineData("0201", 2, 1)]
    [InlineData("0300", 3, 1)]
    [InlineData("8001" + "01", 128, 1)]
    public void A_count_larger_than_the_bytes_left_is_rejected(string hex, int count, int available)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);

        OahuFormatException? exception = null;
        try
        {
            reader.ReadList(ReadBoolItem);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(0, exception.Offset);
        Assert.Equal(CountTooLargeMessage(0, count, available), exception.Message);
        Assert.Equal(input.Length, reader.Remaining);
    }

    [Fact]
    public void A_count_one_larger_than_the_bytes_left_after_the_start_is_rejected()
    {
        // A bool, then a list of three strings with only the bytes of two empty strings after its count.
        var reader = new OahuReader(Hex.Bytes("01" + "03" + "0000"));
        Assert.True(reader.ReadBool());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadList(ReadStringItem);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(1, exception.Offset);
        Assert.Equal(CountTooLargeMessage(1, 3, 2), exception.Message);
        Assert.Equal(3, reader.Remaining);
    }

    [Fact]
    public void A_huge_count_is_rejected_without_allocating_the_list()
    {
        var input = Hex.Bytes("01" + "ffffffff07" + "0102");
        var reader = new OahuReader(input);
        Assert.True(reader.ReadBool());

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        OahuFormatException? exception = null;
        try
        {
            reader.ReadList(ReadBoolItem);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.NotNull(exception);
        Assert.Equal(1, exception.Offset);
        Assert.Equal(CountTooLargeMessage(1, int.MaxValue, 2), exception.Message);
        Assert.Equal(7, reader.Remaining);
        Assert.True(allocated < 1_000_000, $"The failed read allocated {allocated} bytes.");
    }

    [Fact]
    public void A_truncated_last_item_is_rejected_by_the_item_read()
    {
        // Two strings: "a", then a string of two bytes with only one byte left.
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("02" + "0161" + "0262"));
            reader.ReadList(ReadStringItem);
        });

        Assert.Equal(3, exception.Offset);
        Assert.Equal(
            "The string at byte offset 3 is 2 byte(s) long, but only 1 byte(s) remain after its length.",
            exception.Message);
    }

    [Fact]
    public void A_malformed_item_is_rejected_by_the_item_read()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("02" + "00" + "02"));
            reader.ReadList(ReadNullableStringItem);
        });

        Assert.Equal(2, exception.Offset);
        Assert.Equal(
            "The presence byte at byte offset 2 is 0x02, which is neither 0x00 nor 0x01.", exception.Message);
    }

    [Theory]
    [InlineData("", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("80", "The input ends inside a variable-length integer that starts at byte offset 0.")]
    [InlineData("8080808008", "The count 2147483648 at byte offset 0 is larger than the largest count, 2147483647.")]
    public void A_malformed_count_is_rejected_as_a_count(string hex, string message)
    {
        var input = Hex.Bytes(hex);
        var reader = new OahuReader(input);

        OahuFormatException? exception = null;
        try
        {
            reader.ReadList(ReadIntItem);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(0, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(input.Length, reader.Remaining);
    }

    [Theory]
    [InlineData("01" + "80", "The input ends inside a variable-length integer that starts at byte offset 1.", 1)]
    [InlineData(
        "01" + "8080808008" + "00",
        "The count 2147483648 at byte offset 1 is larger than the largest count, 2147483647.",
        6)]
    public void A_malformed_count_after_the_start_names_its_offset_and_does_not_move_the_reader(
        string hex, string message, int remaining)
    {
        var reader = new OahuReader(Hex.Bytes(hex));
        Assert.True(reader.ReadBool());

        OahuFormatException? exception = null;
        try
        {
            reader.ReadList(ReadIntItem);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(1, exception.Offset);
        Assert.Equal(message, exception.Message);
        Assert.Equal(remaining, reader.Remaining);
    }

    [Fact]
    public void The_end_check_passes_after_a_list_that_ends_the_input()
    {
        var reader = new OahuReader(Hex.Bytes("02" + "0102"));
        Assert.Equal<int>([-1, 1], reader.ReadList(ReadIntItem));

        reader.EnsureEnd();
    }

    [Fact]
    public void The_end_check_rejects_bytes_left_over_after_a_list()
    {
        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("01" + "02" + "ff"));
            reader.ReadList(ReadIntItem);
            reader.EnsureEnd();
        });

        Assert.Equal(2, exception.Offset);
        Assert.Equal("1 unread byte(s) remain at the end of the input, from byte offset 2.", exception.Message);
    }

    [Fact]
    public void Writing_null_items_or_a_null_callback_is_an_argument_error_and_writes_nothing()
    {
        var writer = new OahuWriter();
        writer.WriteInt(5);

        Assert.Throws<ArgumentNullException>(() => writer.WriteList<int>(null!, WriteIntItem));
        Assert.Throws<ArgumentNullException>(() => writer.WriteList([1, 2], null!));
        Assert.Equal("0a", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void Reading_with_a_null_callback_is_an_argument_error_and_does_not_move_the_reader()
    {
        var reader = new OahuReader(Hex.Bytes("01" + "02"));

        ArgumentNullException? exception = null;
        try
        {
            reader.ReadList<int>(null!);
        }
        catch (ArgumentNullException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(2, reader.Remaining);
    }

    [Fact]
    public void An_exception_from_the_item_writer_propagates_and_a_reset_writer_writes_again()
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteList([1, 2, 3], static (w, item) =>
            {
                if (item == 2)
                    throw new InvalidOperationException("Item 2 cannot be written.");
                w.WriteInt(item);
            }));

        Assert.Equal("Item 2 cannot be written.", exception.Message);

        writer.Reset();
        writer.WriteList([1, 2, 3], WriteIntItem);
        Assert.Equal("03" + "02" + "04" + "06", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void An_exception_from_the_item_reader_propagates()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new OahuReader(Hex.Bytes("02" + "02" + "04"));
            reader.ReadList(static (ref OahuReader r) =>
            {
                var item = r.ReadInt();
                return item == 2 ? throw new InvalidOperationException("Item 2 cannot be read.") : item;
            });
        });

        Assert.Equal("Item 2 cannot be read.", exception.Message);
    }

    [Fact]
    public void An_empty_list_is_the_same_instance_every_time_and_allocates_nothing()
    {
        var input = Hex.Bytes("00" + "00");

        var reader = new OahuReader(input);
        var first = reader.ReadList(ReadStringItem);
        var second = reader.ReadList(ReadStringItem);
        reader.EnsureEnd();

        Assert.Empty(first);
        Assert.Same(first, second);
        Assert.Equal(0, Allocations.Smallest(() =>
        {
            var r = new OahuReader(input);
            _sink = r.ReadList(ReadStringItem);
        }));
    }

    [Fact]
    public void Reading_a_list_with_a_cached_delegate_allocates_only_the_array_of_its_items()
    {
        var input = Hex.Bytes("03" + "02" + "04" + "06");

        // The same work, less the list: a new array of three ints, kept reachable the same way.
        var arrayOnly = Allocations.Smallest(() => _sink = new int[3]);
        var read = Allocations.Smallest(() =>
        {
            var reader = new OahuReader(input);
            _sink = reader.ReadList(ReadIntItem);
        });

        Assert.True(arrayOnly > 0);
        Assert.Equal(arrayOnly, read);
    }

    [Fact]
    public void Writing_a_list_with_a_cached_delegate_allocates_nothing()
    {
        int[] items = [1, -1, 300, int.MinValue];
        var writer = new OahuWriter();

        Assert.Equal(0, Allocations.Smallest(() =>
        {
            writer.Reset();
            writer.WriteList(items, WriteIntItem);
        }));
    }

    [Fact]
    public void Lists_of_many_lengths_round_trip()
    {
        var random = new Random(20_261_125);
        var intLists = new List<int[]>();
        var stringLists = new List<string[]>();
        for (var i = 0; i < 300; i++)
        {
            var ints = new int[random.Next(0, i % 10 == 0 ? 400 : 20)];
            for (var j = 0; j < ints.Length; j++)
                ints[j] = random.Next(int.MinValue, int.MaxValue);
            intLists.Add(ints);

            var strings = new string[random.Next(0, 20)];
            for (var j = 0; j < strings.Length; j++)
                strings[j] = new string((char)random.Next('a', 'z' + 1), random.Next(0, 200));
            stringLists.Add(strings);
        }

        var writer = new OahuWriter(1);
        for (var i = 0; i < intLists.Count; i++)
        {
            writer.WriteList(intLists[i], WriteIntItem);
            writer.WriteList(stringLists[i], WriteStringItem);
        }

        writer.WriteList(intLists, static (w, list) => w.WriteList(list, WriteIntItem));

        var reader = new OahuReader(writer.WrittenSpan);
        for (var i = 0; i < intLists.Count; i++)
        {
            Assert.Equal<int>(intLists[i], reader.ReadList(ReadIntItem));
            Assert.Equal<string>(stringLists[i], reader.ReadList(ReadStringItem));
        }

        var nested = reader.ReadList(static (ref OahuReader r) => r.ReadList(ReadIntItem));
        Assert.Equal(intLists.Count, nested.Count);
        for (var i = 0; i < intLists.Count; i++)
            Assert.Equal<int>(intLists[i], nested[i]);
        reader.EnsureEnd();
    }

    [Fact]
    public void Lists_mixed_with_other_values_are_read_in_the_order_written()
    {
        var writer = new OahuWriter(1);
        writer.WriteString("Milk");
        writer.WriteList([1, 2], WriteIntItem);
        writer.WriteBool(true);
        writer.WriteList(Array.Empty<string>(), WriteStringItem);
        writer.WritePresence(true);
        writer.WriteList(["a", "bc"], WriteStringItem);
        writer.WritePresence(false);
        writer.WriteDouble(1.5);
        writer.WriteList([new Item("Eggs", 12)], Item.Write);
        writer.WriteCount(3);

        Assert.Equal(
            "044d696c6b" + "02" + "0204" + "01" + "00" + "01" + "02" + "0161" + "026263" + "00" +
            "000000000000f83f" + "01" + "0445676773" + "18" + "03",
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal("Milk", reader.ReadString());
        Assert.Equal<int>([1, 2], reader.ReadList(ReadIntItem));
        Assert.True(reader.ReadBool());
        Assert.Empty(reader.ReadList(ReadStringItem));
        Assert.True(reader.ReadPresence());
        Assert.Equal<string>(["a", "bc"], reader.ReadList(ReadStringItem));
        Assert.False(reader.ReadPresence());
        Assert.Equal(1.5, reader.ReadDouble());
        Assert.Equal<Item>([new Item("Eggs", 12)], reader.ReadList(Item.Read));
        Assert.Equal(3, reader.ReadCount());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Items_that_write_nothing_are_rejected_by_the_writer(int count)
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteList(new int[count], static (_, _) => { }));

        Assert.Equal(TooFewItemBytesMessage(count, 0), exception.Message);
    }

    [Fact]
    public void Items_that_write_fewer_bytes_than_there_are_items_are_rejected_by_the_writer()
    {
        var writer = new OahuWriter();

        // Three items: one writes two bytes, the others nothing.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteList([0, 2, 0], WriteThatManyBools));

        Assert.Equal(TooFewItemBytesMessage(3, 2), exception.Message);
    }

    [Fact]
    public void An_item_that_writes_nothing_is_not_detected_when_the_other_items_make_up_the_bytes()
    {
        var writer = new OahuWriter();

        // Three items write three bytes in all, though two of them write nothing. The check is per list, not per item.
        writer.WriteList([3, 0, 0], WriteThatManyBools);

        Assert.Equal("03" + "010101", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void An_empty_list_with_an_item_writer_that_writes_nothing_is_written()
    {
        var writer = new OahuWriter();
        writer.WriteList(Array.Empty<int>(), static (_, _) => { });

        Assert.Equal("00", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void A_List_of_T_is_written_like_an_array_and_allocates_nothing()
    {
        List<int> items = [1, -1, 300];
        var writer = new OahuWriter();
        writer.WriteList(items, WriteIntItem);

        Assert.Equal("03" + "02" + "01" + "d804", Hex.Of(writer.WrittenSpan));
        Assert.Equal(0, Allocations.Smallest(() =>
        {
            writer.Reset();
            writer.WriteList(items, WriteIntItem);
        }));
    }

    [Fact]
    public void The_count_is_read_once_so_a_list_that_grows_while_it_is_written_writes_its_original_count()
    {
        var items = new GrowingList([1, 2]);
        var writer = new OahuWriter();
        writer.WriteList(items, WriteIntItem);

        Assert.Equal("02" + "02" + "04", Hex.Of(writer.WrittenSpan));
        Assert.Equal(4, items.Count);
    }

    [Fact]
    public void A_type_that_contains_itself_round_trips_when_nested_fifty_deep()
    {
        var root = new Node(0, []);
        for (var depth = 1; depth < 50; depth++)
            root = new Node(depth, [root, new Node(-depth, [])]);

        var writer = new OahuWriter();
        Node.Write(writer, root);

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Node.Read(ref reader);
        reader.EnsureEnd();

        for (var depth = 49; depth > 0; depth--)
        {
            Assert.Equal(depth, read.Value);
            Assert.Equal(2, read.Children.Count);
            Assert.Equal(-depth, read.Children[1].Value);
            Assert.Empty(read.Children[1].Children);
            read = read.Children[0];
        }

        Assert.Equal(0, read.Value);
        Assert.Empty(read.Children);
    }

    private static string TooFewItemBytesMessage(int count, int bytes) =>
        $"The list's {count} item(s) wrote only {bytes} byte(s), fewer than there are items. " +
        "Every item must write at least one byte.";

    private static void WriteThatManyBools(OahuWriter writer, int value)
    {
        for (var i = 0; i < value; i++)
            writer.WriteBool(true);
    }

    private static string CountTooLargeMessage(int start, int count, int available) =>
        $"The list at byte offset {start} has {count} item(s), but only {available} byte(s) remain after its count, " +
        "and every item takes at least one byte.";

    private static void WriteIntItem(OahuWriter writer, int value) => writer.WriteInt(value);

    private static int ReadIntItem(ref OahuReader reader) => reader.ReadInt();

    private static void WriteStringItem(OahuWriter writer, string value) => writer.WriteString(value);

    private static string ReadStringItem(ref OahuReader reader) => reader.ReadString();

    private static void WriteBoolItem(OahuWriter writer, bool value) => writer.WriteBool(value);

    private static bool ReadBoolItem(ref OahuReader reader) => reader.ReadBool();

    private static void WriteDoubleItem(OahuWriter writer, double value) => writer.WriteDouble(value);

    private static double ReadDoubleItem(ref OahuReader reader) => reader.ReadDouble();

    private static void WriteNullableStringItem(OahuWriter writer, string? value)
    {
        writer.WritePresence(value is not null);
        if (value is not null)
            writer.WriteString(value);
    }

    private static string? ReadNullableStringItem(ref OahuReader reader) =>
        reader.ReadPresence() ? reader.ReadString() : null;

    // A record of two fields, with the codec a generator would write for it: its fields in order.
    private sealed record Item(string Name, int Quantity)
    {
        public static void Write(OahuWriter writer, Item item)
        {
            writer.WriteString(item.Name);
            writer.WriteInt(item.Quantity);
        }

        public static Item Read(ref OahuReader reader)
        {
            var name = reader.ReadString();
            var quantity = reader.ReadInt();
            return new Item(name, quantity);
        }
    }

    // A record that contains a list of itself, with the codec a generator would write for it.
    private sealed record Node(int Value, IReadOnlyList<Node> Children)
    {
        public static void Write(OahuWriter writer, Node node)
        {
            writer.WriteInt(node.Value);
            writer.WriteList(node.Children, Write);
        }

        public static Node Read(ref OahuReader reader)
        {
            var value = reader.ReadInt();
            var children = reader.ReadList(Read);
            return new Node(value, children);
        }
    }

    // A list that adds an item each time one is read from it.
    private sealed class GrowingList(List<int> items) : IReadOnlyList<int>
    {
        public int Count => items.Count;

        public int this[int index]
        {
            get
            {
                var item = items[index];
                items.Add(item * 10);
                return item;
            }
        }

        public IEnumerator<int> GetEnumerator() => items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
