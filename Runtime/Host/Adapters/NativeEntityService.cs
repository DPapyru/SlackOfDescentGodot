using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed class NativeEntityService(NativeModServices services) : IDisposable
{
    private sealed record Entry(GmInstance Instance, NativeEntityDefinition Definition, int Owner, NativeResourceScope Scope);
    private readonly Dictionary<int, Entry> spawned = new();
    internal Action<int, string>? Attach;
    internal bool IsNative(int id)=>spawned.ContainsKey(id);
    public ActorContext? Find(int id) => services.World.Instances.FirstOrDefault(i => i.Id == id && !i.Destroyed) is {} instance ? new(services.World, instance) : null;
    public IReadOnlyList<ActorContext> Query(string? type = null) => services.World.Instances.Where(i => !i.Destroyed && (type is null || spawned.TryGetValue(i.Id, out var entry) && entry.Definition.Id == type)).Select(i => new ActorContext(services.World, i)).ToArray();
    public ActorContext Spawn(string id, double x, double y, int owner = 0, IReadOnlyDictionary<string, GmValue>? parameters = null)
    {
        if (!services.Registry.EntityDefinitions.TryGetValue(id, out var definition)) throw new InvalidDataException("实体类型未加载：" + id);
        var world = services.World;
        var values = new Dictionary<string, GmValue>(StringComparer.Ordinal);
        foreach (var pair in definition.Parameters)
        {
            GmValue value = parameters is not null && parameters.TryGetValue(pair.Key, out var supplied) ? supplied : ParameterDefault(pair.Value);
            if (pair.Value.Type != "string" && (pair.Value.Min is {} min && value.Number < min || pair.Value.Max is {} max && value.Number > max)) throw new InvalidDataException("实体参数超出范围：" + pair.Key);
            values.Add(pair.Key, value);
        }
        var instance = world.CreateNativeEntity(x, y);
        instance.Scope.Write("visible", false); instance.Scope.Write("solid", definition.Solid);
        foreach (var pair in values) instance.Scope.Write(pair.Key, pair.Value);
        spawned.Add(instance.Id, new(instance, definition, owner, services.CurrentScope));
        try
        {
            Attach?.Invoke(instance.Id, definition.BehaviorId);
            using var scope = services.EnterScope(services.Registry.ScopeFor("entity", definition.BehaviorId, instance.Id));
            if (!string.IsNullOrEmpty(definition.ResourceId)) services.Presentation.Sprite(instance.Id, "entity", definition.ResourceId, x, y);
            return new(world, instance);
        }
        catch { Destroy(instance.Id); throw; }
    }
    internal void BindExisting(int id, string definitionId)
    {
        if (!services.Registry.EntityDefinitions.TryGetValue(definitionId, out var definition)) throw new InvalidDataException("实体类型未加载：" + definitionId);
        var actor = Find(id) ?? throw new InvalidDataException("实体实例不存在：" + id);
        foreach (var pair in definition.Parameters)
            if (!actor.Instance.Scope.Values.ContainsKey(pair.Key)) actor.Instance.Scope.Write(pair.Key, ParameterDefault(pair.Value));
        actor.Write("solid", definition.Solid ? 1 : 0);
        spawned[id] = new(actor.Instance, definition, 0, services.CurrentScope);
        try { Attach?.Invoke(id, definition.BehaviorId); }
        catch { spawned.Remove(id); services.Presentation.ClearActor(id); throw; }
    }
    private static GmValue ParameterDefault(NativeEntityParameter parameter) => parameter.Type switch
    {
        "string" => parameter.Default,
        "bool" or "boolean" => bool.TryParse(parameter.Default, out bool value) ? value : double.Parse(parameter.Default, System.Globalization.CultureInfo.InvariantCulture),
        _ => double.Parse(parameter.Default, System.Globalization.CultureInfo.InvariantCulture)
    };
    public void Destroy(int id)
    {
        var actor = spawned.TryGetValue(id, out var entry)
            ? services.World.Instances.Contains(entry.Instance) && !entry.Instance.Destroyed ? new ActorContext(services.World, entry.Instance) : null
            : Find(id);
        actor?.Call("instance_destroy");
        spawned.Remove(id); services.Presentation.ClearActor(id);
    }
    internal void Forget(int id) { spawned.Remove(id); services.Presentation.ClearActor(id); }
    public void Interact(int id, ActorContext other) => Interaction?.Invoke(id, other);
    internal Action<int, ActorContext>? Interaction;
    internal bool TryBounds(GmInstance instance, double? x, double? y, out GmBounds bounds)
    {
        if (!spawned.TryGetValue(instance.Id, out var entry)) { bounds = default; return false; }
        double px = x ?? instance.Scope.ReadOr("x").Number, py = y ?? instance.Scope.ReadOr("y").Number;
        bounds = new(px - entry.Definition.Width / 2, py - entry.Definition.Height / 2, px + entry.Definition.Width / 2, py + entry.Definition.Height / 2); return true;
    }
    internal void Update()
    {
        foreach (var entry in spawned.Values.ToArray())
        {
            if (entry.Instance.Destroyed || !services.World.Instances.Contains(entry.Instance) || !services.Registry.EntityDefinitions.ContainsKey(entry.Definition.Id)) { Destroy(entry.Instance.Id); continue; }
            using var scope = services.EnterScope(services.Registry.ScopeFor("entity", entry.Definition.BehaviorId, entry.Instance.Id));
            if (!string.IsNullOrEmpty(entry.Definition.ResourceId)) services.Presentation.Sprite(entry.Instance.Id, "entity", entry.Definition.ResourceId, entry.Instance.Scope.ReadOr("x").Number, entry.Instance.Scope.ReadOr("y").Number);
        }
    }
    internal void ClearScope(NativeResourceScope scope) { foreach (int id in spawned.Values.Where(e => e.Scope == scope).Select(e => e.Instance.Id).ToArray()) Destroy(id); }
    internal void ClearOwner(NativeModOwner owner) { foreach (int id in spawned.Values.Where(e => e.Scope.Owner == owner).Select(e => e.Instance.Id).ToArray()) Destroy(id); }
    internal void ClearOwned(int owner) { foreach (int id in spawned.Values.Where(e => e.Owner == owner).Select(e => e.Instance.Id).ToArray()) Destroy(id); }
    public void Dispose() { foreach (int id in spawned.Keys.ToArray()) Destroy(id); }
}
