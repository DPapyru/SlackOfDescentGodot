using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public static class NativeLocalization
{
    private static Dictionary<string, string> entries = new(StringComparer.Ordinal);
    private static KeyValuePair<string,string>[] fragments = [];
    public static FontFile? Font { get; private set; }
    public static void Initialize(string dataRoot)
    {
        entries = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(dataRoot,"localization","zh-CN.json")))!;
        fragments = entries.Where(p => p.Key.Length > 2).OrderByDescending(p => p.Key.Length).ToArray();
        Font = new FontFile { Data = File.ReadAllBytes(Path.Combine(dataRoot,"localization","NotoSansCJK-Regular.ttc")) };
        ThemeDB.FallbackFont = Font;
    }
    public static string Text(string source)
    {
        if (entries.TryGetValue(source, out var translated)) return translated;
        if (source.Contains('|')) return string.Join('|', source.Split('|').Select(Text));
        if (source.Contains('#') || source.Contains('\n')) return string.Join('\n',source.Replace('#','\n').Split('\n').Select(Text));
        foreach (var pair in fragments)
            if ((pair.Key.EndsWith(' ') || pair.Key.EndsWith('=') || pair.Key.StartsWith("No Exists:",StringComparison.Ordinal)) && source.StartsWith(pair.Key,StringComparison.Ordinal))
                return pair.Value + source[pair.Key.Length..];
        return source;
    }
}
