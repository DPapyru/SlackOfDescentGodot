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
    private DeathShow? death;
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
            Width = 10,
            Height = 10
        }, () => new RopeCreature(Constraints));
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
        death = new DeathShow();
        death.Attach(context);
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
        death?.Dispose();
        death = null;
        bind?.Dispose();
        bind = null;
        staggering = null;
        staggerSnapshot = null;
        Constraints.ReleaseAll();
        RopeCreature.Clear();
    }
}

internal sealed class DeathShow : IDisposable
{
    private const double Interval = .1;
    private const double ParticleCount = 24;
    private readonly record struct Limb(string Part2, string Part3, string Sprite2, string Sprite3, int Length, int Direction, string? Macro);
    private static readonly Limb[] Limbs =
    [
        new("HAND2_ABOVE", "HAND3_ABOVE", "sprHand2_Above", "sprHand3_Above", 12, 270, "Macro_CutHand2_Above"),
        new("HAND2_BELOW", "HAND3_BELOW", "sprHand2_Below", "sprHand3_Below", 12, 270, null),
        new("FOOT2_ABOVE", "FOOT3_ABOVE", "sprFoot2_Above", "sprFoot3_Above", 25, 267, "Macro_CutFoot2_Above"),
        new("FOOT2_BELOW", "FOOT3_BELOW", "sprFoot2_Below", "sprFoot3_Below", 25, 267, null)
    ];
    private GmWorld? world;
    private ActorContext? player;
    private double startedAt;
    private int step;
    private bool running, done;

    public void Attach(ModContext context)
    {
        context.Subscribe("ropebonds.death.created", NativeEventNames.InstanceCreated, NativeEventPhase.After, Created);
        context.Subscribe("ropebonds.death.show", NativeEventNames.StepEnd, NativeEventPhase.After, _ => Tick());
    }

    public void Dispose()
    {
        world = null;
        player = null;
        running = false;
        done = false;
        step = 0;
    }

    private void Created(NativeGameEvent value)
    {
        if (value.Actor is not { } actor) return;
        world = actor.World;
        if (RopeBondsMod.IsPlayer(actor)) player = actor;
    }

    private ActorContext? Actor() =>
        player is { } current && !current.Instance.Destroyed && current.World.Instances.Contains(current.Instance)
            ? current
            : world is null ? null : RopeBondsMod.FindPlayer(world);

    private void Tick()
    {
        if (done)
        {
            if (player is not { } current || current.Instance.Destroyed || current.World.Globals.ReadOr("PlayerHP").Number > 0)
            {
                done = false;
                step = 0;
            }
            return;
        }
        if (running)
        {
            Advance();
            return;
        }
        if (Actor() is not { } actor) return;
        player = actor;
        if (actor.World.Globals.ReadOr("PlayerHP").Number > 0) return;
        if (!actor.World.Globals.ReadOr("config_Blood").Truth)
        {
            done = true;
            return;
        }
        running = true;
        startedAt = actor.Services.Frame.GameTime;
        step = 0;
        Advance();
    }

    private void Advance()
    {
        if (Actor() is not { } actor)
        {
            running = false;
            return;
        }
        player = actor;
        if (!actor.Instance.Active)
        {
            running = false;
            done = false;
            return;
        }
        double elapsed = actor.Services.Frame.GameTime - startedAt;
        while (step < Limbs.Length && elapsed >= step * Interval)
        {
            Cut(actor, step);
            step++;
        }
        if (step < Limbs.Length) return;
        running = false;
        done = true;
    }

    private static void Cut(ActorContext actor, int index)
    {
        Limb limb = Limbs[index];
        if (actor.Part(limb.Part2) is not { } part) return;
        if (limb.Macro is { } macro) actor.Call(macro);
        else Separate(actor, limb, part);
        Hide(part);
        if (actor.Part(limb.Part3) is { } tip) Hide(tip);
        actor.Call("Fluid", part.X, part.Y, Constant(actor, "COLOR_BLOOD"), ParticleCount, 5, 15, 0, 360, .5);
        Console.WriteLine("ROPE_DEATH_CUT index=" + index + " part=" + limb.Part2 + " macro=" + (limb.Macro ?? "none"));
    }

    private static void Separate(ActorContext actor, Limb limb, ActorContext part)
    {
        ActorContext piece = actor.Services.Instances.Spawn("objCutParts", part.X, part.Y);
        piece.Write("sprite_index", Constant(actor, limb.Sprite2));
        piece.Write("vspeed", -8);
        piece.Write("hspeed", Random.Shared.NextDouble() * 10 * -actor.Read("Direction"));
        piece.Write("gravity", .9);
        piece.Write("rotSpeed", 15);
        piece.Write("partsLength", limb.Length);
        piece.Write("partsDirection", limb.Direction);
        piece.Write("width", 3);
        piece.Write("height", 3);
        piece.Write("offsetx", 1);
        piece.Write("offsety", 1);
        piece.Write("blood", 1);
        if (actor.Part(limb.Part3) is not { } tip) return;
        ActorContext end = actor.Services.Instances.Spawn("objCutParts", tip.X, tip.Y);
        end.Write("sprite_index", Constant(actor, limb.Sprite3));
        end.Write("rotSpeed", 15);
        end.Write("minrot", -10);
        end.Write("maxrot", 10);
        end.Write("parent", piece.Id);
    }

    private static void Hide(ActorContext part)
    {
        if (!part.Instance.Destroyed) part.Instance.Scope.Write("image_alpha", 0);
    }

    private static int Constant(ActorContext actor, string name) =>
        actor.World.Constants.TryGetValue(name, out GmValue value) ? value.Int : 0;
}
