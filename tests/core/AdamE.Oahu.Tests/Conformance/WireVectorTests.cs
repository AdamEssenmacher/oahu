using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdamE.Oahu.Wire;
using Xunit;

namespace AdamE.Oahu.Tests.Conformance;

// Runs the wire format's shared test vectors, tests/conformance/wire-format/vectors.json, against OahuWriter and
// OahuReader, and checks that the file agrees with docs/wire-format.md. The README next to the file describes it.
public sealed partial class WireVectorTests
{
    // Set to 1 to rewrite the vector file in its layout with the generated depth vectors, instead of checking it.
    private const string WriteVariable = "OAHU_WRITE_VECTORS";

    // For each cause code, the start of each message OahuReader gives for it: the harness's own reading of the codes.
    private static readonly Dictionary<string, string[]> CauseMessages = new(StringComparer.Ordinal)
    {
        ["varuint-truncated"] = ["The input ends inside a variable-length integer"],
        ["varuint-too-long"] = ["The variable-length integer at byte offset {0} is longer than 10 bytes."],
        ["varuint-overflow"] = ["The variable-length integer at byte offset {0} does not fit in 64 bits."],
        ["count-too-large"] = ["The count {0} at byte offset {0} is larger than the largest count"],
        ["bool-invalid"] = ["The bool at byte offset {0} is 0x{0}, which is neither 0x00 nor 0x01."],
        ["int-overflow"] = ["The int at byte offset {0} does not fit in 32 bits"],
        ["bool-or-double-truncated"] = ["The input ends before the bool", "The input ends inside a double"],
        ["instant-range"] = ["The instant at byte offset {0} is {0} milliseconds from the Unix epoch, outside the range"],
        ["string-length-exceeds-bytes"] = ["The string at byte offset {0} is {0} byte(s) long, but only {0} byte(s) remain"],
        ["string-utf8"] = ["The string at byte offset {0} is not well-formed UTF-8"],
        ["presence-invalid"] = ["The input ends before the presence byte", "The presence byte at byte offset {0} is 0x{0}"],
        ["list-count-exceeds-bytes"] = ["The list at byte offset {0} has {0} item(s), but only {0} byte(s) remain"],
        ["index-range"] = ["The index {0} at byte offset {0} is not below {0}, the number of values or cases"],
        ["too-deep"] = ["The value at byte offset {0} is at nesting depth 65, deeper than the largest nesting depth, 64."],
        ["leftover-bytes"] = ["{0} unread byte(s) remain at the end of the input"],
    };

    private static readonly HashSet<string> ViaCodes =
        new(["int-varuint", "time-long", "string-length", "list-count", "part", "index-varuint"], StringComparer.Ordinal);

    // For each refusal code, the exception OahuWriter throws and the start of its message, if the harness checks it.
    private static readonly Dictionary<string, (Type Exception, string? Message)> RefusalExceptions =
        new(StringComparer.Ordinal)
        {
            ["negative-count"] = (typeof(ArgumentOutOfRangeException), null),
            ["too-deep"] = (typeof(InvalidOperationException),
                "The value at byte offset {0} is at nesting depth 65, deeper than the largest nesting depth, 64."),
        };

    // For each of the page's ## sections, the sections of vector names that belong to it, in order. A section of the
    // page with no vectors maps to none. The order of vector sections is taken from the page's order of its sections.
    private static readonly Dictionary<string, string[]> PageSections = new(StringComparer.Ordinal)
    {
        ["Messages"] = [],
        ["Variable-length integers"] = ["varuint"],
        ["Counts"] = ["count"],
        ["Booleans"] = ["bool"],
        ["Signed integers"] = ["int", "long"],
        ["Doubles"] = ["double"],
        ["Strings"] = ["string"],
        ["Instants and durations"] = ["instant", "duration"],
        ["Nullable values"] = ["nullable"],
        ["Lists"] = ["list"],
        ["Records"] = ["record"],
        ["Enums"] = ["enum"],
        ["Unions"] = ["union"],
        ["Nesting depth"] = ["depth"],
        ["Malformed input"] = [],
        ["Test vectors"] = [],
    };

    // The columns a table with a Bytes column may have. Each is compared with its vector, except Duration, whose
    // cells are words.
    private static readonly HashSet<string> ByteTableColumns =
        new(["Value", "Zigzag value", "Instant", "Milliseconds", "Duration", "Ticks", "Bytes"], StringComparer.Ordinal);

    // The rows of the page's byte tables whose Bytes cell describes the bytes in words, by their keys. Every other row
    // lists its bytes or says it is no bytes, and its vector must have exactly those bytes.
    private static readonly HashSet<string> RowsWithBytesInWords = new(
        [
            "Strings: 128 `a` characters",
            "Strings: 64 `é` characters",
            "Lists: 128 booleans",
            "Enums: `High`, read from a longer form of index 0",
        ],
        StringComparer.Ordinal);

    public static IEnumerable<TheoryDataRow<string>> VectorNames =>
        VectorFile.Current.Vectors.Select(v => new TheoryDataRow<string>(v.Name) { TestDisplayName = v.Name });

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Vector(string name)
    {
        var vector = VectorFile.Current.Vectors.Single(v => v.Name == name);
        switch (vector.Kind)
        {
            case VectorFile.RoundTrip:
                AssertEncodes(vector, vector.Value, vector.Hex!);
                AssertDecodes(vector, vector.Hex!, vector.Value);
                break;
            case VectorFile.DecodeOnly:
                AssertDecodes(vector, vector.Hex!, vector.Value);
                if (vector.Writes is { } writes)
                    AssertEncodes(vector, vector.Value, writes);
                break;
            case VectorFile.EncodeOnly:
                AssertEncodes(vector, vector.Value, vector.Hex!);
                AssertDecodes(vector, vector.Hex!, vector.Reads);
                break;
            case VectorFile.Reject:
                var bytes = Convert.FromHexString(vector.Hex!);
                var rejected = Assert.Throws<OahuFormatException>(() => VectorCodec.Decode(vector.Type, bytes));
                Assert.Equal(vector.Reason, CauseOf(rejected.Message));
                break;
            case VectorFile.Refuse:
                var (type, message) = RefusalExceptions[vector.Reason!];
                var refused = Assert.Throws(type, () => VectorCodec.Encode(vector.Type, vector.Value));
                if (message is not null)
                    AssertMessage([message], refused.Message);
                break;
            default:
                throw new InvalidOperationException($"The vector kind {vector.Kind} has no harness.");
        }
    }

    [Fact]
    public void The_file_loads_and_has_vectors_of_every_kind()
    {
        var kinds = VectorFile.Current.Vectors.Select(v => v.Kind).Distinct().Order(StringComparer.Ordinal);

        Assert.Equal(
            [VectorFile.DecodeOnly, VectorFile.EncodeOnly, VectorFile.Refuse, VectorFile.Reject, VectorFile.RoundTrip],
            kinds);
    }

    [Fact]
    public void The_reasons_are_the_page_s_malformed_input_list_in_order()
    {
        Assert.Equal(WireFormatPage.Current.MalformedInput, VectorFile.Current.Reasons.Select(r => r.Rule));
    }

    [Fact]
    public void The_harness_knows_every_reason_and_refusal_code_and_no_other()
    {
        var file = VectorFile.Current;

        Assert.Equal(
            CauseMessages.Keys.Order(StringComparer.Ordinal),
            file.Reasons.Where(r => r.IsCause).Select(r => r.Code).Order(StringComparer.Ordinal));
        Assert.Equal(
            ViaCodes.Order(StringComparer.Ordinal),
            file.Reasons.Where(r => !r.IsCause).Select(r => r.Code).Order(StringComparer.Ordinal));
        Assert.Equal(
            RefusalExceptions.Keys.Order(StringComparer.Ordinal),
            file.Refusals.Select(r => r.Code).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_cause_and_refusal_code_is_used_by_a_vector_and_no_vector_uses_a_via_code()
    {
        var file = VectorFile.Current;
        var used = file.Vectors.Where(v => v.Kind == VectorFile.Reject).Select(v => v.Reason!).ToHashSet(StringComparer.Ordinal);
        var refused = file.Vectors.Where(v => v.Kind == VectorFile.Refuse).Select(v => v.Reason!).ToHashSet(StringComparer.Ordinal);

        var unusedCauses = file.Reasons.Where(r => r.IsCause && !used.Contains(r.Code)).Select(r => r.Code).ToList();
        var usedVias = file.Reasons.Where(r => !r.IsCause && used.Contains(r.Code)).Select(r => r.Code).ToList();
        var unusedRefusals = file.Refusals.Select(r => r.Code).Where(code => !refused.Contains(code)).ToList();

        Assert.True(unusedCauses.Count == 0, "No reject vector uses the causes " + string.Join(", ", unusedCauses));
        Assert.True(usedVias.Count == 0, "Reject vectors use the via codes " + string.Join(", ", usedVias));
        Assert.True(unusedRefusals.Count == 0, "No refuse vector uses the refusals " + string.Join(", ", unusedRefusals));
    }

    [Fact]
    public void Every_refusal_rule_is_quoted_from_the_page()
    {
        Assert.All(
            VectorFile.Current.Refusals,
            refusal => Assert.Contains(refusal.Rule, WireFormatPage.Current.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Vector_names_are_unique()
    {
        var names = VectorFile.Current.Vectors.Select(v => v.Name).ToList();

        Assert.Empty(names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void The_harness_knows_every_section_of_the_page_and_no_other()
    {
        Assert.Equal(
            PageSections.Keys.Order(StringComparer.Ordinal),
            WireFormatPage.Current.Sections.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Vectors_are_grouped_by_section_in_the_page_s_order()
    {
        // The page's own order of its sections, so that moving a section on the page is noticed.
        var sections = WireFormatPage.Current.Sections
            .SelectMany(title => PageSections.TryGetValue(title, out var prefixes)
                ? prefixes
                : throw new InvalidOperationException($"The harness does not know the page's section {title}."))
            .ToList();
        var order = VectorFile.Current.Vectors
            .Select(v => (v.Name, Index: sections.IndexOf(v.Name[..v.Name.IndexOf('.', StringComparison.Ordinal)])))
            .ToList();

        Assert.Empty(order.Where(v => v.Index < 0).Select(v => v.Name));
        for (var i = 1; i < order.Count; i++)
            Assert.True(order[i].Index >= order[i - 1].Index, $"{order[i].Name} comes after {order[i - 1].Name}.");
    }

    [Fact]
    public void Every_declared_type_is_used_by_a_vector()
    {
        var used = new HashSet<VectorType>();
        foreach (var vector in VectorFile.Current.Vectors)
            Collect(vector.Type, used);

        Assert.Empty(VectorFile.Current.Types.Values.Where(t => !used.Contains(t)).Select(t => t.Text));
    }

    [Fact]
    public void Every_row_of_the_page_s_byte_tables_has_one_vector_with_its_bytes()
    {
        var rows = WireFormatPage.Current.ByteTableRows;
        var documented = VectorFile.Current.Vectors.Where(v => v.Doc is not null).ToList();

        Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            rows.Select(r => r.Key).Order(StringComparer.Ordinal),
            documented.Select(v => v.Doc!).Order(StringComparer.Ordinal));

        var missing = RowsWithBytesInWords.Except(rows.Select(r => r.Key), StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            "The harness lists rows whose bytes are in words that the page does not have: " + string.Join(" | ", missing));

        foreach (var row in rows)
        {
            var vector = documented.Single(v => v.Doc == row.Key);
            var bytes = row.Cells[row.Header.ToList().IndexOf("Bytes")];
            if (RowsWithBytesInWords.Contains(row.Key))
            {
                Assert.False(
                    bytes == "No bytes" || LiteralBytes().IsMatch(bytes),
                    $"The page's row {row.Key} lists its bytes, but the harness lists it as one whose bytes are in words.");
            }
            else
            {
                Assert.True(
                    bytes == "No bytes" || LiteralBytes().IsMatch(bytes),
                    $"The page's row {row.Key} has the bytes {bytes}, which are neither literal bytes, such as `80 01`, " +
                    "nor No bytes. If the row describes its bytes in words, add it to RowsWithBytesInWords.");
                var hex = bytes == "No bytes" ? "" : bytes.Replace("`", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
                Assert.True(hex == vector.Hex, $"{vector.Name} has the bytes {vector.Hex}; the page's row {row.Key} has {hex}.");
            }

            var unknown = row.Header.Where(column => !ByteTableColumns.Contains(column)).ToList();
            Assert.True(unknown.Count == 0, $"The page's row {row.Key} has columns the harness does not compare: " +
                string.Join(", ", unknown));

            // A number in the Value column of a table of integers, and the Milliseconds and Ticks columns, is the
            // vector's value; the Zigzag value column is that value zigzag-encoded, and the value of the bytes.
            var numeric = vector.Type is PrimitiveType { Kind: not (Primitive.Double or Primitive.String or Primitive.Bool) };
            foreach (var column in new[] { "Value", "Milliseconds", "Ticks", "Zigzag value" })
            {
                var index = row.Header.ToList().IndexOf(column);
                if (index < 0 || (column == "Value" && !numeric))
                    continue;

                Assert.True(numeric, $"The page's row {row.Key} has a {column} column, but {vector.Name} is not a number.");
                var number = NumberCell().Match(row.Cells[index]);
                Assert.True(number.Success, $"The page's row {row.Key} has no number in its {column} column.");
                var cell = BigInteger.Parse(number.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
                var value = BigInteger.Parse(vector.Value!.ToJsonString().Trim('"'), CultureInfo.InvariantCulture);
                if (column == "Zigzag value")
                {
                    Assert.True(
                        cell == Zigzag(value) && cell == ReadLeb128(vector.Hex!),
                        $"The page's row {row.Key} has the zigzag value {cell}; {vector.Name} has the value {value}, " +
                        $"whose zigzag value is {Zigzag(value)}, and the bytes {vector.Hex}, whose value is {ReadLeb128(vector.Hex!)}.");
                }
                else
                {
                    Assert.True(cell == value, $"The page's row {row.Key} has {cell} in its {column} column; {vector.Name} has {value}.");
                }
            }

            // The Instant column starts with the instant in ISO 8601, which is the vector's milliseconds.
            var instantIndex = row.Header.ToList().IndexOf("Instant");
            if (instantIndex >= 0)
            {
                var text = row.Cells[instantIndex].Split(',')[0];
                var instant = DateTimeOffset.ParseExact(
                    text, ["yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ"], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal);
                var milliseconds = vector.Value!.GetValue<string>();
                Assert.True(
                    instant.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) == milliseconds,
                    $"The page's row {row.Key} is the instant {text}; {vector.Name} has {milliseconds} milliseconds.");
            }
        }
    }

    [Fact]
    public void The_file_is_in_its_layout_with_the_generated_depth_vectors()
    {
        var path = Repository.VectorFile;
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var expected = VectorFileLayout.Format(DepthVectors.Merge(VectorFile.ParseJson(text)));
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            if (text != expected)
                File.WriteAllText(path, expected);
            return;
        }

        var actualLines = text.Split('\n');
        var expectedLines = expected.Split('\n');
        for (var i = 0; i < Math.Max(actualLines.Length, expectedLines.Length); i++)
        {
            var actual = i < actualLines.Length ? actualLines[i] : "(end of file)";
            var wanted = i < expectedLines.Length ? expectedLines[i] : "(end of file)";
            Assert.True(
                actual == wanted,
                $"Line {i + 1} of the vector file differs from its layout or from the generated depth vectors. Run " +
                $"the tests with {WriteVariable}=1 to rewrite the file, then review the change.\n" +
                $"Expected: {Shorten(wanted)}\nActual:   {Shorten(actual)}");
        }
    }

    private static void AssertEncodes(WireVector vector, JsonNode? value, string hex)
    {
        Assert.True(
            Convert.ToHexStringLower(VectorCodec.Encode(vector.Type, value)) is var written && written == hex,
            $"{vector.Name}: the writer wrote {written}, not {hex}.");
    }

    private static void AssertDecodes(WireVector vector, string hex, JsonNode? value)
    {
        var read = VectorCodec.Decode(vector.Type, Convert.FromHexString(hex));

        Assert.Equal(VectorValues.Canonical(vector.Type, value), VectorValues.Canonical(vector.Type, read));
    }

    // The cause code of OahuReader's message: the one code with a pattern that the message matches.
    private static string CauseOf(string message)
    {
        var codes = CauseMessages.Where(cause => cause.Value.Any(pattern => Matches(pattern, message))).Select(c => c.Key).ToList();
        return codes.Count == 1 ? codes[0] : $"(no single cause: {string.Join(", ", codes)}) {message}";
    }

    private static void AssertMessage(string[] patterns, string message)
    {
        Assert.True(
            patterns.Any(pattern => Matches(pattern, message)),
            $"The message \"{message}\" does not start with any of: {string.Join(" | ", patterns)}");
    }

    // Each pattern is a message's fixed text, with {0} where the message has a number or a byte.
    private static bool Matches(string pattern, string message) =>
        Regex.IsMatch(message, "^" + Regex.Escape(pattern).Replace("\\{0}", "[0-9a-f-]+", StringComparison.Ordinal));

    private static BigInteger Zigzag(BigInteger value) => value >= 0 ? 2 * value : -2 * value - 1;

    // Unsigned LEB128, read here from the page's rules rather than with OahuReader.
    private static BigInteger ReadLeb128(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var value = BigInteger.Zero;
        for (var i = 0; i < bytes.Length; i++)
            value |= new BigInteger(bytes[i] & 0x7F) << (7 * i);
        return value;
    }

    private static void Collect(VectorType type, HashSet<VectorType> used)
    {
        if (!used.Add(type))
            return;

        switch (type)
        {
            case NullableType nullable:
                Collect(nullable.Inner, used);
                break;
            case ListType list:
                Collect(list.Item, used);
                break;
            case RecordType record:
                foreach (var field in record.Fields)
                    Collect(field.Type, used);
                break;
            case UnionType union:
                foreach (var field in union.Cases.SelectMany(c => c.Fields))
                    Collect(field.Type, used);
                break;
        }
    }

    private static string Shorten(string line) => line.Length <= 200 ? line : line[..200] + "…";

    [GeneratedRegex("^`[0-9a-f]{2}( [0-9a-f]{2})*`$")]
    private static partial Regex LiteralBytes();

    [GeneratedRegex("^(-?[0-9][0-9,]*)( \\(.*\\))?$")]
    private static partial Regex NumberCell();
}
