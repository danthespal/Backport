using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Ps5Backport.Core;

/// <summary>
/// Writes JSON exactly like Python's <c>json.dump(obj, f, indent=n)</c> (ensure_ascii, ", " / ": "
/// separators, key order kept), so files written by the app match the ones the former Python tools wrote byte for byte
/// (firmware_db/*.json, fakelibs/*/manifest.json) and don't churn in git.
/// </summary>
public static class PyJson
{
    public static string Serialize(JsonNode? node, int indent)
    {
        var sb = new StringBuilder();
        Write(sb, node, indent, 0);
        return sb.ToString();
    }

    /// <summary>Writes the document followed by a newline, with LF line endings.</summary>
    public static void WriteFile(string path, JsonNode node, int indent) =>
        File.WriteAllText(path, Serialize(node, indent) + "\n", new UTF8Encoding(false));

    static void Write(StringBuilder sb, JsonNode? node, int indent, int level)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    sb.Append("{}");
                    break;
                }
                sb.Append('{');
                bool first = true;
                foreach (var (key, value) in obj)
                {
                    sb.Append(first ? "\n" : ",\n").Append(' ', indent * (level + 1));
                    first = false;
                    WriteString(sb, key);
                    sb.Append(": ");
                    Write(sb, value, indent, level + 1);
                }
                sb.Append('\n').Append(' ', indent * level).Append('}');
                break;
            case JsonArray array:
                if (array.Count == 0)
                {
                    sb.Append("[]");
                    break;
                }
                sb.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    sb.Append(i == 0 ? "\n" : ",\n").Append(' ', indent * (level + 1));
                    Write(sb, array[i], indent, level + 1);
                }
                sb.Append('\n').Append(' ', indent * level).Append(']');
                break;
            case JsonValue value:
                if (value.TryGetValue(out string? s))
                    WriteString(sb, s);
                else if (value.TryGetValue(out bool b))
                    sb.Append(b ? "true" : "false");
                else if (value.TryGetValue(out int i))
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                else if (value.TryGetValue(out uint ui))
                    sb.Append(ui.ToString(CultureInfo.InvariantCulture));
                else if (value.TryGetValue(out long l))
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                else if (value.TryGetValue(out ulong u))
                    sb.Append(u.ToString(CultureInfo.InvariantCulture));
                else
                    throw new NotSupportedException($"PyJson: unsupported value {value.ToJsonString()}");
                break;
        }
    }

    /// <summary>Python json string escaping with ensure_ascii=True.</summary>
    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
