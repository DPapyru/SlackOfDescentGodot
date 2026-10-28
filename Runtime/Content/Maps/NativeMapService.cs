using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

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
    public List<NativeMapEvent> Events { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    [JsonIgnore] public JsonArray Instances => Room["instances"]!.AsArray();
    [JsonIgnore] public JsonArray Tiles => Room["tiles"]!.AsArray();
    public NativeMapDocument Clone() => JsonSerializer.Deserialize<NativeMapDocument>(JsonSerializer.SerializeToUtf8Bytes(this, NativeContentStore.Json), NativeContentStore.Json)!;
}

public sealed record NativeMapLoadResult(NativeMapDocument Document, double Milliseconds, IReadOnlyList<string> Warnings);
public sealed record NativeMapAttachment(int InstanceId, string EntityType);

public sealed class NativeMapService
{
    private static readonly ConditionalWeakTable<GmWorld, Dictionary<int, JsonElement>> retiredObjects = new();
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private int? playRoom;
    private readonly Dictionary<int, JsonElement> replacedRooms = new();
    private readonly HashSet<int> temporaryRooms = new();
    private readonly HashSet<string> installedCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Id, JsonElement Definition)> nativeObjects = new();
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
        NativeMapEventValidation.Validate(document);
        if (string.IsNullOrWhiteSpace(document.Id)) throw new InvalidDataException("地图缺少稳定标识。");
        if (document.Room["width"]?.GetValue<int>() is not > 0 || document.Room["height"]?.GetValue<int>() is not > 0) throw new InvalidDataException("地图宽高必须大于零。");
        if (document.Room["instances"] is not JsonArray || document.Room["tiles"] is not JsonArray || document.Room["views"] is not JsonArray || document.Room["backgrounds"] is not JsonArray) throw new InvalidDataException("地图缺少实体、图块、背景或相机数据。");
        foreach (string field in new[] { "width", "height", "speed" }) if (RequiredNumber(document.Room, field) <= 0) throw new InvalidDataException("房间尺寸和速度必须大于零。");
        RequiredNumber(document.Room, "colour"); RequiredString(document.Room, "code"); RequiredString(document.Room, "name");
        foreach (string field in new[] { "viewsEnabled", "clearScreen" }) RequiredBoolean(document.Room, field);
        foreach (var node in document.Room["views"]!.AsArray())
        {
            var view = node as JsonObject ?? throw new InvalidDataException("相机数据必须是对象。");
            foreach (string field in new[] { "x", "y", "width", "height", "portX", "portY", "portWidth", "portHeight", "hborder", "vborder", "hspeed", "vspeed", "target" }) RequiredNumber(view, field);
            RequiredBoolean(view, "visible");
        }
        foreach (var node in document.Room["backgrounds"]!.AsArray())
        {
            var background = node as JsonObject ?? throw new InvalidDataException("背景数据必须是对象。");
            foreach (string field in new[] { "background", "x", "y", "hspeed", "vspeed" }) RequiredNumber(background, field);
            foreach (string field in new[] { "visible", "foreground", "tileX", "tileY", "stretch" }) RequiredBoolean(background, field);
        }
        var warnings = new List<string>();
        var ids = new HashSet<int>();
        foreach (var item in document.Instances)
        {
            if (item is not JsonObject instance || instance["id"] is null || !ids.Add(Int(instance, "id"))) throw new InvalidDataException("实体标识缺失或重复。");
            if (instance["entityType"] is JsonValue type)
            { if (!HasEntity(type.GetValue<string>())) warnings.Add("缺少扩展实体：" + type.GetValue<string>()); }
            else if (ResolveObject(document, Int(instance, "object", -1)) is null) warnings.Add("缺少原版实体：" + Int(instance, "object", -1));
            foreach (string field in new[] { "id", "object", "x", "y", "xscale", "yscale", "angle", "blend" }) RequiredNumber(instance, field);
            RequiredString(instance, "code");
            if (instance["properties"] is not null && (instance["properties"] is not JsonObject properties || properties.Any(p => !IsIdentifier(p.Key) || p.Value is not JsonValue))) throw new InvalidDataException("实体属性必须是有效变量名到数字、文字或布尔值的对象。");
        }
        ids.Clear();
        foreach (var tile in document.Tiles.OfType<JsonObject>())
        {
            foreach (string field in new[] { "id", "background", "sourceX", "sourceY", "width", "height", "depth", "x", "y", "xscale", "yscale", "blend" }) RequiredNumber(tile, field);
            if (Number(tile, "width") <= 0 || Number(tile, "height") <= 0) throw new InvalidDataException("图块宽高必须大于零。");
            if (tile["id"] is null || !ids.Add(Int(tile, "id"))) throw new InvalidDataException("图块标识缺失或重复。");
            if (ResolveBackground(document, Int(tile, "background", -1)) is null) warnings.Add("缺少图块资源：" + Int(tile, "background", -1));
        }
        foreach (string dependency in document.Dependencies)
            if (!HasEntity(dependency) && !registry.Actions.ContainsKey(dependency) && !registry.Resources.ContainsKey(dependency)) warnings.Add("缺少依赖：" + dependency);
        return LastWarnings = warnings.Distinct().ToArray();
    }
    private bool HasEntity(string id) => registry.EntityDefinitions.TryGetValue(id, out var definition) ? registry.Entities.ContainsKey(definition.BehaviorId) : registry.Entities.ContainsKey(id);
    private static double RequiredNumber(JsonObject item, string field)
    {
        if (item[field] is not JsonValue value || !TryNumber(value, out double number) || !double.IsFinite(number)) throw new InvalidDataException($"字段 {field} 必须是有效数值。");
        return number;
    }
    private static void RequiredString(JsonObject item, string field) { if (item[field] is not JsonValue value || !value.TryGetValue<string>(out _)) throw new InvalidDataException($"字段 {field} 必须是文字。"); }
    private static void RequiredBoolean(JsonObject item, string field) { if (item[field] is not JsonValue value || !value.TryGetValue<bool>(out _)) throw new InvalidDataException($"字段 {field} 必须是布尔值。"); }
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
    public int InstallForPlay(NativeMapDocument document, int? roomId = null, string? owner = null)
    {
        Validate(document);
        CollectUnusedObjects(world);
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
                if (!HasEntity(entityType)) continue;
                if (!nativeObjects.TryGetValue(entityType, out var stub))
                {
                    int created = world.Call("object_add", [], new(new GmInstance())).Int;
                    nativeObjects[entityType] = stub = (created, world.Objects[created]);
                }
                objectId = stub.Id; Attachments.Add(new(Int(item, "id"), entityType));
            }
            else objectId = ResolveObject(document, Int(item, "object", -1));
            if (objectId is null) continue;
            item["object"] = objectId.Value;
            string codeName = item["code"]?.GetValue<string>() ?? "";
            JsonNode initializer = document.CreationCode.TryGetValue(codeName, out var ast) || world.Code.TryGetValue(codeName, out ast) ? JsonNode.Parse(ast.GetRawText())! : new JsonArray("group", new JsonArray());
            if (item["entityType"] is null && item["properties"] is JsonObject properties)
            {
                var statements = new JsonArray(initializer);
                foreach (var property in properties)
                {
                    if (property.Value is not JsonValue value || !IsIdentifier(property.Key)) continue;
                    JsonNode? expression = value.TryGetValue<bool>(out bool boolean) ? new JsonArray("num", boolean ? 1 : 0) : TryNumber(value, out double number) ? new JsonArray("num", number) : value.TryGetValue<string>(out string? text) ? new JsonArray("str", text) : null;
                    if (expression is not null) statements.Add(new JsonArray("binary", "Assign", new JsonArray("id", property.Key), expression));
                }
                initializer = new JsonArray("group", statements);
            }
            string installedInstanceCode = $"native/maps/{owner ?? document.Id}/instances/{Int(item, "id")}";
            world.Code[installedInstanceCode] = JsonSerializer.SerializeToElement(initializer); installedCode.Add(installedInstanceCode);
            item["code"] = installedInstanceCode;
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
        string originalRoomCode = room["code"]?.GetValue<string>() ?? "";
        string installedRoomCode = $"native/maps/{owner ?? document.Id}/create";
        world.Code[installedRoomCode] = document.CreationCode.TryGetValue(originalRoomCode, out var roomAst) || world.Code.TryGetValue(originalRoomCode, out roomAst) ? roomAst.Clone() : JsonSerializer.SerializeToElement(new object[] { "group", Array.Empty<object>() });
        installedCode.Add(installedRoomCode); room["code"] = installedRoomCode;
        int sourceId = Int(document.Room, "id", -1);
        int id = roomId ?? (world.Rooms.ContainsKey(sourceId) && world.Rooms[sourceId].GetProperty("name").GetString() == document.OriginalRoomName ? sourceId : playRoom ??= world.Rooms.Keys.Max() + 1);
        if (world.Rooms.TryGetValue(id, out var previous) && id != playRoom && !replacedRooms.ContainsKey(id)) replacedRooms[id] = previous;
        if (!world.Rooms.ContainsKey(id)) temporaryRooms.Add(id);
        room["id"] = id; room["caption"] = document.Name;
        world.Rooms[id] = JsonSerializer.SerializeToElement(room);
        world.NextId = Math.Max(world.NextId, document.Instances.OfType<JsonObject>().Select(i => Int(i, "id")).DefaultIfEmpty().Max() + 1);
        return id;
    }
    public void RestoreAfterPlaytest()
    {
        foreach (var pair in replacedRooms) world.Rooms[pair.Key] = pair.Value;
        replacedRooms.Clear();
        if (playRoom is int temporaryRoom) world.Rooms.Remove(temporaryRoom);
        foreach (int id in temporaryRooms) world.Rooms.Remove(id);
        temporaryRooms.Clear();
        playRoom = null;
        foreach (string key in installedCode) world.Code.Remove(key);
        installedCode.Clear();
        var pending = retiredObjects.GetOrCreateValue(world);
        foreach (var stub in nativeObjects.Values) pending[stub.Id] = stub.Definition;
        nativeObjects.Clear(); Attachments.Clear(); CollectUnusedObjects(world);
    }
    internal static void CollectUnusedObjects(GmWorld world)
    {
        if (!retiredObjects.TryGetValue(world, out var pending)) return;
        foreach (var pair in pending.ToArray())
        {
            if (world.Objects.TryGetValue(pair.Key, out var definition) && definition.Equals(pair.Value))
            {
                if (world.Instances.Any(instance => !instance.Destroyed && instance.Object == pair.Key)) continue;
                world.Call("object_delete", [pair.Key], new(new GmInstance()));
            }
            pending.Remove(pair.Key);
        }
    }
    public void ApplyProperties(NativeMapDocument document)
    {
        var byId = world.Instances.Where(i => !i.Destroyed).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.Last());
        foreach (var item in document.Instances.OfType<JsonObject>()) if (byId.TryGetValue(Int(item, "id"), out var instance)) ApplyProperties(item, instance);
    }
    internal void ApplyProperties(JsonObject item, GmInstance instance)
    {
        if (item["properties"] is not JsonObject properties) return;
        foreach (var property in properties) if (property.Value is JsonValue value && IsIdentifier(property.Key))
        {
            GmValue converted;
            if (value.TryGetValue<bool>(out bool boolean)) converted = boolean;
            else if (TryNumber(value, out double number)) converted = number;
            else if (value.TryGetValue<string>(out string? text)) converted = text;
            else continue;
            if (item["entityType"] is not null) instance.Scope.Write(property.Key, converted);
            else world.Write(instance, property.Key, converted);
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
    public static bool TryNumber(JsonValue value, out double number)
    {
        if (value.TryGetValue<double>(out number)) return true;
        if (value.TryGetValue<int>(out int integer)) { number = integer; return true; }
        if (value.TryGetValue<long>(out long longInteger)) { number = longInteger; return true; }
        if (value.TryGetValue<float>(out float real)) { number = real; return true; }
        if (value.TryGetValue<decimal>(out decimal precise)) { number = (double)precise; return true; }
        number = 0; return false;
    }
    public static double Number(JsonObject item, string field, double fallback = 0) => item[field] is JsonValue value && TryNumber(value, out double number) ? number : fallback;
}
