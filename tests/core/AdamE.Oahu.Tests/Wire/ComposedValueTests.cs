using System.Diagnostics;
using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

// Records, enums and unions, written and read with hand-written codecs in the shape a generator writes them: a record
// is its fields in order, an enum is the index of its value, and a union is the index of its case, then that case's
// fields.
public sealed class ComposedValueTests
{
    private const string TooDeepEnd = "is at nesting depth 65, deeper than the largest nesting depth, 64.";

    [Theory]
    [InlineData(Priority.High, "00")]
    [InlineData(Priority.Low, "01")]
    [InlineData(Priority.Normal, "02")]
    public void An_enum_is_the_index_of_its_value_in_declaration_order_not_its_numeric_value(
        Priority value, string hex)
    {
        var writer = new OahuWriter();
        PriorityCodec.Write(writer, value);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(value, PriorityCodec.Read(ref reader));
        reader.EnsureEnd();
    }

    [Fact]
    public void An_enum_s_numeric_values_are_never_written()
    {
        // As ints, 10, 5 and 7 would be 14, 0a and 0e.
        Assert.Equal(10, (int)Priority.High);
        Assert.Equal(5, (int)Priority.Low);
        Assert.Equal(7, (int)Priority.Normal);

        var writer = new OahuWriter();
        PriorityCodec.Write(writer, Priority.High);
        PriorityCodec.Write(writer, Priority.Low);
        PriorityCodec.Write(writer, Priority.Normal);

        Assert.Equal("00" + "01" + "02", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void An_enum_index_the_enum_does_not_have_is_rejected()
    {
        var reader = new OahuReader(Hex.Bytes("03"));

        OahuFormatException? exception = null;
        try
        {
            PriorityCodec.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(0, exception.Offset);
        Assert.Equal(
            "The index 3 at byte offset 0 is not below 3, the number of values or cases of its type.",
            exception.Message);
        Assert.Equal(1, reader.Remaining);
    }

    [Theory]
    [InlineData(null, "00")]
    [InlineData(Priority.High, "0100")]
    [InlineData(Priority.Normal, "0102")]
    public void A_nullable_enum_is_a_presence_byte_then_the_index_when_present(Priority? value, string hex)
    {
        var writer = new OahuWriter();
        writer.WritePresence(value.HasValue);
        if (value is { } present)
            PriorityCodec.Write(writer, present);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadPresence() ? PriorityCodec.Read(ref reader) : null);
        reader.EnsureEnd();
    }

    [Fact]
    public void A_list_of_enums_is_its_count_then_each_index()
    {
        OahuList<Priority> values = [Priority.Normal, Priority.High, Priority.Low, Priority.Normal];
        var writer = new OahuWriter();
        writer.WriteList(values, PriorityCodec.Write);

        Assert.Equal("04" + "02" + "00" + "01" + "02", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(values, reader.ReadList(PriorityCodec.Read));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_record_is_its_fields_in_order_with_nothing_before_between_or_after_them()
    {
        var item = new Item("Milk", 2, true, 1.5, null, Priority.Normal, ["a", "bc"]);
        var writer = new OahuWriter();
        Item.Write(writer, item);

        Assert.Equal(
            "044d696c6b" + "04" + "01" + "000000000000f83f" + "00" + "02" + "02" + "0161" + "026263",
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Item.Read(ref reader);
        reader.EnsureEnd();

        Assert.Equal("Milk", read.Name);
        Assert.Equal(2, read.Quantity);
        Assert.True(read.Done);
        Assert.Equal(1.5, read.Weight);
        Assert.Null(read.Due);
        Assert.Equal(Priority.Normal, read.Priority);
        Assert.Equal<string>(["a", "bc"], read.Tags);

        // The record read holds another list instance, and compares equal by the list's items.
        Assert.Equal(item, read);
        Assert.True(item == read);
    }

    [Theory]
    [InlineData("Milk", 2, true, null, "044d696c6b" + "04" + "01" + "00")]
    [InlineData("", -1, false, 1.5, "00" + "01" + "00" + "01" + "000000000000f83f")]
    public void A_record_of_a_few_fields_is_those_fields_alone(
        string name, int quantity, bool done, double? weight, string hex)
    {
        var writer = new OahuWriter();
        writer.EnterNested();
        writer.WriteString(name);
        writer.WriteInt(quantity);
        writer.WriteBool(done);
        writer.WritePresence(weight.HasValue);
        if (weight is { } present)
            writer.WriteDouble(present);
        writer.ExitNested();

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        reader.EnterNested();
        Assert.Equal(name, reader.ReadString());
        Assert.Equal(quantity, reader.ReadInt());
        Assert.Equal(done, reader.ReadBool());
        Assert.Equal(weight, reader.ReadPresence() ? reader.ReadDouble() : null);
        reader.ExitNested();
        reader.EnsureEnd();
    }

    [Fact]
    public void A_record_with_a_present_nullable_field_has_the_field_s_presence_byte_and_value_in_its_place()
    {
        var due = DateTimeOffset.FromUnixTimeMilliseconds(1);
        var item = new Item("", -1, false, 0, due, Priority.High, []);
        var writer = new OahuWriter();
        Item.Write(writer, item);

        Assert.Equal(
            "00" + "01" + "00" + "0000000000000000" + "01" + "02" + "00" + "00", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Item.Read(ref reader);
        reader.EnsureEnd();

        Assert.Equal(due, read.Due);
        Assert.Equal(Priority.High, read.Priority);
        Assert.Empty(read.Tags);
    }

    [Fact]
    public void A_record_with_no_fields_is_no_bytes_as_a_field_of_another_record()
    {
        var writer = new OahuWriter();
        Empty.Write(writer, new Empty());

        Assert.Equal(0, writer.WrittenCount);

        Tagged.Write(writer, new Tagged(new Empty(), -1));

        Assert.Equal("01", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Tagged.Read(ref reader);
        reader.EnsureEnd();

        Assert.NotNull(read.Marker);
        Assert.Equal(-1, read.Value);
    }

    [Fact]
    public void A_record_with_no_fields_still_counts_one_level_of_depth()
    {
        var writer = new OahuWriter();
        EnterLevels(writer, OahuFormat.MaxNestingDepth - 1);

        var written = Assert.Throws<InvalidOperationException>(() => Tagged.Write(writer, new Tagged(new Empty(), 0)));

        Assert.Equal("The value at byte offset 0 " + TooDeepEnd, written.Message);
        Assert.Equal(0, writer.WrittenCount);

        var reader = new OahuReader(Hex.Bytes("00"));
        for (var level = 0; level < OahuFormat.MaxNestingDepth - 1; level++)
            reader.EnterNested();

        OahuFormatException? read = null;
        try
        {
            Tagged.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            read = caught;
        }

        Assert.NotNull(read);
        Assert.Equal(0, read.Offset);
        Assert.Equal("The value at byte offset 0 " + TooDeepEnd, read.Message);
        Assert.Equal(1, reader.Remaining);
    }

    public static TheoryData<string, string> ScreenCases => new()
    {
        { "loading", "00" },
        { "failed", "01" + "0178" },
        { "failed-empty", "01" + "00" },
        { "ready", "02" + "02" + "01" + "02" + "02" + "06" },
        { "ready-empty", "02" + "00" + "00" + "00" },
    };

    [Theory]
    [MemberData(nameof(ScreenCases))]
    public void A_union_is_the_index_of_its_case_then_that_case_s_fields(string name, string hex)
    {
        var screen = SampleScreen(name);
        var writer = new OahuWriter();
        Screen.Write(writer, screen);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Screen.Read(ref reader);
        reader.EnsureEnd();

        Assert.Equal(screen, read);
    }

    [Fact]
    public void A_union_case_index_the_union_does_not_have_is_rejected_at_its_offset()
    {
        var reader = new OahuReader(Hex.Bytes("03"));

        OahuFormatException? exception = null;
        try
        {
            Screen.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(0, exception.Offset);
        Assert.Equal(
            "The index 3 at byte offset 0 is not below 3, the number of values or cases of its type.",
            exception.Message);
        Assert.Equal(1, reader.Remaining);
    }

    [Fact]
    public void A_union_and_its_case_are_one_level_and_a_record_in_the_case_is_one_more()
    {
        // At depth 63 before it, the union is at depth 64: a case of fields that are not records fits, and the Point
        // of a Ready case, at depth 65, is refused. It starts after the case index, at byte offset 1.
        var writer = new OahuWriter();
        EnterLevels(writer, OahuFormat.MaxNestingDepth - 1);
        Screen.Write(writer, new Screen.Failed("x"));
        Assert.Equal("010178", Hex.Of(writer.WrittenSpan));

        writer.Reset();
        EnterLevels(writer, OahuFormat.MaxNestingDepth - 1);
        var written = Assert.Throws<InvalidOperationException>(() =>
            Screen.Write(writer, new Screen.Ready(new Point(0, 0), [])));
        Assert.Equal("The value at byte offset 1 " + TooDeepEnd, written.Message);

        var reader = new OahuReader(Hex.Bytes("02000000"));
        for (var level = 0; level < OahuFormat.MaxNestingDepth - 1; level++)
            reader.EnterNested();

        OahuFormatException? read = null;
        try
        {
            Screen.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            read = caught;
        }

        Assert.NotNull(read);
        Assert.Equal(1, read.Offset);
        Assert.Equal("The value at byte offset 1 " + TooDeepEnd, read.Message);
        Assert.Equal(3, reader.Remaining);
    }

    [Fact]
    public void A_union_in_a_record_in_a_list_is_written_in_place_with_nothing_around_it()
    {
        OahuList<Panel> panels =
        [
            new("a", new Screen.Loading()),
            new("b", new Screen.Failed("x")),
            new("", new Screen.Ready(new Point(0, 0), [])),
        ];
        var writer = new OahuWriter();
        writer.WriteList(panels, Panel.Write);

        Assert.Equal(
            "03" + "0161" + "00" + "0162" + "01" + "0178" + "00" + "02" + "0000" + "00",
            Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = reader.ReadList(Panel.Read);
        reader.EnsureEnd();

        Assert.Equal(panels, read);
    }

    [Fact]
    public void An_out_of_range_case_index_inside_a_list_of_records_is_rejected_at_the_index()
    {
        var input = Hex.Bytes("02" + "0161" + "00" + "0162" + "03");

        var exception = Assert.Throws<OahuFormatException>(() =>
        {
            var reader = new OahuReader(input);
            reader.ReadList(Panel.Read);
        });

        Assert.Equal(6, exception.Offset);
        Assert.Equal(
            "The index 3 at byte offset 6 is not below 3, the number of values or cases of its type.",
            exception.Message);
    }

    [Theory]
    [InlineData(8, "8 is not a declared Priority.")]
    [InlineData((int)(Priority.High | Priority.Low), "15 is not a declared Priority.")]
    public void An_enum_value_that_is_not_exactly_one_declared_value_is_refused_and_nothing_is_written(
        int number, string message)
    {
        var writer = new OahuWriter();
        PriorityCodec.Write(writer, Priority.Low);

        var exception = Assert.Throws<InvalidOperationException>(() => PriorityCodec.Write(writer, (Priority)number));

        Assert.Equal(message, exception.Message);
        Assert.Equal("01", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void An_enum_index_is_read_from_a_longer_form_than_the_shortest()
    {
        var reader = new OahuReader(Hex.Bytes("8000"));
        Assert.Equal(Priority.High, PriorityCodec.Read(ref reader));
        reader.EnsureEnd();

        var writer = new OahuWriter();
        PriorityCodec.Write(writer, Priority.High);
        Assert.Equal("00", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void A_record_of_only_records_with_no_fields_is_no_bytes_and_its_nullable_form_can_be_a_list_item()
    {
        var writer = new OahuWriter();
        Pair.Write(writer, new Pair(new Empty(), new Empty()));

        Assert.Equal(0, writer.WrittenCount);

        OahuList<Pair?> pairs = [new Pair(new Empty(), new Empty()), null];
        writer.WriteList(pairs, static (w, pair) =>
        {
            w.WritePresence(pair is not null);
            if (pair is not null)
                Pair.Write(w, pair);
        });

        Assert.Equal("02" + "01" + "00", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = reader.ReadList<Pair?>(static (ref OahuReader r) => r.ReadPresence() ? Pair.Read(ref r) : null);
        reader.EnsureEnd();

        Assert.Equal(2, read.Count);
        Assert.NotNull(read[0]);
        Assert.Null(read[1]);
    }

    [Fact]
    public void A_union_with_one_case_still_writes_its_index()
    {
        var writer = new OahuWriter();
        Wrapper.Write(writer, new Wrapper.Only(1));

        Assert.Equal("00" + "02", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.Equal(1, Assert.IsType<Wrapper.Only>(Wrapper.Read(ref reader)).Value);
        reader.EnsureEnd();

        var rejected = Assert.Throws<OahuFormatException>(() =>
        {
            var other = new OahuReader(Hex.Bytes("0102"));
            Wrapper.Read(ref other);
        });
        Assert.Equal(0, rejected.Offset);
    }

    [Fact]
    public void A_list_of_cases_with_no_fields_is_its_count_then_each_index()
    {
        OahuList<Screen> screens = [new Screen.Loading(), new Screen.Loading()];
        var writer = new OahuWriter();
        writer.WriteList(screens, Screen.Write);

        Assert.Equal("02" + "00" + "00", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = reader.ReadList(Screen.Read);
        reader.EnsureEnd();

        Assert.Equal(2, read.Count);
        Assert.All(read, screen => Assert.IsType<Screen.Loading>(screen));
    }

    [Theory]
    [InlineData(false, "00")]
    [InlineData(true, "01" + "00")]
    public void A_nullable_union_is_a_presence_byte_then_the_union_when_present(bool present, string hex)
    {
        Screen? screen = present ? new Screen.Loading() : null;
        var writer = new OahuWriter();
        writer.WritePresence(screen is not null);
        if (screen is not null)
            Screen.Write(writer, screen);

        Assert.Equal(hex, Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = reader.ReadPresence() ? Screen.Read(ref reader) : null;
        reader.EnsureEnd();

        if (present)
            Assert.IsType<Screen.Loading>(read);
        else
            Assert.Null(read);
    }

    [Fact]
    public void A_union_case_index_is_read_from_a_longer_form_than_the_shortest()
    {
        var reader = new OahuReader(Hex.Bytes("8000"));
        Assert.IsType<Screen.Loading>(Screen.Read(ref reader));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_case_s_fields_are_its_own_parameters_and_neither_its_base_s_parameter_nor_a_body_property_is_written()
    {
        // Header(string Key, string Title) : Row(Key): Key is written once, as Header's first field; Row's own Key and
        // Header's computed Label are not written.
        var header = new Row.Header("h", "Today");
        Assert.Equal("h: Today", header.Label);

        var writer = new OahuWriter();
        Row.Write(writer, header);

        Assert.Equal("00" + "0168" + "05546f646179", Hex.Of(writer.WrittenSpan));

        writer.Reset();
        Row.Write(writer, new Row.Line("k", "x"));

        Assert.Equal("01" + "016b" + "0178", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(Hex.Bytes("00" + "0168" + "05546f646179"));
        var read = Assert.IsType<Row.Header>(Row.Read(ref reader));
        reader.EnsureEnd();

        Assert.Equal("h", read.Key);
        Assert.Equal("Today", read.Title);
    }

    private static void EnterLevels(OahuWriter writer, int levels)
    {
        for (var level = 0; level < levels; level++)
            writer.EnterNested();
    }

    private static Screen SampleScreen(string name) => name switch
    {
        "loading" => new Screen.Loading(),
        "failed" => new Screen.Failed("x"),
        "failed-empty" => new Screen.Failed(""),
        "ready" => new Screen.Ready(new Point(1, -1), [1, 3]),
        "ready-empty" => new Screen.Ready(new Point(0, 0), []),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    // enum Priority { High = 10, Low = 5, Normal = 7 }: declared in that order, so its indexes are 0, 1 and 2.
    public enum Priority
    {
        High = 10,
        Low = 5,
        Normal = 7,
    }

    // The codec a generator writes for Priority: the index of the value alone, with no nesting depth.
    private static class PriorityCodec
    {
        private const int ValueCount = 3;

        public static void Write(OahuWriter writer, Priority value) => writer.WriteIndex(
            value switch
            {
                Priority.High => 0,
                Priority.Low => 1,
                Priority.Normal => 2,
                _ => throw new InvalidOperationException($"{value} is not a declared Priority."),
            },
            ValueCount);

        public static Priority Read(ref OahuReader reader) => reader.ReadIndex(ValueCount) switch
        {
            0 => Priority.High,
            1 => Priority.Low,
            2 => Priority.Normal,
            _ => throw new UnreachableException(),
        };
    }

    // record Item(string Name, int Quantity, bool Done, double Weight, DateTimeOffset? Due, Priority Priority,
    // OahuList<string> Tags), with the codec a generator writes for it.
    private sealed record Item(
        string Name,
        int Quantity,
        bool Done,
        double Weight,
        DateTimeOffset? Due,
        Priority Priority,
        OahuList<string> Tags)
    {
        public static void Write(OahuWriter writer, Item item)
        {
            writer.EnterNested();
            writer.WriteString(item.Name);
            writer.WriteInt(item.Quantity);
            writer.WriteBool(item.Done);
            writer.WriteDouble(item.Weight);
            writer.WritePresence(item.Due.HasValue);
            if (item.Due is { } due)
                writer.WriteInstant(due);
            PriorityCodec.Write(writer, item.Priority);
            writer.WriteList(item.Tags, static (w, tag) => w.WriteString(tag));
            writer.ExitNested();
        }

        public static Item Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var name = reader.ReadString();
            var quantity = reader.ReadInt();
            var done = reader.ReadBool();
            var weight = reader.ReadDouble();
            DateTimeOffset? due = reader.ReadPresence() ? reader.ReadInstant() : null;
            var priority = PriorityCodec.Read(ref reader);
            var tags = reader.ReadList(static (ref OahuReader r) => r.ReadString());
            reader.ExitNested();
            return new Item(name, quantity, done, weight, due, priority, tags);
        }
    }

    // record Empty(): it enters and exits like any other record, and writes nothing.
    private sealed record Empty
    {
        public static void Write(OahuWriter writer, Empty empty)
        {
            writer.EnterNested();
            writer.ExitNested();
        }

        public static Empty Read(ref OahuReader reader)
        {
            reader.EnterNested();
            reader.ExitNested();
            return new Empty();
        }
    }

    // record Tagged(Empty Marker, int Value).
    private sealed record Tagged(Empty Marker, int Value)
    {
        public static void Write(OahuWriter writer, Tagged tagged)
        {
            writer.EnterNested();
            Empty.Write(writer, tagged.Marker);
            writer.WriteInt(tagged.Value);
            writer.ExitNested();
        }

        public static Tagged Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var marker = Empty.Read(ref reader);
            var value = reader.ReadInt();
            reader.ExitNested();
            return new Tagged(marker, value);
        }
    }

    // record Pair(Empty First, Empty Second): every field is no bytes, so the record is no bytes too.
    private sealed record Pair(Empty First, Empty Second)
    {
        public static void Write(OahuWriter writer, Pair pair)
        {
            writer.EnterNested();
            Empty.Write(writer, pair.First);
            Empty.Write(writer, pair.Second);
            writer.ExitNested();
        }

        public static Pair Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var first = Empty.Read(ref reader);
            var second = Empty.Read(ref reader);
            reader.ExitNested();
            return new Pair(first, second);
        }
    }

    // union Wrapper = Only(int Value): its one case's index, 0, is still written.
    private abstract record Wrapper
    {
        private const int CaseCount = 1;

        public static void Write(OahuWriter writer, Wrapper wrapper)
        {
            writer.EnterNested();
            switch (wrapper)
            {
                case Only only:
                    writer.WriteIndex(0, CaseCount);
                    writer.WriteInt(only.Value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(wrapper), wrapper, "The case is not declared.");
            }

            writer.ExitNested();
        }

        public static Wrapper Read(ref OahuReader reader)
        {
            reader.EnterNested();
            Wrapper wrapper = reader.ReadIndex(CaseCount) switch
            {
                0 => new Only(reader.ReadInt()),
                _ => throw new UnreachableException(),
            };
            reader.ExitNested();
            return wrapper;
        }

        public sealed record Only(int Value) : Wrapper;
    }

    // A union whose abstract record has a parameter of its own, Key. Each case's fields are its own primary
    // constructor's parameters, so a case passes Key on to Row and writes it once, as its own field.
    private abstract record Row(string Key)
    {
        private const int CaseCount = 2;

        public static void Write(OahuWriter writer, Row row)
        {
            writer.EnterNested();
            switch (row)
            {
                case Header header:
                    writer.WriteIndex(0, CaseCount);
                    writer.WriteString(header.Key);
                    writer.WriteString(header.Title);
                    break;
                case Line line:
                    writer.WriteIndex(1, CaseCount);
                    writer.WriteString(line.Key);
                    writer.WriteString(line.Text);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(row), row, "The case is not declared.");
            }

            writer.ExitNested();
        }

        public static Row Read(ref OahuReader reader)
        {
            reader.EnterNested();
            Row row = reader.ReadIndex(CaseCount) switch
            {
                0 => new Header(reader.ReadString(), reader.ReadString()),
                1 => new Line(reader.ReadString(), reader.ReadString()),
                _ => throw new UnreachableException(),
            };
            reader.ExitNested();
            return row;
        }

        // A computed property in the body: not a field, so never written.
        public sealed record Header(string Key, string Title) : Row(Key)
        {
            public string Label => $"{Key}: {Title}";
        }

        public sealed record Line(string Key, string Text) : Row(Key);
    }

    // record Point(int X, int Y).
    private sealed record Point(int X, int Y)
    {
        public static void Write(OahuWriter writer, Point point)
        {
            writer.EnterNested();
            writer.WriteInt(point.X);
            writer.WriteInt(point.Y);
            writer.ExitNested();
        }

        public static Point Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var x = reader.ReadInt();
            var y = reader.ReadInt();
            reader.ExitNested();
            return new Point(x, y);
        }
    }

    // union Screen = Loading() | Failed(string Message) | Ready(Point Origin, OahuList<int> Values), with the
    // codec a generator writes for it: it enters once for the union value and its case, writes the case's index, then
    // the case's fields.
    private abstract record Screen
    {
        private const int CaseCount = 3;

        public static void Write(OahuWriter writer, Screen screen)
        {
            writer.EnterNested();
            switch (screen)
            {
                case Loading:
                    writer.WriteIndex(0, CaseCount);
                    break;
                case Failed failed:
                    writer.WriteIndex(1, CaseCount);
                    writer.WriteString(failed.Message);
                    break;
                case Ready ready:
                    writer.WriteIndex(2, CaseCount);
                    Point.Write(writer, ready.Origin);
                    writer.WriteList(ready.Values, static (w, value) => w.WriteInt(value));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(screen), screen, "The case is not declared.");
            }

            writer.ExitNested();
        }

        public static Screen Read(ref OahuReader reader)
        {
            reader.EnterNested();
            Screen screen = reader.ReadIndex(CaseCount) switch
            {
                0 => new Loading(),
                1 => new Failed(reader.ReadString()),
                2 => new Ready(Point.Read(ref reader), reader.ReadList(static (ref OahuReader r) => r.ReadInt())),
                _ => throw new UnreachableException(),
            };
            reader.ExitNested();
            return screen;
        }

        public sealed record Loading : Screen;

        public sealed record Failed(string Message) : Screen;

        public sealed record Ready(Point Origin, OahuList<int> Values) : Screen;
    }

    // record Panel(string Title, Screen Screen).
    private sealed record Panel(string Title, Screen Screen)
    {
        public static void Write(OahuWriter writer, Panel panel)
        {
            writer.EnterNested();
            writer.WriteString(panel.Title);
            Screen.Write(writer, panel.Screen);
            writer.ExitNested();
        }

        public static Panel Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var title = reader.ReadString();
            var screen = Screen.Read(ref reader);
            reader.ExitNested();
            return new Panel(title, screen);
        }
    }
}
