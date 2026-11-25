using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdamE.Oahu.Tests.Conformance;

/// <summary>
/// The layout of the vector file: a top-level object whose arrays have one element per line, each element written on
/// that line with ", " and ": " between its parts. Strings are ASCII: every other character, and every control
/// character, is escaped.
/// </summary>
internal static class VectorFileLayout
{
    public static string Format(JsonObject root)
    {
        var builder = new StringBuilder("{\n");
        var index = 0;
        foreach (var (key, value) in root)
        {
            builder.Append("  ").Append(Quote(key)).Append(": ");
            if (value is JsonArray { Count: > 0 } array)
            {
                builder.Append("[\n");
                for (var i = 0; i < array.Count; i++)
                    builder.Append("    ").Append(Inline(array[i])).Append(i < array.Count - 1 ? ",\n" : "\n");
                builder.Append("  ]");
            }
            else
            {
                builder.Append(Inline(value));
            }

            builder.Append(++index < root.Count ? ",\n" : "\n");
        }

        return builder.Append("}\n").ToString();
    }

    public static string Inline(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject obj:
                return "{" + string.Join(", ", obj.Select(p => Quote(p.Key) + ": " + Inline(p.Value))) + "}";
            case JsonArray array:
                return "[" + string.Join(", ", array.Select(Inline)) + "]";
            default:
                var value = (JsonValue)node;
                return value.GetValueKind() == JsonValueKind.String ? Quote(value.GetValue<string>()) : value.ToJsonString();
        }
    }

    public static string Quote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < ' ' or > '~':
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
