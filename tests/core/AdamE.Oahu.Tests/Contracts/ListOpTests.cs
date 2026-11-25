using Xunit;

namespace AdamE.Oahu.Tests.Contracts;

public sealed class ListOpTests
{
    public sealed record TaskRow(string Key, string Text, bool Done) : IOahuKeyed;

    public sealed record NoteRow(string Key) : IOahuKeyed;

    public sealed record TagsRow(string Key, IReadOnlyList<string> Tags) : IOahuKeyed;

    private static readonly TaskRow Milk = new("t0", "Milk", false);
    private static readonly TaskRow Bread = new("t1", "Bread", false);

    [Fact]
    public void An_insert_holds_its_index_and_row()
    {
        var insert = new ListOp<TaskRow>.Insert(2, Milk);

        Assert.Equal(2, insert.Index);
        Assert.Same(Milk, insert.Item);

        var (index, item) = insert;
        Assert.Equal(2, index);
        Assert.Same(Milk, item);
    }

    [Fact]
    public void A_remove_holds_its_index()
    {
        var remove = new ListOp<TaskRow>.Remove(3);

        Assert.Equal(3, remove.Index);
    }

    [Fact]
    public void A_move_holds_where_from_and_where_to()
    {
        var move = new ListOp<TaskRow>.Move(4, 1);

        Assert.Equal(4, move.From);
        Assert.Equal(1, move.To);

        var (from, to) = move;
        Assert.Equal(4, from);
        Assert.Equal(1, to);
    }

    [Fact]
    public void A_replace_holds_its_index_and_row()
    {
        var replace = new ListOp<TaskRow>.Replace(0, Bread);

        Assert.Equal(0, replace.Index);
        Assert.Same(Bread, replace.Item);

        var (index, item) = replace;
        Assert.Equal(0, index);
        Assert.Same(Bread, item);
    }

    [Fact]
    public void A_with_expression_gives_an_operation_of_the_same_case()
    {
        ListOp<TaskRow> moved = new ListOp<TaskRow>.Move(4, 1) with { To = 2 };

        Assert.Equal(new ListOp<TaskRow>.Move(4, 2), moved);
    }

    [Fact]
    public void Operations_of_one_case_with_equal_arguments_are_equal()
    {
        ListOp<TaskRow>[] left =
        [
            new ListOp<TaskRow>.Insert(1, Milk),
            new ListOp<TaskRow>.Remove(1),
            new ListOp<TaskRow>.Move(1, 2),
            new ListOp<TaskRow>.Replace(1, Milk),
            new ListOp<TaskRow>.Clear(),
        ];
        ListOp<TaskRow>[] right =
        [
            new ListOp<TaskRow>.Insert(1, Milk with { }),
            new ListOp<TaskRow>.Remove(1),
            new ListOp<TaskRow>.Move(1, 2),
            new ListOp<TaskRow>.Replace(1, Milk with { }),
            new ListOp<TaskRow>.Clear(),
        ];

        for (var i = 0; i < left.Length; i++)
        {
            Assert.NotSame(left[i], right[i]);
            Assert.Equal(left[i], right[i]);
            Assert.True(left[i] == right[i]);
            Assert.False(left[i] != right[i]);
            Assert.True(left[i].Equals((object)right[i]));
            Assert.Equal(left[i].GetHashCode(), right[i].GetHashCode());
        }
    }

    [Fact]
    public void Operations_with_different_arguments_are_not_equal()
    {
        Assert.NotEqual(new ListOp<TaskRow>.Insert(1, Milk), new ListOp<TaskRow>.Insert(2, Milk));
        Assert.NotEqual(new ListOp<TaskRow>.Insert(1, Milk), new ListOp<TaskRow>.Insert(1, Bread));
        Assert.NotEqual(new ListOp<TaskRow>.Insert(1, Milk), new ListOp<TaskRow>.Insert(1, Milk with { Done = true }));
        Assert.NotEqual(new ListOp<TaskRow>.Remove(1), new ListOp<TaskRow>.Remove(2));
        Assert.NotEqual(new ListOp<TaskRow>.Move(1, 2), new ListOp<TaskRow>.Move(2, 1));
        Assert.NotEqual(new ListOp<TaskRow>.Move(1, 2), new ListOp<TaskRow>.Move(1, 3));
        Assert.NotEqual(new ListOp<TaskRow>.Replace(1, Milk), new ListOp<TaskRow>.Replace(2, Milk));
        Assert.NotEqual(new ListOp<TaskRow>.Replace(1, Milk), new ListOp<TaskRow>.Replace(1, Bread));
    }

    [Fact]
    public void Operations_of_different_cases_are_not_equal_even_with_the_same_arguments()
    {
        ListOp<TaskRow>[] ops =
        [
            new ListOp<TaskRow>.Insert(0, Milk),
            new ListOp<TaskRow>.Replace(0, Milk),
            new ListOp<TaskRow>.Remove(0),
            new ListOp<TaskRow>.Move(0, 0),
            new ListOp<TaskRow>.Clear(),
        ];

        for (var i = 0; i < ops.Length; i++)
        {
            for (var j = 0; j < ops.Length; j++)
            {
                if (i != j)
                {
                    Assert.NotEqual(ops[i], ops[j]);
                    Assert.False(ops[i] == ops[j]);
                }
            }
        }

        Assert.False(ops[0].Equals(null));
    }

    [Fact]
    public void Operations_whose_rows_hold_different_list_instances_are_not_equal()
    {
        // Pins C# record equality: the default comparer compares a list field by reference, so rows equal by
        // structure are not equal when they hold different lists. Structural equality comes later.
        var first = new TagsRow("k", ["home"]);
        var second = new TagsRow("k", ["home"]);

        Assert.NotEqual(new ListOp<TagsRow>.Insert(0, first), new ListOp<TagsRow>.Insert(0, second));
        Assert.Equal(new ListOp<TagsRow>.Insert(0, first), new ListOp<TagsRow>.Insert(0, first with { }));
    }

    [Fact]
    public void Operations_on_lists_of_different_row_types_are_not_equal()
    {
        Assert.False(new ListOp<TaskRow>.Clear().Equals(new ListOp<NoteRow>.Clear()));
        Assert.False(new ListOp<TaskRow>.Remove(0).Equals(new ListOp<NoteRow>.Remove(0)));
    }

    [Fact]
    public void An_operation_checks_nothing_when_it_is_created()
    {
        // Pins a decision: an index is checked by whoever applies the operation, against the list as it is then, not
        // by the constructor. Even a negative index constructs, and so does a null row.
        var insert = new ListOp<TaskRow>.Insert(-1, null!);
        var remove = new ListOp<TaskRow>.Remove(int.MinValue);
        var move = new ListOp<TaskRow>.Move(-1, int.MaxValue);
        var replace = new ListOp<TaskRow>.Replace(-5, null!);

        Assert.Equal(-1, insert.Index);
        Assert.Null(insert.Item);
        Assert.Equal(int.MinValue, remove.Index);
        Assert.Equal(-1, move.From);
        Assert.Equal(int.MaxValue, move.To);
        Assert.Equal(-5, replace.Index);
        Assert.Null(replace.Item);
        Assert.Equal(-2, (new ListOp<TaskRow>.Remove(0) with { Index = -2 }).Index);
    }

    [Fact]
    public void A_switch_over_the_five_cases_covers_every_operation()
    {
        // How a caller applies or describes an operation. ListOp<T> is a closed type, so a switch that handles the
        // five cases is exhaustive: it needs no discard arm, and the build treats warnings, such as CS8509, as errors.
        static string Describe(ListOp<TaskRow> op) => op switch
        {
            ListOp<TaskRow>.Insert(var index, var item) => $"insert {item.Key} at {index}",
            ListOp<TaskRow>.Remove(var index) => $"remove at {index}",
            ListOp<TaskRow>.Move(var from, var to) => $"move {from} to {to}",
            ListOp<TaskRow>.Replace(var index, var item) => $"replace {item.Key} at {index}",
            ListOp<TaskRow>.Clear => "clear",
        };

        IReadOnlyList<ListOp<TaskRow>> batch =
        [
            new ListOp<TaskRow>.Clear(),
            new ListOp<TaskRow>.Insert(0, Milk),
            new ListOp<TaskRow>.Insert(1, Bread),
            new ListOp<TaskRow>.Move(1, 0),
            new ListOp<TaskRow>.Replace(1, Milk with { Done = true }),
            new ListOp<TaskRow>.Remove(0),
        ];

        Assert.Equal(
            ["clear", "insert t0 at 0", "insert t1 at 1", "move 1 to 0", "replace t0 at 1", "remove at 0"],
            batch.Select(Describe));
    }

    [Fact]
    public void The_five_cases_are_the_only_types_that_derive_from_it()
    {
        var definition = typeof(ListOp<>);
        var derived = definition.Assembly.GetTypes()
            .Where(type => type.BaseType is { IsGenericType: true } baseType
                && baseType.GetGenericTypeDefinition() == definition)
            .OrderBy(type => type.Name)
            .ToArray();

        Assert.Equal(["Clear", "Insert", "Move", "Remove", "Replace"], derived.Select(type => type.Name));
        Assert.All(derived, type =>
        {
            Assert.True(type.IsSealed);
            Assert.Same(definition, type.DeclaringType);
        });

        // The C# compiler marks a closed type with IsClosedTypeAttribute. It refuses a base type from another
        // assembly that has the marker (CS9382), and it treats a switch over the cases as exhaustive.
        Assert.Contains(
            definition.GetCustomAttributesData(),
            attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsClosedTypeAttribute");
        Assert.True(definition.IsAbstract);
    }

    [Fact]
    public void Its_type_argument_is_a_row()
    {
        Assert.Equal([typeof(IOahuKeyed)], typeof(ListOp<>).GetGenericArguments()[0].GetGenericParameterConstraints());
    }
}
