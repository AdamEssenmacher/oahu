using System.Globalization;
using System.Text.Json.Nodes;
using AdamE.Oahu.Wire;

namespace AdamE.Oahu.Tests.Conformance;

/// <summary>
/// A generic codec over <see cref="OahuWriter"/> and <see cref="OahuReader"/>, driven by a vector's type: it writes a
/// value in the file's JSON representation and reads one back into it.
/// </summary>
/// <remarks>
/// It calls the writer and reader as generated codecs do. A record calls <c>EnterNested</c>, writes or reads its fields
/// and calls <c>ExitNested</c>; a union calls <c>EnterNested</c> once, then the index method, then the case's fields,
/// then <c>ExitNested</c>; an enum calls only the index method. Lists and nullable values add no depth.
/// </remarks>
internal static class VectorCodec
{
    public static byte[] Encode(VectorType type, JsonNode? value)
    {
        var writer = new OahuWriter();
        Write(writer, type, value);
        return writer.WrittenSpan.ToArray();
    }

    public static JsonNode? Decode(VectorType type, byte[] bytes)
    {
        var reader = new OahuReader(bytes);
        var value = Read(ref reader, type);
        reader.EnsureEnd();
        return value;
    }

    private static void Write(OahuWriter writer, VectorType type, JsonNode? value)
    {
        switch (type)
        {
            case NullableType nullable:
                writer.WritePresence(value is not null);
                if (value is not null)
                    Write(writer, nullable.Inner, value);
                break;
            case ListType list:
                var items = (value as JsonArray ?? throw new InvalidDataException("A list is not an array.")).ToArray();
                writer.WriteList(items, (w, item) => Write(w, list.Item, item));
                break;
            case RecordType record:
                writer.EnterNested();
                WriteFields(writer, record.Fields, VectorValues.Fields(record.Fields, value, record.Text));
                writer.ExitNested();
                break;
            case UnionType union:
                var (index, fields) = VectorValues.UnionCase(union, value);
                writer.EnterNested();
                writer.WriteIndex(index, union.Cases.Count);
                WriteFields(writer, union.Cases[index].Fields, fields);
                writer.ExitNested();
                break;
            case EnumType @enum:
                writer.WriteIndex(VectorValues.EnumIndex(@enum, value), @enum.Values.Count);
                break;
            case PrimitiveType primitive:
                WritePrimitive(writer, primitive.Kind, value);
                break;
            default:
                throw new InvalidOperationException($"Unknown type {type}.");
        }
    }

    private static void WriteFields(OahuWriter writer, IReadOnlyList<VectorField> fields, JsonObject value)
    {
        foreach (var field in fields)
            Write(writer, field.Type, value[field.Name]);
    }

    private static void WritePrimitive(OahuWriter writer, Primitive kind, JsonNode? value)
    {
        switch (kind)
        {
            case Primitive.Bool:
                writer.WriteBool(VectorValues.ToBool(value));
                break;
            case Primitive.Int:
                writer.WriteInt(VectorValues.ToInt(value));
                break;
            case Primitive.Count:
                writer.WriteCount(VectorValues.ToInt(value));
                break;
            case Primitive.VarUInt:
                writer.WriteVarUInt(VectorValues.ToVarUInt(value));
                break;
            case Primitive.Long:
                writer.WriteLong(VectorValues.ToLong(value));
                break;
            case Primitive.Instant:
                writer.WriteInstant(DateTimeOffset.FromUnixTimeMilliseconds(VectorValues.ToLong(value)));
                break;
            case Primitive.Duration:
                writer.WriteDuration(new TimeSpan(VectorValues.ToLong(value)));
                break;
            case Primitive.Double:
                writer.WriteDouble(VectorValues.ToDouble(value));
                break;
            case Primitive.String:
                writer.WriteString(VectorValues.ToText(value));
                break;
            default:
                throw new InvalidOperationException($"Unknown primitive {kind}.");
        }
    }

    private static JsonNode? Read(ref OahuReader reader, VectorType type)
    {
        switch (type)
        {
            case NullableType nullable:
                return reader.ReadPresence() ? Read(ref reader, nullable.Inner) : null;
            case ListType list:
                var itemType = list.Item;
                var items = reader.ReadList((ref OahuReader r) => Read(ref r, itemType));
                return new JsonArray([.. items]);
            case RecordType record:
                reader.EnterNested();
                var recordValue = ReadFields(ref reader, record.Fields);
                reader.ExitNested();
                return recordValue;
            case UnionType union:
                reader.EnterNested();
                var @case = union.Cases[reader.ReadIndex(union.Cases.Count)];
                var caseValue = ReadFields(ref reader, @case.Fields);
                reader.ExitNested();
                return new JsonObject { [@case.Name] = caseValue };
            case EnumType @enum:
                return JsonValue.Create(@enum.Values[reader.ReadIndex(@enum.Values.Count)]);
            case PrimitiveType primitive:
                return ReadPrimitive(ref reader, primitive.Kind);
            default:
                throw new InvalidOperationException($"Unknown type {type}.");
        }
    }

    private static JsonObject ReadFields(ref OahuReader reader, IReadOnlyList<VectorField> fields)
    {
        var value = new JsonObject();
        foreach (var field in fields)
            value[field.Name] = Read(ref reader, field.Type);

        return value;
    }

    private static JsonNode ReadPrimitive(ref OahuReader reader, Primitive kind)
    {
        switch (kind)
        {
            case Primitive.Bool:
                return JsonValue.Create(reader.ReadBool());
            case Primitive.Int:
                return JsonValue.Create(reader.ReadInt());
            case Primitive.Count:
                return JsonValue.Create(reader.ReadCount());
            case Primitive.VarUInt:
                return Decimal(reader.ReadVarUInt());
            case Primitive.Long:
                return Decimal(reader.ReadLong());
            case Primitive.Instant:
                var instant = reader.ReadInstant();
                if (instant.Offset != TimeSpan.Zero)
                    throw new InvalidOperationException($"ReadInstant returned the offset {instant.Offset}, not zero.");

                return Decimal(instant.ToUnixTimeMilliseconds());
            case Primitive.Duration:
                return Decimal(reader.ReadDuration().Ticks);
            case Primitive.Double:
                return JsonValue.Create(VectorValues.DoubleBits(reader.ReadDouble()));
            case Primitive.String:
                return VectorValues.TextNode(reader.ReadString());
            default:
                throw new InvalidOperationException($"Unknown primitive {kind}.");
        }
    }

    private static JsonValue Decimal<T>(T value)
        where T : IFormattable => JsonValue.Create(value.ToString(null, CultureInfo.InvariantCulture));
}
