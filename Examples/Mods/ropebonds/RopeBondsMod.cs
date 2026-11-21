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
        Constraints.ReleaseAll();
        RopeCreature.Clear();
    }
}
