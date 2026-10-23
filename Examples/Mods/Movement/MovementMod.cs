using System;
using SlackOfDescent;

namespace MovementExample;

public sealed class MovementMod : IMod
{
    public string Id => "example.movement";
    public void Load(ModContext context)
    {
        context.Content.RegisterAction("example.triple-jump", () => new TripleJump());
        context.Content.RegisterAction("example.nana-spin-jump", () => new NanaSpinJump());
        context.Content.RegisterAction("example.nana-long-dash", () => new NanaLongDash());
        context.Content.RegisterCharacter(new CharacterDefinition {
            Id = "example.triple-yuki", Name = "Yuki · 三段跳", BasePreset = "Yuki", Appearance = "Character-Yuki",
            Actions = new() { [CharacterActionSlot.Jump] = "example.triple-jump" }, Dependencies = new() { Id } });
        context.Content.RegisterCharacter(new CharacterDefinition {
            Id = "example.spin-nana", Name = "Nana · 旋转跳跃与冲刺", BasePreset = "Nana", Appearance = "Character-Nana",
            Actions = new() { [CharacterActionSlot.Jump] = "example.nana-spin-jump", [CharacterActionSlot.Dash] = "example.nana-long-dash" }, Dependencies = new() { Id } });
    }
    public void Unload() { }
}

public sealed class TripleJump : ICharacterAction
{
    public string Id => "example.triple-jump";
    public CharacterActionSlot Slot => CharacterActionSlot.Jump;
    private int jumps;
    private bool airborne;
    private double spinTime;
    private double originalAngle;
    private bool spinning;
    public void Update(ActorContext actor, FrameContext frame)
    {
        bool ground = NativeActorActions.Grounded(actor);
        if (ground && airborne) { jumps = 0; airborne = false; ResetSpin(actor); }
        if (!NativeActorActions.CanAct(actor)) { ResetSpin(actor); return; }
        if (frame.Input.JustPressed(NativeActorActions.Key(actor, 5)) && jumps < 3)
        {
            // All three impulses use the selected preset's first-jump velocity.
            jumps++; airborne = true;
            actor.VerticalSpeed = NativeActorActions.FirstJumpVelocity(actor);
            actor.Write("air", 1); actor.Write("gravity", actor.World.Globals.ReadOr("G", 1).Number);
            NativeActorActions.Motion(actor, actor.World.Globals.ReadOr("Chara").ToString() == "Yuki" ? "JUMPUP2" : "JUMPUP");
            ResetSpin(actor); originalAngle = actor.Read("Angle"); spinning = true; spinTime = 0;
            actor.Write("nativeJumpCount", jumps);
        }
        if (spinning && actor.VerticalSpeed >= 0) ResetSpin(actor);
        if (spinning)
        {
            spinTime += frame.Delta;
            actor.Write("Angle", originalAngle + 360 * Math.Min(spinTime / .36, 1));
            if (spinTime >= .36) ResetSpin(actor);
        }
    }
    private void ResetSpin(ActorContext actor) { if (spinning) actor.Write("Angle", originalAngle); spinning = false; }
    public void Detach(ActorContext actor) { ResetSpin(actor); actor.Write("nativeJumpCount", 0); }
}

public sealed class NanaSpinJump : ICharacterAction
{
    public string Id => "example.nana-spin-jump";
    public CharacterActionSlot Slot => CharacterActionSlot.Jump;
    private bool used;
    private bool airborne;
    private bool spinning;
    private double elapsed;
    private double originalAngle;
    public void Update(ActorContext actor, FrameContext frame)
    {
        bool grounded = NativeActorActions.Grounded(actor);
        if (grounded && airborne) { used = false; airborne = false; Reset(actor); }
        if (!NativeActorActions.CanAct(actor)) { Reset(actor); return; }
        if (!used && frame.Input.JustPressed(NativeActorActions.Key(actor, 5)))
        {
            used = true; airborne = true; originalAngle = actor.Read("Angle"); elapsed = 0; spinning = true;
            actor.VerticalSpeed = NativeActorActions.FirstJumpVelocity(actor);
            actor.Write("air", 1); actor.Write("gravity", actor.World.Globals.ReadOr("G", 1).Number);
            NativeActorActions.Motion(actor, "JUMPUP");
        }
        if (spinning && actor.VerticalSpeed >= 0) Reset(actor);
        if (!spinning) return;
        // Land, bind, damage, replacement and unload restore the original rotation.
        if (NativeActorActions.MotionMatches(actor, "DASH")) { Reset(actor); return; }
        elapsed += frame.Delta;
        actor.Write("Angle", originalAngle + 360 * Math.Min(elapsed / .5, 1));
        if (elapsed >= .5) Reset(actor);
    }
    private void Reset(ActorContext actor) { if (spinning) actor.Write("Angle", originalAngle); spinning = false; }
    public void Detach(ActorContext actor) => Reset(actor);
}

public sealed class NanaLongDash : ICharacterAction
{
    public string Id => "example.nana-long-dash";
    public CharacterActionSlot Slot => CharacterActionSlot.Dash;
    // Original Nana dash moves 40+20+12 = 72 pixels; this moves 108 pixels.
    public const double Distance = 72 * 1.5;
    // Original controller's alarm window is 7 ticks at 30 Hz. Keep that window.
    public const double DoubleTapWindow = 7.0 / 30;
    // The original PlayerCommon/Dash motion lasts 10 ticks at 30 Hz.
    // Its next-dash interval is halved to 5 legacy ticks, measured start to start.
    public const double Cooldown = (10.0 / 30) * .5;
    private double lastLeft = double.NegativeInfinity, lastRight = double.NegativeInfinity;
    private double cooldown, elapsed, moved;
    private int direction;
    private bool dashing;
    public void Update(ActorContext actor, FrameContext frame)
    {
        cooldown = Math.Max(0, cooldown - frame.Delta);
        if (!NativeActorActions.CanAct(actor)) { Stop(actor); return; }
        int requested = 0;
        if (frame.Input.JustPressed(NativeActorActions.Key(actor, 0))) { if (frame.GameTime - lastLeft <= DoubleTapWindow) requested = -1; lastLeft = frame.GameTime; }
        if (frame.Input.JustPressed(NativeActorActions.Key(actor, 1))) { if (frame.GameTime - lastRight <= DoubleTapWindow) requested = 1; lastRight = frame.GameTime; }
        if (!dashing && cooldown <= 0 && requested != 0)
        {
            direction = requested; elapsed = 0; moved = 0; dashing = true; cooldown = Cooldown;
            actor.Write("Direction", -direction); actor.HorizontalSpeed = 0;
            NativeActorActions.Motion(actor, "DASH");
        }
        if (!dashing) return;
        if (!NativeActorActions.MotionMatches(actor, "DASH")) { Stop(actor); return; }
        elapsed += frame.Delta;
        double target = Distance * Math.Min(elapsed / Cooldown, 1), step = target - moved;
        double before = actor.X;
        bool clear = NativeActorActions.MoveHorizontal(actor, direction * step); moved += Math.Abs(actor.X - before);
        actor.Write("nativeDashDistance", moved);
        if (!clear || elapsed >= Cooldown - 1e-9)
        {
            Stop(actor);
            NativeActorActions.Motion(actor, NativeActorActions.Grounded(actor) ? "STAND_0" : "JUMPDOWN");
        }
    }
    private void Stop(ActorContext actor) { if (dashing) actor.HorizontalSpeed = 0; dashing = false; }
    public void Detach(ActorContext actor) => Stop(actor);
}
