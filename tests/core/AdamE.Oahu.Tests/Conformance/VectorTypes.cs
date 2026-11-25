namespace AdamE.Oahu.Tests.Conformance;

/// <summary>The type of a vector's value, as a type expression of the vector file names it.</summary>
internal abstract class VectorType
{
    public abstract string Text { get; }

    public override string ToString() => Text;
}

internal enum Primitive
{
    Bool,
    Int,
    Long,
    Double,
    String,
    Instant,
    Duration,
    VarUInt,
    Count,
}

internal sealed class PrimitiveType(Primitive kind, string text) : VectorType
{
    public Primitive Kind { get; } = kind;

    public override string Text { get; } = text;
}

internal sealed class NullableType(VectorType inner) : VectorType
{
    public VectorType Inner { get; } = inner;

    public override string Text => Inner.Text + "?";
}

internal sealed class ListType(VectorType item) : VectorType
{
    public VectorType Item { get; } = item;

    public override string Text => "list<" + Item.Text + ">";
}

internal sealed record VectorField(string Name, VectorType Type);

internal sealed class RecordType(string name) : VectorType
{
    public List<VectorField> Fields { get; } = [];

    public override string Text { get; } = name;
}

internal sealed class EnumType(string name, IReadOnlyList<string> values) : VectorType
{
    public IReadOnlyList<string> Values { get; } = values;

    public override string Text { get; } = name;
}

internal sealed record VectorCase(string Name, IReadOnlyList<VectorField> Fields);

internal sealed class UnionType(string name) : VectorType
{
    public List<VectorCase> Cases { get; } = [];

    public override string Text { get; } = name;
}

/// <summary>Parses the type expressions of the vector file.</summary>
/// <remarks>
/// <code>
/// type   = single [ "?" ]
/// single = "list&lt;" type "&gt;" | primitive | declared
/// </code>
/// No whitespace is allowed. <c>varuint</c> and <c>count</c> are allowed only as a vector's whole type. Once every
/// declared type has its fields, <see cref="CheckRecordCycles"/> rejects a record that contains itself through record
/// fields alone, which has no value that ends, and then <see cref="CheckListItems"/> rejects a list whose item type is
/// a record that is no bytes, which the format gives no bytes.
/// </remarks>
internal static class VectorTypeParser
{
    private static readonly Dictionary<string, Primitive> Primitives = new(StringComparer.Ordinal)
    {
        ["bool"] = Primitive.Bool,
        ["int"] = Primitive.Int,
        ["long"] = Primitive.Long,
        ["double"] = Primitive.Double,
        ["string"] = Primitive.String,
        ["instant"] = Primitive.Instant,
        ["duration"] = Primitive.Duration,
        ["varuint"] = Primitive.VarUInt,
        ["count"] = Primitive.Count,
    };

    public static bool IsPrimitiveName(string name) => Primitives.ContainsKey(name);

    public static VectorType Parse(string text, IReadOnlyDictionary<string, VectorType> declared, bool wholeType)
    {
        var position = 0;
        var type = ParseType(text, ref position, declared, wholeType);
        if (position != text.Length)
            throw Invalid(text, $"unexpected '{text[position]}' at position {position}");

        return type;
    }

    private static VectorType ParseType(
        string text, ref int position, IReadOnlyDictionary<string, VectorType> declared, bool wholeType)
    {
        var start = position;
        while (position < text.Length && char.IsAsciiLetterOrDigit(text[position]))
            position++;

        var name = text[start..position];
        if (name.Length == 0)
            throw Invalid(text, $"a type name is missing at position {start}");

        VectorType type;
        if (name == "list")
        {
            Expect(text, ref position, '<');
            var item = ParseType(text, ref position, declared, wholeType: false);
            Expect(text, ref position, '>');
            type = new ListType(item);
        }
        else if (Primitives.TryGetValue(name, out var primitive))
        {
            if (primitive is Primitive.VarUInt or Primitive.Count && !wholeType)
                throw Invalid(text, $"{name} is allowed only as a vector's whole type");

            type = new PrimitiveType(primitive, name);
        }
        else if (!declared.TryGetValue(name, out type!))
        {
            throw Invalid(text, $"the type {name} is not declared");
        }

        if (position < text.Length && text[position] == '?')
        {
            if (wholeType && type is PrimitiveType { Kind: Primitive.VarUInt or Primitive.Count })
                throw Invalid(text, $"{name} cannot be nullable");

            position++;
            type = new NullableType(type);
        }

        return type;
    }

    public static void CheckListItems(VectorType type) => CheckListItems(type, []);

    private static void CheckListItems(VectorType type, HashSet<VectorType> seen)
    {
        if (!seen.Add(type))
            return;

        switch (type)
        {
            case NullableType nullable:
                CheckListItems(nullable.Inner, seen);
                break;
            case ListType list:
                if (list.Item is RecordType item && IsNoBytes(item))
                {
                    throw new InvalidDataException(
                        $"The type {list.Text} is not valid: its item type is a record that is no bytes.");
                }

                CheckListItems(list.Item, seen);
                break;
            case RecordType record:
                foreach (var field in record.Fields)
                    CheckListItems(field.Type, seen);
                break;
            case UnionType union:
                foreach (var field in union.Cases.SelectMany(c => c.Fields))
                    CheckListItems(field.Type, seen);
                break;
        }
    }

    /// <summary>
    /// Rejects a record that contains itself through record fields alone: a field whose type is the record itself,
    /// or a record whose fields lead back to it through other records' fields. A field reached through a list, a
    /// nullable value or a union does not make such a cycle, as the page's Records section says.
    /// </summary>
    public static void CheckRecordCycles(IEnumerable<VectorType> types)
    {
        var done = new HashSet<RecordType>();
        foreach (var record in types.OfType<RecordType>())
            CheckRecordCycles(record, [], done);
    }

    private static void CheckRecordCycles(
        RecordType record, List<(RecordType Record, VectorField Field)> path, HashSet<RecordType> done)
    {
        if (done.Contains(record))
            return;

        var start = path.FindIndex(step => step.Record == record);
        if (start >= 0)
        {
            var steps = path.Skip(start).Select(step => $"{step.Record.Text}.{step.Field.Name} is {step.Field.Type.Text}");
            throw new InvalidDataException(
                $"The record {record.Text} contains itself through record fields alone: {string.Join(", ", steps)}.");
        }

        foreach (var field in record.Fields)
        {
            if (field.Type is not RecordType inner)
                continue;

            path.Add((record, field));
            CheckRecordCycles(inner, path, done);
            path.RemoveAt(path.Count - 1);
        }

        done.Add(record);
    }

    // A record is no bytes when all its fields are records that are no bytes. CheckRecordCycles has already rejected
    // a cycle made only of record fields, which the contract rules forbid, so this ends.
    public static bool IsNoBytes(RecordType record) =>
        record.Fields.All(field => field.Type is RecordType inner && IsNoBytes(inner));

    private static void Expect(string text, ref int position, char expected)
    {
        if (position >= text.Length || text[position] != expected)
            throw Invalid(text, $"'{expected}' is expected at position {position}");

        position++;
    }

    private static InvalidDataException Invalid(string text, string problem) =>
        new($"The type expression \"{text}\" is not valid: {problem}.");
}
