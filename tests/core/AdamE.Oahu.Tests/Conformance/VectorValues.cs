using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdamE.Oahu.Tests.Conformance;

/// <summary>
/// The JSON representation of values in the vector file: it checks that a value matches its type and renders it in
/// one canonical form, so two values are equal when their canonical forms are.
/// </summary>
internal static partial class VectorValues
{
    /// <summary>Renders <paramref name="value"/> canonically, or throws if it is not a value of <paramref name="type"/>.</summary>
    public static string Canonical(VectorType type, JsonNode? value)
    {
        var builder = new StringBuilder();
        Append(builder, type, value);
        return builder.ToString();
    }

    public static bool ToBool(JsonNode? value) =>
        value?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid("a bool is true or false", value),
        };

    public static int ToInt(JsonNode? value) =>
        value is JsonValue json && json.GetValueKind() == JsonValueKind.Number && json.TryGetValue<int>(out var number)
            ? number
            : throw Invalid("an int or a count is a JSON integer from -2147483648 to 2147483647", value);

    public static long ToLong(JsonNode? value) =>
        TextOf(value) is { } text && SignedPattern().IsMatch(text) &&
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw Invalid("a long, an instant or a duration is a decimal string of a 64-bit signed integer", value);

    public static ulong ToVarUInt(JsonNode? value) =>
        TextOf(value) is { } text && UnsignedPattern().IsMatch(text) &&
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw Invalid("a varuint is a decimal string of a 64-bit unsigned integer", value);

    public static double ToDouble(JsonNode? value) =>
        TextOf(value) is { } text && DoubleBitsPattern().IsMatch(text)
            ? BitConverter.UInt64BitsToDouble(ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
            : throw Invalid("a double is its bit pattern as 16 lowercase hex digits", value);

    public static string DoubleBits(double value) =>
        BitConverter.DoubleToUInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture);

    public static string ToText(JsonNode? value)
    {
        if (TextOf(value) is { } text)
        {
            if (!IsWellFormed(text))
                throw Invalid("a JSON string is well-formed UTF-16; write other strings as arrays of code units", value);

            return text;
        }

        if (value is JsonArray units)
        {
            var chars = units
                .Select(unit => unit is JsonValue json && json.TryGetValue<int>(out var code) && code is >= 0 and <= 0xFFFF
                    ? (char)code
                    : throw Invalid("a code unit is an integer from 0 to 65535", unit))
                .ToArray();
            var fromUnits = new string(chars);
            if (IsWellFormed(fromUnits))
                throw Invalid("a well-formed string is a JSON string, not an array of code units", value);

            return fromUnits;
        }

        throw Invalid("a string is a JSON string or an array of UTF-16 code units", value);
    }

    public static JsonNode TextNode(string text) =>
        IsWellFormed(text) ? JsonValue.Create(text) : new JsonArray(text.Select(c => (JsonNode)JsonValue.Create((int)c)).ToArray());

    public static int EnumIndex(EnumType type, JsonNode? value)
    {
        var index = TextOf(value) is { } name ? IndexOf(type.Values, name) : -1;
        return index >= 0 ? index : throw Invalid($"a value of {type.Text} is one of its names", value);
    }

    public static (int Index, JsonObject Fields) UnionCase(UnionType type, JsonNode? value)
    {
        if (value is JsonObject union && union.Count == 1)
        {
            var (name, fields) = union.First();
            for (var i = 0; i < type.Cases.Count; i++)
            {
                if (type.Cases[i].Name == name && fields is JsonObject fieldObject)
                    return (i, fieldObject);
            }
        }

        throw Invalid($"a value of {type.Text} is an object with one key, a case's name, whose value is an object of " +
            "the case's fields", value);
    }

    public static JsonObject Fields(IReadOnlyList<VectorField> fields, JsonNode? value, string where)
    {
        if (value is JsonObject record && record.Select(p => p.Key).SequenceEqual(fields.Select(f => f.Name)))
            return record;

        throw Invalid($"a value of {where} is an object of its fields, in order: " +
            string.Join(", ", fields.Select(f => f.Name)), value);
    }

    private static void Append(StringBuilder builder, VectorType type, JsonNode? value)
    {
        switch (type)
        {
            case NullableType nullable:
                if (value is null)
                    builder.Append("null");
                else
                    Append(builder, nullable.Inner, value);
                break;
            case ListType list:
                var items = value as JsonArray ?? throw Invalid("a list is a JSON array", value);
                builder.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    Append(builder, list.Item, items[i]);
                }

                builder.Append(']');
                break;
            case RecordType record:
                AppendFields(builder, record.Fields, Fields(record.Fields, value, record.Text));
                break;
            case UnionType union:
                var (index, fields) = UnionCase(union, value);
                var @case = union.Cases[index];
                builder.Append('{').Append(VectorFileLayout.Quote(@case.Name)).Append(": ");
                AppendFields(builder, @case.Fields, Fields(@case.Fields, fields, union.Text + "." + @case.Name));
                builder.Append('}');
                break;
            case EnumType @enum:
                builder.Append(VectorFileLayout.Quote(@enum.Values[EnumIndex(@enum, value)]));
                break;
            case PrimitiveType primitive:
                AppendPrimitive(builder, primitive.Kind, value);
                break;
            default:
                throw new InvalidOperationException($"Unknown type {type}.");
        }
    }

    private static void AppendFields(StringBuilder builder, IReadOnlyList<VectorField> fields, JsonObject value)
    {
        builder.Append('{');
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(VectorFileLayout.Quote(fields[i].Name)).Append(": ");
            Append(builder, fields[i].Type, value[fields[i].Name]);
        }

        builder.Append('}');
    }

    private static void AppendPrimitive(StringBuilder builder, Primitive kind, JsonNode? value)
    {
        var text = kind switch
        {
            Primitive.Bool => ToBool(value) ? "true" : "false",
            Primitive.Int or Primitive.Count => ToInt(value).ToString(CultureInfo.InvariantCulture),
            Primitive.Long or Primitive.Instant or Primitive.Duration =>
                VectorFileLayout.Quote(ToLong(value).ToString(CultureInfo.InvariantCulture)),
            Primitive.VarUInt => VectorFileLayout.Quote(ToVarUInt(value).ToString(CultureInfo.InvariantCulture)),
            Primitive.Double => VectorFileLayout.Quote(DoubleBits(ToDouble(value))),
            Primitive.String => VectorFileLayout.Inline(TextNode(ToText(value))),
            _ => throw new InvalidOperationException($"Unknown primitive {kind}."),
        };
        builder.Append(text);
    }

    private static string? TextOf(JsonNode? value) =>
        value is JsonValue json && json.GetValueKind() == JsonValueKind.String ? json.GetValue<string>() : null;

    private static int IndexOf(IReadOnlyList<string> values, string name)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] == name)
                return i;
        }

        return -1;
    }

    public static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                return false;
        }

        return true;
    }

    private static InvalidDataException Invalid(string rule, JsonNode? value) =>
        new($"{char.ToUpperInvariant(rule[0])}{rule[1..]}, but the value is {VectorFileLayout.Inline(value)}.");

    [GeneratedRegex("^-?(0|[1-9][0-9]*)$")]
    private static partial Regex SignedPattern();

    [GeneratedRegex("^(0|[1-9][0-9]*)$")]
    private static partial Regex UnsignedPattern();

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex DoubleBitsPattern();
}
