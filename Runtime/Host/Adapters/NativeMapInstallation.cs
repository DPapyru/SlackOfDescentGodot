using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

public sealed partial class NativeMapService
{
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
}
