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

    [Theory]
    [InlineData("list<batch<Keyed>>", "batch is allowed only as a vector's whole type")]
    [InlineData("batch<Keyed>?", "batch cannot be nullable")]
    [InlineData("batch<Keyed?>", "the row type Keyed? is not a record or a union whose record or every case has a string field named Key")]
    [InlineData("batch<string>", "the row type string is not a record or a union whose record or every case has a string field named Key")]
    [InlineData("batch<NoKey>", "the row type NoKey is not a record or a union whose record or every case has a string field named Key")]
    [InlineData("batch<IntKey>", "the row type IntKey is not a record or a union whose record or every case has a string field named Key")]
    [InlineData("batch<HalfKeyed>", "the row type HalfKeyed is not a record or a union whose record or every case has a string field named Key")]
    public void A_batch_is_only_a_vector_s_whole_type_of_rows_with_a_string_key(string type, string problem)
    {
        var error = Assert.Throws<InvalidDataException>(() => VectorTypeParser.Parse(type, RowTypes, wholeType: true));

        Assert.Equal($"The type expression \"{type}\" is not valid: {problem}.", error.Message);
    }

    [Fact]
    public void A_batch_is_not_a_record_s_field()
    {
        var error = Assert.Throws<InvalidDataException>(() => ParseTypes(
            """[{"name": "Keyed", "record": [{"name": "Key", "type": "string"}]}, {"name": "A", "record": [{"name": "B", "type": "batch<Keyed>"}]}]"""));

        Assert.Equal(
            "The type expression \"batch<Keyed>\" is not valid: batch is allowed only as a vector's whole type.", error.Message);
    }

    [Theory]
    [InlineData("""[{"Insert": {"index": 0, "item": {"Key": "k"}}}]""")]
    [InlineData("""[{"insert": {"item": {"Key": "k"}, "index": 0}}]""")]
    [InlineData("""[{"insert": {"index": 0}}]""")]
    [InlineData("""[{"insert": {"index": 0, "item": null}}]""")]
    [InlineData("""[{"remove": {"index": "0"}}]""")]
    [InlineData("""[{"clear": {}, "remove": {"index": 0}}]""")]
    [InlineData("""[{"clear": null}]""")]
    [InlineData("""[{"swap": {}}]""")]
    [InlineData("""[null]""")]
    [InlineData("""{"clear": {}}""")]
    public void A_batch_value_that_is_not_a_list_of_known_operations_is_rejected(string json)
    {
        var batch = VectorTypeParser.Parse("batch<Keyed>", RowTypes, wholeType: true);

        Assert.Throws<InvalidDataException>(() => VectorValues.Canonical(batch, JsonNode.Parse(json)));
    }

    [Fact]
    public void A_batch_value_has_one_canonical_form()
    {
        var batch = VectorTypeParser.Parse("batch<Keyed>", RowTypes, wholeType: true);
        var json = """[{"insert": {"index": 0, "item": {"Key": "k"}}}, {"remove": {"index": 1}}, {"move": {"from": 2, "to": 3}}, {"replace": {"index": 4, "item": {"Key": "j"}}}, {"clear": {}}]""";

        Assert.Equal(json, VectorValues.Canonical(batch, JsonNode.Parse(json)));
    }

    private static readonly Dictionary<string, VectorType> NoTypes = [];

    private static readonly Dictionary<string, VectorType> RowTypes = ParseTypes(
        """
        [
          {"name": "Keyed", "record": [{"name": "Key", "type": "string"}]},
          {"name": "NoKey", "record": [{"name": "Name", "type": "string"}]},
          {"name": "IntKey", "record": [{"name": "Key", "type": "int"}]},
          {"name": "HalfKeyed", "union": [{"name": "A", "fields": [{"name": "Key", "type": "string"}]}, {"name": "B", "fields": []}]}
        ]
        """);

    private static Dictionary<string, VectorType> ParseTypes(string json) =>
        VectorFile.ParseTypes(JsonNode.Parse(json) as JsonArray ?? throw new InvalidOperationException("Not an array."));
}
