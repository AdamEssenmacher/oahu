using System.Text.Json.Nodes;
using Xunit;

namespace AdamE.Oahu.Tests.Conformance;

// Tests of the harness's own checks of the vector file and the page, with small inputs of their own.
public sealed class HarnessCheckTests
{
    [Fact]
    public void A_record_with_a_self_field_after_a_field_that_is_not_a_record_is_rejected()
    {
        var error = Assert.Throws<InvalidDataException>(() => ParseTypes(
            """[{"name": "A", "record": [{"name": "X", "type": "int"}, {"name": "Self", "type": "A"}]}]"""));

        Assert.Equal("The record A contains itself through record fields alone: A.Self is A.", error.Message);
    }

    [Fact]
    public void A_cycle_through_two_records_is_rejected()
    {
        var error = Assert.Throws<InvalidDataException>(() => ParseTypes(
            """
            [
              {"name": "A", "record": [{"name": "X", "type": "bool"}, {"name": "Next", "type": "B"}]},
              {"name": "B", "record": [{"name": "Y", "type": "string"}, {"name": "Next", "type": "A"}]}
            ]
            """));

        Assert.Equal(
            "The record A contains itself through record fields alone: A.Next is B, B.Next is A.", error.Message);
    }

    [Theory]
    [InlineData("list<A>")]
    [InlineData("A?")]
    [InlineData("U")]
    public void A_record_that_holds_itself_through_a_list_a_nullable_value_or_a_union_is_allowed(string type)
    {
        var types = ParseTypes(
            $$"""
            [
              {"name": "A", "record": [{"name": "Self", "type": "{{type}}"}]},
              {"name": "U", "union": [{"name": "None", "fields": []}, {"name": "Some", "fields": [{"name": "Value", "type": "A"}]}]}
            ]
            """);

        Assert.False(VectorTypeParser.IsNoBytes((RecordType)types["A"]));
    }

    [Fact]
    public void The_page_parser_ignores_the_lines_of_fenced_code_blocks()
    {
        var page = WireFormatPage.Parse(
            """
            ## Counts

            | Value | Bytes |
            | --- | --- |
            | 1 | `01` |

            ```text
            ## Not a section
            | 2 | `02` |
            - Not a bullet
            ```

            ## Malformed input

            - A bullet
            ~~~~
            - Not a bullet either
            ~~~
            | 3 | `03` |
            ~~~~

            - Another bullet
            """);

        Assert.Equal(["Counts", "Malformed input"], page.Sections);
        Assert.Equal(["Counts: 1"], page.ByteTableRows.Select(row => row.Key));
        Assert.Equal(["A bullet", "Another bullet"], page.MalformedInput);
    }

    [Theory]
    [InlineData("list<unit>", "unit is allowed only as a vector's whole type")]
    [InlineData("unit?", "unit cannot be nullable")]
    public void The_no_value_type_is_only_a_vector_s_whole_type_and_never_nullable(string type, string problem)
    {
        var error = Assert.Throws<InvalidDataException>(() => VectorTypeParser.Parse(type, NoTypes, wholeType: true));

        Assert.Equal($"The type expression \"{type}\" is not valid: {problem}.", error.Message);
    }

    [Fact]
    public void The_no_value_type_is_not_a_record_s_field()
    {
        var error = Assert.Throws<InvalidDataException>(() => ParseTypes(
            """[{"name": "A", "record": [{"name": "Nothing", "type": "unit"}]}]"""));

        Assert.Equal(
            "The type expression \"unit\" is not valid: unit is allowed only as a vector's whole type.", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("0")]
    [InlineData("\"\"")]
    public void The_no_value_type_s_value_is_null_and_nothing_else(string json)
    {
        var unit = VectorTypeParser.Parse("unit", NoTypes, wholeType: true);

        Assert.Equal("null", VectorValues.Canonical(unit, null));
        Assert.Throws<InvalidDataException>(() => VectorValues.Canonical(unit, JsonNode.Parse(json)));
    }

    private static readonly Dictionary<string, VectorType> NoTypes = [];

    private static Dictionary<string, VectorType> ParseTypes(string json) =>
        VectorFile.ParseTypes(JsonNode.Parse(json) as JsonArray ?? throw new InvalidOperationException("Not an array."));
}
