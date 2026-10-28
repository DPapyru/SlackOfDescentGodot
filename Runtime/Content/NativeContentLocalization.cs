namespace SlackOfDescent;

public static class NativeContentLocalization
{
    private static readonly Dictionary<string, (string Chinese, string English)> originals = new(StringComparer.Ordinal)
    {
        ["objBlock"] = ("碰撞方块", "Collision block"), ["objBlockOneWay"] = ("单向平台", "One way platform"),
        ["objPlayer"] = ("玩家", "Player"), ["objDoll"] = ("角色", "Character"), ["objEnemy"] = ("敌人", "Enemy"),
        ["objNana"] = ("Nana", "Nana"), ["objYuki"] = ("Yuki", "Yuki"), ["objRyoko"] = ("Ryoko", "Ryoko"),
        ["objCamera"] = ("相机", "Camera"), ["objLight"] = ("灯光", "Light"), ["objDoor"] = ("门", "Door"),
        ["objWater"] = ("水域", "Water"), ["objLadder"] = ("梯子", "Ladder"), ["objSwitch"] = ("开关", "Switch"),
        ["objCheckpoint"] = ("检查点", "Checkpoint"), ["objSpawn"] = ("出生点", "Spawn point"), ["objGoal"] = ("终点", "Goal"),
        ["caveTile"] = ("洞穴图块", "Cave tiles"), ["caveTileDark"] = ("暗色洞穴图块", "Dark cave tiles"),
        ["grassTile"] = ("草地图块", "Grass tiles"), ["forestTile"] = ("森林图块", "Forest tiles"),
        ["testTile"] = ("测试图块", "Test tiles"), ["snowTile"] = ("雪地图块", "Snow tiles")
    };
    public static string Display(ContentRegistry registry, string id, string defaultName = "") => Resolve(registry, id, "name", defaultName);
    public static string Description(ContentRegistry registry, string id, string defaultDescription = "") => Resolve(registry, id, "description", defaultDescription, false);
    private static string Resolve(ContentRegistry registry, string id, string field, string fallback, bool useId = true)
    {
        foreach (string locale in NativeLocalization.IsChinese ? new[] { "zh_CN", "en" } : new[] { "en" })
        {
            if (registry.Translations.TryGetValue(locale + "/" + id + "." + field, out string? value) && !string.IsNullOrWhiteSpace(value)) return value;
            if (field == "name" && registry.Translations.TryGetValue(locale + "/" + id, out value) && !string.IsNullOrWhiteSpace(value)) return value;
            if (field == "name" && originals.TryGetValue(id, out var original)) return locale == "zh_CN" ? original.Chinese : original.English;
        }
        return !string.IsNullOrWhiteSpace(fallback) ? fallback : useId ? id : "";
    }
    public static bool Matches(ContentRegistry registry, string id, string defaultName, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if ((id + " " + defaultName).Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (originals.TryGetValue(id, out var original) && (original.Chinese + " " + original.English).Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string locale in new[] { "zh_CN", "en" }) foreach (string key in new[] { id, id + ".name", id + ".description" }) if (registry.Translations.TryGetValue(locale + "/" + key, out string? value) && value.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
