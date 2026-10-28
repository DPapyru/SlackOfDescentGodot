using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;

namespace SlackOfDescent;

public sealed partial class GmNativeBridge : IDisposable
{
    private sealed record ScopeBox(NativeResourceScope Scope);
    private readonly ConditionalWeakTable<object, ScopeBox> scopes = new();
    private IDisposable EnterCallback(object callback, int actor) => Services.EnterScope(scopes.TryGetValue(callback, out var scope) ? scope.Scope with { Actor = actor } : default);
    private void Invoke(object callback, int actor, Action call) { using var scope = EnterCallback(callback, actor); call(); }
    private T Invoke<T>(object callback, int actor, Func<T> call) { using var scope = EnterCallback(callback, actor); return call(); }
    private T Create<T>(string kind, string id, int actor, Func<T> factory) where T : class
    {
        var scope = registry.ScopeFor(kind, id, actor);
        using var lease = Services.EnterScope(scope);
        try { var value = factory(); scopes.Add(value, new(scope)); return value; }
        catch { ClearRuntimeScope(scope); Services.ClearScope(scope); throw; }
    }
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private readonly Dictionary<int, CharacterDefinition> characters = new();
    private readonly HashSet<int> registeredCharacters = new();
    private readonly Dictionary<(int, CharacterActionSlot), Func<ICharacterAction>> actionFactories = new();
    private readonly Dictionary<(int,CharacterActionSlot),string> actionConfigurations=new();
    private readonly Dictionary<int, Func<IEntityBehavior>> entityFactories = new();
    private readonly Dictionary<(int, CharacterActionSlot), ICharacterAction> stagedActions = new();
    private readonly Dictionary<int, IEntityBehavior> stagedEntities = new();
    private readonly Dictionary<(int, CharacterActionSlot), ICharacterAction> actions = new();
    private readonly Dictionary<(int, CharacterActionSlot), ICharacterAction> runtimeActions = new();
    private readonly Dictionary<(int, CharacterActionSlot), NativeResourceScope> runtimeScopes = new();
    private readonly Dictionary<int, string> entityBindings = new();
    private readonly Dictionary<int, IEntityBehavior> entities = new();
    private readonly Dictionary<string, JsonElement> originals = new();
    private readonly Dictionary<int, long> jumpRequests = new();
    private readonly Func<GmInstance, bool>? previousJumpExtension;
    private readonly Func<string, GmContext, bool>? previousSuppressCode;
    private readonly Dictionary<(int, string), long> weaponRequests = new();
    private readonly Dictionary<int,(int Owner,int Ammunition)> originalWeapons = new();
    private readonly Dictionary<int, GmInstance> entityInstances = new();
    private readonly HashSet<GmInstance> roomSurvivors = new();
    private int room;
    public NativeModServices Services { get; }
    public int ActionUpdates { get; private set; }
    public int EntityUpdates { get; private set; }
    public GmNativeBridge(GmWorld world, ContentRegistry registry)
    {
        this.world = world; this.registry = registry;
        Services = new(world, registry); room = world.CurrentRoom;
        Services.Entities.Attach = AttachEntity;
        Services.Entities.Interaction = (id, other) => { if (entities.TryGetValue(id, out var behavior) && Find(id) is {} instance) Invoke(behavior, id, () => behavior.Interacted(new(world, instance), other)); };
        previousSuppressCode = world.SuppressCode;
        world.SuppressCode = TrySuppressCode;
        previousJumpExtension = world.ExtendCharacterJump;
        world.ExtendCharacterJump = ExtendJump;
        world.CharacterJumped += Jumped;
        registry.Changed += Refresh;
        registry.Committed += CommitRefresh;
        Services.LeaveRoom = LeaveRoom;
        Services.EnterRoom = EnterRoom;
        registry.OwnerRetired += ClearRuntimeOwner;
        NativeActorBridges.Register(Services, this);
        PatchMovementSlots();
    }
    public void BindCharacter(GmInstance actor, CharacterDefinition definition)
    {
        ValidateGraphBinding(actor,definition);
        UnbindCharacter(actor.Id);
        characters[actor.Id] = definition;
        if (registry.Characters.TryGetValue(definition.Id, out var registered) && ReferenceEquals(registered, definition)) registeredCharacters.Add(actor.Id);
        BuildActions(actor.Id, definition);
        Services.Motions.Configure(new(world,actor),definition);
        SyncWeapons();
        SetControllerFlags();
    }
    public CharacterDefinition? BoundCharacter(int instanceId) => characters.GetValueOrDefault(instanceId);
    public void UnbindCharacter(int instanceId)
    {
        foreach(var pair in originalWeapons.Where(pair=>pair.Value.Owner==instanceId).ToArray())RestoreWeapon(pair.Key,pair.Value.Ammunition);
        var actor = Find(instanceId);
        foreach (var pair in actions.Where(p => p.Key.Item1 == instanceId).ToArray())
        { if (actor is not null) Detach(pair.Value, actor); actions.Remove(pair.Key); actionFactories.Remove(pair.Key); actionConfigurations.Remove(pair.Key); }
        foreach (var key in runtimeActions.Keys.Where(k => k.Item1 == instanceId).ToArray()) RemoveRuntime(key.Item1, key.Item2);
        characters.Remove(instanceId); registeredCharacters.Remove(instanceId);
        Services.Motions.Forget(instanceId); jumpRequests.Remove(instanceId);
        foreach (var key in weaponRequests.Keys.Where(k => k.Item1 == instanceId).ToArray()) weaponRequests.Remove(key);
        Services.ClearActor(instanceId); SetControllerFlags();
    }
    internal void BindRuntime(ActorContext actor, CharacterActionSlot slot, ICharacterAction action)
    {
        var key = (actor.Id, slot);
        if(Services.Motions.Controls(actor.Id)&&(slot is CharacterActionSlot.Jump or CharacterActionSlot.Dash or CharacterActionSlot.Controller || action.Mode==CharacterActionMode.Controller))throw new InvalidDataException("Motion graph owns character control.");
        if (action.Slot != slot) throw new InvalidDataException("动作槽与绑定槽位不一致：" + action.Id);
        if (action.Mode == CharacterActionMode.Controller && actions.Concat(runtimeActions).Any(pair => pair.Key.Item1 == actor.Id && pair.Key.Item2 != slot && pair.Value.Mode == CharacterActionMode.Controller))
            throw new InvalidDataException("角色只能绑定一个完整控制器。");
        bool rebound = runtimeActions.TryGetValue(key, out var replaced);
        var previous = rebound ? runtimeScopes.GetValueOrDefault(key) : RuntimeScopeFor(actions.GetValueOrDefault(key), actor.Id);
        runtimeActions[key] = action; runtimeScopes[key] = Services.CurrentScope;
        try { if (replaced is not null && !ReferenceEquals(replaced, action)) DetachManaged(previous, replaced, actor.Instance); }
        catch { runtimeActions.Remove(key); runtimeScopes.Remove(key); throw; }
        SetControllerFlags();
    }
    internal void UnbindRuntime(ActorContext actor, CharacterActionSlot slot)
    {
        var key = (actor.Id, slot);
        if (!runtimeActions.Remove(key, out var action)) return;
        runtimeScopes.Remove(key, out var scope);
        if (Find(actor.Id) is {} instance) DetachManaged(scope, action, instance);
        SetControllerFlags();
    }
    private NativeResourceScope RuntimeScopeFor(ICharacterAction? action, int actor) => action is not null && action is not ICharacterJumpExtension
        && scopes.TryGetValue(action, out var box) ? box.Scope with { Actor = actor } : default;
    private ICharacterAction? Binding(int id, CharacterActionSlot slot) => runtimeActions.TryGetValue((id, slot), out var action) ? action : actions.GetValueOrDefault((id, slot));
    internal ICharacterAction? BoundAction(int instanceId, CharacterActionSlot slot) => Binding(instanceId, slot);
    private void DetachManaged(NativeResourceScope scope, ICharacterAction action, GmInstance actor)
    {
        using var lease = Services.EnterScope(scope);
        try { action.Detach(new(world, actor)); } catch (Exception error) { registry.ReportLifecycle(Services.CurrentScope, "ACTION_DETACH", error); }
    }
    private void RemoveRuntime(int id, CharacterActionSlot slot)
    {
        var key = (id, slot);
        if (!runtimeActions.Remove(key, out var action)) return;
        runtimeScopes.Remove(key, out var scope);
        if (Find(id) is {} actor) DetachManaged(scope, action, actor);
    }
    internal void ClearRuntimeScope(NativeResourceScope scope)
    {
        foreach (var key in runtimeScopes.Where(pair => pair.Value == scope).Select(pair => pair.Key).ToArray()) RemoveRuntime(key.Item1, key.Item2);
    }
    internal void ClearRuntimeOwner(NativeModOwner owner)
    {
        foreach (var key in runtimeScopes.Where(pair => pair.Value.Owner == owner).Select(pair => pair.Key).ToArray()) RemoveRuntime(key.Item1, key.Item2);
    }
    public void AttachEntity(int instanceId, string registryId)
    {
        if (!registry.Entities.TryGetValue(registryId, out var factory)) throw new InvalidDataException("实体行为未加载：" + registryId);
        if (entities.Remove(instanceId, out var previous) && Find(instanceId) is {} actor) Detach(previous, actor);
        entityBindings[instanceId] = registryId;
        var behavior = Create("entity", registryId, instanceId, factory);
        entities[instanceId] = behavior; entityFactories[instanceId] = factory;
        if (Find(instanceId) is {} instance)
        {
            entityInstances[instanceId] = instance;
            try { Invoke(behavior, instanceId, () => { behavior.Created(new(world, instance)); behavior.RoomEntered(new(world, instance)); }); }
            catch
            {
                Detach(behavior, instance);
                entities.Remove(instanceId); entityBindings.Remove(instanceId); entityFactories.Remove(instanceId); entityInstances.Remove(instanceId);
                throw;
            }
        }
    }
    private void LeaveRoom()
    {
        roomSurvivors.Clear();
        foreach (var pair in entities.ToArray())
        {
            if (!entityInstances.TryGetValue(pair.Key, out var instance)) continue;
            Invoke(pair.Value, pair.Key, () => pair.Value.RoomExited(new(world, instance)));
            if (instance.Scope.ReadOr("persistent").Truth) { roomSurvivors.Add(instance); continue; }
            Detach(pair.Value, instance); Services.ClearActor(pair.Key);
            Services.Entities.Forget(pair.Key);
            entities.Remove(pair.Key); entityBindings.Remove(pair.Key); entityFactories.Remove(pair.Key); entityInstances.Remove(pair.Key);
        }
    }
    private void EnterRoom()
    {
        room = world.CurrentRoom;
        foreach (var pair in entities.ToArray())
            if (Find(pair.Key) is {} instance && roomSurvivors.Contains(instance)) Invoke(pair.Value, pair.Key, () => pair.Value.RoomEntered(new(world, instance)));
        roomSurvivors.Clear();
    }

    public void Update(FrameContext frame)
    {
        Services.Frame = frame;
        Services.Motions.CaptureInput(frame.Input??InputSnapshot.Empty);
        Services.Presentation.Update();
        if (room != world.CurrentRoom)
        {
            foreach (var pair in entities) if (entityInstances.TryGetValue(pair.Key, out var instance)) Invoke(pair.Value, pair.Key, () => pair.Value.RoomExited(new(world, instance)));
            room = world.CurrentRoom;
            foreach (var pair in entities) if (Find(pair.Key) is {} instance) Invoke(pair.Value, pair.Key, () => pair.Value.RoomEntered(new(world, instance)));
        }
        Services.Entities.Update();
        foreach (var pair in actions.ToArray())
        {
            var actor = Find(pair.Key.Item1);
            if (actor is null) { UnbindCharacter(pair.Key.Item1); continue; }
            if (!actor.Active || runtimeActions.ContainsKey(pair.Key) || Services.Motions.Controls(actor.Id)&&(pair.Key.Item2 is CharacterActionSlot.Jump or CharacterActionSlot.Dash or CharacterActionSlot.Controller)) continue;
            Invoke(pair.Value, actor.Id, () => pair.Value.Update(new(world, actor), frame)); ActionUpdates++;
        }
        foreach (var pair in runtimeActions.ToArray())
        {
            if (Find(pair.Key.Item1) is not {} actor) { RemoveRuntime(pair.Key.Item1, pair.Key.Item2); continue; }
            if (actor.Active && !(Services.Motions.Controls(actor.Id)&&(pair.Key.Item2 is CharacterActionSlot.Jump or CharacterActionSlot.Dash or CharacterActionSlot.Controller))) { Invoke(pair.Value, actor.Id, () => pair.Value.Update(new(world, actor), frame)); ActionUpdates++; }
        }
        SyncWeapons();
        foreach (var pair in entities.ToArray())
        {
            var actor = Find(pair.Key);
            if (actor is null)
            {
                if (entityInstances.Remove(pair.Key, out var instance)) { Invoke(pair.Value, instance.Id, () => pair.Value.Destroyed(new(world, instance))); Detach(pair.Value, instance); }
                entities.Remove(pair.Key); entityBindings.Remove(pair.Key); entityFactories.Remove(pair.Key); Services.ClearActor(pair.Key); continue;
            }
            if (actor.Active)
            {
                using var scope = EnterCallback(pair.Value, actor.Id);
                pair.Value.Update(new(world, actor), frame); EntityUpdates++;
                var bounds = world.Bounds(actor);
                foreach (var other in world.Instances.ToArray())
                    if (other.Id != actor.Id && other.Active && !other.Destroyed && !bounds.Empty && bounds.Overlaps(world.Bounds(other))) pair.Value.Collided(new(world, actor), new(world, other));
            }
        }
        SetControllerFlags();
    }
    public bool TrySuppressCode(string key, GmContext context)
    {
        if(Services.Motions.Suppress(key,context))return true;
        if(key.StartsWith("objects/objController/3/",StringComparison.Ordinal))
        {
            int target=context.Self.Scope.ReadOr("CtrlTarget",-4).Int;
            if(Find(target) is null)target=world.Instances.FirstOrDefault(instance=>!instance.Destroyed&&world.IsObject(instance.Object,target))?.Id??target;
            if(Binding(target,CharacterActionSlot.Controller)?.Mode==CharacterActionMode.Controller)return true;
        }
        if (key.StartsWith("objects/objNana_Gun/8/", StringComparison.Ordinal) || key.StartsWith("objects/objNana_BulletNum/8/", StringComparison.Ordinal))
        {
            int target = context.Self.Scope.ReadOr("DrawTarget", -4).Int;
            if (Find(target) is {} owner&&Binding(target, CharacterActionSlot.Attack) is ICharacterWeapon drawable && Invoke(drawable, target, () => drawable.IsEquipped(new(world,owner)))&&drawable.Mode != CharacterActionMode.Additive) return true;
        }
        if (key is "objects/objNana_Gun/7/10/0" or "objects/objNana_Gun/7/11/0" or "objects/objNana_Gun/7/12/0")
        {
            int target = context.Self.Scope.ReadOr("DrawTarget", -4).Int;
            var actor = Find(target);
            if (actor is not null && Binding(target, CharacterActionSlot.Attack) is ICharacterWeapon weapon
                &&Invoke(weapon, target, () => weapon.IsEquipped(new(world,actor)))
                && (key != "objects/objNana_Gun/7/11/0" || context.Self.Scope.Values.ContainsKey("Gun")))
            {
                using var scope = EnterCallback(weapon, target);
                if (weaponRequests.GetValueOrDefault((target, key), -1) != world.Frame)
                {
                    weaponRequests[(target, key)] = world.Frame;
                    var owner = new ActorContext(world, actor);
                    if (key == "objects/objNana_Gun/7/11/0") Services.Invoke("weapon.reload",owner,()=>{weapon.Reload(owner);return 0;});
                    else
                    {
                        var controller = NativeActorActions.Controller(owner);
                        double angle = controller?.Scope.ReadOr("m_dir").Number ?? 0;
                        var hand = owner.Part("HAND3_ABOVE") ?? owner;
                        var request = new NativeFireRequest(hand.X + 32 * Math.Cos(angle * Math.PI / 180), hand.Y - 32 * Math.Sin(angle * Math.PI / 180), angle, controller?.Scope.ReadOr("Aim").Truth ?? false);
                        Services.Invoke("weapon.fire", owner, () => { weapon.Fire(owner, request); return 0; });
                    }
                    SyncWeapons();
                }
                return weapon.Mode != CharacterActionMode.Additive;
            }
        }
        if (key is "objects/objController/3/0/13" or "objects/objController/3/0/17" or "objects/objController/3/0/22") SetControllerFlags();
        return previousSuppressCode?.Invoke(key, context) ?? false;
    }
    private void RestoreWeapon(int id,int ammunition)
    {
        if(Find(id) is {} gun)gun.Scope.Write("Bullet",ammunition);
        originalWeapons.Remove(id);
    }
    private void SyncWeapons()
    {
        if(!world.Constants.TryGetValue("objNana_Gun",out var kind))return;
        foreach(var gun in world.Instances.Where(instance=>!instance.Destroyed&&world.IsObject(instance.Object,kind.Int)))
        {
            int target=gun.Scope.ReadOr("DrawTarget",-4).Int;
            if(Find(target) is {} actor&&Binding(target,CharacterActionSlot.Attack) is ICharacterWeapon weapon
                &&weapon.Mode!=CharacterActionMode.Additive&&Invoke(weapon, target, () => weapon.IsEquipped(new(world,actor)))&&Invoke(weapon, target, () => weapon.State) is {} state)
            {
                originalWeapons.TryAdd(gun.Id,(target,gun.Scope.ReadOr("Bullet").Int));
                gun.Scope.Write("Bullet",Math.Clamp(state.Ammunition,0,state.Capacity));
            }
            else if(originalWeapons.TryGetValue(gun.Id,out var original))RestoreWeapon(gun.Id,original.Ammunition);
        }
        foreach(int id in originalWeapons.Keys.Where(id=>Find(id) is null).ToArray())originalWeapons.Remove(id);
    }
    private bool ExtendJump(GmInstance actor)
    {
        var context = new ActorContext(world, actor);
        var controller = NativeActorActions.Controller(context);
        if (controller is null || controller.Scope.ReadOr("JumpNum").Number != 0
            || !(controller.Scope.ReadOr("b2").Int == 2 || (controller.Scope.ReadOr("ctrl").Int != world.Constants["CTRL_JOYPAD"].Int && controller.Scope.ReadOr("up").Int == 2))
            || jumpRequests.GetValueOrDefault(actor.Id, -1) == world.Frame
            || Binding(actor.Id, CharacterActionSlot.Jump) is not ICharacterJumpExtension extension) return false;
        jumpRequests[actor.Id] = world.Frame;
        if (!Invoke(extension, actor.Id, () => extension.TryExtendJump(context))) return false;
        controller.Scope.Write("JumpNum", 1);
        return true;
    }
    private void Jumped(GmInstance actor)
    {
        if (Binding(actor.Id, CharacterActionSlot.Jump) is ICharacterJumpExtension extension)
        {
            jumpRequests[actor.Id] = world.Frame;
            Invoke(extension, actor.Id, () => extension.Jumped(new(world, actor)));
        }
    }
    private void Detach(ICharacterAction action, GmInstance actor)
    {
        using var scope = EnterCallback(action, actor.Id);
        try { action.Detach(new(world, actor)); } catch (Exception error) { registry.ReportLifecycle(Services.CurrentScope, "ACTION_DETACH", error); }
        finally { ClearRuntimeScope(Services.CurrentScope); Services.ClearScope(Services.CurrentScope); }
    }
    private void Detach(IEntityBehavior behavior, GmInstance actor)
    {
        using var scope = EnterCallback(behavior, actor.Id);
        try { behavior.Detach(new(world, actor)); } catch (Exception error) { registry.ReportLifecycle(Services.CurrentScope, "ENTITY_DETACH", error); }
        finally { ClearRuntimeScope(Services.CurrentScope); Services.ClearScope(Services.CurrentScope); }
    }
    private GmInstance? Find(int id) => world.Instances.FirstOrDefault(i => i.Id == id && !i.Destroyed);
    private void BuildActions(int id, CharacterDefinition definition)
    {
        foreach (var pair in definition.Actions)
            if (registry.Actions.TryGetValue(pair.Value, out var factory))
            {
                var action = Create("action", pair.Value, id, ()=>ConfiguredAction(definition,pair.Key,pair.Value,factory));
                if (action.Slot != pair.Key) throw new InvalidDataException("动作槽与注册动作不一致：" + pair.Value);
                if (action.Mode == CharacterActionMode.Controller && actions.Any(p => p.Key.Item1 == id && p.Value.Mode == CharacterActionMode.Controller)) throw new InvalidDataException("角色只能绑定一个完整控制器。");
                actions[(id, pair.Key)] = action; actionFactories[(id, pair.Key)] = factory;actionConfigurations[(id,pair.Key)]=ConfigSignature(definition,pair.Key);
            }
    }
    private CharacterDefinition? CurrentDefinition(int id, CharacterDefinition previous) => registeredCharacters.Contains(id)
        ? registry.Characters.GetValueOrDefault(previous.Id) : previous;
    private void Refresh()
    {
        stagedActions.Clear(); stagedEntities.Clear();
        foreach (var character in characters)
        {
            var definition = CurrentDefinition(character.Key, character.Value);
            if (definition is null) continue;
            int controllers = 0;
            foreach (var pair in definition.Actions)
            {
                if(Services.Motions.Controls(character.Key)&&pair.Key is CharacterActionSlot.Jump or CharacterActionSlot.Dash)continue;
                if (!registry.Actions.TryGetValue(pair.Value, out var factory)) continue;
                var key = (character.Key, pair.Key);
                var action = actionFactories.TryGetValue(key, out var previous) && ReferenceEquals(factory, previous) && actions.ContainsKey(key) && actionConfigurations.GetValueOrDefault(key)==ConfigSignature(definition,pair.Key) ? actions[key] : Create("action", pair.Value, character.Key, ()=>ConfiguredAction(definition,pair.Key,pair.Value,factory));
                if (action.Slot != pair.Key) throw new InvalidDataException("动作槽与注册动作不一致：" + pair.Value);
                if (action.Mode == CharacterActionMode.Controller && ++controllers > 1) throw new InvalidDataException("角色只能绑定一个完整控制器。");
                if (!ReferenceEquals(action, actions.GetValueOrDefault(key))) stagedActions.Add(key, action);
            }
        }
        foreach (var pair in entityBindings)
            if (registry.Entities.TryGetValue(pair.Value, out var factory) && (!entityFactories.TryGetValue(pair.Key, out var previous) || !ReferenceEquals(factory, previous))) stagedEntities.Add(pair.Key, Create("entity", pair.Value, pair.Key, factory));
    }
    private void CommitRefresh()
    {
        foreach (var character in characters.ToArray())
        {
            var definition = CurrentDefinition(character.Key, character.Value);
            if (definition is null) { UnbindCharacter(character.Key); continue; }
            foreach (var pair in actions.Where(p => p.Key.Item1 == character.Key).ToArray())
            {
                bool same = definition.Actions.TryGetValue(pair.Key.Item2, out string? id) && registry.Actions.TryGetValue(id, out var factory)
                    && actionFactories.TryGetValue(pair.Key, out var previous) && ReferenceEquals(factory, previous) && actionConfigurations.GetValueOrDefault(pair.Key)==ConfigSignature(definition,pair.Key.Item2);
                if (same) continue;
                if (Find(character.Key) is {} actor) Detach(pair.Value, actor);
                actions.Remove(pair.Key); actionFactories.Remove(pair.Key); actionConfigurations.Remove(pair.Key);
                foreach (var key in weaponRequests.Keys.Where(k => k.Item1 == character.Key).ToArray()) weaponRequests.Remove(key);
            }
            characters[character.Key] = definition;
            foreach (var pair in definition.Actions)
            {
                var key = (character.Key, pair.Key);
                if (actions.ContainsKey(key) || !registry.Actions.TryGetValue(pair.Value, out var factory) || !stagedActions.Remove(key, out var action)) continue;
                actions[key] = action; actionFactories[key] = factory;actionConfigurations[key]=ConfigSignature(definition,pair.Key);
            }
        }
        foreach (var pair in entityBindings.ToArray())
        {
            bool present = registry.Entities.TryGetValue(pair.Value, out var factory);
            if (present && entityFactories.TryGetValue(pair.Key, out var previous) && ReferenceEquals(factory, previous)) continue;
            if (entities.Remove(pair.Key, out var behavior) && entityInstances.TryGetValue(pair.Key, out var instance)) Detach(behavior, instance);
            entityFactories.Remove(pair.Key);
            if (!present) { entityBindings.Remove(pair.Key); entityInstances.Remove(pair.Key); continue; }
            if (!stagedEntities.Remove(pair.Key, out var replacement)) continue;
            entities[pair.Key] = replacement; entityFactories[pair.Key] = factory!;
            if (Find(pair.Key) is {} actor)
            {
                entityInstances[pair.Key] = actor;
                try { Invoke(replacement, pair.Key, () => { replacement.Created(new(world, actor)); replacement.RoomEntered(new(world, actor)); }); }
                catch (Exception error)
                {
                    using var scope = EnterCallback(replacement, pair.Key);
                    registry.ReportLifecycle(Services.CurrentScope, "ENTITY_ATTACH", error);
                    Detach(replacement, actor);
                    entities.Remove(pair.Key); entityBindings.Remove(pair.Key); entityFactories.Remove(pair.Key); entityInstances.Remove(pair.Key);
                    Services.Entities.Destroy(pair.Key);
                }
            }
        }
        stagedActions.Clear(); stagedEntities.Clear();
        foreach(var pair in characters)if(Find(pair.Key) is {} actor)Services.Motions.Configure(new(world,actor),pair.Value);
        Services.Entities.Update(); SetControllerFlags();
    }
    private void SetControllerFlags()
    {
        if (!world.Constants.TryGetValue("objController", out var controllerObject)) return;
        foreach (var controller in world.Instances.Where(i => !i.Destroyed && world.IsObject(i.Object, controllerObject.Int)))
        {
            int target = controller.Scope.ReadOr("CtrlTarget", -4).Int;
            if (Find(target) is null)
                target = world.Instances.FirstOrDefault(i => !i.Destroyed && world.IsObject(i.Object, target))?.Id ?? target;
            var jump = Binding(target, CharacterActionSlot.Jump);
            controller.Scope.Write("slackNativeJump", jump is not null && jump is not ICharacterJumpExtension && jump.Mode != CharacterActionMode.Additive);
            var dash = Binding(target, CharacterActionSlot.Dash);
            controller.Scope.Write("slackNativeDash", dash is not null && dash.Mode != CharacterActionMode.Additive);
        }
    }
    private void PatchMovementSlots()
    {
        foreach (string key in new[] { "objects/objController/3/0/13", "objects/objController/3/0/17", "objects/objController/3/0/22" })
        {
            if (!world.Code.TryGetValue(key, out var original)) continue;
            var tree = JsonNode.Parse(original.GetRawText())!.AsArray();
            foreach (var node in Nodes(tree).ToArray())
            {
                if (node.Count == 4 && node[0]?.ToString() == "if" && HasIdentifier(node[1], "JumpNum"))
                {
                    string field = HasMotion(node[2], "DASH") ? "slackNativeDash" : "slackNativeJump";
                    var condition = node[1]!.DeepClone();
                    if (field == "slackNativeJump")
                    {
                        var request = condition.DeepClone();
                        RemoveJumpCount(request);
                        condition = new JsonArray("call", "slack_character_jump_available", new JsonArray(condition, request));
                        var body = node[2]!;
                        node[2] = null;
                        node[2] = new JsonArray("group", new JsonArray(body, new JsonArray("call", "slack_character_jumped", new JsonArray())));
                    }
                    node[1] = new JsonArray("binary", "And", Gate(field), condition);
                }
                if (node.Count > 0 && node[0] is JsonArray)
                {
                    for (int i = 0; i < node.Count; i++)
                    {
                        if (node[i] is not JsonArray label || label[0]?.ToString() != "case" || label[1]?[1]?.ToString() != "DASH") continue;
                        int end = i + 1;
                        while (end < node.Count && node[end]?[0]?.ToString() is not ("case" or "default" or "break")) end++;
                        var body = new JsonArray();
                        for (int n = i + 1; n < end; n++)
                        {
                            var statement = node[i + 1]!;
                            node.RemoveAt(i + 1); body.Add(statement);
                        }
                        node.Insert(i + 1, new JsonArray("if", Gate("slackNativeDash"), new JsonArray("group", body), null));
                    }
                }
            }
            originals[key] = original;
            world.Code[key] = JsonSerializer.SerializeToElement(tree);
        }
        static JsonArray Gate(string field) => new("unary", "Not", new JsonArray("binary", "Deref", new JsonArray("id", "other"), new JsonArray("id", field)));
        static void RemoveJumpCount(JsonNode node)
        {
            if (node is not JsonArray array) return;
            if (array.Count == 4 && array[0]?.ToString() == "binary" && array[1]?.ToString() == "Deref" && array[3]?[1]?.ToString() == "JumpNum")
            { array.Clear(); array.Add("num"); array.Add(3); return; }
            foreach (var child in array) if (child is not null) RemoveJumpCount(child);
        }
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
        registry.Changed -= Refresh; registry.Committed -= CommitRefresh; registry.OwnerRetired -= ClearRuntimeOwner;
        if (world.SuppressCode == TrySuppressCode) world.SuppressCode = previousSuppressCode;
        world.CharacterJumped -= Jumped;
        if (world.ExtendCharacterJump == ExtendJump) world.ExtendCharacterJump = previousJumpExtension;
        foreach (var key in runtimeActions.Keys.ToArray()) RemoveRuntime(key.Item1, key.Item2);
        foreach (int id in characters.Keys.ToArray()) UnbindCharacter(id);
        foreach (var pair in entities) if (Find(pair.Key) is {} actor) Detach(pair.Value, actor);
        entities.Clear(); entityBindings.Clear(); entityInstances.Clear(); entityFactories.Clear(); stagedActions.Clear(); stagedEntities.Clear(); weaponRequests.Clear(); Services.Dispose();
        foreach (var pair in originals) world.Code[pair.Key] = pair.Value;
    }
}

internal static class NativeActorBridges
{
    private static readonly ConditionalWeakTable<NativeModServices, GmNativeBridge> bridges = new();
    internal static void Register(NativeModServices services, GmNativeBridge bridge) => bridges.AddOrUpdate(services, bridge);
    internal static GmNativeBridge? Find(NativeModServices services) => bridges.TryGetValue(services, out var bridge) ? bridge : null;
}

public static class NativeActorActions
{
    public static bool CanUseWeapon(ActorContext actor)
    {
        if (actor.Read("BINDED") != 0 || actor.Read("HALFBINDED") != 0 || actor.World.Globals.ReadOr("PlayerHP", 1).Number <= 0 || actor.Instance.Destroyed
            || actor.World.Globals.ReadOr("CurrentWeapon").Int != 0) return false;
        if (IsLocomotion(actor)) return true;
        double motion = actor.Read("ChangeMotion") != 0 ? actor.Read("ChangeMotionNum") : actor.Read("MotionNum");
        return actor.Instance.Scope.Values.Any(p => p.Value.Number == motion && (p.Key.Contains("SHOT", StringComparison.Ordinal) || p.Key.Contains("RELOAD", StringComparison.Ordinal)));
    }
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
