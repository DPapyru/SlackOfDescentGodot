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
    public string? MotionGraph { get; set; }
    public Dictionary<CharacterActionSlot, NativeActionConfig> ActionConfigs { get; set; } = new();
    public Dictionary<CharacterActionSlot, string> Actions { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
