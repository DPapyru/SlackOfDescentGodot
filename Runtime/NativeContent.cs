using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

public sealed partial class CharacterDefinition
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新角色";
    public string BasePreset { get; set; } = "Nana";
    public string Appearance { get; set; } = "Character-Nana";
    public Dictionary<CharacterActionSlot, string> Actions { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class NativeContentStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public string Root { get; }
    public string Characters => Path.Combine(Root, "Characters");
    public string Maps => Path.Combine(Root, "Maps");
    public string Mods => Path.Combine(Root, "Mods");
    public NativeContentStore(string userRoot)
    {
        Root = Path.Combine(userRoot, "Content");
        foreach (var path in new[] { Characters, Maps, Mods }) Directory.CreateDirectory(path);
    }
    public static void WriteAtomic(string path, byte[] data)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, data); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Save<T>(string path, T value) => WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    public static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("文件内容为空：" + path);
    public IReadOnlyList<string> DiscoverCharacters(ContentRegistry registry)
    {
        var errors = new List<string>();
        foreach (string path in Directory.EnumerateFiles(Characters, "*.character.json", SearchOption.AllDirectories))
            try { registry.RegisterCharacter(Load<CharacterDefinition>(path)); }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { errors.Add(path + ": " + e.Message); }
        return errors;
    }
}
