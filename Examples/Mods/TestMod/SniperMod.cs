using System;
using System.Collections.Generic;
using SlackOfDescent;

namespace TestMod;

public sealed class SniperMod : IMod
{
    private readonly List<SniperWeapon> weapons = new();
    public string Id => "example.nana-sniper";
    public void Load(ModContext context)
    {
        context.Content.RegisterInput(new() { Id = "testmod.reload", Name = "狙击枪：换弹", Description = "填满五发弹匣，需要 2.5 秒。", DefaultKey = 82 });
        context.Content.RegisterEntity(new NativeEntityDefinition { Id = "testmod.magazine", Name = "狙击枪弹匣", ResourceId = "testmod.magazine", Width = 8, Height = 4,
            Parameters = new() { ["horizontal"] = new(), ["vertical"] = new() } }, () => new Magazine());
        context.Content.RegisterAction("testmod.sniper", () => { var weapon = new SniperWeapon(); weapons.Add(weapon); return weapon; });
        context.Content.RegisterCharacter(new() { Id = "testmod.nana-sniper", Name = "Nana · 狙击枪", BasePreset = "Nana", Appearance = "Character-Nana",
            Actions = new() { [CharacterActionSlot.Attack] = "testmod.sniper" }, Dependencies = new() { Id } });
        context.Subscribe("testmod.reset", "engine.respawn", NativeEventPhase.Before, _ => { foreach (var weapon in weapons) weapon.Reset(); });
        context.Subscribe("testmod.room", "room", NativeEventPhase.Before, _ => { foreach (var weapon in weapons) weapon.CancelReload(); });
    }
    public void Unload() => weapons.Clear();
}

public sealed class SniperWeapon : ICharacterWeapon
{
    public string Id => "testmod.sniper";
    public CharacterActionSlot Slot => CharacterActionSlot.Attack;
    private int ammunition = 5;
    private double cooldown;
    private double reload;
    private double recoil;
    private double requestExpires;
    private bool requested;
    private bool previousJoyFire;
    public NativeWeaponState? State => new(ammunition,5,reload>0);
    public bool IsEquipped(ActorContext actor)=>actor.World.Globals.ReadOr("CurrentWeapon").Int==0;
    public void Reset() { ammunition = 5; cooldown = 0; recoil = 0; CancelReload(); }
    public void CancelReload() { reload = 0; requested = false; }
    public void Update(ActorContext actor, FrameContext frame)
    {
        cooldown = Math.Max(0, cooldown - frame.Delta); recoil = Math.Max(0, recoil - frame.Delta);
        bool usable = NativeActorActions.CanUseWeapon(actor);
        if (!usable) CancelReload();
        if (requested && frame.GameTime > requestExpires) requested = false;
        var controller = NativeActorActions.Controller(actor);
        bool joyFire = controller?.Scope.ReadOr("b1").Int == 2;
        bool pressed = frame.Input.JustPressed(-1) || frame.Input.JustPressed(NativeActorActions.Key(actor, 4)) || joyFire && !previousJoyFire;
        previousJoyFire = joyFire;
        if (pressed)
        {
            requested = usable && cooldown <= 1e-9 && reload <= 0 && ammunition > 0;
            requestExpires = frame.GameTime + .5;
        }
        if (usable && actor.Services.Input.Pressed("testmod.reload", frame.Input)) Reload(actor);
        if (reload > 0)
        {
            reload = Math.Max(0, reload - frame.Delta);
            if (reload <= 1e-9) { reload = 0; ammunition = 5; }
        }
        if (usable)
        {
            var pose = WeaponPose(actor);
            actor.Services.Presentation.Sprite(actor.Id, "sniper", "testmod.sniper", pose.X, pose.Y, pose.Angle, pose.ScaleX, pose.ScaleY, 18, 7,
                (actor.Part("HAND3_ABOVE") ?? actor).Read("depth") - 1);
        }
        else actor.Services.Presentation.Remove(actor.Id, "sniper");
        actor.Services.Presentation.Text(actor.Id, "ammunition", reload > 0 ? $"狙击枪  {ammunition}/5 · 换弹 {reload:0.0}s" : $"狙击枪  {ammunition}/5 · ∞");
        actor.Write("sniperAmmunition", ammunition); actor.Write("sniperReload", reload); actor.Write("sniperCooldown", cooldown);
    }
    private NativePartPose WeaponPose(ActorContext actor)
    {
        var hand = actor.Part("HAND3_ABOVE") ?? actor;
        double angle = NativeActorActions.Controller(actor)?.Scope.ReadOr("m_dir").Number ?? 0;
        double radians = angle * Math.PI / 180;
        double kick = 5 * recoil / .12;
        return new(hand.X - Math.Cos(radians) * kick, hand.Y + Math.Sin(radians) * kick, angle, 1, Math.Cos(radians) < 0 ? -1 : 1);
    }
    public void Fire(ActorContext actor, NativeFireRequest request)
    {
        if (!requested || cooldown > 1e-9 || reload > 0 || ammunition <= 0 || !NativeActorActions.CanUseWeapon(actor)) return;
        requested = false; ammunition--; cooldown = 1; recoil = .12;
        var pose = WeaponPose(actor);
        var muzzle = pose.Transform(64, -3);
        double muzzleAngle=Math.Atan2(pose.Y-muzzle.Y,muzzle.X-pose.X)*180/Math.PI;
        var obstruction = actor.Services.Combat.Raycast(actor, pose.X, pose.Y, muzzleAngle, Math.Sqrt(Math.Pow(muzzle.X - pose.X, 2) + Math.Pow(muzzle.Y - pose.Y, 2)));
        var hit = obstruction ?? actor.Services.Combat.Raycast(actor, muzzle.X, muzzle.Y, request.Angle, 1600);
        if (hit is {} target) actor.Services.Combat.Damage(actor, target, 300, request.Angle);
        double radians = request.Angle * Math.PI / 180;
        actor.Services.Presentation.Line(actor.Id, "shot", muzzle.X, muzzle.Y, hit?.X ?? muzzle.X + 1600 * Math.Cos(radians), hit?.Y ?? muzzle.Y - 1600 * Math.Sin(radians));
        actor.Services.Audio.PlayBuiltin(actor, "sndBullet");
        actor.Write("sniperShots", actor.Read("sniperShots") + 1);
        actor.Write("sniperAmmunition", ammunition);
    }
    public void Reload(ActorContext actor)
    {
        if (reload > 0 || ammunition >= 5 || !NativeActorActions.CanUseWeapon(actor)) return;
        requested = false; reload = 2.5;
        var pose = WeaponPose(actor); var point = pose.Transform(1, 4);
        actor.Services.Entities.Spawn("testmod.magazine", point.X, point.Y, actor.Id,
            new Dictionary<string,GmValue>{["horizontal"]=pose.ScaleY*-70,["vertical"]=-90});
        actor.Services.Audio.PlayBuiltin(actor, "sndReload");
    }
    public void Detach(ActorContext actor) { CancelReload(); actor.Services.Presentation.Clear(actor.Id); }
}

public sealed class Magazine : IEntityBehavior
{
    public string Id => "testmod.magazine";
    private double elapsed;
    private double angle;
    private double horizontal;
    private double vertical;
    public void Created(ActorContext actor){horizontal=actor.Read("horizontal");vertical=actor.Read("vertical");}
    public void Update(ActorContext actor, FrameContext frame)
    {
        elapsed += frame.Delta;
        if (elapsed >= 3) { actor.Services.Entities.Destroy(actor.Id); return; }
        vertical += 420 * frame.Delta;
        double remaining = vertical * frame.Delta;
        while (Math.Abs(remaining) > 1e-9)
        {
            double step = Math.Sign(remaining) * Math.Min(1, Math.Abs(remaining));
            if (!actor.Call("place_free", actor.X, actor.Y + step).Truth) { vertical = 0; horizontal *= .9; break; }
            actor.Y += step; remaining -= step;
        }
        double dx = horizontal * frame.Delta;
        if (actor.Call("place_free", actor.X + dx, actor.Y).Truth) actor.X += dx; else horizontal = 0;
        if (Math.Abs(vertical) > 1) angle += 240 * frame.Delta;
        actor.Services.Presentation.Sprite(actor.Id, "entity", "testmod.magazine", actor.X, actor.Y, angle, 1, 1, 4, 2);
    }
}
