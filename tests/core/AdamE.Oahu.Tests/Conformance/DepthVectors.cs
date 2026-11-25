using System.Text.Json.Nodes;

namespace AdamE.Oahu.Tests.Conformance;

/// <summary>
/// Generates the vectors of the nesting depth section, whose values and bytes are too long to write by hand.
/// </summary>
/// <remarks>
/// The bytes are built by repeating each level's bytes, worked out from docs/wire-format.md, not with
/// <see cref="Wire.OahuWriter"/>. Every name in the strings is empty, and every double is 0. A rejected vector's bytes
/// are what a writer without the depth limit writes: the value too deep is complete, so the reader rejects it only for
/// its depth.
/// </remarks>
internal static class DepthVectors
{
    public const string Prefix = "depth.";

    private const string ZeroDouble = "0000000000000000";

    // A DoublePoint of two zeros: 16 bytes.
    private static readonly string ZeroPointHex = Repeat("00", 16);

    public static IEnumerable<JsonObject> Generate()
    {
        // A record with a list of itself. Each level is its name, 00, then its list's count: 01, or 00 for the last.
        yield return RoundTrip("depth.tree-64", "Tree", Tree(64), Chain("0001", "0000", 64),
            "64 Trees, each in its parent's Children, the last at depth 64");
        yield return Rejected("depth.tree-65-rejected", "Tree", Chain("0001", "0000", 65),
            "65 Trees; the 65th is at depth 65");
        yield return Refused("depth.tree-65-refused", "Tree", Tree(65), "65 Trees; the 65th is at depth 65");

        // A record with a nullable field of itself: its name, 00, then the presence byte of Next.
        yield return RoundTrip("depth.link-64", "Link", Link(64), Chain("0001", "0000", 64),
            "64 Links, each the Next of the one before");
        yield return Rejected("depth.link-65-rejected", "Link", Chain("0001", "0000", 65),
            "65 Links; the 65th is at depth 65");
        yield return Refused("depth.link-65-refused", "Link", Link(65), "65 Links; the 65th is at depth 65");

        // A record with a field that is a record with no fields, which is no bytes but still counts a level. Each
        // level is the presence byte of Next alone.
        yield return RoundTrip("depth.marked-63", "Marked", Marked(63), Chain("01", "00", 63),
            "63 Marked; the Empty Marker of the 63rd is at depth 64");
        yield return Rejected("depth.marked-64-rejected", "Marked", Chain("01", "00", 64),
            "64 Marked; the Empty Marker of the 64th is at depth 65, although it is no bytes");
        yield return Refused("depth.marked-64-refused", "Marked", Marked(64),
            "64 Marked; the Empty Marker of the 64th is at depth 65, although it is no bytes");

        // A union with a list of itself in a case. A union and its case are one level: the Group index, 01, then
        // the count of Shapes.
        yield return RoundTrip("depth.shape-64-groups", "Shape", Groups(64), Chain("0101", "0100", 64),
            "64 Groups, each in the Shapes of the one before; a union and its case are one level");
        yield return Rejected("depth.shape-65-groups-rejected", "Shape", Chain("0101", "0100", 65),
            "65 Groups; the 65th is at depth 65");
        yield return Refused("depth.shape-65-groups-refused", "Shape", Groups(65),
            "65 Groups; the 65th is at depth 65");

        // A union with a case of no fields, Stop, inside a chain of Steps, each the Next of the one before. A union and
        // its case are one level, so the Stop is at the depth of its Steps value: Step's index, 01, for each level,
        // then Stop's index, 00, alone.
        yield return RoundTrip("depth.steps-64", "Steps", Steps(64), Chain("01", "00", 64),
            "63 Steps, each the Next of the one before, then a Stop at depth 64: a case with no fields");
        yield return Rejected("depth.steps-65-rejected", "Steps", Chain("01", "00", 65),
            "64 Steps, then a Stop at depth 65: a case with no fields still counts its union's level");
        yield return Refused("depth.steps-65-refused", "Steps", Steps(65),
            "64 Steps, then a Stop at depth 65: a case with no fields still counts its union's level");

        // A Circle's Center is a record in a field of a union's case, one level below its Shape.
        yield return RoundTrip("depth.shape-circle-63", "Shape", Circle(63), CircleHex(63),
            "62 Groups, then a Circle at depth 63, whose Center is at depth 64");
        yield return Rejected("depth.shape-circle-64-rejected", "Shape", CircleHex(64),
            "63 Groups, then a Circle at depth 64, whose Center is at depth 65");

        // The Node of the page's depth table. Its Origin is a record one level below it.
        yield return RoundTrip("depth.node-63", "Node", Node(63), NodeHex(63),
            "63 Nodes; the Origin of the 63rd is at depth 64");
        yield return Rejected("depth.node-64-rejected", "Node", NodeHex(64),
            "64 Nodes; the Origin of the 64th is at depth 65, although DoublePoint never contains itself");
        yield return Refused("depth.node-64-refused", "Node", Node(64),
            "64 Nodes; the Origin of the 64th is at depth 65, although DoublePoint never contains itself");

        // A Node inside a list inside a nullable value inside a record: the Folder's depth plus 1. The Folder is
        // the presence byte of Nodes, 01, then its count, 01.
        yield return RoundTrip("depth.folder-62-nodes", "Folder", new JsonObject { ["Nodes"] = new JsonArray(Node(62)) },
            "0101" + NodeHex(62), "A Folder, then 62 Nodes; the Origin of the 62nd is at depth 64");
        yield return Rejected("depth.folder-63-nodes-rejected", "Folder", "0101" + NodeHex(63),
            "A Folder, then 63 Nodes; the Origin of the 63rd is at depth 65");

        // Each Node of a message whose value is a list of Nodes is at depth 1: the list adds none.
        yield return RoundTrip("depth.list-of-node-63", "list<Node>", new JsonArray(Node(63)), "01" + NodeHex(63),
            "A list of one Node, then 62 more inside it; the Origin of the 63rd is at depth 64");
        yield return Rejected("depth.list-of-node-64-rejected", "list<Node>", "01" + NodeHex(64),
            "A list of one Node, then 63 more inside it; the Origin of the 64th is at depth 65");
    }

    /// <summary>
    /// Replaces the vectors of the depth section in <paramref name="root"/> with the generated ones, in place. They
    /// must be one block; when there are none, the generated ones go at the end.
    /// </summary>
    public static JsonObject Merge(JsonObject root)
    {
        var vectors = VectorFile.Array(root, "vectors", "the file");
        var positions = Enumerable.Range(0, vectors.Count)
            .Where(i => vectors[i]?["name"]?.GetValue<string>().StartsWith(Prefix, StringComparison.Ordinal) == true)
            .ToList();
        if (positions.Count > 0 && positions[^1] - positions[0] != positions.Count - 1)
            throw new InvalidDataException("The vectors of the depth section are not one block.");

        var start = positions.Count > 0 ? positions[0] : vectors.Count;
        for (var i = positions.Count - 1; i >= 0; i--)
            vectors.RemoveAt(positions[i]);

        foreach (var vector in Generate())
            vectors.Insert(start++, vector);

        return root;
    }

    private static JsonObject RoundTrip(string name, string type, JsonNode value, string hex, string note) =>
        new()
        {
            ["name"] = name,
            ["type"] = type,
            ["kind"] = VectorFile.RoundTrip,
            ["value"] = value,
            ["hex"] = hex,
            ["note"] = note,
        };

    private static JsonObject Rejected(string name, string type, string hex, string note) =>
        new()
        {
            ["name"] = name,
            ["type"] = type,
            ["kind"] = VectorFile.Reject,
            ["hex"] = hex,
            ["reason"] = "too-deep",
            ["note"] = note,
        };

    private static JsonObject Refused(string name, string type, JsonNode value, string note) =>
        new()
        {
            ["name"] = name,
            ["type"] = type,
            ["kind"] = VectorFile.Refuse,
            ["value"] = value,
            ["reason"] = "too-deep",
            ["note"] = note,
        };

    private static JsonObject Tree(int levels) => new()
    {
        ["Name"] = "",
        ["Children"] = levels > 1 ? new JsonArray(Tree(levels - 1)) : new JsonArray(),
    };

    private static JsonObject Link(int levels) => new()
    {
        ["Name"] = "",
        ["Next"] = levels > 1 ? Link(levels - 1) : null,
    };

    private static JsonObject Marked(int levels) => new()
    {
        ["Marker"] = new JsonObject(),
        ["Next"] = levels > 1 ? Marked(levels - 1) : null,
    };

    private static JsonObject Groups(int levels) => new()
    {
        ["Group"] = new JsonObject
        {
            ["Shapes"] = levels > 1 ? new JsonArray(Groups(levels - 1)) : new JsonArray(),
        },
    };

    // levels - 1 Steps around a Stop.
    private static JsonObject Steps(int levels) => levels > 1
        ? new JsonObject { ["Step"] = new JsonObject { ["Next"] = Steps(levels - 1) } }
        : new JsonObject { ["Stop"] = new JsonObject() };

    // levels - 1 Groups around a Circle.
    private static JsonObject Circle(int levels) => levels > 1
        ? new JsonObject { ["Group"] = new JsonObject { ["Shapes"] = new JsonArray(Circle(levels - 1)) } }
        : new JsonObject { ["Circle"] = new JsonObject { ["Center"] = ZeroPoint(), ["Radius"] = ZeroDouble } };

    // Each Group is its index, 01, and a count of 01; the Circle is its index, 00, its Center and its Radius.
    private static string CircleHex(int levels) => Repeat("0101", levels - 1) + "00" + ZeroPointHex + Repeat("00", 8);

    private static JsonObject Node(int levels) => new()
    {
        ["Name"] = "",
        ["Origin"] = ZeroPoint(),
        ["Children"] = levels > 1 ? new JsonArray(Node(levels - 1)) : new JsonArray(),
    };

    // Each Node is its name, 00, its Origin, then the count of its Children: 01, or 00 for the last.
    private static string NodeHex(int levels) => Chain("00" + ZeroPointHex + "01", "00" + ZeroPointHex + "00", levels);

    private static JsonObject ZeroPoint() => new() { ["X"] = ZeroDouble, ["Y"] = ZeroDouble };

    private static string Chain(string level, string last, int levels) => Repeat(level, levels - 1) + last;

    private static string Repeat(string hex, int count) => string.Concat(Enumerable.Repeat(hex, count));
}
