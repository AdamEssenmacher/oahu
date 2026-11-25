using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdamE.Oahu.Tests.Conformance;

internal sealed record VectorReason(string Code, bool IsCause, string Rule);

internal sealed record VectorRefusal(string Code, string Rule);

internal sealed record WireVector(
    string Name,
    string TypeText,
    VectorType Type,
    string Kind,
    JsonNode? Value,
    string? Hex,
    string? Writes,
    JsonNode? Reads,
    string? Reason,
    string? Doc);

/// <summary>
/// The vector file, tests/conformance/wire-format/vectors.json, loaded strictly: an unknown field, type, kind or
/// reason code, or a value that does not match its type, throws <see cref="InvalidDataException"/>.
/// </summary>
internal sealed partial class VectorFile
{
    public const string RoundTrip = "round-trip";
    public const string DecodeOnly = "decode-only";
    public const string EncodeOnly = "encode-only";
    public const string Reject = "reject";
    public const string Refuse = "refuse";

    // The file's values nest deeply, and a harness's JSON parser must allow this many levels.
    public const int MaxJsonDepth = 256;

    private static readonly Lazy<VectorFile> LazyCurrent = new(() => Parse(File.ReadAllText(Repository.VectorFile)));

    private static readonly Dictionary<string, (string[] Required, string[] Optional)> KindFields =
        new(StringComparer.Ordinal)
        {
            [RoundTrip] = (["value", "hex"], []),
            [DecodeOnly] = (["value", "hex"], ["writes"]),
            [EncodeOnly] = (["value", "hex", "reads"], []),
            [Reject] = (["hex", "reason"], []),
            [Refuse] = (["value", "reason"], []),
        };

    private VectorFile(
        IReadOnlyList<VectorReason> reasons,
        IReadOnlyList<VectorRefusal> refusals,
        IReadOnlyDictionary<string, VectorType> types,
        IReadOnlyList<WireVector> vectors)
    {
        Reasons = reasons;
        Refusals = refusals;
        Types = types;
        Vectors = vectors;
    }

    public static VectorFile Current => LazyCurrent.Value;

    public static JsonDocumentOptions JsonOptions { get; } = new()
    {
        MaxDepth = MaxJsonDepth,
        AllowDuplicateProperties = false,
    };

    public IReadOnlyList<VectorReason> Reasons { get; }

    public IReadOnlyList<VectorRefusal> Refusals { get; }

    public IReadOnlyDictionary<string, VectorType> Types { get; }

    public IReadOnlyList<WireVector> Vectors { get; }

    public static JsonObject ParseJson(string text) =>
        JsonNode.Parse(text, documentOptions: JsonOptions) as JsonObject
        ?? throw new InvalidDataException("The vector file is not a JSON object.");

    public static VectorFile Parse(string text)
    {
        var root = ParseJson(text);
        CheckKeys(root, "the file", ["about", "reasons", "refusals", "types", "vectors"], []);
        String(root, "about", "the file");

        var reasons = Array(root, "reasons", "the file").Select(ParseReason).ToList();
        var refusals = Array(root, "refusals", "the file").Select(ParseRefusal).ToList();
        CheckUnique(reasons.Select(r => r.Code), "reason code");
        CheckUnique(refusals.Select(r => r.Code), "refusal code");

        var types = ParseTypes(Array(root, "types", "the file"));

        var causes = reasons.Where(r => r.IsCause).Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var refusalCodes = refusals.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var vectors = Array(root, "vectors", "the file")
            .Select(node => ParseVector(node, types, causes, refusalCodes))
            .ToList();
        CheckUnique(vectors.Select(v => v.Name), "vector name");

        return new VectorFile(reasons, refusals, types, vectors);
    }

    public static JsonArray Array(JsonObject parent, string key, string where) =>
        parent[key] as JsonArray ?? throw new InvalidDataException($"In {where}, \"{key}\" is not an array.");

    private static VectorReason ParseReason(JsonNode? node)
    {
        var entry = Object(node, "a reason");
        CheckKeys(entry, "a reason", ["code", "kind", "rule"], []);
        var code = Code(String(entry, "code", "a reason"));
        var kind = String(entry, "kind", code);
        if (kind is not ("cause" or "via"))
            throw new InvalidDataException($"The reason {code} has the kind \"{kind}\", not \"cause\" or \"via\".");

        return new VectorReason(code, kind == "cause", String(entry, "rule", code));
    }

    private static VectorRefusal ParseRefusal(JsonNode? node)
    {
        var entry = Object(node, "a refusal");
        CheckKeys(entry, "a refusal", ["code", "rule"], []);
        var code = Code(String(entry, "code", "a refusal"));
        return new VectorRefusal(code, String(entry, "rule", code));
    }

    public static Dictionary<string, VectorType> ParseTypes(JsonArray entries)
    {
        // Two passes, so that a type can refer to itself and to types declared after it.
        var types = new Dictionary<string, VectorType>(StringComparer.Ordinal);
        var declarations = new List<(JsonObject Entry, VectorType Type)>();
        foreach (var node in entries)
        {
            var entry = Object(node, "a type");
            var name = Identifier(String(entry, "name", "a type"), "type name");
            if (VectorTypeParser.IsPrimitiveName(name) || name is "list" or "batch" || types.ContainsKey(name))
                throw new InvalidDataException($"The type name {name} is declared twice or is reserved.");

            var shapes = new[] { "record", "enum", "union" }.Where(entry.ContainsKey).ToList();
            if (shapes.Count != 1)
                throw new InvalidDataException($"The type {name} must have exactly one of record, enum and union.");

            CheckKeys(entry, name, ["name", shapes[0]], []);
            VectorType type = shapes[0] switch
            {
                "record" => new RecordType(name),
                "union" => new UnionType(name),
                _ => new EnumType(name, EnumValues(entry, name)),
            };
            types.Add(name, type);
            declarations.Add((entry, type));
        }

        foreach (var (entry, type) in declarations)
        {
            switch (type)
            {
                case RecordType record:
                    record.Fields.AddRange(Fields(Array(entry, "record", record.Text), record.Text, types));
                    break;
                case UnionType union:
                    foreach (var caseNode in Array(entry, "union", union.Text))
                    {
                        var where = "a case of " + union.Text;
                        var caseEntry = Object(caseNode, where);
                        CheckKeys(caseEntry, where, ["name", "fields"], []);
                        var caseName = Identifier(String(caseEntry, "name", where), "case name");
                        where = union.Text + "." + caseName;
                        union.Cases.Add(new VectorCase(caseName, Fields(Array(caseEntry, "fields", where), where, types)));
                    }

                    if (union.Cases.Count == 0)
                        throw new InvalidDataException($"The union {union.Text} has no cases.");

                    CheckUnique(union.Cases.Select(c => c.Name), "case name of " + union.Text);
                    break;
            }
        }

        VectorTypeParser.CheckRecordCycles(types.Values);
        foreach (var type in types.Values)
            VectorTypeParser.CheckListItems(type);

        return types;
    }

    private static string[] EnumValues(JsonObject entry, string name)
    {
        var values = Array(entry, "enum", name)
            .Select(v => Identifier(v is JsonValue value && value.TryGetValue<string>(out var s)
                ? s
                : throw new InvalidDataException($"A value of the enum {name} is not a string."), "enum value"))
            .ToArray();
        if (values.Length == 0)
            throw new InvalidDataException($"The enum {name} has no values.");

        CheckUnique(values, "value of " + name);
        return values;
    }

    private static List<VectorField> Fields(JsonArray entries, string where, IReadOnlyDictionary<string, VectorType> types)
    {
        var fields = new List<VectorField>();
        foreach (var node in entries)
        {
            var entry = Object(node, "a field of " + where);
            CheckKeys(entry, "a field of " + where, ["name", "type"], []);
            var name = Identifier(String(entry, "name", where), "field name");
            var type = VectorTypeParser.Parse(String(entry, "type", where + "." + name), types, wholeType: false);
            fields.Add(new VectorField(name, type));
        }

        CheckUnique(fields.Select(f => f.Name), "field name of " + where);
        return fields;
    }

    private static WireVector ParseVector(
        JsonNode? node,
        IReadOnlyDictionary<string, VectorType> types,
        HashSet<string> causes,
        HashSet<string> refusals)
    {
        var entry = Object(node, "a vector");
        var name = String(entry, "name", "a vector");
        if (!NamePattern().IsMatch(name))
            throw new InvalidDataException($"The vector name \"{name}\" is not a section, a dot and a lowercase name.");

        var kind = String(entry, "kind", name);
        if (!KindFields.TryGetValue(kind, out var fields))
            throw new InvalidDataException($"The vector {name} has the unknown kind \"{kind}\".");

        CheckKeys(entry, name, ["name", "type", "kind", .. fields.Required], [.. fields.Optional, "doc", "note"]);
        var typeText = String(entry, "type", name);
        var type = VectorTypeParser.Parse(typeText, types, wholeType: true);
        if (entry.ContainsKey("note"))
            String(entry, "note", name);

        var value = entry.ContainsKey("value") ? entry["value"] : null;
        var reads = entry.ContainsKey("reads") ? entry["reads"] : null;
        try
        {
            if (entry.ContainsKey("value"))
                VectorValues.Canonical(type, value);
            if (entry.ContainsKey("reads"))
                VectorValues.Canonical(type, reads);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"The vector {name} has a value that is not valid: {e.Message}", e);
        }

        string? reason = null;
        if (entry.ContainsKey("reason"))
        {
            reason = String(entry, "reason", name);
            var known = kind == Refuse ? refusals : causes;
            if (!known.Contains(reason))
            {
                throw new InvalidDataException(
                    $"The vector {name} has the reason \"{reason}\", which is not a {(kind == Refuse ? "refusal" : "cause")} code.");
            }
        }

        return new WireVector(
            name,
            typeText,
            type,
            kind,
            value,
            entry.ContainsKey("hex") ? Hex(entry, "hex", name) : null,
            entry.ContainsKey("writes") ? Hex(entry, "writes", name) : null,
            reads,
            reason,
            entry.ContainsKey("doc") ? String(entry, "doc", name) : null);
    }

    private static void CheckKeys(JsonObject entry, string where, string[] required, string[] optional)
    {
        foreach (var key in required)
        {
            if (!entry.ContainsKey(key))
                throw new InvalidDataException($"In {where}, the field \"{key}\" is missing.");
        }

        foreach (var (key, _) in entry)
        {
            if (!required.Contains(key) && !optional.Contains(key))
                throw new InvalidDataException($"In {where}, the field \"{key}\" is not known.");
        }
    }

    private static void CheckUnique(IEnumerable<string> names, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!seen.Add(name))
                throw new InvalidDataException($"The {what} \"{name}\" is used twice.");
        }
    }

    private static JsonObject Object(JsonNode? node, string what) =>
        node as JsonObject ?? throw new InvalidDataException($"{what} is not a JSON object: {node?.ToJsonString()}");

    private static string String(JsonObject entry, string key, string where) =>
        entry[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new InvalidDataException($"In {where}, \"{key}\" is not a string.");

    private static string Hex(JsonObject entry, string key, string where)
    {
        var text = String(entry, key, where);
        if (!HexPattern().IsMatch(text))
            throw new InvalidDataException($"In {where}, \"{key}\" is not lowercase hex bytes with no separators.");

        return text;
    }

    private static string Code(string code) =>
        CodePattern().IsMatch(code) ? code : throw new InvalidDataException($"The code \"{code}\" is not valid.");

    private static string Identifier(string name, string what) =>
        IdentifierPattern().IsMatch(name)
            ? name
            : throw new InvalidDataException($"The {what} \"{name}\" does not start with a capital letter or has a " +
                "character other than a letter or digit.");

    [GeneratedRegex("^[a-z]+\\.[a-z0-9][a-z0-9.+-]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^([0-9a-f]{2})*$")]
    private static partial Regex HexPattern();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex IdentifierPattern();
}
