using System.Diagnostics;
using System.Globalization;
using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Wire;

public sealed class NestingDepthTests
{
    // Each level of a chain with empty names is two bytes: the name's length, 00, then a list count or a presence
    // byte, 01. So the 65th value starts at byte offset 128.
    private const int TooDeepOffset = 2 * OahuFormat.MaxNestingDepth;

    private const string TooDeepMessage =
        "The value at byte offset 128 is at nesting depth 65, deeper than the largest nesting depth, 64.";

    private const string UnbalancedExitMessage = "ExitNested was called with no nested value entered.";

    [Fact]
    public void The_largest_nesting_depth_is_64()
    {
        Assert.Equal(64, OahuFormat.MaxNestingDepth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(63)]
    [InlineData(64)]
    public void A_record_with_a_list_of_itself_round_trips_up_to_the_largest_depth(int depth)
    {
        var writer = new OahuWriter();
        Tree.Write(writer, Tree.Deep(depth));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Tree.Read(ref reader);
        reader.EnsureEnd();

        Tree.AssertDeep(depth, read);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(63)]
    [InlineData(64)]
    public void A_record_linked_through_a_nullable_field_round_trips_up_to_the_largest_depth(int depth)
    {
        var writer = new OahuWriter();
        Link.Write(writer, Link.Chain(depth, numbered: true));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Link.Read(ref reader);
        reader.EnsureEnd();

        for (var level = 1; level <= depth; level++)
        {
            Assert.NotNull(read);
            Assert.Equal(level.ToString(CultureInfo.InvariantCulture), read.Name);
            read = read.Next;
        }

        Assert.Null(read);
    }

    [Fact]
    public void A_depth_of_one_is_the_record_alone()
    {
        var writer = new OahuWriter();
        Tree.Write(writer, new Tree("a", []));

        Assert.Equal("0161" + "00", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Tree.Read(ref reader);
        reader.EnsureEnd();

        Assert.Equal("a", read.Name);
        Assert.Empty(read.Children);
    }

    [Fact]
    public void The_writer_refuses_a_record_with_a_list_of_itself_at_depth_65_before_writing_any_of_it()
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<InvalidOperationException>(() => Tree.Write(writer, Tree.Chain(65)));

        Assert.Equal(TooDeepMessage, exception.Message);
        Assert.Equal(TooDeepOffset, writer.WrittenCount);
        Assert.Equal(ChainPrefixHex(OahuFormat.MaxNestingDepth), Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void The_writer_refuses_a_record_linked_through_a_nullable_field_at_depth_65_before_writing_any_of_it()
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Link.Write(writer, Link.Chain(65, numbered: false)));

        Assert.Equal(TooDeepMessage, exception.Message);
        Assert.Equal(TooDeepOffset, writer.WrittenCount);
        Assert.Equal(ChainPrefixHex(OahuFormat.MaxNestingDepth), Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void The_writer_refuses_a_very_deep_value_without_overflowing_the_stack()
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Link.Write(writer, Link.Chain(100_000, numbered: false)));

        Assert.Equal(TooDeepMessage, exception.Message);
        Assert.Equal(TooDeepOffset, writer.WrittenCount);
    }

    [Fact]
    public void The_reader_rejects_a_record_with_a_list_of_itself_at_depth_65_and_stays_at_its_start()
    {
        // Written by a codec that does not count depth, as a writer without the limit would write it.
        var writer = new OahuWriter();
        Tree.WriteUnchecked(writer, Tree.Chain(65));
        var input = writer.WrittenSpan.ToArray();
        Assert.Equal(ChainPrefixHex(OahuFormat.MaxNestingDepth) + "00" + "00", Hex.Of(input));

        var reader = new OahuReader(input);
        OahuFormatException? exception = null;
        try
        {
            Tree.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(TooDeepOffset, exception.Offset);
        Assert.Equal(TooDeepMessage, exception.Message);
        Assert.Equal(input.Length - TooDeepOffset, reader.Remaining);
        AssertReaderDepth(ref reader, OahuFormat.MaxNestingDepth);
    }

    [Fact]
    public void The_reader_rejects_a_record_linked_through_a_nullable_field_at_depth_65_and_stays_at_its_start()
    {
        var input = Hex.Bytes(ChainPrefixHex(OahuFormat.MaxNestingDepth) + "00" + "00");

        var reader = new OahuReader(input);
        OahuFormatException? exception = null;
        try
        {
            Link.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(TooDeepOffset, exception.Offset);
        Assert.Equal(TooDeepMessage, exception.Message);
        Assert.Equal(2, reader.Remaining);
        AssertReaderDepth(ref reader, OahuFormat.MaxNestingDepth);
    }

    [Fact]
    public void The_reader_rejects_very_deep_input_without_overflowing_the_stack()
    {
        // 100,000 levels of the linked form, each an empty name and a present next value, then the last value.
        var input = Hex.Bytes(ChainPrefixHex(100_000) + "00" + "00");

        var reader = new OahuReader(input);
        OahuFormatException? exception = null;
        try
        {
            Link.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(TooDeepOffset, exception.Offset);
        Assert.Equal(TooDeepMessage, exception.Message);
        Assert.Equal(input.Length - TooDeepOffset, reader.Remaining);
    }

    [Fact]
    public void A_wide_but_shallow_value_round_trips()
    {
        var children = new Tree[5_000];
        for (var i = 0; i < children.Length; i++)
            children[i] = new Tree(i.ToString(CultureInfo.InvariantCulture), []);

        var writer = new OahuWriter();
        Tree.Write(writer, new Tree("root", children.ToOahuList()));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Tree.Read(ref reader);
        reader.EnsureEnd();

        Assert.Equal("root", read.Name);
        Assert.Equal(children.Length, read.Children.Count);
        for (var i = 0; i < children.Length; i++)
        {
            Assert.Equal(children[i].Name, read.Children[i].Name);
            Assert.Empty(read.Children[i].Children);
        }
    }

    [Fact]
    public void The_depth_returns_to_zero_after_a_complete_value_so_the_next_value_starts_fresh()
    {
        var writer = new OahuWriter();
        Tree.Write(writer, Tree.Deep(OahuFormat.MaxNestingDepth));
        Tree.Write(writer, Tree.Deep(OahuFormat.MaxNestingDepth));

        var reader = new OahuReader(writer.WrittenSpan);
        Tree.AssertDeep(OahuFormat.MaxNestingDepth, Tree.Read(ref reader));
        Tree.AssertDeep(OahuFormat.MaxNestingDepth, Tree.Read(ref reader));
        reader.EnsureEnd();
    }

    [Fact]
    public void Reset_returns_the_writer_to_depth_zero()
    {
        var writer = new OahuWriter();
        for (var i = 0; i < OahuFormat.MaxNestingDepth; i++)
            writer.EnterNested();
        Assert.Throws<InvalidOperationException>(writer.EnterNested);

        writer.Reset();

        var exception = Assert.Throws<InvalidOperationException>(writer.ExitNested);
        Assert.Equal(UnbalancedExitMessage, exception.Message);
        Tree.Write(writer, Tree.Deep(OahuFormat.MaxNestingDepth));
    }

    [Fact]
    public void A_writer_that_refused_a_value_writes_again_after_a_reset()
    {
        var writer = new OahuWriter();
        Assert.Throws<InvalidOperationException>(() => Tree.Write(writer, Tree.Chain(65)));

        writer.Reset();
        Tree.Write(writer, new Tree("a", []));

        Assert.Equal("0161" + "00", Hex.Of(writer.WrittenSpan));
    }

    [Fact]
    public void Exiting_the_writer_with_no_value_entered_is_an_error()
    {
        var writer = new OahuWriter();
        var exception = Assert.Throws<InvalidOperationException>(writer.ExitNested);
        Assert.Equal(UnbalancedExitMessage, exception.Message);

        writer.EnterNested();
        writer.ExitNested();
        exception = Assert.Throws<InvalidOperationException>(writer.ExitNested);
        Assert.Equal(UnbalancedExitMessage, exception.Message);
    }

    [Fact]
    public void Exiting_the_reader_with_no_value_entered_is_an_error()
    {
        var reader = new OahuReader([]);
        AssertReaderDepth(ref reader, 0);

        reader.EnterNested();
        AssertReaderDepth(ref reader, 1);
    }

    [Fact]
    public void The_writer_completion_check_rejects_a_top_level_value_still_entered()
    {
        var writer = new OahuWriter();
        writer.EnterNested();
        writer.WriteInt(42);

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = writer.GetCompletedSpan();
        });

        Assert.Equal(
            "1 nested value(s) entered with EnterNested have not been exited with ExitNested.", exception.Message);
        Assert.Equal("54", Hex.Of(writer.WrittenSpan));

        writer.ExitNested();

        Assert.Equal("54", Hex.Of(writer.GetCompletedSpan()));
    }

    [Fact]
    public void The_writer_completion_check_accepts_an_empty_writer()
    {
        var writer = new OahuWriter();

        Assert.True(writer.GetCompletedSpan().IsEmpty);
    }

    [Fact]
    public void The_writer_completion_check_accepts_a_writer_reset_after_a_value_too_deep()
    {
        var writer = new OahuWriter();
        Assert.Throws<InvalidOperationException>(() => Tree.Write(writer, Tree.Chain(65)));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = writer.GetCompletedSpan();
        });

        writer.Reset();

        Assert.True(writer.GetCompletedSpan().IsEmpty);
    }

    private const string SequenceInProgressMessage =
        "A list or batch is still being written, or one threw and the writer was not reset since.";

    [Fact]
    public void The_writer_completion_check_rejects_a_call_from_an_item_writer_of_a_top_level_list()
    {
        var writer = new OahuWriter();
        InvalidOperationException? exception = null;

        writer.WriteList(new[] { 1, 2 }.ToOahuList(), (w, item) =>
        {
            w.WriteInt(item);
            if (item == 1)
                exception = Assert.Throws<InvalidOperationException>(() => { _ = w.GetCompletedSpan(); });
        });

        Assert.Equal(SequenceInProgressMessage, exception?.Message);
        Assert.Equal("02" + "02" + "04", Hex.Of(writer.GetCompletedSpan()));
    }

    [Fact]
    public void The_writer_completion_check_rejects_a_call_from_a_row_writer_of_a_batch()
    {
        var writer = new OahuWriter();
        InvalidOperationException? exception = null;

        writer.WriteBatch<BatchTests.TaskRow>([new ListOp<BatchTests.TaskRow>.Insert(0, new("a", "", false))], (w, row) =>
        {
            BatchTests.TaskRow.Write(w, row);
            exception = Assert.Throws<InvalidOperationException>(() => { _ = w.GetCompletedSpan(); });
        });

        Assert.Equal(SequenceInProgressMessage, exception?.Message);
        Assert.False(writer.GetCompletedSpan().IsEmpty);
    }

    [Fact]
    public void The_writer_completion_check_rejects_a_list_that_threw_until_the_writer_is_reset()
    {
        var writer = new OahuWriter();
        Assert.Throws<InvalidOperationException>(() =>
            writer.WriteList(new[] { 1 }.ToOahuList(), static (_, _) => { }));
        var exception = Assert.Throws<InvalidOperationException>(() => { _ = writer.GetCompletedSpan(); });
        Assert.Equal(SequenceInProgressMessage, exception.Message);

        writer.Reset();

        Assert.True(writer.GetCompletedSpan().IsEmpty);
    }

    [Fact]
    public void The_end_check_rejects_a_value_still_entered()
    {
        var reader = new OahuReader([]);
        reader.EnterNested();
        reader.EnterNested();

        var exception = AssertEnsureEndThrows(ref reader);
        Assert.Equal(
            "2 nested value(s) entered with EnterNested have not been exited with ExitNested.", exception.Message);

        reader.ExitNested();
        reader.ExitNested();
        reader.EnsureEnd();
    }

    [Fact]
    public void The_end_check_reports_a_value_still_entered_before_bytes_left_over()
    {
        var reader = new OahuReader(Hex.Bytes("00"));
        reader.EnterNested();

        var exception = AssertEnsureEndThrows(ref reader);
        Assert.Equal(
            "1 nested value(s) entered with EnterNested have not been exited with ExitNested.", exception.Message);
    }

    [Fact]
    public void An_item_writer_that_enters_without_exiting_is_rejected_by_its_list_and_not_as_too_deep()
    {
        // A flat list of 70 items: more items than the largest depth.
        var writer = new OahuWriter();
        var items = Enumerable.Range(0, 70).ToOahuList();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteList(items, static (w, item) =>
            {
                w.EnterNested();
                w.WriteInt(item);
            }));

        Assert.Equal(
            "An item of the list left the nesting depth at 1, not 0 as before it. Every item must call ExitNested " +
            "once for each EnterNested.",
            exception.Message);
    }

    [Fact]
    public void An_item_writer_that_exits_the_value_around_its_list_is_rejected_by_the_list()
    {
        var writer = new OahuWriter();
        writer.EnterNested();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteList([1], static (w, item) =>
            {
                w.WriteInt(item);
                w.ExitNested();
            }));

        Assert.Equal(
            "An item of the list left the nesting depth at 0, not 1 as before it. Every item must call ExitNested " +
            "once for each EnterNested.",
            exception.Message);
    }

    [Fact]
    public void An_item_writer_that_resets_the_writer_inside_a_record_is_rejected_as_a_reset()
    {
        var writer = new OahuWriter();
        writer.EnterNested();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteList([1], static (w, item) =>
            {
                w.EnterNested();
                w.Reset();
                w.WriteInt(item);
            }));

        Assert.Equal("An item writer reset the writer while its list was being written.", exception.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(63)]
    [InlineData(64)]
    public void A_union_and_the_case_it_holds_are_one_level_up_to_the_largest_depth(int depth)
    {
        var writer = new OahuWriter();
        Shape.Write(writer, Shape.Chain(depth));

        // Each level above the deepest is its case index and a list count or a presence byte; the deepest is a leaf.
        Assert.Equal(2 * (depth - 1) + 1, writer.WrittenCount);

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Shape.Read(ref reader);
        reader.EnsureEnd();

        Shape.AssertChain(depth, read);
    }

    [Fact]
    public void The_writer_refuses_a_union_at_depth_65_before_writing_any_of_it()
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<InvalidOperationException>(() => Shape.Write(writer, Shape.Chain(65)));

        Assert.Equal(TooDeepMessage, exception.Message);
        Assert.Equal(TooDeepOffset, writer.WrittenCount);
    }

    [Fact]
    public void The_reader_rejects_a_union_at_depth_65_and_stays_at_its_start()
    {
        var writer = new OahuWriter();
        Shape.WriteUnchecked(writer, Shape.Chain(65));
        var input = writer.WrittenSpan.ToArray();
        Assert.Equal(TooDeepOffset + 1, input.Length);

        var reader = new OahuReader(input);
        OahuFormatException? exception = null;
        try
        {
            Shape.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(TooDeepOffset, exception.Offset);
        Assert.Equal(TooDeepMessage, exception.Message);
        Assert.Equal(1, reader.Remaining);
        AssertReaderDepth(ref reader, OahuFormat.MaxNestingDepth);
    }

    [Fact]
    public void A_value_that_nests_through_lists_and_nullable_links_round_trips_at_the_largest_depth()
    {
        var writer = new OahuWriter();
        Knot.Write(writer, Knot.Chain(OahuFormat.MaxNestingDepth));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Knot.Read(ref reader);
        reader.EnsureEnd();

        Knot.AssertChain(OahuFormat.MaxNestingDepth, read);
    }

    [Fact]
    public void A_value_that_nests_through_lists_and_nullable_links_is_refused_and_rejected_at_depth_65()
    {
        const string message =
            "The value at byte offset 160 is at nesting depth 65, deeper than the largest nesting depth, 64.";

        var writer = new OahuWriter();
        var refused = Assert.Throws<InvalidOperationException>(() => Knot.Write(writer, Knot.Chain(65)));
        Assert.Equal(message, refused.Message);
        Assert.Equal(Knot.TooDeepOffset, writer.WrittenCount);

        writer.Reset();
        Knot.WriteUnchecked(writer, Knot.Chain(65));
        var reader = new OahuReader(writer.WrittenSpan);
        OahuFormatException? exception = null;
        try
        {
            Knot.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(Knot.TooDeepOffset, exception.Offset);
        Assert.Equal(message, exception.Message);
        AssertReaderDepth(ref reader, OahuFormat.MaxNestingDepth);
    }

    [Fact]
    public void A_record_that_never_contains_itself_counts_as_a_level()
    {
        // The Origin of the deepest node of a 63-node chain is at depth 64.
        var writer = new OahuWriter();
        Placed.Write(writer, Placed.Chain(OahuFormat.MaxNestingDepth - 1));

        var reader = new OahuReader(writer.WrittenSpan);
        var read = Placed.Read(ref reader);
        reader.EnsureEnd();

        Placed.AssertChain(OahuFormat.MaxNestingDepth - 1, read);
    }

    [Fact]
    public void The_writer_refuses_the_point_of_a_node_at_depth_64()
    {
        var writer = new OahuWriter();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Placed.Write(writer, Placed.Chain(OahuFormat.MaxNestingDepth)));

        Assert.Equal(Placed.TooDeepMessage, exception.Message);
        Assert.Equal(Placed.TooDeepOffset, writer.WrittenCount);
    }

    [Fact]
    public void The_reader_rejects_the_point_of_a_node_at_depth_64()
    {
        var writer = new OahuWriter();
        Placed.WriteUnchecked(writer, Placed.Chain(OahuFormat.MaxNestingDepth));
        var input = writer.WrittenSpan.ToArray();

        var reader = new OahuReader(input);
        OahuFormatException? exception = null;
        try
        {
            Placed.Read(ref reader);
        }
        catch (OahuFormatException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(Placed.TooDeepOffset, exception.Offset);
        Assert.Equal(Placed.TooDeepMessage, exception.Message);
        Assert.Equal(input.Length - Placed.TooDeepOffset, reader.Remaining);
        AssertReaderDepth(ref reader, OahuFormat.MaxNestingDepth);
    }

    [Fact]
    public void An_item_reader_that_enters_without_exiting_is_rejected_by_its_list_and_not_as_too_deep()
    {
        // A flat list of 70 booleans: more items than the largest depth.
        var input = Hex.Bytes("46" + string.Concat(Enumerable.Repeat("00", 70)));

        var reader = new OahuReader(input);
        InvalidOperationException? exception = null;
        try
        {
            reader.ReadList(static (ref OahuReader r) =>
            {
                r.EnterNested();
                return r.ReadBool();
            });
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(
            "An item of the list at byte offset 0 left the nesting depth at 1, not 0 as before it. Every item must " +
            "call ExitNested once for each EnterNested.",
            exception.Message);
    }

    [Fact]
    public void An_item_reader_that_exits_the_value_around_its_list_is_rejected_by_the_list()
    {
        var input = Hex.Bytes("00" + "01" + "00");

        var reader = new OahuReader(input);
        reader.ReadBool();
        reader.EnterNested();
        InvalidOperationException? exception = null;
        try
        {
            reader.ReadList(static (ref OahuReader r) =>
            {
                var item = r.ReadBool();
                r.ExitNested();
                return item;
            });
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(
            "An item of the list at byte offset 1 left the nesting depth at 0, not 1 as before it. Every item must " +
            "call ExitNested once for each EnterNested.",
            exception.Message);
    }

    [Fact]
    public void Entering_and_exiting_allocates_nothing()
    {
        var writer = new OahuWriter();
        Assert.Equal(0, Allocations.Smallest(() =>
        {
            writer.EnterNested();
            writer.ExitNested();
        }));

        var input = Hex.Bytes("00");
        Assert.Equal(0, Allocations.Smallest(() =>
        {
            var reader = new OahuReader(input);
            reader.EnterNested();
            reader.ReadBool();
            reader.ExitNested();
            reader.EnsureEnd();
        }));
    }

    // The bytes of the first levels of a chain with empty names: each level is its name, 00, then 01, a list count of
    // one or the presence byte of the next value.
    private static string ChainPrefixHex(int levels) => string.Concat(Enumerable.Repeat("0001", levels));

    // Checks the reader's depth through its behaviour: it exits that many times, then refuses one more.
    private static void AssertReaderDepth(ref OahuReader reader, int depth)
    {
        for (var i = 0; i < depth; i++)
            reader.ExitNested();

        InvalidOperationException? exception = null;
        try
        {
            reader.ExitNested();
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal(UnbalancedExitMessage, exception.Message);
    }

    private static InvalidOperationException AssertEnsureEndThrows(ref OahuReader reader)
    {
        InvalidOperationException? exception = null;
        try
        {
            reader.EnsureEnd();
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        return exception;
    }

    // A record with a name and a list of itself, with the codec a generator would write for it. A class, not a record,
    // so that nothing compares or prints a deep value recursively.
    private sealed class Tree(string name, OahuList<Tree> children)
    {
        public string Name { get; } = name;

        public OahuList<Tree> Children { get; } = children;

        public static void Write(OahuWriter writer, Tree tree)
        {
            writer.EnterNested();
            writer.WriteString(tree.Name);
            writer.WriteList(tree.Children, Write);
            writer.ExitNested();
        }

        // The same bytes as Write, without counting depth.
        public static void WriteUnchecked(OahuWriter writer, Tree tree)
        {
            writer.WriteString(tree.Name);
            writer.WriteList(tree.Children, WriteUnchecked);
        }

        public static Tree Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var name = reader.ReadString();
            var children = reader.ReadList(Read);
            reader.ExitNested();
            return new Tree(name, children);
        }

        // A value of the given depth: each level is named by its depth and, above the deepest, has two children, the
        // next level and a leaf, which is one level deeper than its parent.
        public static Tree Deep(int depth)
        {
            var tree = new Tree(depth.ToString(CultureInfo.InvariantCulture), []);
            for (var level = depth - 1; level >= 1; level--)
                tree = new Tree(level.ToString(CultureInfo.InvariantCulture), [tree, new Tree("leaf", [])]);
            return tree;
        }

        public static void AssertDeep(int depth, Tree tree)
        {
            for (var level = 1; level < depth; level++)
            {
                Assert.Equal(level.ToString(CultureInfo.InvariantCulture), tree.Name);
                Assert.Equal(2, tree.Children.Count);
                Assert.Equal("leaf", tree.Children[1].Name);
                Assert.Empty(tree.Children[1].Children);
                tree = tree.Children[0];
            }

            Assert.Equal(depth.ToString(CultureInfo.InvariantCulture), tree.Name);
            Assert.Empty(tree.Children);
        }

        // A value of the given depth with empty names, each level with one child.
        public static Tree Chain(int depth)
        {
            var tree = new Tree("", []);
            for (var level = depth - 1; level >= 1; level--)
                tree = new Tree("", [tree]);
            return tree;
        }
    }

    // A record linked to the next through a nullable field, which nests without any list, with the codec a generator
    // would write for it.
    private sealed class Link(string name, Link? next)
    {
        public string Name { get; } = name;

        public Link? Next { get; } = next;

        public static void Write(OahuWriter writer, Link link)
        {
            writer.EnterNested();
            writer.WriteString(link.Name);
            writer.WritePresence(link.Next is not null);
            if (link.Next is not null)
                Write(writer, link.Next);
            writer.ExitNested();
        }

        public static Link Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var name = reader.ReadString();
            var next = reader.ReadPresence() ? Read(ref reader) : null;
            reader.ExitNested();
            return new Link(name, next);
        }

        // A chain of the given depth, named by depth or with empty names.
        public static Link Chain(int depth, bool numbered)
        {
            Link? link = null;
            for (var level = depth; level >= 1; level--)
                link = new Link(numbered ? level.ToString(CultureInfo.InvariantCulture) : "", link);
            return link!;
        }
    }

    // A union with a case of no fields, a case holding a list of the union and a case holding a nullable value of the
    // union, with the codec a generator would write for it: it enters once for the union value and not again for its
    // case. A case is written as its index, then its fields.
    private sealed class Shape
    {
        private const int CaseCount = 3;

        private Shape(int caseIndex, OahuList<Shape> shapes, Shape? inner)
        {
            CaseIndex = caseIndex;
            Shapes = shapes;
            Inner = inner;
        }

        // 0 is Leaf(), 1 is Group(OahuList<Shape> Shapes) and 2 is Wrap(Shape? Inner).
        public int CaseIndex { get; }

        public OahuList<Shape> Shapes { get; }

        public Shape? Inner { get; }

        public static Shape Leaf() => new(0, [], null);

        public static Shape Group(OahuList<Shape> shapes) => new(1, shapes, null);

        public static Shape Wrap(Shape? inner) => new(2, [], inner);

        public static void Write(OahuWriter writer, Shape shape)
        {
            writer.EnterNested();
            WriteCase(writer, shape, Write);
            writer.ExitNested();
        }

        // The same bytes as Write, without counting depth.
        public static void WriteUnchecked(OahuWriter writer, Shape shape) =>
            WriteCase(writer, shape, WriteUnchecked);

        public static Shape Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var shape = reader.ReadIndex(CaseCount) switch
            {
                0 => Leaf(),
                1 => Group(reader.ReadList(Read)),
                2 => Wrap(reader.ReadPresence() ? Read(ref reader) : null),
                _ => throw new UnreachableException(),
            };
            reader.ExitNested();
            return shape;
        }

        // A value of the given depth: the levels above the deepest alternate between a Group of one shape and a Wrap
        // of a present shape, and the deepest is a Leaf.
        public static Shape Chain(int depth)
        {
            var shape = Leaf();
            for (var level = depth - 1; level >= 1; level--)
                shape = level % 2 == 1 ? Group([shape]) : Wrap(shape);
            return shape;
        }

        public static void AssertChain(int depth, Shape shape)
        {
            for (var level = 1; level < depth; level++)
            {
                if (level % 2 == 1)
                {
                    Assert.Equal(1, shape.CaseIndex);
                    shape = Assert.Single(shape.Shapes);
                }
                else
                {
                    Assert.Equal(2, shape.CaseIndex);
                    Assert.NotNull(shape.Inner);
                    shape = shape.Inner;
                }
            }

            Assert.Equal(0, shape.CaseIndex);
        }

        private static void WriteCase(OahuWriter writer, Shape shape, Action<OahuWriter, Shape> writeShape)
        {
            writer.WriteIndex(shape.CaseIndex, CaseCount);
            if (shape.CaseIndex == 1)
            {
                writer.WriteList(shape.Shapes, writeShape);
            }
            else if (shape.CaseIndex == 2)
            {
                writer.WritePresence(shape.Inner is not null);
                if (shape.Inner is not null)
                    writeShape(writer, shape.Inner);
            }
        }
    }

    // A record with a list of nullable values of itself and a nullable value of itself, so that a value can nest
    // through a list and a nullable value in turn, with the codec a generator would write for it.
    private sealed class Knot(OahuList<Knot?> items, Knot? next)
    {
        // An odd level is a list count of two, an absent item and a present one: three bytes before the next level.
        // An even level is an empty list and a present next value: two bytes. So the 65th value starts at 32 × 5.
        public const int TooDeepOffset = 160;

        public OahuList<Knot?> Items { get; } = items;

        public Knot? Next { get; } = next;

        public static void Write(OahuWriter writer, Knot knot)
        {
            writer.EnterNested();
            WriteFields(writer, knot, Write);
            writer.ExitNested();
        }

        // The same bytes as Write, without counting depth.
        public static void WriteUnchecked(OahuWriter writer, Knot knot) => WriteFields(writer, knot, WriteUnchecked);

        public static Knot Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var items = reader.ReadList<Knot?>(static (ref OahuReader r) => r.ReadPresence() ? Read(ref r) : null);
            var next = reader.ReadPresence() ? Read(ref reader) : null;
            reader.ExitNested();
            return new Knot(items, next);
        }

        // A value of the given depth: an odd level holds the next level as the second item of its list, an even level
        // as its next value.
        public static Knot Chain(int depth)
        {
            var knot = new Knot([], null);
            for (var level = depth - 1; level >= 1; level--)
                knot = level % 2 == 1 ? new Knot([null, knot], null) : new Knot([], knot);
            return knot;
        }

        public static void AssertChain(int depth, Knot knot)
        {
            for (var level = 1; level < depth; level++)
            {
                if (level % 2 == 1)
                {
                    Assert.Equal(2, knot.Items.Count);
                    Assert.Null(knot.Items[0]);
                    Assert.Null(knot.Next);
                    Assert.NotNull(knot.Items[1]);
                    knot = knot.Items[1]!;
                }
                else
                {
                    Assert.Empty(knot.Items);
                    Assert.NotNull(knot.Next);
                    knot = knot.Next;
                }
            }

            Assert.Empty(knot.Items);
            Assert.Null(knot.Next);
        }

        private static void WriteFields(OahuWriter writer, Knot knot, Action<OahuWriter, Knot> writeKnot)
        {
            writer.WriteList(knot.Items, (w, item) =>
            {
                w.WritePresence(item is not null);
                if (item is not null)
                    writeKnot(w, item);
            });
            writer.WritePresence(knot.Next is not null);
            if (knot.Next is not null)
                writeKnot(writer, knot.Next);
        }
    }

    // A record that never contains itself, with the codec a generator would write for it.
    private sealed class Point(double x, double y)
    {
        public double X { get; } = x;

        public double Y { get; } = y;

        public static void Write(OahuWriter writer, Point point)
        {
            writer.EnterNested();
            writer.WriteDouble(point.X);
            writer.WriteDouble(point.Y);
            writer.ExitNested();
        }

        public static void WriteUnchecked(OahuWriter writer, Point point)
        {
            writer.WriteDouble(point.X);
            writer.WriteDouble(point.Y);
        }

        public static Point Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var x = reader.ReadDouble();
            var y = reader.ReadDouble();
            reader.ExitNested();
            return new Point(x, y);
        }
    }

    // A node with a Point and a list of itself: record Node(Point Origin, OahuList<Node> Children).
    private sealed class Placed(Point origin, OahuList<Placed> children)
    {
        // Each node above the 64th is its Origin, 16 bytes, and a list count of one, so the Origin of the 64th node,
        // at depth 65, starts at 63 × 17.
        public const int TooDeepOffset = 63 * 17;

        public const string TooDeepMessage =
            "The value at byte offset 1071 is at nesting depth 65, deeper than the largest nesting depth, 64.";

        public Point Origin { get; } = origin;

        public OahuList<Placed> Children { get; } = children;

        public static void Write(OahuWriter writer, Placed node)
        {
            writer.EnterNested();
            Point.Write(writer, node.Origin);
            writer.WriteList(node.Children, Write);
            writer.ExitNested();
        }

        // The same bytes as Write, without counting depth.
        public static void WriteUnchecked(OahuWriter writer, Placed node)
        {
            Point.WriteUnchecked(writer, node.Origin);
            writer.WriteList(node.Children, WriteUnchecked);
        }

        public static Placed Read(ref OahuReader reader)
        {
            reader.EnterNested();
            var origin = Point.Read(ref reader);
            var children = reader.ReadList(Read);
            reader.ExitNested();
            return new Placed(origin, children);
        }

        // A chain of the given number of nodes, each with its level as its Origin's X.
        public static Placed Chain(int nodes)
        {
            var node = new Placed(new Point(nodes, 0.5), []);
            for (var level = nodes - 1; level >= 1; level--)
                node = new Placed(new Point(level, 0.5), [node]);
            return node;
        }

        public static void AssertChain(int nodes, Placed node)
        {
            for (var level = 1; level < nodes; level++)
            {
                Assert.Equal(level, node.Origin.X);
                Assert.Equal(0.5, node.Origin.Y);
                node = Assert.Single(node.Children);
            }

            Assert.Equal(nodes, node.Origin.X);
            Assert.Empty(node.Children);
        }
    }
}
