using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

/// <summary>Native actions read/write the live GM actor and retain GM collision and damage handling.</summary>
public sealed class GmNativeBridge : IDisposable
{
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private readonly Dictionary<int, CharacterDefinition> characters = new();
    private readonly Dictionary<(int, CharacterActionSlot), ICharacterAction> actions = new();
    private readonly Dictionary<int, string> entityBindings = new();
    private readonly Dictionary<int, IEntityBehavior> entities = new();
    private readonly Dictionary<string, JsonElement> originals = new();
    public int ActionUpdates { get; private set; }
    public int EntityUpdates { get; private set; }
    public GmNativeBridge(GmWorld world, ContentRegistry registry)
    {
        this.world = world; this.registry = registry;
        registry.Changed += Refresh;
        PatchMovementSlots();
    }
    public void BindCharacter(GmInstance actor, CharacterDefinition definition)
    {
        UnbindCharacter(actor.Id);
        characters[actor.Id] = definition;
        BuildActions(actor.Id, definition);
        SetControllerFlags();
    }
    public CharacterDefinition? BoundCharacter(int instanceId) => characters.GetValueOrDefault(instanceId);
    public void UnbindCharacter(int instanceId)
    {
        var actor = Find(instanceId);
        foreach (var pair in actions.Where(p => p.Key.Item1 == instanceId).ToArray())
        { if (actor is not null) Detach(pair.Value, actor); actions.Remove(pair.Key); }
        characters.Remove(instanceId); SetControllerFlags();
    }
    public void AttachEntity(int instanceId, string registryId)
    {
        if (!registry.Entities.TryGetValue(registryId, out var factory)) throw new InvalidDataException("实体行为未加载：" + registryId);
        if (entities.Remove(instanceId, out var previous) && Find(instanceId) is {} actor) Detach(previous, actor);
        entityBindings[instanceId] = registryId; entities[instanceId] = factory();
    }
    public void Update(FrameContext frame)
    {
        foreach (var pair in actions.ToArray())
        {
            var actor = Find(pair.Key.Item1);
            if (actor is null) { UnbindCharacter(pair.Key.Item1); continue; }
            if (actor.Active) { pair.Value.Update(new(world, actor), frame); ActionUpdates++; }
        }
        foreach (var pair in entities.ToArray())
        {
            var actor = Find(pair.Key);
            if (actor is null) { entities.Remove(pair.Key); entityBindings.Remove(pair.Key); continue; }
            if (actor.Active) { pair.Value.Update(new(world, actor), frame); EntityUpdates++; }
        }
        SetControllerFlags();
    }
    // Reserved evaluator interception point. Movement is gated per slot inside the shared script.
    public bool TrySuppressCode(string key, GmContext context)
    {
        if (key is "objects/objController/3/0/13" or "objects/objController/3/0/17" or "objects/objController/3/0/22") SetControllerFlags();
        return false;
    }
    private void Detach(ICharacterAction action, GmInstance actor)
    {
        try { action.Detach(new(world, actor)); } catch (Exception error) { Console.Error.WriteLine("MOD_ACTION_DETACH " + error.Message); }
    }
    private void Detach(IEntityBehavior behavior, GmInstance actor)
    {
        try { behavior.Detach(new(world, actor)); } catch (Exception error) { Console.Error.WriteLine("MOD_ENTITY_DETACH " + error.Message); }
    }
    private GmInstance? Find(int id) => world.Instances.FirstOrDefault(i => i.Id == id && !i.Destroyed);
    private void BuildActions(int id, CharacterDefinition definition)
    {
        foreach (var pair in definition.Actions)
            if (registry.Actions.TryGetValue(pair.Value, out var factory))
            {
                var action = factory();
                if (action.Slot != pair.Key) throw new InvalidDataException("动作槽与注册动作不一致：" + pair.Value);
                actions[(id, pair.Key)] = action;
            }
    }
    private void Refresh()
    {
        foreach (var pair in actions)
            if (Find(pair.Key.Item1) is {} actor) Detach(pair.Value, actor);
        actions.Clear();
        foreach (var pair in characters) BuildActions(pair.Key, pair.Value);
        foreach (var pair in entities)
            if (Find(pair.Key) is {} actor) Detach(pair.Value, actor);
        entities.Clear();
        foreach (var pair in entityBindings)
            if (registry.Entities.TryGetValue(pair.Value, out var factory)) entities[pair.Key] = factory();
        SetControllerFlags();
    }
    private void SetControllerFlags()
    {
        if (!world.Constants.TryGetValue("objController", out var controllerObject)) return;
        foreach (var controller in world.Instances.Where(i => !i.Destroyed && world.IsObject(i.Object, controllerObject.Int)))
        {
            int target = controller.Scope.ReadOr("CtrlTarget", -4).Int;
            if (Find(target) is null)
                target = world.Instances.FirstOrDefault(i => !i.Destroyed && world.IsObject(i.Object, target))?.Id ?? target;
            controller.Scope.Write("slackNativeJump", actions.ContainsKey((target, CharacterActionSlot.Jump)));
            controller.Scope.Write("slackNativeDash", actions.ContainsKey((target, CharacterActionSlot.Dash)));
        }
    }
    private void PatchMovementSlots()
    {
        // Nana, Ryoko and Yuki combine walking, jumping and dashing in these scripts.
        // Gate only the initiating JumpNum branch and native DASH case, preserving the rest.
        foreach (string key in new[] { "objects/objController/3/0/13", "objects/objController/3/0/17", "objects/objController/3/0/22" })
        {
            if (!world.Code.TryGetValue(key, out var original)) continue;
            var tree = JsonNode.Parse(original.GetRawText())!.AsArray();
            foreach (var node in Nodes(tree).ToArray())
            {
                if (node.Count == 4 && node[0]?.ToString() == "if" && HasIdentifier(node[1], "JumpNum"))
                {
                    string field = HasMotion(node[2], "DASH") ? "slackNativeDash" : "slackNativeJump";
                    node[1] = new JsonArray("binary", "And", Gate(field), node[1]!.DeepClone());
                }
                if (node.Count > 0 && node[0] is JsonArray)
                {
                    for (int i = 0; i < node.Count; i++)
                    {
                        if (node[i] is not JsonArray label || label[0]?.ToString() != "case" || label[1]?[1]?.ToString() != "DASH") continue;
                        int end = i + 1;
                        while (end < node.Count && node[end]?[0]?.ToString() is not ("case" or "default" or "break")) end++;
                        var body = new JsonArray();
                        for (int n = i + 1; n < end; n++) body.Add(node[n]!.DeepClone());
                        for (int n = end - 1; n > i; n--) node.RemoveAt(n);
                        node.Insert(i + 1, new JsonArray("if", Gate("slackNativeDash"), new JsonArray("group", body), null));
                    }
                }
            }
            originals[key] = original;
            world.Code[key] = JsonSerializer.SerializeToElement(tree);
        }
        static JsonArray Gate(string field) => new("unary", "Not", new JsonArray("binary", "Deref", new JsonArray("id", "other"), new JsonArray("id", field)));
        static bool HasIdentifier(JsonNode? node, string id) => node is JsonArray a && (a.Count == 2 && a[0]?.ToString() == "id" && a[1]?.ToString() == id || a.Any(n => HasIdentifier(n, id)));
        static bool HasMotion(JsonNode? node, string motion) => node is JsonArray a && (a.Count == 3 && a[0]?.ToString() == "call" && a[1]?.ToString() == "MotionChange" && HasIdentifier(a[2]?[0], motion) || a.Any(n => HasMotion(n, motion)));
    }
    private static IEnumerable<JsonArray> Nodes(JsonArray tree)
    {
        yield return tree;
        foreach (var child in tree.OfType<JsonArray>()) foreach (var node in Nodes(child)) yield return node;
    }
    public void Dispose()
    {
        registry.Changed -= Refresh;
        foreach (int id in characters.Keys.ToArray()) UnbindCharacter(id);
        foreach (var pair in entities) if (Find(pair.Key) is {} actor) Detach(pair.Value, actor);
        entities.Clear(); entityBindings.Clear();
        foreach (var pair in originals) world.Code[pair.Key] = pair.Value;
    }
}

/// <summary>Shared movement queries use the actor's original GM collision and motion data.</summary>
public static class NativeActorActions
{
    public static GmInstance? Controller(ActorContext actor) => actor.World.Constants.TryGetValue("objController", out var kind)
        ? actor.World.Instances.FirstOrDefault(i => !i.Destroyed && actor.World.IsObject(i.Object, kind.Int) && (i.Scope.ReadOr("CtrlTarget", -4).Int == actor.Id || actor.World.IsObject(actor.Instance.Object, i.Scope.ReadOr("CtrlTarget", -4).Int))) : null;
    public static int Key(ActorContext actor, int slot)
    {
        int offset = actor.World.Globals.ReadOr("controllMode").Equals(actor.World.Constants.GetValueOrDefault("CTRL_KEY")) ? 12 : 0;
        return actor.World.Globals.ReadOr(GmScope.Key("key", new[] { offset + slot })).Int;
    }
    public static bool Grounded(ActorContext actor) => actor.VerticalSpeed >= 0 && !actor.Call("place_free", actor.X, actor.Y + 2).Truth;
    public static bool CanAct(ActorContext actor) => actor.Read("BINDED") == 0 && actor.Read("HALFBINDED") == 0 && actor.World.Globals.ReadOr("PlayerHP", 1).Number > 0
        && !actor.Instance.Destroyed && IsLocomotion(actor);
    public static bool IsLocomotion(ActorContext actor)
    {
        double motion = actor.Read("ChangeMotion") != 0 ? actor.Read("ChangeMotionNum") : actor.Read("MotionNum");
        return new[] { "STAND_0", "STAND_1", "STAND_0_RYOTE", "STAND_1_RYOTE", "WALK", "RUN", "JUMPUP", "JUMPUP2", "JUMPUP3", "JUMPDOWN", "DASH", "CROUCH" }
            .Any(name => actor.Instance.Scope.Values.ContainsKey(name) && actor.Read(name) == motion);
    }
    public static double FirstJumpVelocity(ActorContext actor)
    {
        string preset = actor.World.Globals.ReadOr("Chara", "Nana").ToString();
        double velocity = preset == "Yuki" ? -18 : preset == "Ryoko" ? -22 : -20;
        if (actor.World.Globals.ReadOr("SurvivalGame").Truth) velocity *= preset == "Yuki" ? .5 : preset == "Nana" ? .9 : 1;
        if (preset == "Nana") velocity *= Controller(actor)?.Scope.ReadOr("JumpMultiplier", 1).Number ?? 1;
        return velocity;
    }
    public static bool MotionMatches(ActorContext actor, string motion)
    {
        if (!actor.Instance.Scope.Values.ContainsKey(motion)) return false;
        double expected = actor.Read(motion);
        return actor.Read("ChangeMotion") != 0 ? actor.Read("ChangeMotionNum") == expected : actor.Read("MotionNum") == expected;
    }
    public static void Motion(ActorContext actor, string motion)
    { if (actor.Instance.Scope.Values.ContainsKey(motion)) actor.Call("MotionChange", actor.Read(motion), 0, 1); }
    public static bool MoveHorizontal(ActorContext actor, double distance)
    {
        // Sweep at one-pixel intervals; the original dash only checked the endpoint.
        double direction = Math.Sign(distance), remaining = Math.Abs(distance);
        while (remaining > 0)
        {
            double step = direction * Math.Min(1, remaining);
            if (!actor.Call("place_free", actor.X + step, actor.Y).Truth) return false;
            actor.X += step; remaining -= Math.Abs(step);
        }
        return true;
    }
}
