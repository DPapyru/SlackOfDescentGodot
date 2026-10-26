using SlackOfDescent;

public static class GmActorSdkTests
{
    private sealed class Probe(string id, CharacterActionSlot slot, string counter) : ICharacterAction
    {
        public string Id => id;
        public CharacterActionSlot Slot => slot;
        public void Update(ActorContext actor, FrameContext frame) => actor.Write(counter, actor.Read(counter) + 1);
        public void Detach(ActorContext actor) => actor.Write(counter + "Detached", actor.Read(counter + "Detached") + 1);
    }

    public static int Run(GmWorld world)
    {
        int assertions = 0;
        void Expect(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL " + name); assertions++; }
        var registry = new ContentRegistry();
        using var bridge = new GmNativeBridge(world, registry);
        var actors = new NativeActorService(bridge.Services);
        var frame = new FrameContext(FrameContext.FixedDelta, 1, 1, InputSnapshot.Empty);
        void Step() => bridge.Update(frame);
        void Register(string id) { registry.Actions[id] = () => new Probe(id, CharacterActionSlot.Attack, id); registry.Refresh(); }

        Register("actor.definition");
        var instance = world.CreateNativeEntity(4000, 4000);
        var actor = new ActorContext(world, instance);
        bridge.BindCharacter(instance, new CharacterDefinition { Id = "actor.probe", Actions = new() { [CharacterActionSlot.Attack] = "actor.definition" } });
        Step();
        Expect(actor.Read("actor.definition") == 1, "definition action updates before any runtime binding");

        var runtime = new Probe("actor.runtime", CharacterActionSlot.Attack, "actor.runtime");
        actors.Bind(actor, CharacterActionSlot.Attack, runtime);
        Step();
        Expect(actor.Read("actor.runtime") == 1 && actor.Read("actor.definition") == 1, "runtime binding shadows the definition action");
        Expect(ReferenceEquals(actors.Bound(actor, CharacterActionSlot.Attack), runtime), "Bound prefers the runtime action");

        registry.Refresh();
        Step();
        Expect(actor.Read("actor.runtime") == 2 && actor.Read("actor.definition") == 1, "registry refresh keeps the runtime binding");
        Expect(ReferenceEquals(actors.Bound(actor, CharacterActionSlot.Attack), runtime), "runtime binding survives registry refresh");

        Register("actor.definition");
        Step();
        Expect(actor.Read("actor.runtime") == 3 && actor.Read("actor.definition") == 1, "definition factory replacement does not resurrect the definition action");
        Expect(ReferenceEquals(actors.Bound(actor, CharacterActionSlot.Attack), runtime), "runtime binding still wins after factory replacement");

        actors.Unbind(actor, CharacterActionSlot.Attack);
        Step();
        Expect(actor.Read("actor.runtime") == 3 && actor.Read("actor.definition") == 2, "unbind restores the original definition action");
        Expect(actors.Bound(actor, CharacterActionSlot.Attack)?.Id == "actor.definition", "Bound falls back to the definition action after unbind");
        Expect(actor.Read("actor.runtimeDetached") == 1, "unbind detaches the runtime action once");

        var standaloneInstance = world.CreateNativeEntity(4100, 4100);
        var standalone = new ActorContext(world, standaloneInstance);
        var solo = new Probe("actor.solo", CharacterActionSlot.Dash, "actor.solo");
        actors.Bind(standalone, CharacterActionSlot.Dash, solo);
        Step();
        Expect(standalone.Read("actor.solo") == 1, "runtime binding works without a character definition");
        Expect(ReferenceEquals(actors.Bound(standalone, CharacterActionSlot.Dash), solo), "runtime binding is visible without a character definition");
        actors.Unbind(standalone, CharacterActionSlot.Dash);
        Step();
        Expect(standalone.Read("actor.solo") == 1 && standalone.Read("actor.soloDetached") == 1, "unbind without a definition only detaches");
        Expect(actors.Bound(standalone, CharacterActionSlot.Dash) is null, "unbound slot without a definition has no action");

        var controller = world.Create(world.Constants["objController"].Int, 0, 0, run: false);
        controller.Scope.Write("CtrlTarget", standalone.Id);
        actors.Bind(standalone, CharacterActionSlot.Dash, new Probe("actor.dash", CharacterActionSlot.Dash, "actor.dash"));
        Expect(controller.Scope.ReadOr("slackNativeDash").Truth, "runtime dash raises slackNativeDash");
        actors.Unbind(standalone, CharacterActionSlot.Dash);
        Expect(!controller.Scope.ReadOr("slackNativeDash").Truth, "unbind clears slackNativeDash");
        world.Instances.Remove(controller);

        var scoped = new Probe("actor.scoped", CharacterActionSlot.Attack, "actor.scoped");
        var scope = registry.ScopeFor("mod-owner", "actor.scoped", standalone.Id);
        using (bridge.Services.EnterScope(scope)) actors.Bind(standalone, CharacterActionSlot.Attack, scoped);
        Step();
        Expect(standalone.Read("actor.scoped") == 1, "scoped runtime binding updates while installed");
        actors.ClearScope(scope);
        Step();
        Expect(standalone.Read("actor.scoped") == 1 && standalone.Read("actor.scopedDetached") == 1, "ClearScope removes the runtime binding and detaches it");
        Expect(actors.Bound(standalone, CharacterActionSlot.Attack) is null, "ClearScope leaves no bound action");

        var owned = new Probe("actor.owned", CharacterActionSlot.Attack, "actor.owned");
        registry.Actions["actor.owned"] = () => owned; registry.AssignOwner("actor.owner");
        using (bridge.Services.EnterScope(registry.ScopeFor("action", "actor.owned", standalone.Id))) actors.Bind(standalone, CharacterActionSlot.Attack, owned);
        Step();
        Expect(standalone.Read("actor.owned") == 1, "owner scoped binding is active");
        registry.RetireOwner(registry.OwnerScope);
        Step();
        Expect(standalone.Read("actor.owned") == 1 && standalone.Read("actor.ownedDetached") == 1, "owner retirement removes the runtime binding");
        Expect(actors.Bound(standalone, CharacterActionSlot.Attack) is null, "owner retirement leaves no bound action");

        Expect(actors.MotionIs(actor, "STAND_0") == NativeActorActions.MotionMatches(actor, "STAND_0"), "MotionIs forwards to NativeActorActions.MotionMatches");
        Expect(actors.MotionIs(actor, "MISSING_MOTION") == NativeActorActions.MotionMatches(actor, "MISSING_MOTION"), "MotionIs reports unknown motions consistently");
        Expect(actors.PlaceFree(standalone, standalone.X, standalone.Y) == standalone.Call("place_free", standalone.X, standalone.Y).Truth, "PlaceFree forwards to place_free");

        bool rejected = false;
        try { actors.Bind(standalone, CharacterActionSlot.Jump, new Probe("actor.wrong", CharacterActionSlot.Attack, "actor.wrong")); } catch (InvalidDataException) { rejected = true; }
        Expect(rejected && actors.Bound(standalone, CharacterActionSlot.Jump) is null, "slot mismatch is rejected without leaving a binding");

        bridge.UnbindCharacter(instance.Id);
        bridge.Update(frame);
        Expect(actors.Bound(actor, CharacterActionSlot.Attack) is null, "unbinding the character drops its definition action");
        world.Instances.Remove(instance); world.Instances.Remove(standaloneInstance);
        return assertions;
    }
}
