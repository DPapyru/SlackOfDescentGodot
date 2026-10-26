using Godot;

namespace SlackOfDescent;

public partial class DoubleDashAudit : Node
{
    private const double OriginalDirection = 0;
    private int assertions;
    private GmWorld world = null!;
    private ContentRegistry content = null!;
    private GmNativeBridge bridge = null!;
    private GmInstance actor = null!;
    private GmInstance floor = null!;
    private void Expect(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); assertions++; }
    public override void _Ready()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "double-dash-audit-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temporary);
        try
        {
            string project = ProjectSettings.GlobalizePath("res://");
            content = new ContentRegistry();
            using var manager = new NativeModManager(content);
            world = new GmWorld(Path.Combine(project, "GameData"), Path.Combine(project, "GameData", "assets"), temporary);
            using var bridge = new GmNativeBridge(world, content);
            this.bridge = bridge;
            string package = CopyExample(project, temporary);
            var loaded = manager.Reload(package);
            Expect(loaded.Success, "double dash mod compiles: " + loaded.Diagnostics);
            Expect(content.Actions.ContainsKey("example.double-dash"), "double dash registers its action factory");
            var schema = content.ActionSchema("example.double-dash");
            Expect(schema is not null && schema.Slot == CharacterActionSlot.Dash && schema.Parameters.Count == 7, "double dash publishes a seven parameter dash schema");
            var tuned = content.CreateAction("example.double-dash", new Dictionary<string, string> { ["firstDistance"] = "24", ["secondDistance"] = "48", ["firstFrames"] = "6" });
            Expect(tuned is IActionConfigurable configurable && configurable.Definition.Parameter("secondWindow") is not null, "action rebuild accepts declared parameters");
            bool rejected = false;
            try { content.CreateAction("example.double-dash", new Dictionary<string, string> { ["firstDistance"] = "5000" }); }
            catch (InvalidDataException) { rejected = true; }
            Expect(rejected, "action parameter validation rejects out of range values");
            Expect(content.Characters.ContainsKey("example.double-dash-nana"), "double dash registers its Nana character");
            Prepare();
            VerifyChain();
            VerifyWindow();
            VerifyLimit();
            VerifyLanding();
            VerifyUnload(manager, package);
            GD.Print("DOUBLEDASH_AUDIT_OK assertions=", assertions);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("DOUBLEDASH_AUDIT_FAILED " + error); GetTree().Quit(1); }
        finally { System.IO.Directory.Delete(temporary, true); }
    }
    private static string CopyExample(string root, string temporary)
    {
        string source = Path.Combine(root, "Examples", "Mods", "DoubleDash"), target = Path.Combine(temporary, "DoubleDash");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            if (!file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                NativeContentStore.WriteAtomic(Path.Combine(target, Path.GetRelativePath(source, file)), File.ReadAllBytes(file));
        return target;
    }
    private void Prepare()
    {
        world.Globals.Write("Chara", "Nana"); world.Globals.Write("PlayerHP", 100); world.Globals.Write("G", 1);
        world.Globals.Write("key", 32, [5]); world.Globals.Write("key", 37, [0]); world.Globals.Write("key", 39, [1]);
        world.Globals.Write("controllMode", -999);
        actor = new GmInstance { Id = 900101, Object = -1 };
        actor.Scope.Write("MotionNum", 100); actor.Scope.Write("STAND_0", 100); actor.Scope.Write("STAND_1", 101);
        actor.Scope.Write("JUMPUP", 102); actor.Scope.Write("JUMPDOWN", 103); actor.Scope.Write("DASH", 104);
        actor.Scope.Write("x", 400); actor.Scope.Write("y", 100); actor.Scope.Write("Direction", OriginalDirection);
        actor.Scope.Write("mask_index", world.Constants["sprMask"]);
        world.Instances.Add(actor);
        var bounds = world.Bounds(actor);
        floor = new GmInstance { Id = 900102, Object = -1, Active = true };
        floor.Scope.Write("mask_index", world.Constants["sprMask"]); floor.Scope.Write("solid", 1);
        floor.Scope.Write("image_xscale", 200); floor.Scope.Write("x", 400);
        floor.Scope.Write("y", actor.Scope.ReadOr("y").Number + bounds.Bottom - bounds.Top + 1);
        world.Instances.Add(floor);
        world.Scripts.Remove("MotionChange");
        world.Platform = (name, args, context) => name switch
        {
            "MotionChange" => SetMotion(args[0]),
            "sound_play" => 0,
            _ => (GmValue?)null
        };
    }
    private GmValue SetMotion(GmValue value) { world.Write(actor, "MotionNum", value); return 0; }
    private static FrameContext Frame(long tick, params int[] pressed) => new(FrameContext.FixedDelta, tick / 60.0, tick,
        new InputSnapshot(new HashSet<int>(pressed), new HashSet<int>(pressed), new HashSet<int>()));
    private void Reset(double x = 400, bool grounded = true)
    {
        world.Write(actor, "x", x); world.Write(actor, "y", 100); world.Write(actor, "hspeed", 0); world.Write(actor, "vspeed", 0);
        actor.Scope.Write("Direction", OriginalDirection); actor.Scope.Write("nativeDashDistance", 0);
        floor.Active = grounded;
        bridge.BindCharacter(actor, content.Characters["example.double-dash-nana"]);
        NativeActorActions.Motion(new(world, actor), "STAND_0");
    }
    private void Tick(long from, long to, int pressAt = -1, int key = 0)
    {
        for (long tick = from; tick <= to; tick++) bridge.Update(Frame(tick, tick == pressAt ? key : 0));
    }
    private void VerifyChain()
    {
        Reset();
        bridge.Update(Frame(1, 39)); bridge.Update(Frame(2)); bridge.Update(Frame(3, 39));
        Expect(NativeActorActions.MotionMatches(new(world, actor), "DASH"), "double tap starts the native dash motion");
        Tick(4, 24);
        double first = actor.Scope.ReadOr("x").Number - 400;
        Expect(Math.Abs(first - 72) < 1e-5, "first stage covers 72 pixels, got " + first);
        Expect(Math.Abs(actor.Scope.ReadOr("nativeDashDistance").Number - 72) < 1e-5, "first stage reports its distance");
        Expect(NativeActorActions.MotionMatches(new(world, actor), "STAND_0"), "first stage returns to stand");
        double pivot = actor.Scope.ReadOr("x").Number;
        bridge.Update(Frame(25, 37));
        Tick(26, 46);
        double second = actor.Scope.ReadOr("x").Number - pivot;
        Expect(Math.Abs(second + 72) < 1e-5, "second stage reverses 72 pixels, got " + second);
        Expect(Math.Abs(actor.Scope.ReadOr("nativeDashDistance").Number - 144) < 1e-5, "two stages report 144 pixels");
        Expect(Math.Abs(actor.Scope.ReadOr("x").Number - 400) < 1e-5, "a reversed second stage returns to the origin");
        Expect(NativeActorActions.MotionMatches(new(world, actor), "STAND_0"), "the chain returns to stand after two stages");
        Expect(actor.Scope.ReadOr("Direction").Number == 1, "facing follows the second stage while attached");
    }
    private void VerifyWindow()
    {
        Reset(400);
        bridge.Update(Frame(100, 39)); bridge.Update(Frame(101)); bridge.Update(Frame(102, 39));
        Tick(103, 155);
        double first = actor.Scope.ReadOr("x").Number - 400;
        Expect(Math.Abs(first - 72) < 1e-5, "window scenario tallied one stage, got " + first);
        double pivot = actor.Scope.ReadOr("x").Number;
        bridge.Update(Frame(200, 37));
        Tick(201, 221);
        Expect(actor.Scope.ReadOr("x").Number == pivot, "press after the window never starts a second stage");
        Expect(Math.Abs(actor.Scope.ReadOr("nativeDashDistance").Number - 72) < 1e-5, "expired window keeps the single stage distance");
    }
    private void VerifyLimit()
    {
        Reset(400);
        bridge.Update(Frame(300, 39)); bridge.Update(Frame(301)); bridge.Update(Frame(302, 39));
        Tick(303, 323);
        bridge.Update(Frame(324, 39));
        Tick(325, 345);
        Expect(Math.Abs(actor.Scope.ReadOr("nativeDashDistance").Number - 144) < 1e-5, "two stages already consumed the budget");
        double before = actor.Scope.ReadOr("x").Number;
        Tick(346, 358, pressAt: 346, key: 39);
        Tick(359, 380);
        Expect(actor.Scope.ReadOr("x").Number == before, "a lone third press never produces a third stage");
        Expect(Math.Abs(actor.Scope.ReadOr("nativeDashDistance").Number - 144) < 1e-5, "a lone third press keeps the two stage distance");
        bridge.Update(Frame(381, 39)); bridge.Update(Frame(382)); bridge.Update(Frame(383, 39));
        Tick(384, 396);
        Expect(Math.Abs(actor.Scope.ReadOr("nativeDashDistance").Number - 72) < 1e-5, "a fresh chain restarts at stage one instead of adding a third stage");
    }
    private void VerifyLanding()
    {
        Reset(400);
        bridge.Update(Frame(400, 39)); bridge.Update(Frame(401)); bridge.Update(Frame(402, 39));
        Expect(NativeActorActions.MotionMatches(new(world, actor), "DASH"), "airborne scenario starts the dash");
        Tick(403, 423);
        Expect(actor.Scope.ReadOr("nativeDashDistance").Number > 0, "airborne first stage still travels");
        double landed = actor.Scope.ReadOr("x").Number;
        floor.Active = false;
        Tick(424, 430);
        floor.Active = true;
        Tick(431, 470);
        Expect(Math.Abs(actor.Scope.ReadOr("x").Number - landed) < 1e-5, "landing clears the pending second stage");
        bridge.Update(Frame(471, 37));
        Tick(472, 492);
        Expect(Math.Abs(actor.Scope.ReadOr("x").Number - landed) < 1e-5, "a stale window never resumes after landing");
        Reset(400, grounded: false);
        bridge.Update(Frame(500, 39)); bridge.Update(Frame(501)); bridge.Update(Frame(502, 39));
        Tick(503, 523);
        Expect(actor.Scope.ReadOr("nativeDashDistance").Number > 0, "airborne dash without a floor still travels");
    }
    private GmInstance AddController()
    {
        var existing = NativeActorActions.Controller(new(world, actor));
        if (existing is not null) return existing;
        if (!world.Constants.TryGetValue("objController", out var kind)) throw new InvalidOperationException("objController constant is missing");
        var controller = new GmInstance { Id = 900103, Object = kind.Int, Active = true };
        controller.Scope.Write("CtrlTarget", actor.Id);
        world.Instances.Add(controller);
        return controller;
    }
    private void VerifyUnload(NativeModManager manager, string package)
    {
        Reset(400);
        bridge.Update(Frame(600, 39)); bridge.Update(Frame(601)); bridge.Update(Frame(602, 39));
        Tick(603, 623);
        Expect(actor.Scope.ReadOr("nativeDashDistance").Number > 0, "chain state is written before unload");
        var controller = AddController();
        bridge.Update(Frame(620));
        Expect(controller.Scope.ReadOr("slackNativeDash").Truth, "replacement dash gates the original GM slot");
        var unloaded = manager.Unload("example.double-dash");
        Expect(unloaded.Success, "double dash mod unloads: " + unloaded.Diagnostics);
        Expect(!content.Actions.ContainsKey("example.double-dash") && !content.Characters.ContainsKey("example.double-dash-nana"), "registry drops the action and character");
        Expect(!controller.Scope.ReadOr("slackNativeDash").Truth, "unload restores the original GM dash slot");
        Expect(actor.Scope.ReadOr("nativeDashDistance").Number == 0, "detach clears the dash distance");
        Expect(Math.Abs(actor.Scope.ReadOr("Direction").Number - OriginalDirection) < 1e-9, "detach restores the facing direction");
        double before = actor.Scope.ReadOr("x").Number;
        bridge.Update(Frame(624, 39)); bridge.Update(Frame(625, 39)); bridge.Update(Frame(626));
        bridge.Update(Frame(627, 39));
        Tick(628, 648);
        Expect(actor.Scope.ReadOr("x").Number == before, "unloaded mod leaves no residual dash behaviour");
        var reloaded = manager.Reload(package);
        Expect(reloaded.Success, "double dash mod reloads: " + reloaded.Diagnostics);
        Expect(actor.Scope.ReadOr("nativeDashDistance").Number == 0, "reload starts from a clean chain");
    }
}
