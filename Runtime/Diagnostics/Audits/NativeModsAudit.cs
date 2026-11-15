using Godot;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SlackOfDescent;

public partial class NativeModsAudit : Node
{
    private int assertions;
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
            VerifyLiveGameplay(root, temporary, content);
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
            System.Environment.SetEnvironmentVariable("ROPEBONDS_SELFCHECK", "1");
            var ropebonds = manager.Reload(CopyExample(root, temporary, "ropebonds"));
            System.Environment.SetEnvironmentVariable("ROPEBONDS_SELFCHECK", null);
            Expect(ropebonds.Success, "ropebonds example compiles and self-checks: " + ropebonds.Diagnostics);
            VerifyRopeBondsBinding(root, temporary, manager, content);
            VerifyRopeBondsMap(root, world, content);
            GD.Print("NATIVE_MOD_AUDIT_OK assertions=", assertions, " reloads=20 collected=20 tracked_nodes=0 tracked_tasks=0");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("NATIVE_MOD_AUDIT_FAILED " + error); GetTree().Quit(1); }
        finally { Directory.Delete(temporary, true); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void VerifyRopeBondsBinding(string root, string temporary, NativeModManager manager, ContentRegistry content)
    {
        var (live, player) = BindingPlayground(root, temporary);
        using var bridge = new GmNativeBridge(live, content);
        live.Evaluator.ResetBudget(); live.Call("NanaLoad", [], new(player));
        var actor = new ActorContext(live, player);
        var mod = FindLoadedMod(manager, "example.ropebonds");
        var constraints = mod.GetType().GetProperty("Constraints")!.GetValue(mod)!;
        var partType = mod.GetType().Assembly.GetType("RopeBonds.BindPart")!;
        var bind = constraints.GetType().GetMethod("Bind", new[] { typeof(ActorContext), partType.MakeArrayType() })!;
        var release = constraints.GetType().GetMethod("Release", new[] { typeof(ActorContext) })!;
        var isBound = constraints.GetType().GetMethod("IsBound")!;
        var parts = Array.CreateInstance(partType, 1);
        parts.SetValue(Enum.Parse(partType, "HandAbove"), 0);
        Expect((bool)bind.Invoke(constraints, new object[] { actor, parts })!, "ropebonds constrains a real Nana hand");
        Expect((bool)isBound.Invoke(constraints, new object[] { player.Id })!, "ropebonds records the bound actor");
        player.Active = false;
        var hands = new[] { "HAND1_ABOVE", "HAND2_ABOVE", "HAND3_ABOVE" }.Select(name => actor.Part(name)!.Instance).ToArray();
        var angles = hands.Select(hand => hand.Scope.ReadOr("Angle").Number).ToArray();
        VerifyFrozenHands(live, player, hands, angles);
        VerifyParentTransform(live, player, hands);
        release.Invoke(constraints, new object[] { actor });
        Expect(!(bool)isBound.Invoke(constraints, new object[] { player.Id })!, "release clears the constraint");
        Expect(hands.Select((hand, index) => Math.Abs(hand.Scope.ReadOr("Angle").Number - angles[index]) < 1e-9).All(value => value), "release restores the entry pose");
        foreach (var hand in hands) hand.Scope.Write("Angle", 456);
        live.Step();
        Expect(hands.All(hand => Math.Abs(hand.Scope.ReadOr("Angle").Number - 456) < 1e-9), "release stops restoring the hands");
        GD.Print("ROPEBONDS_PARENTS ", string.Join(" ", NativeCharacterBones.Order.Select(name => name + "=" + (actor.Part(name)?.Instance.Scope.ReadOr("parent", player.Id).Int ?? -4))));
    }
    private static (GmWorld Live, GmInstance Player) BindingPlayground(string root, string temporary)
    {
        var live = new GmWorld(Path.Combine(root, "GameData"), Path.Combine(root, "GameData", "assets"), Path.Combine(temporary, "bind"));
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
        foreach (var instance in live.Instances)
            if (instance != player && !instance.Scope.ReadOr("solid").Truth
                && !live.IsObject(instance.Object, live.Constants["objController"].Int)
                && !live.IsObject(instance.Object, live.Constants["objPlayerParts"].Int)) instance.Active = false;
        return (live, player);
    }
    private void VerifyFrozenHands(GmWorld live, GmInstance player, GmInstance[] hands, double[] angles)
    {
        foreach (var hand in hands)
        {
            hand.Scope.Write("Angle", 123);
            hand.Scope.Write("ScaleX", 3);
            hand.Scope.Write("x", 9999);
            hand.Scope.Write("y", -9999);
        }
        live.Step();
        Expect(hands.Select((hand, index) => Math.Abs(hand.Scope.ReadOr("Angle").Number - angles[index]) < 1e-9).All(value => value), "frozen hands keep their angle against a clip write");
        Expect(hands.All(hand => Math.Abs(hand.Scope.ReadOr("x").Number) < 5000 && Math.Abs(hand.Scope.ReadOr("y").Number) < 5000), "frozen hands recompute a finite world position");
        Expect(hands.All(hand => Math.Abs(hand.Scope.ReadOr("image_angle").Number - (hand.Scope.ReadOr("Angle").Number + ParentOf(live, player, hand).Scope.ReadOr("image_angle").Number)) < 1e-9), "frozen image_angle follows the live parent");
    }
    private void VerifyParentTransform(GmWorld live, GmInstance player, GmInstance[] hands)
    {
        var rootPart = ParentOf(live, player, hands[0]);
        var before = hands.Select(hand => hand.Scope.ReadOr("x").Number).ToArray();
        rootPart.Scope.Write("x", rootPart.Scope.ReadOr("x").Number + 20);
        live.Step();
        Expect(hands.Select((hand, index) => Math.Abs(hand.Scope.ReadOr("x").Number - before[index] - 20) < 1e-9).All(value => value), "frozen hands follow parent translation");
        var offsets = hands.Select(hand => Distance(hand, ParentOf(live, player, hand))).ToArray();
        rootPart.Scope.Write("image_angle", rootPart.Scope.ReadOr("image_angle").Number + 30);
        live.Step();
        Expect(hands.Select((hand, index) => Math.Abs(Distance(hand, ParentOf(live, player, hand)) - offsets[index]) < 1e-9).All(value => value), "frozen hands keep parent-relative offset under rotation");
        Expect(hands.All(hand => Math.Abs(hand.Scope.ReadOr("image_angle").Number - (hand.Scope.ReadOr("Angle").Number + ParentOf(live, player, hand).Scope.ReadOr("image_angle").Number)) < 1e-9), "frozen image_angle follows parent rotation");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void VerifyRopeBondsMap(string root, GmWorld world, ContentRegistry content)
    {
        var service = new NativeMapService(world, content);
        var loaded = service.Load(Path.Combine(root, "Examples", "Mods", "ropebonds", "maps", "demo.map.json"));
        service.InstallForPlay(loaded.Document);
        Expect(service.Attachments.Count(attachment => attachment.EntityType == "ropebonds.creature") == 3, "ropebonds map attaches three creature entities");
        Expect(!loaded.Warnings.Any(warning => warning.Contains("缺少扩展实体") || warning.Contains("缺少依赖")), "ropebonds map has no missing creature dependency");
        service.RestoreAfterPlaytest();
    }
    private static IMod FindLoadedMod(NativeModManager manager, string id)
    {
        var field = typeof(NativeModManager).GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("loaded field missing");
        foreach (var pair in (System.Collections.IEnumerable)field.GetValue(manager)!)
        {
            var value = pair!.GetType().GetProperty("Value")!.GetValue(pair);
            if (value!.GetType().GetProperty("Entry")!.GetValue(value) is IMod mod && mod.Id == id) return mod;
        }
        throw new InvalidOperationException("mod not loaded: " + id);
    }
    private static GmInstance ParentOf(GmWorld world, GmInstance actor, GmInstance bone)
    {
        int id = bone.Scope.ReadOr("parent", actor.Id).Int;
        return id == actor.Id ? actor : world.Instances.First(instance => instance.Id == id && !instance.Destroyed);
    }
    private static double Distance(GmInstance left, GmInstance right)
    {
        double dx = left.Scope.ReadOr("x").Number - right.Scope.ReadOr("x").Number;
        double dy = left.Scope.ReadOr("y").Number - right.Scope.ReadOr("y").Number;
        return Math.Sqrt(dx * dx + dy * dy);
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
        void Tick(bool jump = false, int extraKey = 0)
        {
            clock.BeginTick();
            input.Key(live, jumpKey, jump);
            var held = new HashSet<int>(); if (jump) held.Add(jumpKey); if (extraKey != 0) held.Add(extraKey);
            bridge.Update(new(FrameContext.FixedDelta, tick / 60.0, tick,
                new InputSnapshot(held, held, new HashSet<int>())));
            if (clock.LegacyDue(live.Engine.Read("room_speed").Number)) { live.Step(); input.EndStep(); }
            tick++;
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
    }
    private static GmValue SetMotion(GmWorld world, GmInstance actor, GmValue value) { world.Write(actor, "MotionNum", value); return 0; }
}
