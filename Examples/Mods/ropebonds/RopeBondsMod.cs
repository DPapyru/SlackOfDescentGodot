using System;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed class RopeBondsMod : IMod
{
    public string Id => "example.ropebonds";
    public ConstraintService Constraints { get; } = new();
    internal static string? AssetDirectory;
    private BindProbe? bind;
    private ActorContext? staggering;
    private double staggerStart, staggerLandedAt;
    private NativePoseService.Snapshot? staggerSnapshot;

    public void Load(ModContext context)
    {
        AssetDirectory = System.IO.Path.Combine(context.Directory, "assets");
        context.Content.RegisterEntity(new NativeEntityDefinition
        {
            Id = "ropebonds.creature",
            Name = "RopeBonds creature",
            Description = "Rope creature.",
            BehaviorId = "ropebonds.creature",
            BaseObject = "objEnemyDoll",
            Width = 10,
            Height = 10,
            Parameters = new()
            {
                ["HP"] = new() { Default = "10000" },
                ["INV"] = new() { Default = "0" },
                ["WEAK"] = new() { Default = "0" },
                ["MotionNum"] = new() { Default = "0" },
                ["DEAD_MOTION"] = new() { Default = "0" },
                ["DAMAGE_ACTIVE"] = new() { Default = "1" },
                ["BindTarget"] = new() { Default = "0" }
            }
        }, () => new RopeCreature(Constraints));
        context.Subscribe("ropebonds.creature.damage", "combat.damage", NativeEventPhase.Before, RopeCreature.ModifyDamage);
        context.Subscribe("ropebonds.creature.qte", NativeEventNames.StepEnd, NativeEventPhase.After, _ => RopeCreature.TickQte());
        string controllerStep = NativeEventNames.EnginePrefix + "objects/objController/3/0/28";
        context.Subscribe("ropebonds.qte.unbind", controllerStep, NativeEventPhase.Before, value => RopeCreature.BindFlag(value.Actor?.Id ?? -1, false));
        context.Subscribe("ropebonds.qte.rebind", controllerStep, NativeEventPhase.After, value => RopeCreature.BindFlag(value.Actor?.Id ?? -1, true));
        Constraints.Attach(context);
        context.Subscribe("ropebonds.hands.toggle", NativeEventNames.Call("ropebonds_hands"), NativeEventPhase.Before, value => Toggle(value, BindLayer.Hands));
        context.Subscribe("ropebonds.feet.toggle", NativeEventNames.Call("ropebonds_feet"), NativeEventPhase.Before, value => Toggle(value, BindLayer.Feet));
        context.Subscribe("ropebonds.legqte.toggle", NativeEventNames.Call("ropebonds_legqte"), NativeEventPhase.Before, value => ToggleLegQte(value));
        context.Subscribe("ropebonds.bound.query", NativeEventNames.Call("ropebonds_bound"), NativeEventPhase.Before, value => QueryBound(value));
        context.Subscribe("ropebonds.stagger", NativeEventNames.Call("ropebonds_stagger"), NativeEventPhase.Before, value => StartStagger(value));
        context.Subscribe("ropebonds.stagger.step", NativeEventNames.StepEnd, NativeEventPhase.After, _ => TickStagger());
        context.Subscribe("ropebonds.creature.prepare", NativeEventNames.FramePrepare, NativeEventPhase.After, _ => RopeCreature.Prepare());
        bind = new BindProbe(Constraints);
        bind.Attach(context);
    }

    private void Toggle(NativeGameEvent value, BindLayer layer)
    {
        value.Handled = true;
        value.Result = 1;
        if (value.Actor is not { } actor || !IsPlayer(actor)) return;
        if (Constraints.Bound(actor.Id, layer)) Constraints.Release(actor, layer);
        else Constraints.Bind(actor, layer);
        Console.WriteLine("ROPE_LAYER_TOGGLE layer=" + layer + " actor=" + actor.Id + " bound=" + (Constraints.Bound(actor.Id, layer) ? 1 : 0));
    }

    private void ToggleLegQte(NativeGameEvent value)
    {
        value.Handled = true;
        value.Result = 1;
        if (value.Actor is not { } actor || !IsPlayer(actor)) return;
        if (Constraints.Bound(actor.Id, BindLayer.Feet)) Constraints.Release(actor, BindLayer.Feet);
        else Constraints.Bind(actor, BindLayer.Feet, "ropebonds.legqte", "legqte");
        Console.WriteLine("ROPE_LEG_QTE_TOGGLE actor=" + actor.Id + " bound=" + (Constraints.Bound(actor.Id, BindLayer.Feet) ? 1 : 0));
    }

    private void QueryBound(NativeGameEvent value)
    {
        value.Handled = true;
        value.Result = 0;
        if (value.Actor is not { } actor) return;
        int mask = 0;
        if (Constraints.Bound(actor.Id, BindLayer.Hands)) mask |= 1;
        if (Constraints.Bound(actor.Id, BindLayer.Feet)) mask |= 2;
        if (Constraints.Clip(actor.Id, BindLayer.Feet) == "legqte") mask |= 4;
        value.Result = mask;
    }

    private void StartStagger(NativeGameEvent value)
    {
        value.Handled = true;
        value.Result = 1;
        if (value.Actor is not { } actor || !IsPlayer(actor) || staggering is not null) return;
        staggering = actor;
        staggerStart = actor.Services.Frame.GameTime;
        staggerLandedAt = 0;
        staggerSnapshot = actor.Services.Poses.Capture(actor, ConstraintService.FootBones);
        NativeActorActions.Motion(actor, "DOWN_0");
        Console.WriteLine("ROPE_STAGGER_START actor=" + actor.Id);
    }

    private void TickStagger()
    {
        if (staggering is not { } actor) return;
        if (actor.Instance.Destroyed)
        {
            staggering = null;
            return;
        }
        double now = actor.Services.Frame.GameTime;
        if (staggerLandedAt <= 0 && RopeCreature.StaggerLanded(actor)) staggerLandedAt = now;
        if (!RopeCreature.StaggerFinished(staggerLandedAt, staggerStart, now)) return;
        staggering = null;
        Constraints.Bind(actor, BindLayer.Feet, "ropebonds.legqte", "legqte", staggerSnapshot);
        Console.WriteLine("ROPE_STAGGER_END actor=" + actor.Id);
    }

    internal static ActorContext? FindPlayer(GmWorld world)
    {
        if (!world.Constants.ContainsKey("objPlayerDoll")) return null;
        foreach (ActorContext actor in world.Instances.Where(instance => !instance.Destroyed).Select(instance => new ActorContext(world, instance)).ToArray())
            if (actor.Instance.Active && IsPlayer(actor)) return actor;
        return null;
    }

    internal static bool IsPlayer(ActorContext actor) =>
        actor.World.Constants.ContainsKey("objPlayerDoll") && actor.Services.Instances.IsObjectOf(actor, "objPlayerDoll");

    public void Unload()
    {
        bind?.Dispose();
        bind = null;
        staggering = null;
        staggerSnapshot = null;
        Constraints.ReleaseAll();
        RopeCreature.Clear();
    }
}
