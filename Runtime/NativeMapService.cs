using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

/// <summary>A lossless room document: original fields, layers, camera and creation code remain editable.</summary>
public sealed class NativeMapDocument
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新地图";
    public string OriginalRoomName { get; set; } = "";
    public JsonObject Room { get; set; } = new();
    public Dictionary<string, string> ObjectNames { get; set; } = new();
    public Dictionary<string, string> BackgroundNames { get; set; } = new();
    public Dictionary<string, JsonElement> CreationCode { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    [JsonIgnore] public JsonArray Instances => Room["instances"]!.AsArray();
    [JsonIgnore] public JsonArray Tiles => Room["tiles"]!.AsArray();
    public NativeMapDocument Clone() => JsonSerializer.Deserialize<NativeMapDocument>(JsonSerializer.SerializeToUtf8Bytes(this, NativeContentStore.Json), NativeContentStore.Json)!;
}

public sealed record NativeMapLoadResult(NativeMapDocument Document, double Milliseconds, IReadOnlyList<string> Warnings);
public sealed record NativeMapAttachment(int InstanceId, string EntityType);

/// <summary>Imports the actual EnemyTestRoom used by the title menu; no parallel room format is invented.</summary>
public sealed class NativeMapService
{
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private int? playRoom;
    private readonly Dictionary<string, int> nativeObjects = new();
    public List<NativeMapAttachment> Attachments { get; } = new();
    public IReadOnlyList<string> LastWarnings { get; private set; } = Array.Empty<string>();
    public NativeMapService(GmWorld world, ContentRegistry registry) { this.world = world; this.registry = registry; }
    public int TestRoomId => world.Constants["EnemyTestRoom"].Int;
    public NativeMapLoadResult ImportOriginal(int roomId)
    {
        long start = Stopwatch.GetTimestamp();
        var room = world.Rooms[roomId];
        var document = new NativeMapDocument { Name = room.GetProperty("name").GetString()!, OriginalRoomName = room.GetProperty("name").GetString()!, Room = JsonNode.Parse(room.GetRawText())!.AsObject() };
        foreach (var obj in world.Objects) document.ObjectNames[obj.Key.ToString()] = obj.Value.GetProperty("name").GetString()!;
        foreach (var background in world.Manifest.GetProperty("backgrounds").EnumerateArray()) document.BackgroundNames[background.GetProperty("id").GetInt32().ToString()] = background.GetProperty("name").GetString()!;
        foreach (var code in document.Instances.Select(i => i!["code"]!.GetValue<string>()).Append(document.Room["code"]!.GetValue<string>()).Distinct())
            if (world.Code.TryGetValue(code, out var ast)) document.CreationCode[code] = ast.Clone();
        Validate(document);
        return new(document, Stopwatch.GetElapsedTime(start).TotalMilliseconds, LastWarnings);
    }
    public NativeMapLoadResult Load(string path)
    {
        long start = Stopwatch.GetTimestamp();
        var document = NativeContentStore.Load<NativeMapDocument>(path);
        Validate(document);
        return new(document, Stopwatch.GetElapsedTime(start).TotalMilliseconds, LastWarnings);
    }
    public void Save(NativeMapDocument document, string path) { Validate(document); NativeContentStore.Save(path, document); }
    public IReadOnlyList<string> Validate(NativeMapDocument document)
    {
        if (document.FormatVersion != 1) throw new InvalidDataException($"不支持地图格式版本 {document.FormatVersion}；文件未被修改。");
        if (string.IsNullOrWhiteSpace(document.Id)) throw new InvalidDataException("地图缺少稳定标识。");
        if (document.Room["width"]?.GetValue<int>() is not > 0 || document.Room["height"]?.GetValue<int>() is not > 0) throw new InvalidDataException("地图宽高必须大于零。");
        if (document.Room["instances"] is not JsonArray || document.Room["tiles"] is not JsonArray || document.Room["views"] is not JsonArray || document.Room["backgrounds"] is not JsonArray) throw new InvalidDataException("地图缺少实体、图块、背景或相机数据。");
        var warnings = new List<string>();
        var ids = new HashSet<int>();
        foreach (var item in document.Instances)
        {
            if (item is not JsonObject instance || instance["id"] is null || !ids.Add(Int(instance, "id"))) throw new InvalidDataException("实体标识缺失或重复。");
            if (instance["entityType"] is JsonValue type)
            { if (!registry.Entities.ContainsKey(type.GetValue<string>())) warnings.Add("缺少扩展实体：" + type.GetValue<string>()); }
            else if (ResolveObject(document, Int(instance, "object", -1)) is null) warnings.Add("缺少原版实体：" + Int(instance, "object", -1));
            foreach (string field in new[] { "x", "y", "xscale", "yscale", "angle" }) if (!double.IsFinite(Number(instance, field, field.EndsWith("scale", StringComparison.Ordinal) ? 1 : 0))) throw new InvalidDataException("实体坐标或缩放不是有效数值。");
        }
        ids.Clear();
        foreach (var tile in document.Tiles.OfType<JsonObject>())
        {
            if (tile["id"] is null || !ids.Add(Int(tile, "id"))) throw new InvalidDataException("图块标识缺失或重复。");
            if (ResolveBackground(document, Int(tile, "background", -1)) is null) warnings.Add("缺少图块资源：" + Int(tile, "background", -1));
        }
        foreach (string dependency in document.Dependencies)
            if (!registry.Entities.ContainsKey(dependency) && !registry.Actions.ContainsKey(dependency) && !registry.Resources.ContainsKey(dependency)) warnings.Add("缺少依赖：" + dependency);
        return LastWarnings = warnings.Distinct().ToArray();
    }
    private int? ResolveObject(NativeMapDocument document, int id)
    {
        if (!document.ObjectNames.TryGetValue(id.ToString(), out string? name)) return world.Objects.ContainsKey(id) ? id : null;
        return world.Objects.FirstOrDefault(p => p.Value.GetProperty("name").GetString() == name) is var match && match.Value.ValueKind != JsonValueKind.Undefined ? match.Key : null;
    }
    private int? ResolveBackground(NativeMapDocument document, int id)
    {
        string? name = document.BackgroundNames.GetValueOrDefault(id.ToString());
        foreach (var asset in world.Manifest.GetProperty("backgrounds").EnumerateArray()) if (name is null ? asset.GetProperty("id").GetInt32() == id : asset.GetProperty("name").GetString() == name) return asset.GetProperty("id").GetInt32();
        return null;
    }
    /// <summary>Registers a playable clone. Missing dependencies are omitted from playback only, never the document.</summary>
    public int InstallForPlay(NativeMapDocument document)
    {
        Validate(document);
        foreach (var code in document.CreationCode) world.Code[code.Key] = code.Value.Clone();
        var room = document.Room.DeepClone().AsObject();
        Attachments.Clear();
        var instances = new JsonArray();
        foreach (var original in document.Instances.OfType<JsonObject>())
        {
            var item = original.DeepClone().AsObject();
            int? objectId;
            if (item["entityType"] is JsonValue type)
            {
                string entityType = type.GetValue<string>();
                if (!registry.Entities.ContainsKey(entityType)) continue;
                if (!nativeObjects.TryGetValue(entityType, out int stub)) nativeObjects[entityType] = stub = world.Call("object_add", [], new(new GmInstance())).Int;
                objectId = stub; Attachments.Add(new(Int(item, "id"), entityType));
            }
            else objectId = ResolveObject(document, Int(item, "object", -1));
            if (objectId is null) continue;
            item["object"] = objectId.Value;
            string codeName = item["code"]?.GetValue<string>() ?? "";
            JsonNode initializer = world.Code.TryGetValue(codeName, out var ast) ? JsonNode.Parse(ast.GetRawText())! : new JsonArray("group", new JsonArray());
            if (item["properties"] is JsonObject properties)
            {
                var statements = new JsonArray(initializer);
                foreach (var property in properties)
                {
                    if (property.Value is not JsonValue value || !IsIdentifier(property.Key)) continue;
                    JsonNode? expression = value.TryGetValue<bool>(out bool boolean) ? new JsonArray("num", boolean ? 1 : 0) : value.TryGetValue<double>(out double number) ? new JsonArray("num", number) : value.TryGetValue<string>(out string? text) ? new JsonArray("str", text) : null;
                    if (expression is not null) statements.Add(new JsonArray("binary", "Assign", new JsonArray("id", property.Key), expression));
                }
                initializer = new JsonArray("group", statements);
            }
            string installedCode = $"native/maps/{document.Id}/instances/{Int(item, "id")}";
            world.Code[installedCode] = JsonSerializer.SerializeToElement(initializer);
            item["code"] = installedCode;
            instances.Add(item);
        }
        room["instances"] = instances;
        var tiles = new JsonArray();
        foreach (var original in document.Tiles.OfType<JsonObject>())
        {
            int? background = ResolveBackground(document, Int(original, "background", -1));
            if (background is null) continue;
            var item = original.DeepClone().AsObject(); item["background"] = background.Value; tiles.Add(item);
        }
        room["tiles"] = tiles;
        if (room["code"] is not JsonValue roomCode || !world.Code.ContainsKey(roomCode.GetValue<string>()))
        { string code = $"native/maps/{document.Id}/create"; world.Code[code] = JsonSerializer.SerializeToElement(new object[] { "group", Array.Empty<object>() }); room["code"] = code; }
        int id = playRoom ??= world.Rooms.Keys.Max() + 1;
        room["id"] = id; room["name"] = document.Name;
        world.Rooms[id] = JsonSerializer.SerializeToElement(room);
        world.NextId = Math.Max(world.NextId, document.Instances.OfType<JsonObject>().Select(i => Int(i, "id")).DefaultIfEmpty().Max() + 1);
        return id;
    }
    /// <summary>Called after room creation to retain user overrides even when original Create events set defaults.</summary>
    public void ApplyProperties(NativeMapDocument document)
    {
        var byId = world.Instances.Where(i => !i.Destroyed).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.Last());
        foreach (var item in document.Instances.OfType<JsonObject>()) if (byId.TryGetValue(Int(item, "id"), out var instance) && item["properties"] is JsonObject properties)
            foreach (var property in properties) if (property.Value is JsonValue value && IsIdentifier(property.Key))
            {
                if (value.TryGetValue<bool>(out bool boolean)) world.Write(instance, property.Key, boolean);
                else if (value.TryGetValue<double>(out double number)) world.Write(instance, property.Key, number);
                else if (value.TryGetValue<string>(out string? text)) world.Write(instance, property.Key, text);
            }
    }
    public JsonObject AddEntity(NativeMapDocument document, int objectId, double x, double y, string? entityType = null)
    {
        int id = Math.Max(world.NextId, document.Instances.OfType<JsonObject>().Select(i => Int(i, "id")).DefaultIfEmpty().Max() + 1);
        var item = new JsonObject { ["id"] = id, ["object"] = objectId, ["x"] = x, ["y"] = y, ["xscale"] = 1d, ["yscale"] = 1d, ["angle"] = 0d, ["blend"] = 16777215L, ["code"] = "", ["properties"] = new JsonObject() };
        if (entityType is not null) { item["entityType"] = entityType; if (!document.Dependencies.Contains(entityType)) document.Dependencies.Add(entityType); }
        else if (world.Objects.TryGetValue(objectId, out var asset)) document.ObjectNames[objectId.ToString()] = asset.GetProperty("name").GetString()!;
        document.Instances.Add(item); return item;
    }
    public JsonObject AddTile(NativeMapDocument document, int background, double x, double y, int sourceX, int sourceY, int width, int height, int depth)
    {
        int id = Math.Max(world.Manifest.GetProperty("lastTileId").GetInt32() + 1, document.Tiles.OfType<JsonObject>().Select(i => Int(i, "id")).DefaultIfEmpty().Max() + 1);
        var item = new JsonObject { ["id"] = id, ["background"] = background, ["sourceX"] = sourceX, ["sourceY"] = sourceY, ["width"] = width, ["height"] = height, ["x"] = x, ["y"] = y, ["depth"] = depth, ["xscale"] = 1d, ["yscale"] = 1d, ["blend"] = 16777215L };
        document.Tiles.Add(item); return item;
    }
    public static bool IsIdentifier(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') && value.All(c => char.IsLetterOrDigit(c) || c == '_');
    public static int Int(JsonObject item, string field, int fallback = 0) => item[field] is JsonValue value && value.TryGetValue<int>(out int result) ? result : (int)Number(item, field, fallback);
    public static double Number(JsonObject item, string field, double fallback = 0) => item[field] is JsonValue value && value.TryGetValue<double>(out double number) ? number : fallback;
}
