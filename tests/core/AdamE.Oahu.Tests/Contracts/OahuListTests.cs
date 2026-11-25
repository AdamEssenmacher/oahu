using System.Collections;
using AdamE.Oahu.Tests.Wire;
using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Contracts;

public sealed class OahuListTests
{
    // Keep each list made in the allocation tests reachable, so that the runtime cannot drop the work. A list is kept
    // in a field of its own type, because storing it as an object would box it.
    private static OahuList<int> _ints;
    private static object? _sink;
    private static int _number;
    private static bool _flag;

    public sealed record Tagged(string Name, OahuList<string> Tags);

    public sealed record Holder(OahuList<int>? Numbers);

    public sealed record Reading(double Value, DateTimeOffset At);

    public readonly record struct Branch(string Name, OahuList<Branch> Children);

    public abstract record Screen
    {
        public sealed record Loading : Screen;

        public sealed record Ready(string Title, OahuList<Tagged> Items) : Screen;
    }

    // Construction

    [Fact]
    public void A_collection_expression_builds_a_list_of_its_items_in_order()
    {
        OahuList<string> tags = ["a", "b", "c"];

        Assert.Equal(3, tags.Count);
        Assert.False(tags.IsEmpty);
        Assert.Equal("a", tags[0]);
        Assert.Equal("b", tags[1]);
        Assert.Equal("c", tags[2]);
    }

    [Fact]
    public void A_collection_expression_can_spread_another_list()
    {
        OahuList<int> first = [1, 2];
        OahuList<int> both = [0, .. first, 3];

        AssertEqual([0, 1, 2, 3], both);
    }

    [Fact]
    public void The_empty_collection_expression_default_new_and_Empty_are_the_empty_list()
    {
        OahuList<int> fromExpression = [];
        OahuList<int> fromDefault = default;
        var fromNew = new OahuList<int>();
        var empty = OahuList<int>.Empty;

        foreach (var list in new[] { fromExpression, fromDefault, fromNew, empty, OahuList.Create<int>(), Array.Empty<int>().ToOahuList() })
        {
            Assert.True(list.Count == 0);
            Assert.True(list.IsEmpty);
            Assert.True(list.AsSpan().IsEmpty);
            Assert.Empty(list);
            Assert.Equal("[]", list.ToString());
            AssertEqual(empty, list);
        }
    }

    [Fact]
    public void Each_list_of_an_array_of_lists_starts_empty()
    {
        var lists = new OahuList<string>[2];

        Assert.True(lists[0].IsEmpty);
        AssertEqual([], lists[1]);
    }

    [Fact]
    public void A_record_s_default_list_field_is_the_empty_list()
    {
        Assert.True(new Tagged("a", []) == new Tagged("a", default));
    }

    [Fact]
    public void Create_takes_items_one_by_one_an_array_or_a_span()
    {
        int[] array = [1, 2, 3];
        ReadOnlySpan<int> span = [1, 2, 3];

        AssertEqual([1, 2, 3], OahuList.Create(1, 2, 3));
        AssertEqual([1, 2, 3], OahuList.Create(array));
        AssertEqual([1, 2, 3], OahuList.Create(span));
        AssertEqual([1, 2, 3], span.ToOahuList());
        AssertEqual([1, 2, 3], array.AsSpan().ToOahuList());
    }

    [Fact]
    public void CreateRange_and_ToOahuList_take_any_enumerable()
    {
        List<string> list = ["a", "b"];

        AssertEqual(["a", "b"], OahuList.CreateRange(list));
        AssertEqual(["a", "b"], list.ToOahuList());
        AssertEqual(["a", "b"], new[] { "a", "b" }.ToOahuList());
        AssertEqual([0, 1, 2], Enumerable.Range(0, 3).ToOahuList());
        AssertEqual([4, 16], Yield(4, 16).ToOahuList());
        AssertEqual([], Enumerable.Empty<int>().ToOahuList());
        AssertEqual<object>(["a", "b"], ((IEnumerable<object>)new[] { "a", "b" }).ToOahuList());
        AssertEqual([1, 2], new HashSet<int> { 1, 2 }.ToOahuList());
    }

    [Fact]
    public void CreateRange_of_a_null_enumerable_is_an_argument_error()
    {
        Assert.Throws<ArgumentNullException>(() => OahuList.CreateRange<int>(null!));
        Assert.Throws<ArgumentNullException>(() => ((IEnumerable<int>)null!).ToOahuList());
    }

    [Fact]
    public void CreateRange_of_a_boxed_list_returns_it_without_copying()
    {
        OahuList<int> list = [1, 2, 3];
        IEnumerable<int> boxed = list;

        AssertEqual(list, OahuList.CreateRange(boxed));
        Assert.Equal(0, Allocations.Smallest(() => _ints = OahuList.CreateRange(boxed)));
    }

    [Fact]
    public void Creating_an_empty_list_allocates_nothing()
    {
        Assert.Equal(0, Allocations.Smallest(() => _ints = OahuList.Create<int>()));
        Assert.Equal(0, Allocations.Smallest(() => _ints = []));
    }

    // Immutability

    [Fact]
    public void Changing_the_array_a_list_was_built_from_does_not_change_the_list()
    {
        int[] source = [1, 2, 3];
        var created = OahuList.Create(source);
        var fromSpan = source.AsSpan().ToOahuList();
        var fromEnumerable = source.ToOahuList();
        OahuList<int> spread = [.. source];

        source[0] = 9;
        Array.Resize(ref source, 1);

        AssertEqual([1, 2, 3], created);
        AssertEqual([1, 2, 3], fromSpan);
        AssertEqual([1, 2, 3], fromEnumerable);
        AssertEqual([1, 2, 3], spread);
    }

    [Fact]
    public void Changing_the_list_a_list_was_built_from_does_not_change_the_list()
    {
        List<string> source = ["a", "b"];
        var list = source.ToOahuList();

        source[0] = "z";
        source.Add("c");
        source.Clear();

        AssertEqual(["a", "b"], list);
    }

    [Fact]
    public void Changing_the_span_a_list_was_built_from_does_not_change_the_list()
    {
        Span<int> source = stackalloc int[] { 1, 2 };
        var list = OahuList.Create<int>(source);

        source[1] = 7;

        AssertEqual([1, 2], list);
    }

    [Fact]
    public void A_collection_that_keeps_the_array_it_copies_to_cannot_change_the_list()
    {
        var source = new KeepingCollection([1, 2, 3]);
        var created = OahuList.CreateRange(source);
        var converted = source.ToOahuList();
        OahuList<int> spread = [.. source];

        source.ChangeKeptArrays(9);

        AssertEqual([1, 2, 3], created);
        AssertEqual([1, 2, 3], converted);
        AssertEqual([1, 2, 3], spread);
    }

    [Fact]
    public void An_enumerable_built_by_LINQ_over_such_a_collection_cannot_change_the_list()
    {
        var source = new KeepingCollection([1, 2]);
        var concatenated = source.Concat(new KeepingCollection([3])).ToOahuList();
        var appended = source.Append(3).ToOahuList();
        var selected = source.Select(item => item).ToOahuList();

        source.ChangeKeptArrays(9);

        AssertEqual([1, 2, 3], concatenated);
        AssertEqual([1, 2, 3], appended);
        AssertEqual([1, 2], selected);
    }

    [Fact]
    public void A_collection_whose_count_is_wrong_still_gives_the_items_it_enumerates()
    {
        AssertEqual([1, 2, 3], new KeepingCollection([1, 2, 3], count: 1).ToOahuList());
        AssertEqual([1, 2, 3], new KeepingCollection([1, 2, 3], count: 7).ToOahuList());
        AssertEqual([], new KeepingCollection([], count: 2).ToOahuList());
    }

    [Fact]
    public void A_collection_that_reports_a_huge_count_gives_its_items_without_allocating_that_count()
    {
        AssertEqual([1, 2, 3], new KeepingCollection([1, 2, 3], count: int.MaxValue).ToOahuList());
        AssertEqual([1, 2, 3], new KeepingCollection([1, 2, 3], count: Array.MaxLength).ToOahuList());
    }

    [Fact]
    public void A_collection_that_reports_far_fewer_items_than_it_gives_still_gives_them_all()
    {
        var items = Enumerable.Range(0, 10_000).ToArray();

        AssertEqual(OahuList.Create(items), new KeepingCollection(items, count: 1).ToOahuList());
        AssertEqual(OahuList.Create(items), new KeepingCollection(items, count: 3).ToOahuList());
    }

    [Fact]
    public void A_collection_that_reports_more_items_than_it_gives_gives_only_its_items()
    {
        var aboveTheCap = OahuList.MostTrustedCount + 1;
        var items = Enumerable.Range(0, OahuList.MostTrustedCount + 10).ToArray();

        AssertEqual([1, 2, 3], new KeepingCollection([1, 2, 3], count: OahuList.MostTrustedCount).ToOahuList());
        AssertEqual([1, 2, 3], new KeepingCollection([1, 2, 3], count: aboveTheCap).ToOahuList());
        AssertEqual([1, 2, 3], new KeepingCollection([1, 2, 3], count: 10 * OahuList.MostTrustedCount).ToOahuList());
        AssertEqual(OahuList.Create(items), new KeepingCollection(items, count: 100_000).ToOahuList());
    }

    [Fact]
    public void A_large_collection_that_tells_its_count_is_copied_whole_and_kept_from_the_source()
    {
        var items = Enumerable.Range(0, 100_000).ToArray();
        var source = new KeepingCollection(items);

        var list = source.ToOahuList();
        source.ChangeKeptArrays(-1);

        Assert.Equal(100_000, list.Count);
        Assert.True(list.AsSpan().SequenceEqual(items));
    }

    [Fact]
    public void A_type_derived_from_List_gives_the_items_it_enumerates()
    {
        var evens = new EvenList { 1, 2, 3, 4, 5, 6 };

        AssertEqual([2, 4, 6], evens.ToOahuList());
        AssertEqual([2, 4, 6], OahuList.CreateRange(evens));
        AssertEqual([2, 4, 6], ((IEnumerable<int>)evens).ToOahuList());
    }

    [Fact]
    public void A_List_is_copied_into_a_single_array()
    {
        List<int> source = [1, 2, 3, 4];

        var arrayOnly = Allocations.Smallest(() => _sink = new int[4]);
        var copied = Allocations.Smallest(() => _ints = source.ToOahuList());

        Assert.True(arrayOnly > 0);
        Assert.Equal(arrayOnly, copied);
        AssertEqual([1, 2, 3, 4], _ints);
    }

    [Fact]
    public void An_array_of_a_derived_type_passed_as_an_enumerable_of_its_base_type_is_copied()
    {
        string[] source = ["a", "b"];
        var asObjects = (IEnumerable<object>)source;

        var list = asObjects.ToOahuList();
        source[0] = "z";

        AssertEqual<object>(["a", "b"], list);
        AssertEqual<object>(["z", "b"], OahuList.CreateRange(asObjects));
    }

    [Fact]
    public void Its_interfaces_have_no_member_that_changes_it()
    {
        var interfaces = typeof(OahuList<int>).GetInterfaces().Select(type => type.IsGenericType ? type.GetGenericTypeDefinition() : type);

        Assert.Equal(
            new[] { typeof(IEnumerable), typeof(IEnumerable<>), typeof(IEquatable<>), typeof(IReadOnlyCollection<>), typeof(IReadOnlyList<>) }
                .OrderBy(type => type.Name),
            interfaces.OrderBy(type => type.Name));
    }

    // Reading

    [Fact]
    public void The_indexer_count_enumeration_and_span_give_the_items_in_order()
    {
        OahuList<string> list = ["x", "y"];

        Assert.Equal(2, list.Count);
        Assert.Equal("x", list[0]);
        Assert.Equal("y", list[1]);

        var seen = new List<string>();
        foreach (var item in list)
            seen.Add(item);
        Assert.Equal<string>(["x", "y"], seen);

        Assert.Equal<string>(["x", "y"], list.AsSpan().ToArray());

        IReadOnlyList<string> readOnly = list;
        Assert.Equal(2, readOnly.Count);
        Assert.Equal("y", readOnly[1]);
        Assert.Equal<string>(["x", "y"], readOnly.ToArray());

        var untyped = new List<object?>();
        foreach (var item in (IEnumerable)list)
            untyped.Add(item);
        Assert.Equal<object?>(["x", "y"], untyped);
    }

    [Fact]
    public void The_empty_list_enumerates_nothing_through_each_way_in()
    {
        OahuList<int> list = default;

        foreach (var _ in list)
            Assert.Fail("The empty list has no items.");

        Assert.Empty((IEnumerable<int>)list);
        Assert.False(((IEnumerable)list).GetEnumerator().MoveNext());
        Assert.False(new OahuList<int>.Enumerator().MoveNext());
    }

    [Fact]
    public void An_enumerator_at_its_end_stays_at_its_end()
    {
        OahuList<int> list = [1];
        var enumerator = list.GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, enumerator.Current);
        Assert.False(enumerator.MoveNext());
        Assert.False(enumerator.MoveNext());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void An_index_out_of_range_is_an_argument_error(int index)
    {
        OahuList<int> list = [1, 2];

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => list[index]);
        Assert.Equal("index", exception.ParamName);
        Assert.Equal(index, exception.ActualValue);
    }

    [Fact]
    public void Any_index_of_the_empty_list_is_an_argument_error()
    {
        OahuList<int> list = default;

        Assert.Throws<ArgumentOutOfRangeException>(() => list[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ((IReadOnlyList<int>)list)[0]);
    }

    [Fact]
    public void ToString_writes_the_items_as_a_collection_expression_does()
    {
        Assert.Equal("[a, b]", OahuList.Create("a", "b").ToString());
        Assert.Equal("[[1], []]", OahuList.Create<OahuList<int>>([1], []).ToString());
        Assert.Equal("[null, x]", OahuList.Create<string?>(null, "x").ToString());
        Assert.Equal("Tagged { Name = a, Tags = [b] }", new Tagged("a", ["b"]).ToString());
    }

    // Equality

    [Fact]
    public void Lists_with_equal_items_in_the_same_order_are_equal()
    {
        AssertEqual(["a", "b"], OahuList.Create("a", "b"));
        AssertEqual([1, 2, 3], Enumerable.Range(1, 3).ToOahuList());
    }

    [Fact]
    public void A_list_equals_itself_and_its_copies()
    {
        OahuList<int> list = [1, 2];
        var copy = list;

        AssertEqual(list, list);
        AssertEqual(list, copy);
    }

    [Fact]
    public void Lists_with_the_same_items_in_a_different_order_are_not_equal()
    {
        AssertNotEqual([1, 2], [2, 1]);
    }

    [Fact]
    public void Lists_of_different_counts_are_not_equal()
    {
        AssertNotEqual([1, 2], [1, 2, 3]);
        AssertNotEqual([1], []);
        AssertNotEqual([], [0]);
        AssertNotEqual(default, [0]);
    }

    [Fact]
    public void A_list_does_not_equal_another_type_holding_the_same_items()
    {
        OahuList<int> list = [1, 2];

        Assert.False(list.Equals((object)new[] { 1, 2 }));
        Assert.False(list.Equals((object)new List<int> { 1, 2 }));
        Assert.False(list.Equals((object)OahuList.Create(1L, 2L)));
        Assert.False(list.Equals(null));
        Assert.True(list.Equals((object)OahuList.Create(1, 2)));
    }

    [Fact]
    public void Nested_lists_compare_by_their_items_all_the_way_down()
    {
        OahuList<OahuList<string>> first = [["a"], [], ["b", "c"]];
        OahuList<OahuList<string>> second = [["a"], [], ["b", "c"]];
        OahuList<OahuList<string>> third = [["a"], [], ["b", "d"]];

        AssertEqual(first, second);
        AssertNotEqual(first, third);
    }

    [Fact]
    public void Lists_of_records_compare_by_each_record_s_fields()
    {
        OahuList<Tagged> first = [new("a", ["x"]), new("b", [])];
        OahuList<Tagged> second = [new("a", ["x"]), new("b", [])];

        AssertEqual(first, second);
        AssertNotEqual(first, [new("a", ["y"]), new("b", [])]);
    }

    [Fact]
    public void A_record_with_a_list_field_equals_a_record_rebuilt_from_the_same_values()
    {
        var first = new Tagged("Milk", ["dairy", "fridge"]);
        var second = new Tagged("Milk", new List<string> { "dairy", "fridge" }.ToOahuList());

        Assert.NotSame(first, second);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(EqualityComparer<Tagged>.Default.Equals(first, second));

        Assert.True(first != second with { Tags = ["dairy"] });
        Assert.True(first == first with { Name = "Milk" });
    }

    [Fact]
    public void A_union_whose_case_holds_a_list_equals_one_rebuilt_from_the_same_values()
    {
        Screen first = new Screen.Ready("Shopping", [new("Milk", ["dairy"]), new("Eggs", [])]);
        Screen second = new Screen.Ready("Shopping", [new("Milk", ["dairy"]), new("Eggs", [])]);
        Screen other = new Screen.Ready("Shopping", [new("Milk", ["dairy"])]);

        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first != other);
        Assert.True(first != new Screen.Loading());
    }

    [Fact]
    public void A_record_struct_can_hold_a_list_of_itself_and_compares_all_the_way_down()
    {
        var first = new Branch("root", [new("a", []), new("b", [new("c", [])])]);
        var second = new Branch("root", [new("a", []), new("b", [new("c", [])])]);

        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first != second with { Children = [new("a", [])] });
    }

    [Fact]
    public void Each_NaN_equals_every_other_whatever_its_bits()
    {
        var quiet = BitConverter.Int64BitsToDouble(0x7ff8000000000000);
        var payload = BitConverter.Int64BitsToDouble(0x7ff8000000000001);
        var negative = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000000));

        AssertEqual([quiet], [payload]);
        AssertEqual([quiet], [negative]);
        AssertEqual([double.NaN], [payload]);
        Assert.True(new Reading(payload, default) == new Reading(negative, default));
        Assert.NotEqual(Bytes(quiet), Bytes(payload));
    }

    [Fact]
    public void Zero_equals_negative_zero_though_they_encode_differently()
    {
        AssertEqual([0.0], [-0.0]);
        Assert.True(new Reading(0.0, default) == new Reading(-0.0, default));
        Assert.Equal("0000000000000000", Hex.Of(Bytes(0.0)));
        Assert.Equal("0000000000000080", Hex.Of(Bytes(-0.0)));
    }

    [Fact]
    public void Doubles_that_differ_are_not_equal()
    {
        AssertNotEqual([1.5], [1.5000000000000002]);
        AssertNotEqual([double.PositiveInfinity], [double.NegativeInfinity]);
        AssertNotEqual([double.NaN], [0.0]);
    }

    [Fact]
    public void Instants_compare_by_the_instant_whatever_their_offsets()
    {
        var utc = new DateTimeOffset(2025, 11, 2, 15, 0, 0, TimeSpan.Zero);
        var east = new DateTimeOffset(2025, 11, 3, 5, 0, 0, TimeSpan.FromHours(14));

        AssertEqual([utc], [east]);
        Assert.True(new Reading(1, utc) == new Reading(1, east));
        Assert.Equal(InstantBytes(utc), InstantBytes(east));
    }

    [Fact]
    public void Instants_in_the_same_millisecond_are_not_equal_though_they_encode_alike()
    {
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(1_762_095_600_123);
        var tickLater = instant.AddTicks(1);

        AssertNotEqual([instant], [tickLater]);
        Assert.Equal(InstantBytes(instant), InstantBytes(tickLater));
    }

    [Fact]
    public void Strings_compare_ordinally_so_composed_and_decomposed_forms_are_not_equal()
    {
        const string composed = "é";
        const string decomposed = "é";

        Assert.Equal(composed.Normalize(), decomposed.Normalize());
        AssertNotEqual([composed], [decomposed]);
        AssertNotEqual(["a"], ["A"]);
        AssertEqual([composed], [new string('é', 1)]);
    }

    [Fact]
    public void A_lone_surrogate_does_not_equal_the_replacement_character_though_they_encode_alike()
    {
        const string lone = "\ud800";
        const string replacement = "�";

        AssertNotEqual([lone], [replacement]);
        Assert.Equal(StringBytes(lone), StringBytes(replacement));
    }

    [Fact]
    public void An_absent_list_differs_from_an_empty_one()
    {
        OahuList<int>? absent = null;
        OahuList<int>? empty = [];
        OahuList<int>? alsoEmpty = default(OahuList<int>);

        Assert.False(absent == empty);
        Assert.True(absent != empty);
        Assert.True(empty == alsoEmpty);
        Assert.True(absent == null);
        Assert.False(empty == null);
        Assert.True(empty.Value.IsEmpty);

        Assert.True(new Holder(null) == new Holder(null));
        Assert.True(new Holder([]) == new Holder(default(OahuList<int>)));
        Assert.True(new Holder([1]) == new Holder([1]));
        Assert.False(new Holder(null) == new Holder([]));
        Assert.Equal(new Holder([1, 2]).GetHashCode(), new Holder([1, 2]).GetHashCode());
    }

    [Fact]
    public void An_absent_list_is_written_with_its_presence_byte_and_reads_back_absent()
    {
        var writer = new OahuWriter();
        foreach (OahuList<int>? value in new OahuList<int>?[] { null, [], [5] })
        {
            writer.WritePresence(value.HasValue);
            if (value is { } present)
                writer.WriteList(present, static (w, item) => w.WriteInt(item));
        }

        Assert.Equal("00" + "0100" + "01010a", Hex.Of(writer.WrittenSpan));

        var reader = new OahuReader(writer.WrittenSpan);
        Assert.False(reader.ReadPresence());
        Assert.True(reader.ReadPresence());
        AssertEqual([], reader.ReadList(static (ref OahuReader r) => r.ReadInt()));
        Assert.True(reader.ReadPresence());
        AssertEqual([5], reader.ReadList(static (ref OahuReader r) => r.ReadInt()));
        reader.EnsureEnd();
    }

    [Fact]
    public void A_list_read_back_equals_the_list_written()
    {
        OahuList<Tagged> written = [new("Milk", ["dairy"]), new("Eggs", [])];
        var writer = new OahuWriter();
        writer.WriteList(written, static (w, item) =>
        {
            w.EnterNested();
            w.WriteString(item.Name);
            w.WriteList(item.Tags, static (inner, tag) => inner.WriteString(tag));
            w.ExitNested();
        });

        var reader = new OahuReader(writer.WrittenSpan);
        var read = reader.ReadList(static (ref OahuReader r) =>
        {
            r.EnterNested();
            var name = r.ReadString();
            var tags = r.ReadList(static (ref OahuReader inner) => inner.ReadString());
            r.ExitNested();
            return new Tagged(name, tags);
        });
        reader.EnsureEnd();

        AssertEqual(written, read);
    }

    [Fact]
    public void Equal_lists_have_equal_hash_codes()
    {
        Assert.Equal(OahuList.Create(1, 2, 3).GetHashCode(), Enumerable.Range(1, 3).ToOahuList().GetHashCode());
        Assert.Equal(OahuList<string>.Empty.GetHashCode(), default(OahuList<string>).GetHashCode());
        Assert.Equal(OahuList.Create<string?>(null, "a").GetHashCode(), OahuList.Create<string?>(null, "a").GetHashCode());
        Assert.Equal(OahuList.Create(0.0).GetHashCode(), OahuList.Create(-0.0).GetHashCode());
        Assert.Equal(
            OahuList.Create(BitConverter.Int64BitsToDouble(0x7ff8000000000001)).GetHashCode(),
            OahuList.Create(double.NaN).GetHashCode());
        Assert.Equal(
            OahuList.Create(new DateTimeOffset(2025, 11, 2, 15, 0, 0, TimeSpan.Zero)).GetHashCode(),
            OahuList.Create(new DateTimeOffset(2025, 11, 3, 5, 0, 0, TimeSpan.FromHours(14))).GetHashCode());
    }

    [Fact]
    public void Lists_work_as_keys_of_a_dictionary_and_a_set()
    {
        var counts = new Dictionary<OahuList<string>, int> { [OahuList.Create("a", "b")] = 1 };
        counts[OahuList.Create("a", "b")]++;

        Assert.Equal(2, counts[new List<string> { "a", "b" }.ToOahuList()]);
        Assert.Single(new HashSet<OahuList<int>> { OahuList.Create(1), Enumerable.Repeat(1, 1).ToOahuList() });
    }

    // Allocations

    [Fact]
    public void Enumerating_indexing_and_reading_the_span_of_a_list_of_ints_allocate_nothing()
    {
        OahuList<int> list = [1, 2, 3, 4];

        Assert.Equal(0, Allocations.Smallest(() =>
        {
            var sum = 0;
            foreach (var item in list)
                sum += item;
            _number = sum;
        }));
        Assert.Equal(0, Allocations.Smallest(() => _number = list[2] + list.Count));
        Assert.Equal(0, Allocations.Smallest(() => _number = list.AsSpan()[3]));
    }

    [Fact]
    public void Comparing_and_hashing_lists_of_value_types_allocate_nothing()
    {
        OahuList<int> ints = [1, 2, 3];
        OahuList<int> sameInts = [1, 2, 3];
        OahuList<double> doubles = [double.NaN, 0.0];
        OahuList<double> sameDoubles = [double.NaN, -0.0];
        OahuList<DateTimeOffset> instants = [DateTimeOffset.UnixEpoch];
        OahuList<DateTimeOffset> sameInstants = [DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1))];
        OahuList<OahuList<int>> nested = [[1], [2, 3]];
        OahuList<OahuList<int>> sameNested = [[1], [2, 3]];

        Assert.True(ints == sameInts && doubles == sameDoubles && instants == sameInstants && nested == sameNested);
        Assert.Equal(0, Allocations.Smallest(() => _flag = ints == sameInts));
        Assert.Equal(0, Allocations.Smallest(() => _flag = ints.Equals(sameInts)));
        Assert.Equal(0, Allocations.Smallest(() => _flag = doubles == sameDoubles));
        Assert.Equal(0, Allocations.Smallest(() => _flag = instants == sameInstants));
        Assert.Equal(0, Allocations.Smallest(() => _flag = nested == sameNested));
        Assert.Equal(0, Allocations.Smallest(() => _flag = EqualityComparer<OahuList<int>>.Default.Equals(ints, sameInts)));
        Assert.Equal(0, Allocations.Smallest(() => _number = ints.GetHashCode()));
        Assert.Equal(0, Allocations.Smallest(() => _number = nested.GetHashCode()));
    }

    [Fact]
    public void Comparing_records_with_list_fields_allocates_nothing()
    {
        var first = new Tagged("Milk", ["dairy", "fridge"]);
        var second = new Tagged("Milk", ["dairy", "fridge"]);

        Assert.Equal(0, Allocations.Smallest(() => _flag = first == second));
        Assert.Equal(0, Allocations.Smallest(() => _number = first.GetHashCode()));
    }

    [Fact]
    public void Writing_a_list_allocates_nothing_and_reading_it_back_allocates_only_its_array()
    {
        OahuList<int> list = [1, -1, 300, int.MinValue];
        var writer = new OahuWriter();
        writer.WriteList(list, WriteInt);
        var bytes = writer.WrittenSpan.ToArray();

        Assert.Equal(0, Allocations.Smallest(() =>
        {
            writer.Reset();
            writer.WriteList(list, WriteInt);
        }));

        var arrayOnly = Allocations.Smallest(() => _sink = new int[4]);
        var read = Allocations.Smallest(() =>
        {
            var reader = new OahuReader(bytes);
            _ints = reader.ReadList(ReadInt);
        });

        Assert.True(arrayOnly > 0);
        Assert.Equal(arrayOnly, read);
        AssertEqual(list, _ints);
    }

    [Fact]
    public void Using_a_list_as_an_interface_boxes_it()
    {
        OahuList<int> list = [1, 2];

        Assert.True(Allocations.Smallest(() => _sink = (IReadOnlyList<int>)list) > 0);
    }

    private static void AssertEqual<T>(OahuList<T> expected, OahuList<T> actual)
    {
        Assert.True(expected == actual, $"{expected} == {actual}");
        Assert.False(expected != actual, $"{expected} != {actual}");
        Assert.True(expected.Equals(actual));
        Assert.True(actual.Equals(expected));
        Assert.True(expected.Equals((object)actual));
        Assert.True(EqualityComparer<OahuList<T>>.Default.Equals(expected, actual));
        Assert.Equal(expected.GetHashCode(), actual.GetHashCode());
    }

    private static void AssertNotEqual<T>(OahuList<T> expected, OahuList<T> actual)
    {
        Assert.False(expected == actual, $"{expected} == {actual}");
        Assert.True(expected != actual, $"{expected} != {actual}");
        Assert.False(expected.Equals(actual));
        Assert.False(actual.Equals(expected));
        Assert.False(expected.Equals((object)actual));
        Assert.False(EqualityComparer<OahuList<T>>.Default.Equals(expected, actual));
    }

    private static IEnumerable<int> Yield(params int[] items)
    {
        foreach (var item in items)
            yield return item;
    }

    // A hostile collection: it keeps every array CopyTo is given, so that it can change them afterwards, and it can
    // report a count that is not its number of items.
    private sealed class KeepingCollection(int[] items, int? count = null) : ICollection<int>
    {
        private readonly List<int[]> _kept = [];

        public int Count => count ?? items.Length;

        public bool IsReadOnly => true;

        public void ChangeKeptArrays(int value)
        {
            foreach (var array in _kept)
                array.AsSpan().Fill(value);
        }

        public void CopyTo(int[] array, int arrayIndex)
        {
            _kept.Add(array);
            items.CopyTo(array, arrayIndex);
        }

        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool Contains(int item) => items.Contains(item);

        public void Add(int item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Remove(int item) => throw new NotSupportedException();
    }

    // A list whose enumeration through IEnumerable<int> gives only its even items, unlike the items it stores.
    private sealed class EvenList : List<int>, IEnumerable<int>
    {
        IEnumerator<int> IEnumerable<int>.GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                if (this[i] % 2 == 0)
                    yield return this[i];
            }
        }
    }

    private static byte[] Bytes(double value)
    {
        var writer = new OahuWriter();
        writer.WriteDouble(value);
        return writer.WrittenSpan.ToArray();
    }

    private static string InstantBytes(DateTimeOffset value)
    {
        var writer = new OahuWriter();
        writer.WriteInstant(value);
        return Hex.Of(writer.WrittenSpan);
    }

    private static string StringBytes(string value)
    {
        var writer = new OahuWriter();
        writer.WriteString(value);
        return Hex.Of(writer.WrittenSpan);
    }

    private static void WriteInt(OahuWriter writer, int value) => writer.WriteInt(value);

    private static int ReadInt(ref OahuReader reader) => reader.ReadInt();
}
