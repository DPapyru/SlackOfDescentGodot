using Godot;
using System.Runtime.CompilerServices;

namespace SlackOfDescent;

/// <summary>Run res://Runtime/NativeModsAudit.tscn in a real Godot .NET process.</summary>
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
            string movementSource = Path.Combine(movement, "MovementMod.cs");
            File.AppendAllText(movementSource, "\nthis is not valid C# !!!\n");
            var failed = manager.Reload(movement);
            Expect(!failed.Success && failed.Diagnostics.Contains("MovementMod.cs"), "compile error reports source diagnostics");
            Expect(content.Actions.Count == 3 && manager.LoadedMods.Count == 1, "failed compile preserves active registry");
            VerifyMovement(world, content, bridge, actor);
            var moveUnload = manager.Unload("example.movement");
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
        // Use the actual registry delegates compiled from the user's example sources.
        actor.Scope.Write("MotionNum", 100); actor.Scope.Write("STAND_0", 100); actor.Scope.Write("JUMPUP", 101);
        actor.Scope.Write("JUMPUP2", 102); actor.Scope.Write("JUMPDOWN", 103); actor.Scope.Write("DASH", 104);
        actor.Scope.Write("x", 400); actor.Scope.Write("y", 100); actor.Scope.Write("Angle", 17);
        world.Globals.Write("Chara", "Yuki"); world.Globals.Write("PlayerHP", 100); world.Globals.Write("G", 1);
        world.Globals.Write("key", 32, [5]); world.Globals.Write("key", 37, [0]); world.Globals.Write("key", 39, [1]);
        world.Globals.Write("controllMode", -999);
        // Replace motion/collision only at the platform boundary, preserving live actor field writes.
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
        bridge.BindCharacter(actor, content.Characters["example.triple-yuki"]);
        for (int jump = 0; jump < 4; jump++)
        {
            world.Write(actor, "vspeed", 7); bridge.Update(Frame(jump * 2, 32));
            Expect(actor.Scope.ReadOr("vspeed").Number == (jump < 3 ? -18 : 7), "triple jump impulse/no fourth " + jump);
            if (jump < 2) bridge.Update(Frame(jump * 2 + 1));
        }
        floor.Active = true; world.Write(actor, "vspeed", 0); bridge.Update(Frame(10));
        floor.Active = false; bridge.Update(Frame(11, 32));
        Expect(actor.Scope.ReadOr("nativeJumpCount").Int == 1, "triple jump resets after actual landing");
        world.Globals.Write("Chara", "Nana"); bridge.BindCharacter(actor, content.Characters["example.spin-nana"]);
        world.Write(actor, "vspeed", 0); bridge.Update(Frame(20, 32));
        for (int i = 21; i < 51; i++) bridge.Update(Frame(i));
        Expect(Math.Abs(actor.Scope.ReadOr("Angle").Number - 17) < 1e-6, "Nana finishes exactly one rotation and restores angle");
        floor.Active = true; world.Write(actor, "vspeed", 0); bridge.Update(Frame(52)); floor.Active = false;
        bridge.Update(Frame(53, 32)); actor.Scope.Write("BINDED", 1); bridge.Update(Frame(54));
        Expect(actor.Scope.ReadOr("Angle").Number == 17, "binding interrupts Nana spin cleanly"); actor.Scope.Write("BINDED", 0);
        actor.Scope.Write("MotionNum", 100); actor.Scope.Write("x", 400);
        bridge.Update(Frame(60, 39)); bridge.Update(Frame(61)); bridge.Update(Frame(66, 39));
        for (int i = 67; i <= 77; i++) bridge.Update(Frame(i));
        Expect(Math.Abs(actor.Scope.ReadOr("x").Number - 508) < 1e-5, "Nana dash covers 1.5 times original 72 pixels");
        // Seven original 30 Hz ticks retain the original double-tap window.
        actor.Scope.Write("x", 400); bridge.Update(Frame(100, 37)); bridge.Update(Frame(115, 37));
        Expect(actor.Scope.ReadOr("x").Number == 400, "dash rejects taps outside original seven-tick window");
        bridge.UnbindCharacter(actor.Id);
        Expect(actor.Scope.ReadOr("Angle").Number == 17, "unbind restores actor rotation");
    }
    private static GmValue SetMotion(GmWorld world, GmInstance actor, GmValue value) { world.Write(actor, "MotionNum", value); return 0; }
}
