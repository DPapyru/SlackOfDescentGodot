using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

public static class MotionToml
{
    private static readonly JsonSerializerOptions Strict = new(NativeContentStore.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static string Render(MotionGraph graph)
    {
        var root = JsonSerializer.SerializeToNode(graph, NativeContentStore.Json) as JsonObject ?? throw new InvalidDataException("动作图无法序列化。");
        var text = new StringBuilder();
        text.AppendLine("# 动作图源码；仅编辑器内视图，保存仍写 .motion.json。");
        WriteTable(text, root, "");
        return text.ToString();
    }

    public static MotionGraph Parse(string text)
    {
        text = (text ?? "").TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var root = new JsonObject();
        var current = root;
        var defined = new HashSet<string>(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string source in text.Split('\n'))
        {
            lineNumber++;
            string line = StripComment(source).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                if (!line.EndsWith("]]", StringComparison.Ordinal)) throw new InvalidDataException("数组表头缺少 ]]：第 " + lineNumber + " 行。");
                current = AppendTable(root, ParsePath(line[2..^2], lineNumber), defined, lineNumber);
            }
            else if (line[0] == '[')
            {
                if (!line.EndsWith(']')) throw new InvalidDataException("表头缺少 ]：第 " + lineNumber + " 行。");
                current = OpenTable(root, ParsePath(line[1..^1], lineNumber), defined, lineNumber);
            }
            else
            {
                int split = FindAssignment(line);
                if (split < 0) throw new InvalidDataException("无法识别的 TOML 行：第 " + lineNumber + " 行。");
                string key = ParseKey(line[..split].Trim(), lineNumber);
                JsonNode? value = ParseValue(line[(split + 1)..].Trim(), lineNumber);
                if (current.ContainsKey(key)) throw new InvalidDataException("重复的键：" + key + "（第 " + lineNumber + " 行）。");
                current[key] = value;
            }
        }
        if (root.Count == 0) throw new InvalidDataException("TOML 内容为空。");
        MotionGraph graph;
        try
        {
            graph = JsonSerializer.Deserialize<MotionGraph>(root.ToJsonString(), Strict) ?? throw new InvalidDataException("TOML 内容为空。");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("TOML 结构与动作图字段不匹配：" + error.Message);
        }
        graph.Validate();
        return graph;
    }

    private static void WriteTable(StringBuilder text, JsonObject table, string path)
    {
        var deferred = new List<KeyValuePair<string, JsonNode?>>();
        foreach (var pair in table)
        {
            if (pair.Value is null) continue;
            if (pair.Value is JsonObject || IsObjectArray(pair.Value)) { deferred.Add(pair); continue; }
            text.Append(Key(pair.Key)).Append(" = ").Append(Value(pair.Value)).Append('\n');
        }
        foreach (var pair in deferred)
        {
            string child = path.Length == 0 ? Key(pair.Key) : path + "." + Key(pair.Key);
            if (pair.Value is JsonObject value)
            {
                text.Append('[').Append(child).Append("]\n");
                WriteTable(text, value, child);
                text.Append('\n');
                continue;
            }
            foreach (var item in pair.Value!.AsArray())
            {
                text.Append("[[").Append(child).Append("]]\n");
                WriteTable(text, item!.AsObject(), child);
                text.Append('\n');
            }
        }
    }

    private static bool IsObjectArray(JsonNode value) => value is JsonArray array && array.Count > 0 && array.All(item => item is JsonObject);

    private static string Value(JsonNode value)
    {
        if (value is not JsonArray array) return value.ToJsonString(NativeContentStore.Json);
        if (array.Any(item => item is JsonObject or JsonArray)) throw new InvalidDataException("TOML 视图不支持混合数组。");
        return "[" + string.Join(", ", array.Select(item => item?.ToJsonString(NativeContentStore.Json) ?? "null")) + "]";
    }

    private static string Key(string key)
    {
        if (key.Length > 0 && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) return key;
        return JsonSerializer.Serialize(key, NativeContentStore.Json);
    }

    private static JsonObject OpenTable(JsonObject root, List<string> segments, HashSet<string> defined, int line)
    {
        var (parent, path) = Walk(root, segments, line);
        string key = segments[^1];
        string canonical = path.Length == 0 ? Key(key) : path + "." + Key(key);
        if (parent[key] is JsonObject existing)
        {
            if (!defined.Add(canonical)) throw new InvalidDataException("表重复定义：第 " + line + " 行。");
            return existing;
        }
        if (parent[key] is not null) throw new InvalidDataException("表与已有值冲突：第 " + line + " 行。");
        var table = new JsonObject();
        parent[key] = table;
        defined.Add(canonical);
        return table;
    }

    private static JsonObject AppendTable(JsonObject root, List<string> segments, HashSet<string> defined, int line)
    {
        var (parent, path) = Walk(root, segments, line);
        string key = segments[^1];
        JsonArray array;
        if (parent[key] is JsonArray existing) array = existing;
        else if (parent[key] is null) { array = new JsonArray(); parent[key] = array; }
        else throw new InvalidDataException("数组表与已有值冲突：第 " + line + " 行。");
        var table = new JsonObject();
        array.Add(table);
        return table;
    }

    private static (JsonObject Parent, string Path) Walk(JsonObject root, List<string> segments, int line)
    {
        var parent = root;
        string path = "";
        for (int i = 0; i < segments.Count - 1; i++)
        {
            string key = segments[i];
            if (parent[key] is JsonObject value) { path = path.Length == 0 ? Key(key) : path + "." + Key(key); parent = value; continue; }
            if (parent[key] is JsonArray array)
            {
                if (array.Count == 0 || array[^1] is not JsonObject element) throw new InvalidDataException("表路径指向空数组或非表元素：第 " + line + " 行。");
                path = (path.Length == 0 ? Key(key) : path + "." + Key(key)) + "[" + (array.Count - 1) + "]";
                parent = element;
                continue;
            }
            if (parent[key] is not null) throw new InvalidDataException("表路径与已有值冲突：第 " + line + " 行。");
            var created = new JsonObject();
            parent[key] = created;
            path = path.Length == 0 ? Key(key) : path + "." + Key(key);
            parent = created;
        }
        return (parent, path);
    }

    private static List<string> ParsePath(string text, int line)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, escape = false;
        foreach (char character in text)
        {
            if (quoted)
            {
                current.Append(character);
                if (escape) escape = false;
                else if (character == '\\') escape = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') { quoted = true; current.Append(character); }
            else if (character == '.') { segments.Add(ParseKey(current.ToString().Trim(), line)); current.Clear(); }
            else current.Append(character);
        }
        if (quoted) throw new InvalidDataException("表头键名中的字符串未闭合：第 " + line + " 行。");
        segments.Add(ParseKey(current.ToString().Trim(), line));
        return segments;
    }

    private static string ParseKey(string text, int line)
    {
        if (text.Length == 0) throw new InvalidDataException("空键名：第 " + line + " 行。");
        if (text[0] == '"')
        {
            var (value, end) = ReadString(text, line);
            if (end != text.Length) throw new InvalidDataException("键名后有多余内容：第 " + line + " 行。");
            return value;
        }
        if (text[0] == '\'') throw new InvalidDataException("不支持单引号键名：第 " + line + " 行。");
        foreach (char character in text)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) throw new InvalidDataException("键名需要加引号：第 " + line + " 行。");
        return text;
    }

    private static JsonNode? ParseValue(string text, int line)
    {
        if (text.Length == 0) throw new InvalidDataException("缺少值：第 " + line + " 行。");
        if (text.StartsWith("\"\"\"", StringComparison.Ordinal)) throw new InvalidDataException("不支持多行字符串：第 " + line + " 行。");
        if (text[0] == '"')
        {
            var (value, end) = ReadString(text, line);
            if (end != text.Length) throw new InvalidDataException("字符串后有多余内容：第 " + line + " 行。");
            return JsonValue.Create(value);
        }
        if (text[0] == '\'') throw new InvalidDataException("不支持单引号字符串：第 " + line + " 行。");
        if (text[0] == '{') throw new InvalidDataException("不支持内联表：第 " + line + " 行。");
        if (text[0] == '[')
        {
            if (!text.EndsWith(']')) throw new InvalidDataException("数组缺少 ]：第 " + line + " 行。");
            string inner = text[1..^1].Trim();
            var array = new JsonArray();
            if (inner.Length > 0)
                foreach (string part in SplitValues(inner, line))
                    array.Add(ParseScalar(part.Trim(), line));
            return array;
        }
        return ParseScalar(text, line);
    }

    private static JsonNode? ParseScalar(string text, int line)
    {
        if (text.Length == 0) throw new InvalidDataException("数组中有空值：第 " + line + " 行。");
        if (text is "true") return JsonValue.Create(true);
        if (text is "false") return JsonValue.Create(false);
        if (text.IndexOfAny(['\'', '"', '{', '}', ']', '[']) >= 0) throw new InvalidDataException("不支持的值语法：第 " + line + " 行。");
        if (!IsNumber(text)) throw new InvalidDataException("不支持的值语法：第 " + line + " 行。");
        if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw new InvalidDataException("数值无效：第 " + line + " 行。");
            return JsonValue.Create(number);
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) return JsonValue.Create(integer);
        throw new InvalidDataException("整数超出范围：第 " + line + " 行。");
    }

    private static bool IsNumber(string text)
    {
        int index = text[0] is '+' or '-' ? 1 : 0, start = index;
        while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
        if (index == start || (index - start > 1 && text[start] == '0')) return false;
        if (index < text.Length && text[index] == '.')
        {
            start = ++index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
            if (index == start) return false;
        }
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            if (index < text.Length && text[index] is '+' or '-') index++;
            start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
            if (index == start) return false;
        }
        return index == text.Length;
    }

    private static IEnumerable<string> SplitValues(string text, int line)
    {
        var current = new StringBuilder();
        bool quoted = false, escape = false;
        foreach (char character in text)
        {
            if (quoted)
            {
                current.Append(character);
                if (escape) escape = false;
                else if (character == '\\') escape = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') { quoted = true; current.Append(character); }
            else if (character == ',') { yield return current.ToString(); current.Clear(); }
            else current.Append(character);
        }
        if (quoted) throw new InvalidDataException("数组中的字符串未闭合：第 " + line + " 行。");
        if (current.ToString().Trim().Length > 0) yield return current.ToString();
        else if (text.TrimEnd().EndsWith(',')) throw new InvalidDataException("数组中有空值：第 " + line + " 行。");
    }

    private static string StripComment(string line)
    {
        bool quoted = false, escape = false;
        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (quoted)
            {
                if (escape) escape = false;
                else if (character == '\\') escape = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') quoted = true;
            else if (character == '#') return line[..i];
        }
        return line;
    }

    private static int FindAssignment(string line)
    {
        bool quoted = false, escape = false;
        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (quoted)
            {
                if (escape) escape = false;
                else if (character == '\\') escape = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') quoted = true;
            else if (character == '=') return i;
        }
        return -1;
    }

    private static (string Value, int End) ReadString(string text, int line)
    {
        var value = new StringBuilder();
        int index = 1;
        while (index < text.Length)
        {
            char character = text[index++];
            if (character == '"') return (value.ToString(), index);
            if (character != '\\')
            {
                if (char.IsControl(character)) throw new InvalidDataException("字符串不能包含控制字符：第 " + line + " 行。");
                value.Append(character);
                continue;
            }
            if (index >= text.Length) break;
            char escape = text[index++];
            switch (escape)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case '/': value.Append('/'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'u':
                    if (index + 4 > text.Length || !text.AsSpan(index, 4).ToArray().All(char.IsAsciiHexDigit) || !ushort.TryParse(text.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                        throw new InvalidDataException("无效的 \\u 转义：第 " + line + " 行。");
                    value.Append((char)code);
                    index += 4;
                    break;
                default: throw new InvalidDataException("不支持的转义：\\" + escape + "（第 " + line + " 行）。");
            }
        }
        throw new InvalidDataException("字符串未闭合：第 " + line + " 行。");
    }
}
