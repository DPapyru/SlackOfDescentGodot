using Godot;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeModsAudit : Node
{
    private int assertions;
    private double spawnX, spawnY;
    private void Expect(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); assertions++; }
    private async Task Frames(int count = 2) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    public override async void _Ready()
    {
        string root = ProjectSettings.GlobalizePath("res://");
        string temporary = Path.Combine(Path.GetTempPath(), "native-mod-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await Frames(1);
            var content = new ContentRegistry();
            using var manager = new NativeModManager(content);
            var world = new GmWorld(Path.Combine(root, "GameData"), Path.Combine(root, "GameData", "assets"), temporary);
            world.Platform = (name, args, context) => name == "sound_play" ? 0 : null;
            using var bridge = new GmNativeBridge(world, content);
            string lifecycle = CopyExample(root, temporary, "ResourceLifecycle");
            string movement = CopyExample(root, temporary, "Movement");
            var actor = new GmInstance { Id = 900001, Object = -1 };
            world.Instances.Add(actor);
            var collected = new List<WeakReference>();
            for (int reload = 0; reload < 20; reload++)
            {
                var result = manager.Reload(lifecycle);
                Expect(result.Success, "lifecycle compile/load " + reload + ": " + result.Diagnostics);
                if (result.PreviousContext is {} weak) collected.Add(weak);
                bridge.AttachEntity(actor.Id, "example.lifecycle-counter");
                bridge.Update(new(FrameContext.FixedDelta, reload / 60.0, reload, InputSnapshot.Empty));
                Expect(actor.Scope.ReadOr("nativeLifecycleTicks").Number == reload + 1, "new entity handler used exactly once");
                await Frames(2);
                Expect(GetTree().GetNodesInGroup("native_lifecycle").Count == 1, "one tracked node after reload");
                var node = GetTree().GetNodesInGroup("native_lifecycle").Single();
                Expect(node.GetMeta("frames", 0).AsInt32() > 0, "tracked signal callback runs");
                Expect(node.GetMeta("timer_ticks", 0).AsInt32() > 0, "tracked timer runs");
            }
            var unloaded = manager.Unload("example.lifecycle");
            Expect(unloaded.Success, "final lifecycle unload"); collected.Add(unloaded.PreviousContext!);
            await Frames(3);
            Expect(GetTree().GetNodesInGroup("native_lifecycle").Count == 0, "tracked nodes and timers removed");
            Expect(File.Exists(Path.Combine(lifecycle, "task-stopped.txt")), "tracked tasks cancelled and joined");
            Expect(collected.All(w => NativeModManager.VerifyUnloaded(w)), "all 20 collectible contexts released");
            int previous = (int)actor.Scope.ReadOr("nativeLifecycleTicks").Number;
            bridge.Update(new(FrameContext.FixedDelta, 1, 60, InputSnapshot.Empty));
            Expect(actor.Scope.ReadOr("nativeLifecycleTicks").Number == previous, "unload detached entity callbacks");
            var movementResult = manager.Reload(movement);
            Expect(movementResult.Success, "movement examples compile: " + movementResult.Diagnostics);
            Expect(content.Actions.Count == 3 && content.Characters.Count == 2, "movement registers three real actions and two characters");
            string ropebonds = CopyExample(root, temporary, "ropebonds");
            var ropebondsResult = manager.Reload(ropebonds);
            Expect(ropebondsResult.Success, "ropebonds examples compile: " + ropebondsResult.Diagnostics);
            Expect(content.MotionGraphs.TryGetValue("ropebonds.legs", out var legGraph) && legGraph.Clips.Count == 1 && legGraph.Clips[0].Tracks.Count == 6
                && legGraph.Clips[0].Tracks.All(track => track.Bone.StartsWith("FOOT", StringComparison.Ordinal)), "legs clip registers six foot tracks");
            Expect(content.MotionGraphs.TryGetValue("ropebonds.legqte", out var legQteGraph) && legQteGraph.Clips.Count == 1 && legQteGraph.Clips[0].Loop
                && legQteGraph.Clips[0].Tracks.Count == 6
                && legQteGraph.Clips[0].Tracks.All(track => track.Bone.StartsWith("FOOT", StringComparison.Ordinal)), "leg qte clip registers foot-only struggle tracks");
            VerifyLiveGameplay(root, temporary, content);
            var ropebondsUnload = manager.Unload("example.ropebonds");
            Expect(ropebondsUnload.Success && manager.LoadedMods.Count == 1, "ropebonds unload releases both layers");
            string movementSource = Path.Combine(movement, "MovementMod.cs");
            File.AppendAllText(movementSource, "\nthis is not valid C# !!!\n");
            var failed = manager.Reload(movement);
            Expect(!failed.Success && failed.Diagnostics.Contains("MovementMod.cs"), "compile error reports source diagnostics");
            Expect(content.Actions.Count == 3 && manager.LoadedMods.Count == 1, "failed compile preserves active registry");
            VerifyMovement(world, content, bridge, actor);
            string validMovement = File.ReadAllText(Path.Combine(root, "Examples", "Mods", "Movement", "MovementMod.cs"));
            bridge.BindCharacter(actor, content.Characters["example.triple-yuki"]);
            File.WriteAllText(movementSource, validMovement.Replace("() => new TripleJump()", "() => throw new InvalidOperationException(\"factory-failed\")"));
            var factoryFailed = manager.Reload(movement);
            Expect(!factoryFailed.Success && factoryFailed.Diagnostics.Contains("factory-failed"), "activation failure rolls back live action factories");
            Expect(content.Actions.Count == 3 && manager.LoadedMods.Count == 1, "activation rollback preserves previous active registry");
            File.WriteAllText(Path.Combine(lifecycle, "LifecycleMod.cs"), File.ReadAllText(Path.Combine(root, "Examples", "Mods", "ResourceLifecycle", "LifecycleMod.cs"))
                .Replace("context.Content.RegisterEntity", "throw new InvalidOperationException(\"load-failed\"); context.Content.RegisterEntity"));
            var loadFailed = manager.Reload(lifecycle);
            Expect(!loadFailed.Success && loadFailed.Diagnostics.Contains("load-failed"), "mod Load failure reports diagnostics");
            await Frames(3);
            Expect(GetTree().GetNodesInGroup("native_lifecycle").Count == 0, "failed Load cleans partial nodes signals timers tasks");
            Action unloadObserver = () => throw new InvalidOperationException("observer-failed");
            content.Changed += unloadObserver;
            var moveUnload = manager.Unload("example.movement");
            content.Changed -= unloadObserver;
            Expect(moveUnload.Success && moveUnload.Diagnostics.Contains("observer-failed"), "unload releases resources even when a registry observer throws");
            await Frames(3);
            Expect(NativeModManager.VerifyUnloaded(moveUnload.PreviousContext!), "movement context releases bound action references");
            GD.Print("NATIVE_MOD_AUDIT_OK assertions=", assertions, " reloads=20 collected=20 tracked_nodes=0 tracked_tasks=0");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("NATIVE_MOD_AUDIT_FAILED " + error); GetTree().Quit(1); }
        finally { Directory.Delete(temporary, true); }
    }
    private static string CopyExample(string root, string temporary, string name)
    {
        string source = Path.Combine(root, "Examples", "Mods", name), target = Path.Combine(temporary, name);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            NativeContentStore.WriteAtomic(Path.Combine(target, Path.GetRelativePath(source, file)), File.ReadAllBytes(file));
        return target;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void VerifyMovement(GmWorld world, ContentRegistry content, GmNativeBridge bridge, GmInstance actor)
    {
        actor.Scope.Write("MotionNum", 100); actor.Scope.Write("STAND_0", 100); actor.Scope.Write("JUMPUP", 101);
        actor.Scope.Write("JUMPUP2", 102); actor.Scope.Write("JUMPDOWN", 103); actor.Scope.Write("DASH", 104);
        actor.Scope.Write("x", 400); actor.Scope.Write("y", 100); actor.Scope.Write("Angle", 17);
        world.Globals.Write("Chara", "Yuki"); world.Globals.Write("PlayerHP", 100); world.Globals.Write("G", 1);
        world.Globals.Write("key", 32, [5]); world.Globals.Write("key", 37, [0]); world.Globals.Write("key", 39, [1]);
        world.Globals.Write("controllMode", -999);
        actor.Scope.Write("mask_index", world.Constants["sprMask"]);
        var floor = new GmInstance { Id = 900002, Object = -1, Active = false };
        floor.Scope.Write("mask_index", world.Constants["sprMask"]); floor.Scope.Write("solid", 1);
        floor.Scope.Write("image_xscale", 100); floor.Scope.Write("x", 400);
        floor.Scope.Write("y", actor.Scope.ReadOr("y").Number + world.Bounds(actor).Bottom - world.Bounds(actor).Top + 1);
        world.Instances.Add(floor);
        world.Scripts.Remove("MotionChange");
        world.Platform = (name, args, context) => name switch
        {
            "MotionChange" => SetMotion(world, actor, args[0]),
            "sound_play" => 0,
            _ => (GmValue?)null
        };
        FrameContext Frame(int tick, params int[] pressed) => new(FrameContext.FixedDelta, tick / 60.0, tick,
            new InputSnapshot(new HashSet<int>(pressed), new HashSet<int>(pressed), new HashSet<int>()));
        actor.Object = world.Constants["objPlayerDoll"].Int;
        var controller = new GmInstance { Id = 900003, Object = world.Constants["objController"].Int };
        controller.Scope.Write("CtrlTarget", actor.Object); world.Instances.Add(controller);
        bridge.BindCharacter(actor, content.Characters["example.triple-yuki"]);
        Expect(!controller.Scope.ReadOr("slackNativeJump").Truth && !controller.Scope.ReadOr("slackNativeDash").Truth, "additive jump action preserves original GM action slots");
        world.Globals.Write("Chara", "Nana"); bridge.BindCharacter(actor, content.Characters["example.spin-nana"]);
        world.Write(actor, "vspeed", 0); bridge.Update(Frame(20, 32));
        Expect(actor.Scope.ReadOr("Angle").Number > 17, "Nana first jump starts one full rotation");
        for (int i = 21; i < 51; i++) bridge.Update(Frame(i));
        Expect(Math.Abs(actor.Scope.ReadOr("Angle").Number - 17) < 1e-6, "Nana finishes exactly one rotation and restores angle");
        floor.Active = true; world.Write(actor, "vspeed", 0); bridge.Update(Frame(52)); floor.Active = false;
        bridge.Update(Frame(53, 32)); actor.Scope.Write("BINDED", 1); bridge.Update(Frame(54));
        Expect(actor.Scope.ReadOr("Angle").Number == 17, "binding interrupts Nana spin cleanly"); actor.Scope.Write("BINDED", 0);
        floor.Active = true; world.Write(actor, "vspeed", 0); bridge.Update(Frame(55)); floor.Active = false;
        bridge.Update(Frame(56, 32)); world.Write(actor, "vspeed", 0); bridge.Update(Frame(57));
        Expect(actor.Scope.ReadOr("Angle").Number == 17, "ceiling stop interrupts Nana spin cleanly");
        actor.Scope.Write("MotionNum", 100); actor.Scope.Write("x", 400);
        bridge.Update(Frame(60, 39)); bridge.Update(Frame(61)); bridge.Update(Frame(66, 39));
        for (int i = 67; i <= 77; i++) bridge.Update(Frame(i));
        Expect(Math.Abs(actor.Scope.ReadOr("x").Number - 508) < 1e-5, "Nana dash covers 1.5 times original 72 pixels");
        actor.Scope.Write("x", 400); bridge.Update(Frame(100, 37)); bridge.Update(Frame(115, 37));
        Expect(actor.Scope.ReadOr("x").Number == 400, "dash rejects taps outside original seven-tick window");
        bridge.UnbindCharacter(actor.Id);
        Expect(!controller.Scope.ReadOr("slackNativeJump").Truth && !controller.Scope.ReadOr("slackNativeDash").Truth, "unbind restores original GM action slots");
        Expect(actor.Scope.ReadOr("Angle").Number == 17, "unbind restores actor rotation");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void VerifyLiveGameplay(string root, string temporary, ContentRegistry content)
    {
        var live = new GmWorld(Path.Combine(root, "GameData"), Path.Combine(root, "GameData", "assets"), Path.Combine(temporary, "live"));
        var input = new GmInput();
        live.Platform = (name, arguments, context) => input.Call(name, arguments, live)
            ?? (name.StartsWith("place_", StringComparison.Ordinal) || name.StartsWith("collision_", StringComparison.Ordinal)
                || name.StartsWith("move_", StringComparison.Ordinal) || name is "instance_place" or "action_if_empty" or "action_if_object" or "distance_to_object"
                ? null : (GmValue?)0);
        live.Wait = (name, arguments, context) => new ValueTask<GmValue>(1);
        live.LoadRoom(live.Manifest.GetProperty("roomOrder")[0].GetInt32());
        for (int i = 0; i < 180; i++) live.Step();
        var title = live.Instances.First(i => !i.Destroyed && live.IsObject(i.Object, live.Constants["objTitle"].Int));
        live.Globals.Write("StartArea", 0);
        live.Evaluator.ResetBudget(); live.Call("DataItemReset", [], new(title)); live.Call("DataItemLoad", [], new(title));
        live.LoadRoom(live.Constants["Playground_Loading"].Int);
        for (int i = 0; i < 30; i++) live.Step();
        var player = live.Instances.First(i => !i.Destroyed && live.IsObject(i.Object, live.Constants["objPlayerDoll"].Int));
        spawnX = player.Scope.ReadOr("x").Number;
        spawnY = player.Scope.ReadOr("y").Number;
        foreach (var instance in live.Instances)
            if (instance != player && !instance.Scope.ReadOr("solid").Truth
                && !live.IsObject(instance.Object, live.Constants["objController"].Int)
                && !live.IsObject(instance.Object, live.Constants["objPlayerParts"].Int)) instance.Active = false;
        using var bridge = new GmNativeBridge(live, content);
        live.Evaluator.ResetBudget(); live.Call("YukiLoad", [], new(player));
        bridge.BindCharacter(player, content.Characters["example.triple-yuki"]);
        int jumpKey = NativeActorActions.Key(new(live, player), 5);
        var clock = new GmFixedClock();
        long tick = 0;
        double timeOffset = 0;
        void Tick(bool jump = false, int extraKey = 0)
        {
            clock.BeginTick();
            input.Key(live, jumpKey, jump);
            var held = new HashSet<int>(); if (jump) held.Add(jumpKey); if (extraKey != 0) held.Add(extraKey);
            bridge.Update(new(FrameContext.FixedDelta, tick / 60.0 + timeOffset, tick,
                new InputSnapshot(held, held, new HashSet<int>())));
            if (clock.LegacyDue(live.Engine.Read("room_speed").Number)) { live.Step(); input.EndStep(); }
            tick++;
        }
        void Advance(double seconds)
        {
            timeOffset += seconds;
            Tick();
        }
        for (int jump = 0; jump < 4; jump++)
        {
            long before = live.Frame;
            do { Tick(true); } while (live.Frame == before);
            before = live.Frame;
            do { Tick(); } while (live.Frame == before);
            Expect(player.Scope.ReadOr("nativeJumpCount").Int == Math.Min(jump + 1, 3), "live Yuki extends original jump only once " + jump);
            Expect(player.Scope.ReadOr("vspeed").Number >= -24.001, "live Yuki preserves original aerial jump impulse " + jump);
        }
        bridge.UnbindCharacter(player.Id);
        var controller = NativeActorActions.Controller(new(live, player))!;
        Expect(!controller.Scope.ReadOr("slackNativeJump").Truth, "live original GM jump resumes after replacement removed");
        live.Evaluator.ResetBudget(); live.Call("NanaLoad", [], new(player));
        live.Write(player, "x", 96); live.Write(player, "y", 64); live.Write(player, "hspeed", 0); live.Write(player, "vspeed", 0);
        NativeActorActions.Motion(new(live, player), "STAND_0");
        for (int i = 0; i < 4; i++) live.Step();
        bool corridor = false;
        for (int y = 64; y < 1200 && !corridor; y += 64)
            for (int x = 96; x < 1500 && !corridor; x += 128)
            {
                corridor = Enumerable.Range(0, 109).All(dx => live.Call("place_free", [x + dx, y], new(player)).Truth && live.Call("place_free", [x + dx, y + 64], new(player)).Truth);
                if (corridor) { live.Write(player, "x", x); live.Write(player, "y", y); }
            }
        Expect(corridor, "live room contains unobstructed dash corridor");
        bridge.BindCharacter(player, content.Characters["example.spin-nana"]);
        int rightKey = NativeActorActions.Key(new(live, player), 1);
        Tick(extraKey: rightKey); Tick(); Tick(extraKey: rightKey);
        double beforeDash = player.Scope.ReadOr("x").Number;
        for (int i = 0; i < 15; i++) Tick();
        Expect(player.Scope.ReadOr("x").Number > beforeDash + 80, "live Nana dash survives deferred original MotionChange transition");
        Expect(player.Scope.ReadOr("nativeDashDistance").Number == 108, "live Nana dash writes exactly108 swept native pixels");
        bridge.UnbindCharacter(player.Id);
        VerifyLegQteStagger(live, player, content, bridge, () => Tick(), seconds => Advance(seconds));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void VerifyLegQteStagger(GmWorld live, GmInstance player, ContentRegistry content, GmNativeBridge bridge, Action tick, Action<double> advance)
    {
        var legs = content.MotionGraphs["ropebonds.legs"].Clips[0].Sample(0);
        var bound = content.MotionGraphs["ropebonds.bound"].Clips[0].Sample(0);
        string[] feet = ["FOOT1_ABOVE", "FOOT2_ABOVE", "FOOT3_ABOVE", "FOOT1_BELOW", "FOOT2_BELOW", "FOOT3_BELOW"];
        string[] hands = ["HAND1_ABOVE", "HAND2_ABOVE", "HAND3_ABOVE", "HAND1_BELOW", "HAND2_BELOW", "HAND3_BELOW"];
        tick();
        double[] feetEntry = BoneState(live, player, feet), handsEntry = BoneState(live, player, hands);
        live.Call("ropebonds_feet", [], new(player));
        live.Events!.PrepareFrame();
        Expect(Matches(live, player, feet, legs), "leg layer locks the ankle bone set");
        Expect(Same(BoneState(live, player, hands), handsEntry), "leg layer leaves the hands untouched");
        live.Call("ropebonds_hands", [], new(player));
        live.Events!.PrepareFrame();
        Expect(Matches(live, player, hands, bound), "hand layer locks the hand bone set");
        Expect(Matches(live, player, feet, legs), "hand layer leaves the bound legs untouched");
        live.Call("ropebonds_feet", [], new(player));
        Expect(Same(BoneState(live, player, feet), feetEntry), "releasing the leg layer restores its entry pose");
        Expect(Matches(live, player, hands, bound), "releasing a layer keeps the other layer bound");
        live.Call("ropebonds_hands", [], new(player));
        Expect(Same(BoneState(live, player, hands), handsEntry), "releasing the hand layer restores its entry pose");
        Expect(player.Scope.Values.ContainsKey("DOWN_0"), "stagger uses the native down motion");
        Expect(player.Scope.Values.ContainsKey("FLYING_SPIN"), "roll uses the native spin motion");
        var legQte = content.MotionGraphs["ropebonds.legqte"].Clips[0].Sample(0);
        live.Call("ropebonds_hands", [], new(player));
        live.Call("ropebonds_legqte", [], new(player));
        live.Events!.PrepareFrame();
        Expect(Matches(live, player, feet, legQte), "leg qte locks the foot bone set");
        Expect(Matches(live, player, hands, bound), "leg qte keeps the hand layer locked");
        live.Call("ropebonds_legqte", [], new(player));
        Expect(Same(BoneState(live, player, feet), feetEntry), "leg qte releases its own layer only");
        live.Call("ropebonds_hands", [], new(player));
        Expect(Same(BoneState(live, player, hands), handsEntry), "leg qte leaves the hand layer bound until released");
        double[] standing = BoneState(live, player, feet);
        Expect((live.Call("ropebonds_bound", [], new(player)).Int & 2) == 0, "leg qte starts after the stagger and uses the leg qte clip");
        live.Call("ropebonds_stagger", [], new(player));
        for (int index = 0; index < 100; index++) tick();
        live.Events!.PrepareFrame();
        Expect((live.Call("ropebonds_bound", [], new(player)).Int & 6) == 6, "leg qte starts after the stagger and uses the leg qte clip");
        live.Call("ropebonds_legqte", [], new(player));
        Expect(Same(BoneState(live, player, feet), standing), "leg qte failure restores the standing entry pose");
        VerifyRopeCreature(live, bridge, player, hands, feet, bound, legs, tick, advance);
        VerifySecondBind(live, bridge, player, hands, feet, bound, legs, tick);
        VerifyQteSuccess(live, bridge, player, tick);
    }

    private void VerifyRopeCreature(GmWorld live, GmNativeBridge bridge, GmInstance player, string[] hands, string[] feet,
        IReadOnlyDictionary<string, MotionPose> bound, IReadOnlyDictionary<string, MotionPose> legs, Action tick, Action<double> advance)
    {
        var creature = bridge.Services.Entities.Spawn("ropebonds.creature", player.Scope.ReadOr("x").Number + 8, player.Scope.ReadOr("y").Number);
        Expect(live.IsObject(creature.Instance.Object, live.Constants["objEnemyDoll"].Int), "rope creature joins the objEnemyDoll family");
        Expect(creature.Read("HP") == 10000 && creature.Read("INV") == 0 && creature.Read("WEAK") == 0 && creature.Read("MotionNum") == 0
            && creature.Read("DEAD_MOTION") == 0 && creature.Read("DAMAGE_ACTIVE") == 1 && creature.Read("BindTarget") == 0, "rope creature initializes native damage variables");
        foreach (string script in new[] { "Quake", "Spark" })
            if (live.Scripts.TryGetValue(script, out var scriptKey)) live.Code[scriptKey] = JsonSerializer.SerializeToElement(new object[] { "group", Array.Empty<object>() });
        live.Globals.Write("controllMode", live.Constants["CTRL_KEY"].Int);
        var gun = live.Create(live.Constants["objNana_Gun"].Int, creature.X, creature.Y, run: false);
        gun.Active = false;
        gun.Scope.Write("DrawTarget", player.Id);
        GmInstance Shot()
        {
            var bullet = live.Create(live.Constants["objBullet"].Int, creature.X, creature.Y, run: false);
            bullet.Scope.Write("creator", gun.Id);
            bullet.Scope.Write("preX", creature.X - 40);
            bullet.Scope.Write("preY", creature.Y);
            bullet.Scope.Write("fireAngle", 0.0);
            return bullet;
        }
        live.Write(player, "x", spawnX);
        live.Write(player, "y", spawnY);
        live.Write(player, "vspeed", 0);
        for (int settle = 0; settle < 600 && !NativeActorActions.Grounded(new(live, player)); settle++) tick();
        Expect(NativeActorActions.Grounded(new(live, player)), "rope creature audit player stands on the ground");
        live.Event(Shot(), 3, 1);
        Expect(creature.Read("HP") == 9900, "native bullet subtracts exactly 100 damage on the ground");
        creature.Write("INV", 1);
        live.Event(Shot(), 3, 1);
        Expect(creature.Read("HP") == 9900, "invulnerable rope creature ignores damage like the original");
        creature.Write("INV", 0);
        live.Write(player, "vspeed", -10);
        live.Event(Shot(), 3, 1);
        Expect(creature.Read("HP") == 8900, "airborne shot multiplies rope creature damage by ten");
        live.Write(player, "vspeed", 0);
        int drops = DropCount(live);
        creature.X = player.Scope.ReadOr("x").Number;
        creature.Y = player.Scope.ReadOr("y").Number;
        for (int attach = 0; attach < 30 && player.Scope.ReadOr("HALFBINDED").Number == 0; attach++) tick();
        Expect(player.Scope.ReadOr("HALFBINDED").Number == 1, "rope creature binds the standing player");
        Expect(live.Globals.ReadOr("BindMax").Number == 25, "an unbound catch starts the hand qte");
        Expect((live.Call("ropebonds_bound", [], new(player)).Int & 2) == 0, "the hand qte leaves the leg layer free");
        for (int frame = 0; frame < 180; frame++) tick();
        Expect(Matches(live, player, hands, bound), "failed qte binds the hand layer");
        live.Call("ropebonds_feet", [], new(player));
        live.Events!.PrepareFrame();
        Expect(Matches(live, player, feet, legs), "hand and leg layers stay stacked");
        creature.Write("HP", 0);
        tick();
        Expect(player.Scope.ReadOr("HALFBINDED").Number == 1, "killing the hand binder leaves the leg layer half-bound");
        Expect(Matches(live, player, feet, legs), "killing the hand binder keeps the leg layer locked");
        Expect((live.Call("ropebonds_bound", [], new(player)).Int & 1) == 0, "killing the hand binder releases the hand layer");
        Expect(creature.Read("MotionNum") == 0, "killing a rope creature never enters the vanilla death motion");
        Expect(DropCount(live) == drops, "killing a rope creature spawns no vanilla drops");
        for (int fade = 0; fade < 30; fade++) tick();
        Expect(bridge.Services.Entities.Query("ropebonds.creature").All(actor => actor.Id != creature.Id), "dead rope creature is destroyed after the fade");
        Expect(!bridge.Services.Presentation.Sprites.Any(sprite => sprite.Owner == creature.Id), "dead rope creature clears its rope visuals");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void VerifySecondBind(GmWorld live, GmNativeBridge bridge, GmInstance player, string[] hands, string[] feet,
        IReadOnlyDictionary<string, MotionPose> bound, IReadOnlyDictionary<string, MotionPose> legs, Action tick)
    {
        if ((live.Call("ropebonds_bound", [], new(player)).Int & 2) != 0) live.Call("ropebonds_feet", [], new(player));
        player.Scope.Write("HALFBINDED", 0);
        live.Events!.PrepareFrame();
        for (int frame = 0; frame < 10; frame++) tick();
        var first = bridge.Services.Entities.Spawn("ropebonds.creature", player.Scope.ReadOr("x").Number, player.Scope.ReadOr("y").Number);
        for (int frame = 0; frame < 60 && player.Scope.ReadOr("HALFBINDED").Number == 0; frame++) tick();
        Expect(player.Scope.ReadOr("HALFBINDED").Number == 1, "the second encounter starts with the hand qte");
        Expect(live.Globals.ReadOr("BindMax").Number == 25, "the second encounter caps at the hand qte counter");
        Expect((live.Call("ropebonds_bound", [], new(player)).Int & 2) == 0, "the second encounter leaves the leg layer free");
        live.Events!.PrepareFrame();
        double wrist = BoneState(live, player, ["HAND3_ABOVE"])[0];
        var wristHead = bridge.Services.Presentation.Sprites.FirstOrDefault(sprite => sprite.Owner == first.Id && sprite.Id == "rope.head");
        Expect(wristHead is not null && Math.Abs(wristHead.X - wrist) < .01, "the hand binder keeps the rope on the wrist");
        for (int frame = 0; frame < 180; frame++) tick();
        Expect((live.Call("ropebonds_bound", [], new(player)).Int & 1) == 1, "the failed hand qte binds the hand layer only");
        var second = bridge.Services.Entities.Spawn("ropebonds.creature", player.Scope.ReadOr("x").Number, player.Scope.ReadOr("y").Number);
        int mask = 0;
        for (int frame = 0; frame < 300; frame++)
        {
            tick();
            mask = live.Call("ropebonds_bound", [], new(player)).Int;
            if ((mask & 4) != 0) break;
        }
        Expect((mask & 6) == 6, "the bound hand falls into the leg qte");
        Expect(live.Globals.ReadOr("BindMax").Number == 50, "the leg qte caps at twice the hand counter");
        var controller = NativeActorActions.Controller(new(live, player))!;
        controller.Scope.Write("BindCounter", 7);
        for (int frame = 0; frame < 3; frame++) tick();
        Expect(controller.Scope.ReadOr("BindCounter").Number >= 7, "the leg qte keeps the progress it already has");
        for (int frame = 0; frame < 180; frame++) tick();
        Expect((live.Call("ropebonds_bound", [], new(player)).Int & 4) == 4, "the leg qte still runs after half its hand window");
        for (int frame = 0; frame < 150; frame++) tick();
        mask = live.Call("ropebonds_bound", [], new(player)).Int;
        Expect(mask == 3, "a failed leg qte stacks both layers");
        Expect(player.Scope.Values.ContainsKey("DOWN_0") && player.Scope.ReadOr("DOWN_0").Number != 0
            && player.Scope.ReadOr("MotionNum").Number == player.Scope.ReadOr("DOWN_0").Number, "the failed leg qte keeps the player down");
        live.Events!.PrepareFrame();
        Expect(Matches(live, player, feet, legs), "the failed leg qte keeps the legs in the bound clip");
        Expect(Matches(live, player, hands, bound), "the failed leg qte keeps the hands in the bound clip");
        double ankle = BoneState(live, player, ["FOOT3_ABOVE"])[0];
        var head = bridge.Services.Presentation.Sprites.FirstOrDefault(sprite => sprite.Owner == second.Id && sprite.Id == "rope.head");
        Expect(head is not null && Math.Abs(head.X - ankle) < .01, "the leg binder keeps the rope on the ankle after the failed qte");
        bridge.Services.Entities.Destroy(first.Id);
        bridge.Services.Entities.Destroy(second.Id);
        for (int frame = 0; frame < 10; frame++) tick();
        Expect(live.Call("ropebonds_bound", [], new(player)).Int == 0, "destroying both binders releases both layers");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void VerifyQteSuccess(GmWorld live, GmNativeBridge bridge, GmInstance player, Action tick)
    {
        var win = bridge.Services.Entities.Spawn("ropebonds.creature", player.Scope.ReadOr("x").Number, player.Scope.ReadOr("y").Number);
        for (int frame = 0; frame < 60 && player.Scope.ReadOr("HALFBINDED").Number == 0; frame++) tick();
        Expect(player.Scope.ReadOr("HALFBINDED").Number == 1, "the success check re-enters the hand qte");
        NativeActorActions.Controller(new(live, player))!.Scope.Write("BindCounter", 25);
        tick();
        Expect(live.Call("ropebonds_bound", [], new(player)).Int == 0, "a filled hand qte frees the player");
        bridge.Services.Entities.Destroy(win.Id);
        for (int frame = 0; frame < 10; frame++) tick();
        live.Call("ropebonds_hands", [], new(player));
        live.Events!.PrepareFrame();
        var roll = bridge.Services.Entities.Spawn("ropebonds.creature", player.Scope.ReadOr("x").Number, player.Scope.ReadOr("y").Number);
        int mask = 0;
        for (int frame = 0; frame < 300; frame++)
        {
            tick();
            mask = live.Call("ropebonds_bound", [], new(player)).Int;
            if ((mask & 4) != 0) break;
        }
        Expect((mask & 4) == 4, "the leg success check reaches the leg qte");
        NativeActorActions.Controller(new(live, player))!.Scope.Write("BindCounter", 50);
        tick();
        mask = live.Call("ropebonds_bound", [], new(player)).Int;
        Expect(mask == 1, "a filled leg qte leaves only the hand layer mask=" + mask + " counter=" + (NativeActorActions.Controller(new(live, player))?.Scope.ReadOr("BindCounter").ToString() ?? "none") + " max=" + live.Globals.ReadOr("BindMax").Number);
        Expect(player.Scope.ReadOr("MotionNum").Number == player.Scope.ReadOr("FLYING_SPIN").Number, "a filled leg qte rolls the player out");
        bridge.Services.Entities.Destroy(roll.Id);
        live.Call("ropebonds_hands", [], new(player));
        for (int frame = 0; frame < 10; frame++) tick();
    }

    private static double[] BoneState(GmWorld world, GmInstance actor, string[] bones)
    {
        double[] state = new double[bones.Length * 3];
        for (int index = 0; index < bones.Length; index++)
        {
            if (NativeCharacterBones.Resolve(world, actor, bones[index]) is not { } part) continue;
            state[index * 3] = part.Scope.ReadOr("x").Number;
            state[index * 3 + 1] = part.Scope.ReadOr("y").Number;
            state[index * 3 + 2] = part.Scope.ReadOr("Angle").Number;
        }
        return state;
    }

    private static bool Same(double[] left, double[] right) =>
        left.Length == right.Length && !left.Where((value, index) => Math.Abs(value - right[index]) > 1e-9).Any();

    private static bool Near(double[] left, double[] right, double tolerance) =>
        left.Length == right.Length && !left.Where((value, index) => Math.Abs(value - right[index]) > tolerance).Any();

    private static int DropCount(GmWorld world) =>
        new[] { "objHPRecover", "objItem_SMG", "objItem_AR", "objItem_HG" }
            .Where(name => world.Constants.ContainsKey(name))
            .Sum(name => world.Instances.Count(instance => !instance.Destroyed && world.IsObject(instance.Object, world.Constants[name].Int)));

    private static bool Matches(GmWorld world, GmInstance actor, string[] bones, IReadOnlyDictionary<string, MotionPose> sample)
    {
        foreach (string bone in bones)
        {
            if (NativeCharacterBones.Resolve(world, actor, bone) is not { } part) return false;
            if (!sample.TryGetValue(bone, out MotionPose pose)) return false;
            if (Math.Abs(part.Scope.ReadOr("Angle").Number - pose.Angle) > 1e-9) return false;
            if (part.Scope.ReadOr("PartPicture").Int != pose.Picture) return false;
        }
        return true;
    }
    private static GmValue SetMotion(GmWorld world, GmInstance actor, GmValue value) { world.Write(actor, "MotionNum", value); return 0; }
}
