using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed record NativePlayableMap(string Id, string Owner, NativeMapDocument Document);

public sealed class NativeMapFlowService(NativeModServices services)
{
    internal NativeMapRuntime? Runtime;
    internal Action<string>? ShowMessage;
    public string CurrentRoom => Runtime?.CurrentMap?.Id ?? "";
    public IReadOnlyDictionary<string, bool> State => Runtime?.State ?? new Dictionary<string, bool>();
    public ActorContext? Find(string logicalId) => Runtime?.Find(logicalId);
    public void ChangeRoom(string id) => Active().ChangeRoom(id);
    public void CompleteLevel() => Active().CompleteLevel();
    public void Restart() => Active().Restart();
    public void Message(string text) => ShowMessage?.Invoke(text);
    public void SetSwitch(string key, bool enabled, string targetId = "") => Active().SetSwitch(key, enabled, targetId);
    public ActorContext Spawn(string type, double x, double y, string logicalId = "") => Active().Spawn(type, x, y, logicalId);
    public void Destroy(string logicalId) { if (Find(logicalId) is {} actor) services.Entities.Destroy(actor.Id); }
    public void Interact(string targetId, ActorContext actor) => Active().Interact(targetId, actor);
    private NativeMapRuntime Active() => Runtime ?? throw new InvalidOperationException("当前没有正在运行的地图。");
    internal void BeforeRoomLoad(int id) { services.LeaveRoom?.Invoke(); Runtime?.BeforeRoomLoad(id); }
    internal void EntityCreated(GmInstance instance) => Runtime?.EntityCreated(instance);
    internal void RoomEntered(int id) { services.EnterRoom?.Invoke(); Runtime?.RoomEntered(id); NativeMapService.CollectUnusedObjects(services.World); }
    internal int ResolveRoom(int id) => Runtime?.ResolveRoom(id) ?? id;
    internal void Update() { NativeMapService.CollectUnusedObjects(services.World); Runtime?.Update(); }
}

public sealed class NativeMapRuntime : IDisposable
{
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private readonly NativeModServices services;
    private readonly NativeMapService loader;
    private readonly List<NativePlayableMap> maps;
    private readonly Dictionary<string, int> rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<int, NativePlayableMap> documents = new();
    private readonly Dictionary<string, GmInstance> identities = new(StringComparer.Ordinal);
    private readonly HashSet<string> fired = new(StringComparer.Ordinal);
    private readonly HashSet<string> inside = new(StringComparer.Ordinal);
    private NativePlayableMap? preparing;
    private long lastInputTick = -1;
    private bool disposed;
    public NativePlayableMap? CurrentMap { get; private set; }
    public Dictionary<string, bool> State { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> Rooms => rooms;
    public NativeMapRuntime(GmWorld world, ContentRegistry registry, NativeModServices services, IEnumerable<NativePlayableMap> maps)
    {
        this.world = world; this.registry = registry; this.services = services; this.maps = maps.ToList(); loader = new(world, registry);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in this.maps)
        {
            if (string.IsNullOrWhiteSpace(map.Id) || !identifiers.Add(map.Id)) throw new InvalidDataException("地图运行标识缺失或重复。");
            loader.Validate(map.Document);
        }
        try
        {
            int room = world.Rooms.Keys.Max() + 1;
            string session = Guid.NewGuid().ToString("N");
            foreach (var map in this.maps)
            {
                int id = loader.InstallForPlay(map.Document, room++, session + "/" + map.Owner + "/" + map.Id);
                rooms.Add(map.Id, id); documents.Add(id, map);
            }
            if (!registry.InputActions.ContainsKey("native.map.interact")) registry.RegisterInput(new() { Id = "native.map.interact", Name = "地图互动", DefaultKey = 69 });
            services.Maps.Runtime = this;
        }
        catch { loader.RestoreAfterPlaytest(); throw; }
    }
    public int Room(string id)
    {
        if (rooms.TryGetValue(id, out int room)) return room;
        var matches = maps.Where(m => m.Document.Id == id && (CurrentMap is null || m.Owner == CurrentMap.Owner)).ToArray();
        if (matches.Length == 1) return rooms[matches[0].Id];
        throw new InvalidDataException("地图房间未启用或标识不明确：" + id);
    }
    public ActorContext? Find(string id) => identities.TryGetValue(id, out var instance) && !instance.Destroyed && world.Instances.Contains(instance) ? new(world, instance) : null;
    internal int ResolveRoom(int id)
    {
        if (documents.ContainsKey(id) || CurrentMap is null || id == world.Constants["Title"].Int) return id;
        var matches = maps.Where(m => m.Owner == CurrentMap.Owner && NativeMapService.Int(m.Document.Room, "id", -1) == id).ToArray();
        return matches.Length == 1 ? rooms[matches[0].Id] : id;
    }
    internal void BeforeRoomLoad(int id)
    {
        preparing = documents.GetValueOrDefault(id); CurrentMap = null;
        identities.Clear(); fired.Clear(); inside.Clear(); lastInputTick = -1;
    }
    internal void EntityCreated(GmInstance instance)
    {
        if (preparing is null) return;
        var item = preparing.Document.Instances.OfType<JsonObject>().FirstOrDefault(i => NativeMapService.Int(i, "id") == instance.Id);
        if (item is null) return;
        identities[NativeMapEntityIdentity.Of(item)] = instance;
        if (item["entityType"] is JsonValue value)
        {
            string type = value.GetValue<string>();
            loader.ApplyProperties(item, instance);
            if (registry.EntityDefinitions.ContainsKey(type)) services.Entities.BindExisting(instance.Id, type);
            else if (registry.Entities.ContainsKey(type)) services.Entities.Attach?.Invoke(instance.Id, type);
        }
    }
    internal void RoomEntered(int id)
    {
        CurrentMap = documents.GetValueOrDefault(id); preparing = null;
    }
    private ActorContext? Player() => world.Instances.FirstOrDefault(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objPlayerDoll"].Int)) is {} instance ? new(world, instance) : null;
    private static bool Contains(NativeMapEvent item, ActorContext actor) => actor.X >= item.X && actor.X <= item.X + item.Width && actor.Y >= item.Y && actor.Y <= item.Y + item.Height;
    internal void Update()
    {
        if (CurrentMap is null || world.PendingRoom is not null) return;
        var player = Player(); bool interaction = false;
        if (lastInputTick != services.Frame.Tick)
        {
            lastInputTick = services.Frame.Tick;
            interaction = services.Input.Pressed("native.map.interact", services.Frame.Input ?? InputSnapshot.Empty);
        }
        foreach (var item in CurrentMap.Document.Events.ToArray())
        {
            if (item.Once && fired.Contains(item.Id)) continue;
            bool active = item.Trigger switch
            {
                NativeMapTrigger.EnterArea => player is not null && Contains(item, player),
                NativeMapTrigger.Interact => interaction && player is not null && CanInteract(item, player),
                NativeMapTrigger.TargetDestroyed => identities.ContainsKey(item.TargetId) && Find(item.TargetId) is null,
                NativeMapTrigger.RoomCleared => !world.Instances.Any(i => !i.Destroyed && (world.IsObject(i.Object, world.Constants["objEnemyDoll"].Int) || i.Scope.ReadOr("mapEnemy").Truth)),
                _ => false
            };
            bool previous = inside.Contains(item.Id);
            if (active) inside.Add(item.Id); else inside.Remove(item.Id);
            if (active && (!previous || item.Trigger == NativeMapTrigger.Interact)) Execute(item, player);
            if (world.PendingRoom is not null || CurrentMap is null) break;
        }
    }
    private bool CanInteract(NativeMapEvent item, ActorContext actor)
    {
        if (item.TargetId.Length == 0) return Contains(item, actor);
        var target = Find(item.TargetId);
        return target is not null && Math.Abs(target.X - actor.X) <= 64 && Math.Abs(target.Y - actor.Y) <= 64;
    }
    public void Interact(string targetId, ActorContext actor)
    {
        if (Find(targetId) is {} target) services.Entities.Interact(target.Id, actor);
        if (CurrentMap is null) return;
        foreach (var item in CurrentMap.Document.Events.Where(e => e.Trigger == NativeMapTrigger.Interact && e.TargetId == targetId).ToArray())
            if ((!item.Once || !fired.Contains(item.Id)) && CanInteract(item, actor)) Execute(item, actor);
    }
    private void Execute(NativeMapEvent item, ActorContext? actor)
    {
        fired.Add(item.Id);
        var value = new NativeGameEvent("map.event", actor); value.Arguments["id"] = item.Id; value.Arguments["room"] = CurrentMap?.Id ?? "";
        services.Dispatch(value, NativeEventPhase.Before);
        if (!value.Handled) foreach (var action in item.Actions)
        {
            switch (action.Kind)
            {
                case NativeMapEventActionKind.Message: services.Maps.Message(action.Text); break;
                case NativeMapEventActionKind.SetSwitch: services.Maps.SetSwitch(action.StateKey, action.Enabled, action.TargetId); break;
                case NativeMapEventActionKind.Spawn: services.Maps.Spawn(action.EntityType, action.X, action.Y, action.TargetId); break;
                case NativeMapEventActionKind.Destroy: services.Maps.Destroy(action.TargetId); break;
                case NativeMapEventActionKind.ChangeRoom: services.Maps.ChangeRoom(action.MapId); break;
                case NativeMapEventActionKind.CompleteLevel: services.Maps.CompleteLevel(); break;
                case NativeMapEventActionKind.Restart: services.Maps.Restart(); break;
            }
        }
        services.Dispatch(value, NativeEventPhase.After);
    }
    public void SetSwitch(string key, bool enabled, string targetId = "")
    {
        if (key.Length > 0) State[key] = enabled;
        if (Find(targetId) is {} actor) actor.Instance.Active = enabled;
    }
    public ActorContext Spawn(string type, double x, double y, string logicalId = "")
    {
        ActorContext actor;
        if (type.StartsWith("native:", StringComparison.Ordinal))
        {
            string id = type[7..];
            if (registry.EntityDefinitions.ContainsKey(id)) actor = services.Entities.Spawn(id, x, y);
            else
            {
                if (!registry.Entities.ContainsKey(id)) throw new InvalidDataException("实体行为未加载：" + id);
                var instance = world.CreateNativeEntity(x, y); actor = new(world, instance);
                try { services.Entities.Attach?.Invoke(instance.Id, id); }
                catch { services.Entities.Destroy(instance.Id); throw; }
            }
        }
        else if (type.StartsWith("object:", StringComparison.Ordinal))
        {
            string objectId = type[7..]; int id = int.TryParse(objectId, out int parsed) ? parsed : world.Constants[objectId].Int;
            actor = new(world, world.Create(id, x, y));
        }
        else throw new InvalidDataException("未知实体类型：" + type);
        identities[logicalId.Length > 0 ? logicalId : "spawn:" + actor.Id] = actor.Instance; return actor;
    }
    private void Transition(int destination, bool complete)
    {
        if (world.Instances.Any(i => !i.Destroyed && (world.IsObject(i.Object, world.Constants["objChangeRoom"].Int) || world.IsObject(i.Object, world.Constants["objMissionClear"].Int)))) return;
        if (complete)
        {
            world.Globals.Write("StartArea", 0);
            foreach (var timer in world.Instances.Where(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objTimer"].Int))) timer.Scope.Write("alarm", 1, 3);
        }
        var transition = world.Create(world.Constants[complete ? "objMissionClear" : "objChangeRoom"].Int, 0, 0);
        transition.Scope.Write("nextRoom", destination);
    }
    public void ChangeRoom(string id) => Transition(Room(id), false);
    public void CompleteLevel()
    {
        if (CurrentMap is null) return;
        var order = maps.Where(m => m.Owner == CurrentMap.Owner).ToArray(); int index = Array.FindIndex(order, m => m.Id == CurrentMap.Id);
        Transition(index + 1 < order.Length ? rooms[order[index + 1].Id] : world.Constants["Title"].Int, true);
    }
    public void Restart()
    {
        var actor = Player(); if (actor is null) { world.PendingRoom = world.CurrentRoom; return; }
        bool retry = world.Globals.ReadOr("RetryValid").Truth;
        string key = retry ? "RespawnPosRetry" : "RespawnPos";
        world.Call("Respawn", [world.Globals.Read(key, world.Constants["X"].Int), world.Globals.Read(key, world.Constants["Y"].Int)], new(actor.Instance));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (services.Maps.Runtime == this) services.Maps.Runtime = null;
        CurrentMap = null; preparing = null; identities.Clear(); loader.RestoreAfterPlaytest();
    }
}
