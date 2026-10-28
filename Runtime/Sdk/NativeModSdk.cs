using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

[Flags]
public enum NativeInputContext { Game = 1, Menu = 2, Editor = 4, All = 7 }
public enum NativeEventPhase { Before, After }

public sealed class NativeInputAction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int DefaultKey { get; set; }
    public NativeInputContext Contexts { get; set; } = NativeInputContext.Game;
}

public sealed class NativeEntityParameter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "number";
    public string Default { get; set; } = "0";
    public double? Min { get; set; }
    public double? Max { get; set; }
}

public sealed class NativeEntityDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string BehaviorId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public double Width { get; set; } = 16;
    public double Height { get; set; } = 16;
    public bool Solid { get; set; }
    public Dictionary<string, NativeEntityParameter> Parameters { get; set; } = new(StringComparer.Ordinal);
}

public sealed class NativeGameEvent(string name, ActorContext? actor = null)
{
    public string Name { get; } = name;
    public ActorContext? Actor { get; } = actor;
    public Dictionary<string, GmValue> Arguments { get; } = new(StringComparer.Ordinal);
    public GmValue Result { get; set; }
    public bool Handled { get; set; }
}

public sealed record NativeEventHandler(string Owner, string Event, NativeEventPhase Phase, Action<NativeGameEvent> Handle);
public readonly record struct NativeFireRequest(double X, double Y, double Angle, bool Aiming);
public readonly record struct NativeWeaponState(int Ammunition,int Capacity,bool Reloading);
public interface ICharacterWeapon : ICharacterAction
{
    NativeWeaponState? State => null;
    bool IsEquipped(ActorContext actor) => true;
    void Fire(ActorContext actor, NativeFireRequest request);
    void Reload(ActorContext actor) { }
}

internal readonly record struct NativeModOwner(string Id, string Generation);
internal readonly record struct NativeResourceScope(NativeModOwner Owner, string Contribution, int Actor);

public sealed partial class ContentRegistry
{
    internal NativeModOwner OwnerScope { get; private set; }
    private readonly Dictionary<(string, string), NativeModOwner> contributionOwners = new();
    internal event Action<NativeModOwner>? OwnerRetired;
    internal event Action<IReadOnlyCollection<string>>? ResourcesCommitted;
    internal readonly List<string> LifecycleDiagnostics = new();
    internal void AssignOwner(string id)
    {
        OwnerScope = new(id, Guid.NewGuid().ToString("N"));
        foreach (var key in ContributionIds()) contributionOwners[key] = OwnerScope;
    }
    internal NativeResourceScope ScopeFor(string kind, string id, int actor) => new(contributionOwners.GetValueOrDefault((kind, id)), kind + ":" + id, actor);
    internal void RetireOwner(NativeModOwner owner) => OwnerRetired?.Invoke(owner);
    internal void CommitResources(IEnumerable<string> paths)
    {
        if (ResourcesCommitted is null) return;
        var changed = paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        foreach (Action<IReadOnlyCollection<string>> observer in ResourcesCommitted.GetInvocationList())
            try { observer(changed); } catch (Exception error) { Console.Error.WriteLine("MOD_RESOURCE_OBSERVER " + error.GetBaseException().Message); }
    }
    internal void ReportLifecycle(NativeResourceScope scope, string phase, Exception error)
    {
        string message = "MOD_" + phase + " " + (string.IsNullOrEmpty(scope.Owner.Id) ? scope.Contribution : scope.Owner.Id + "/" + scope.Contribution) + ": " + error.GetBaseException().Message;
        if (CollectingLifecycleDiagnostics) LifecycleDiagnostics.Add(message); Console.Error.WriteLine(message);
    }
    internal string? ModDataRoot { get; set; }
    public Dictionary<string, NativeInputAction> InputActions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, NativeEntityDefinition> EntityDefinitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, NativeEventHandler> EventHandlers { get; } = new(StringComparer.Ordinal);
    public void RegisterInput(NativeInputAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Id) || action.DefaultKey is < 2 or > 255) throw new InvalidDataException("输入操作需要稳定标识和有效默认按键。");
        InputActions.Add(action.Id, action); Refresh();
    }
    public void RegisterEntity(NativeEntityDefinition definition, Func<IEntityBehavior> factory)
    {
        if (string.IsNullOrWhiteSpace(definition.Id) || definition.Width <= 0 || definition.Height <= 0) throw new InvalidDataException("实体需要稳定标识和有效碰撞尺寸。");
        if (string.IsNullOrEmpty(definition.BehaviorId)) definition.BehaviorId = definition.Id;
        EntityDefinitions.Add(definition.Id, definition); Entities.Add(definition.BehaviorId, factory); Refresh();
    }
    partial void AppendSdkIds(List<(string Kind, string Id)> ids)
    {
        ids.AddRange(InputActions.Keys.Select(id => ("input", id)));
        ids.AddRange(EntityDefinitions.Keys.Select(id => ("entity-definition", id)));
        ids.AddRange(EventHandlers.Keys.Select(id => ("event-handler", id)));
    }
    partial void CopySdkTo(ContentRegistry target)
    {
        foreach (var pair in contributionOwners) target.contributionOwners.Add(pair.Key, pair.Value);
        foreach (var pair in InputActions) target.InputActions.Add(pair.Key, pair.Value);
        foreach (var pair in EntityDefinitions) target.EntityDefinitions.Add(pair.Key, pair.Value);
        foreach (var pair in EventHandlers) target.EventHandlers.Add(pair.Key, pair.Value);
    }
    partial void RemoveSdkFrom(ContentRegistry target)
    {
        foreach (var key in contributionOwners.Keys) target.contributionOwners.Remove(key);
        foreach (string id in InputActions.Keys) target.InputActions.Remove(id);
        foreach (string id in EntityDefinitions.Keys) target.EntityDefinitions.Remove(id);
        foreach (string id in EventHandlers.Keys) target.EventHandlers.Remove(id);
    }
    partial void ClearSdk() { InputActions.Clear(); EntityDefinitions.Clear(); EventHandlers.Clear(); contributionOwners.Clear(); }
}

public sealed partial class ModContext
{
    internal string OwnerId { get; set; } = "";
    internal string? DataRoot { get; set; }
    private NativeModData? data;
    public NativeModData Data => data ??= new(DataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlackOfDescentGodot", "Content", "ModData"),
        string.IsNullOrEmpty(OwnerId) ? Path.GetFileName(Directory) : OwnerId);
    public void Subscribe(string id, string eventName, NativeEventPhase phase, Action<NativeGameEvent> handler)
    { Content.EventHandlers.Add(id, new(OwnerId, eventName, phase, handler)); Content.Refresh(); }
}

public sealed class NativeModData(string root, string owner)
{
    private readonly string directory = Path.Combine(root, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(owner)));
    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || key.Contains('/') || key.Contains('\\') || key is "." or "..")
            throw new ArgumentException("数据键必须是单一文件名。", nameof(key));
        return Path.Combine(directory, key + ".json");
    }
    public T Read<T>(string key, T fallback) => File.Exists(PathFor(key)) ? NativeContentStore.Load<T>(PathFor(key)) : fallback;
    public void Write<T>(string key, T value) => NativeContentStore.Save(PathFor(key), value);
    public void Delete(string key) => File.Delete(PathFor(key));
}

public sealed class NativeInputService
{
    private readonly ContentRegistry registry;
    private readonly string path;
    private readonly Dictionary<string, int> bindings;
    public NativeInputContext Context { get; set; } = NativeInputContext.Game;
    public NativeInputService(ContentRegistry registry, string userRoot)
    {
        this.registry = registry; path = Path.Combine(userRoot, "Content", "input-bindings.json");
        bindings = File.Exists(path) ? NativeContentStore.Load<Dictionary<string, int>>(path) : new(StringComparer.Ordinal);
    }
    public int GetKey(string id) => bindings.TryGetValue(id, out int key) && key is >= 2 and <= 255 ? key : Definition(id).DefaultKey;
    public void SetKey(string id, int key)
    {
        Definition(id);
        if (key is < 2 or > 255) throw new ArgumentOutOfRangeException(nameof(key));
        bindings[id] = key; NativeContentStore.Save(path, bindings);
    }
    private NativeInputAction Definition(string id) => registry.InputActions.TryGetValue(id, out var action) ? action : throw new KeyNotFoundException("输入操作未加载：" + id);
    private bool Enabled(string id) => registry.InputActions.TryGetValue(id, out var action) && (action.Contexts & Context) != 0;
    public bool Down(string id, InputSnapshot input) => Enabled(id) && input.Down(GetKey(id));
    public bool Pressed(string id, InputSnapshot input) => Enabled(id) && input.JustPressed(GetKey(id));
    public bool Released(string id, InputSnapshot input) => Enabled(id) && input.Released.Contains(GetKey(id));
}

public sealed partial class ActorContext
{
    public NativeModServices Services => World.NativeServices ?? throw new InvalidOperationException("原生模组服务尚未初始化。");
    public ActorContext? Part(string name)
    {
        int id = Instance.Scope.ReadOr(name, -4).Int;
        return Services.Entities.Find(id);
    }
}

public sealed partial class GmWorld
{
    internal NativeModServices? NativeServices;
    internal GmInstance CreateNativeEntity(double x, double y)
    {
        const int kind = -100001;
        if (!Objects.ContainsKey(kind))
        {
            var definition = JsonNode.Parse(Objects[Constants["objLine"].Int].GetRawText())!.AsObject();
            definition["id"] = kind; definition["name"] = "native.entity"; definition["events"] = new JsonArray();
            definition["sprite"] = -1; definition["mask"] = -1; definition["parent"] = -100;
            Objects[kind] = JsonSerializer.SerializeToElement(definition); parents[kind] = -100; InvalidateObjects();
        }
        return Create(kind, x, y, run: false);
    }
}

public sealed partial class NativeModServices : IDisposable
{
    internal readonly GmWorld World;
    internal readonly ContentRegistry Registry;
    public NativeInputService Input { get; }
    public NativeEntityService Entities { get; }
    public NativeCombatService Combat { get; }
    public NativePresentation Presentation { get; }
    public NativeAudioService Audio { get; }
    public NativeMapFlowService Maps { get; }
    public NativeInstanceService Instances { get; }
    public NativeEventService Events { get; }
    public NativeActorService Actors { get; }
    internal NativeResourceScope CurrentScope { get; private set; }
    internal IDisposable EnterScope(NativeResourceScope scope)
    {
        var previous = CurrentScope; CurrentScope = scope; return new ScopeLease(this, previous);
    }
    private sealed class ScopeLease(NativeModServices services, NativeResourceScope previous) : IDisposable
    { public void Dispose() => services.CurrentScope = previous; }
    internal void ClearScope(NativeResourceScope scope) { Entities.ClearScope(scope); Presentation.ClearScope(scope); Audio.ClearScope(scope); Instances.ClearScope(scope); Actors.ClearScope(scope); }
    private void ClearOwner(NativeModOwner owner) { Entities.ClearOwner(owner); Presentation.ClearOwner(owner); Audio.ClearOwner(owner); Instances.ClearOwner(owner); Actors.ClearOwner(owner); }
    internal Action? LeaveRoom;
    internal Action? EnterRoom;
    public FrameContext Frame { get; internal set; }
    public NativeModServices(GmWorld world, ContentRegistry registry)
    {
        World = world; Registry = registry; registry.ModDataRoot = Path.Combine(world.UserRoot, "Content", "ModData"); Input = new(registry, world.UserRoot);
        Entities = new(this); Combat = new(this); Presentation = new(this); Audio = new(this); Maps = new(this); world.NativeServices = this; Instances = new(this); Events = new(this); Actors = new(this); registry.OwnerRetired += ClearOwner;
    }
    public NativeGameEvent Dispatch(NativeGameEvent value, NativeEventPhase phase)
    {
        foreach (var pair in Registry.EventHandlers.Where(p => p.Value.Event == value.Name && p.Value.Phase == phase).ToArray())
        {
            using var scope = EnterScope(Registry.ScopeFor("event-handler", pair.Key, value.Actor?.Id ?? 0));
            try { pair.Value.Handle(value); } catch (Exception error) { Registry.ReportLifecycle(CurrentScope, "EVENT", error); }
        }
        return value;
    }
    internal NativeGameEvent? BeforeCode(string key,GmContext context)
    {
        string name="engine."+(key.StartsWith("scripts/",StringComparison.Ordinal)?key[8..].ToLowerInvariant():key);
        if(!Registry.HasEventHandlers(name))return null;
        var value=new NativeGameEvent(name,new(World,context.Self));
        value.Arguments["code"]=key;value.Arguments["other"]=context.Other.Id;
        value.Arguments["eventKind"]=context.EventKind;value.Arguments["eventSub"]=context.EventSub;
        for(int i=0;i<context.Arguments.Length;i++)value.Arguments["argument"+i]=context.Arguments[i];
        return Dispatch(value,NativeEventPhase.Before);
    }
    internal GmValue AfterCode(NativeGameEvent? value,GmValue result)
    {
        if(value is null)return result;
        value.Result=result;return Dispatch(value,NativeEventPhase.After).Result;
    }
    public GmValue Invoke(string name, ActorContext actor, Func<GmValue> original)
    {
        var value = Dispatch(new(name, actor), NativeEventPhase.Before);
        if (!value.Handled) value.Result = original();
        Dispatch(value, NativeEventPhase.After); return value.Result;
    }
    internal void ClearActor(int id) { Presentation.ClearActor(id); Audio.ClearActor(id); Entities.ClearOwned(id); }
    public void Dispose() { Registry.OwnerRetired -= ClearOwner; Events.Dispose(); Entities.Dispose(); Presentation.ClearAll(); Audio.Dispose(); Instances.Dispose(); if (World.NativeServices == this) World.NativeServices = null; }
}

public sealed class NativeEntityService(NativeModServices services) : IDisposable
{
    private sealed record Entry(GmInstance Instance, NativeEntityDefinition Definition, int Owner, NativeResourceScope Scope);
    private readonly Dictionary<int, Entry> spawned = new();
    internal Action<int, string>? Attach;
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

public readonly record struct NativeRayHit(ActorContext Target, double X, double Y, double Distance, bool Wall);

public sealed class NativeCombatService
{
    private readonly NativeModServices services;
    private readonly JsonElement damage;
    internal NativeCombatService(NativeModServices services)
    {
        this.services = services;
        var tree = JsonNode.Parse(services.World.Code["objects/objBullet/3/1/0"].GetRawText())!;
        var body = Nodes(tree).Single(n => n[0]?.ToString() == "with" && n[1]?[0]?.ToString() == "id" && n[1]?[1]?.ToString() == "col").DeepClone();
        var amount = Nodes(body).Single(n => n[0]?.ToString() == "binary" && n[1]?.ToString() == "AssignSubtract" && n[2]?[1]?.ToString() == "HP");
        amount[3]![2] = new JsonArray("id", "argument0");
        damage = JsonSerializer.SerializeToElement(body);
    }
    private static IEnumerable<JsonArray> Nodes(JsonNode node)
    {
        if (node is not JsonArray array) yield break;
        if (array.Count > 0 && array[0] is JsonValue) yield return array;
        foreach (var child in array) if (child is not null) foreach (var descendant in Nodes(child)) yield return descendant;
    }
    public NativeRayHit? Raycast(ActorContext source, double x, double y, double angle, double range)
    {
        if (!double.IsFinite(range) || range <= 0) throw new ArgumentOutOfRangeException(nameof(range));
        double radians = angle * Math.PI / 180, dx = Math.Cos(radians), dy = -Math.Sin(radians), closest = range;
        NativeRayHit? result = null;
        var world = services.World;
        foreach (var target in world.Instances)
        {
            if (target.Destroyed || !target.Active || target.Id == source.Id) continue;
            bool wall = target.Scope.ReadOr("solid").Truth || world.IsObject(target.Object, world.Constants["objBlock"].Int);
            bool enemy = world.IsObject(target.Object, world.Constants["objEnemyDoll"].Int) && target.Scope.ReadOr("DAMAGE_ACTIVE").Truth;
            if (!wall && !enemy) continue;
            var bounds = world.Bounds(target); if (bounds.Empty) continue;
            double near = 0, far = range;
            bool Axis(double start, double direction, double low, double high)
            {
                if (Math.Abs(direction) < 1e-12) return start >= low && start <= high;
                double a = (low - start) / direction, b = (high - start) / direction;
                near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b)); return near <= far;
            }
            if (Axis(x, dx, bounds.Left, bounds.Right) && Axis(y, dy, bounds.Top, bounds.Bottom) && near <= closest && (result is null || near < closest || wall))
            { closest = near; result = new(new(world, target), x + dx * near, y + dy * near, near, wall); }
        }
        return result;
    }
    public bool Damage(ActorContext source, NativeRayHit hit, double amount, double angle)
    {
        if (hit.Wall || hit.Target.Instance.Destroyed || !hit.Target.Instance.Scope.ReadOr("DAMAGE_ACTIVE").Truth || !double.IsFinite(amount) || amount < 0) return false;
        var value = new NativeGameEvent("combat.damage", hit.Target); value.Arguments["damage"] = amount; value.Arguments["source"] = source.Id;
        services.Dispatch(value, NativeEventPhase.Before);
        if (!value.Handled)
        {
            var probe = new GmInstance { Id = -999999, Object = services.World.Constants["objBullet"].Int };
            foreach (var pair in new Dictionary<string, double> { ["preX"] = source.X, ["preY"] = source.Y, ["x"] = hit.X, ["y"] = hit.Y, ["fireAngle"] = angle, ["direction"] = angle }) probe.Scope.Write(pair.Key, pair.Value);
            var context = new GmContext(probe, args: [value.Arguments["damage"]]); context.Locals.Write("col", hit.Target.Id);
            services.World.Evaluator.Eval(damage, context);
            value.Result = hit.Target.Read("INV") == 0;
        }
        services.Dispatch(value, NativeEventPhase.After); return value.Result.Truth;
    }
}

public sealed record NativeSprite(int Owner, string Id, string Resource, double X, double Y, double Angle, double ScaleX, double ScaleY, double OriginX, double OriginY, double Depth);
public sealed record NativeHudItem(int Owner, string Id, string Text, double X, double Y);
public sealed record NativeLine(int Owner, string Id, double X1, double Y1, double X2, double Y2, double Expires, int Colour, double Width);
public sealed class NativePresentation(NativeModServices services)
{
    private readonly Dictionary<(NativeResourceScope Scope, int Owner, string Id), NativeSprite> sprites = new();
    private readonly Dictionary<(NativeResourceScope Scope, int Owner, string Id), NativeHudItem> hud = new();
    private readonly Dictionary<(NativeResourceScope Scope, int Owner, string Id), NativeLine> lines = new();
    public IReadOnlyCollection<NativeSprite> Sprites => sprites.Values;
    public IReadOnlyCollection<NativeHudItem> Hud => hud.Values;
    public IReadOnlyCollection<NativeLine> Lines => lines.Values;
    public void Line(int owner, string id, double x1, double y1, double x2, double y2, double seconds = .08, int colour = 0x88eeff, double width = 1)
        => lines[(services.CurrentScope, owner, id)] = new(owner, id, x1, y1, x2, y2, services.Frame.GameTime + Math.Max(0, seconds), colour, width);
    internal void Update() { foreach (var pair in lines.Where(p => p.Value.Expires <= services.Frame.GameTime).ToArray()) lines.Remove(pair.Key); }
    public void Sprite(int owner, string id, string resource, double x, double y, double angle = 0, double scaleX = 1, double scaleY = 1, double originX = 0, double originY = 0, double depth = -10000)
    {
        if (!services.Registry.Resources.ContainsKey(resource)) throw new InvalidDataException("资源未加载：" + resource);
        sprites[(services.CurrentScope, owner, id)] = new(owner, id, resource, x, y, angle, scaleX, scaleY, originX, originY, depth);
    }
    public void Text(int owner, string id, string text, double x = 12, double y = 56) => hud[(services.CurrentScope, owner, id)] = new(owner, id, text, x, y);
    public void Remove(int owner, string id) { var key = (services.CurrentScope, owner, id); sprites.Remove(key); hud.Remove(key); lines.Remove(key); }
    private void ClearWhere(Func<(NativeResourceScope Scope, int Owner, string Id), bool> matches)
    {
        foreach (var key in sprites.Keys.Where(matches).ToArray()) sprites.Remove(key);
        foreach (var key in hud.Keys.Where(matches).ToArray()) hud.Remove(key);
        foreach (var key in lines.Keys.Where(matches).ToArray()) lines.Remove(key);
    }
    public void Clear(int owner) => ClearWhere(k => k.Owner == owner && k.Scope == services.CurrentScope);
    public void Clear() => ClearScope(services.CurrentScope);
    internal void ClearScope(NativeResourceScope scope) => ClearWhere(k => k.Scope == scope);
    internal void ClearOwner(NativeModOwner owner) => ClearWhere(k => k.Scope.Owner == owner);
    internal void ClearActor(int owner) => ClearWhere(k => k.Owner == owner);
    internal void ClearAll() { sprites.Clear(); hud.Clear(); lines.Clear(); }
}
