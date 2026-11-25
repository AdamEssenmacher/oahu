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
    Unit,
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

/// <summary>A keyed list's batch of operations on rows of <see cref="Row"/>, a record or a union with a Key.</summary>
internal sealed class BatchType(VectorType row) : VectorType
{
    public VectorType Row { get; } = row;

    public override string Text => "batch<" + Row.Text + ">";
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
/// whole  = type | "batch&lt;" declared "&gt;"
/// type   = single [ "?" ]
/// single = "list&lt;" type "&gt;" | primitive | declared
/// </code>
/// A vector's type is a <c>whole</c>; a field's type is a <c>type</c>. No whitespace is allowed. <c>varuint</c>, <c>count</c>, <c>unit</c> and <c>batch&lt;Row&gt;</c> are allowed only as
/// a vector's whole type, and never nullable. A batch's row type is a declared record or union, not nullable, whose
/// record, or every case, has a field named <c>Key</c> of type <c>string</c>. Once every
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
        ["unit"] = Primitive.Unit,
    };

    // The primitives that are allowed only as a vector's whole type, never inside another type and never nullable:
    // the building blocks of the format, and the no-value type, which a contract uses only as an events stream's type.
    private static bool IsWholeTypeOnly(Primitive primitive) =>
        primitive is Primitive.VarUInt or Primitive.Count or Primitive.Unit;

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
        if (name == "batch")
        {
            if (!wholeType)
                throw Invalid(text, "batch is allowed only as a vector's whole type");

            Expect(text, ref position, '<');
            var row = ParseType(text, ref position, declared, wholeType: false);
            Expect(text, ref position, '>');
            CheckRow(text, row);
            if (position < text.Length && text[position] == '?')
                throw Invalid(text, "batch cannot be nullable");

            return new BatchType(row);
        }

        if (name == "list")
        {
            Expect(text, ref position, '<');
            var item = ParseType(text, ref position, declared, wholeType: false);
            Expect(text, ref position, '>');
            type = new ListType(item);
        }
        else if (Primitives.TryGetValue(name, out var primitive))
        {
            if (IsWholeTypeOnly(primitive) && !wholeType)
                throw Invalid(text, $"{name} is allowed only as a vector's whole type");

            type = new PrimitiveType(primitive, name);
        }
        else if (!declared.TryGetValue(name, out type!))
        {
            throw Invalid(text, $"the type {name} is not declared");
        }

        if (position < text.Length && text[position] == '?')
        {
            if (type is PrimitiveType primitiveType && IsWholeTypeOnly(primitiveType.Kind))
                throw Invalid(text, $"{name} cannot be nullable");

            position++;
            type = new NullableType(type);
        }

        return type;
    }

    // A row is a record or a union that implements IOahuKeyed through a positional string Key: on the record, or on
    // every case of the union.
    private static void CheckRow(string text, VectorType row)
    {
        static bool HasKey(IEnumerable<VectorField> fields) =>
            fields.Any(field => field.Name == "Key" && field.Type is PrimitiveType { Kind: Primitive.String });

        var valid = row switch
        {
            RecordType record => HasKey(record.Fields),
            UnionType union => union.Cases.All(@case => HasKey(@case.Fields)),
            _ => false,
        };
        if (!valid)
        {
            throw Invalid(text, $"the row type {row.Text} is not a record or a union whose record or every case has " +
                "a string field named Key");
        }
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
