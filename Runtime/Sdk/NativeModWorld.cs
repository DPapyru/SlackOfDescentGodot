namespace SlackOfDescent;

public sealed class NativeInstanceService(NativeModServices services)
{
    private sealed record Owned(GmInstance Instance, NativeResourceScope Scope);
    private readonly Dictionary<int, Owned> owned = new();
    public int CurrentRoom => services.World.CurrentRoom;
    public string CurrentRoomName => services.World.Rooms.TryGetValue(CurrentRoom, out var room) ? room.GetProperty("name").GetString() ?? "" : "";
    public ActorContext Spawn(string objectName, double x, double y, bool runEvents = true) => Spawn(ObjectId(objectName), x, y, runEvents);
    public ActorContext Spawn(int objectId, double x, double y, bool runEvents = true)
    {
        if (!services.World.Objects.ContainsKey(objectId)) throw new InvalidOperationException("对象未加载：" + objectId);
        if (owned.Count > 32) Prune();
        var instance = services.World.Create(objectId, x, y, run: runEvents);
        owned[instance.Id] = new(instance, services.CurrentScope);
        return new(services.World, instance);
    }
    public ActorContext? Find(int instanceId) => services.World.Instances.FirstOrDefault(i => i.Id == instanceId && !i.Destroyed) is {} instance ? new(services.World, instance) : null;
    public ActorContext? FindObject(string objectName, int index = 0)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        return All(objectName, true).Skip(index).FirstOrDefault();
    }
    public IReadOnlyList<ActorContext> All(string? objectName = null, bool activeOnly = false)
    {
        int objectId = objectName is null ? -1 : ObjectId(objectName);
        return services.World.Instances.Where(i => !i.Destroyed && (!activeOnly || i.Active) && (objectName is null || services.World.IsObject(i.Object, objectId))).Select(i => new ActorContext(services.World, i)).ToArray();
    }
    public int Count(string objectName, bool activeOnly = false)
    {
        int objectId = ObjectId(objectName);
        return services.World.Instances.Count(i => !i.Destroyed && (!activeOnly || i.Active) && services.World.IsObject(i.Object, objectId));
    }
    public bool Exists(ActorContext actor) => actor is not null && !actor.Instance.Destroyed && services.World.Instances.Contains(actor.Instance);
    public bool IsObjectOf(ActorContext actor, string objectName) => services.World.IsObject(actor.Instance.Object, ObjectId(objectName));
    public void Destroy(ActorContext actor)
    {
        int id = actor.Id; bool tracked = owned.Remove(id);
        if (!actor.Instance.Destroyed) actor.Call("instance_destroy");
        if (tracked) services.Presentation.ClearActor(id);
    }
    public void Alarm(ActorContext actor, int index, int ticks)
    {
        if (index is < 0 or > 11) throw new ArgumentOutOfRangeException(nameof(index));
        actor.Instance.Scope.Write("alarm", ticks, index);
    }
    public void Fire(ActorContext actor, int kind, int sub, ActorContext? other = null) => services.World.Event(actor.Instance, kind, sub, other?.Instance);
    public void UserEvent(ActorContext actor, int number)
    {
        if (number is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(number));
        services.World.Event(actor.Instance, 7, 10 + number);
    }
    public int ObjectId(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("对象名不能为空。");
        if (!services.World.Constants.TryGetValue(name, out var value) || !services.World.Objects.ContainsKey(value.Int)) throw new InvalidOperationException("对象未加载：" + name);
        return value.Int;
    }
    public string? ObjectName(int id) => services.World.Objects.TryGetValue(id, out var definition) ? definition.GetProperty("name").GetString() : null;
    public void GoToRoom(int roomId) => Switch(services.Maps.ResolveRoom(roomId));
    public void RestartRoom() => Switch(services.World.CurrentRoom);
    private void Switch(int roomId)
    {
        if (!services.World.Rooms.ContainsKey(roomId)) throw new InvalidOperationException("房间未加载：" + roomId);
        services.World.PendingRoom = roomId;
    }
    internal void ClearScope(NativeResourceScope scope) => Reclaim(entry => entry.Scope == scope);
    internal void ClearOwner(NativeModOwner owner) => Reclaim(entry => entry.Scope.Owner == owner);
    internal void Dispose() => Reclaim(_ => true);
    private void Prune() { foreach (int id in owned.Where(p => p.Value.Instance.Destroyed).Select(p => p.Key).ToArray()) owned.Remove(id); }
    private void Reclaim(Func<Owned, bool> matches)
    {
        foreach (var pair in owned.Where(p => matches(p.Value)).ToArray())
            if (!pair.Value.Instance.Destroyed && services.World.Instances.Contains(pair.Value.Instance)) Destroy(new(services.World, pair.Value.Instance));
            else owned.Remove(pair.Key);
    }
}
