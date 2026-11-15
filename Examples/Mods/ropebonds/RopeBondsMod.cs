using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed class RopeBondsMod : IMod
{
    public string Id => "example.ropebonds";
    public HandConstraint Constraints { get; } = new();
    private QteProbe? qte;
    private BindProbe? bind;

    public void Load(ModContext context)
    {
        context.Content.RegisterEntity(new NativeEntityDefinition
        {
            Id = "ropebonds.creature",
            Name = "RopeBonds creature",
            Description = "Rope creature.",
            BehaviorId = "ropebonds.creature",
            Width = 10,
            Height = 10
        }, () => new RopeCreature(Constraints));
        Constraints.Attach(context);
        context.Subscribe("ropebonds.creature.prepare", NativeEventNames.FramePrepare, NativeEventPhase.After, _ => RopeCreature.Prepare());
        bind = new BindProbe(Constraints);
        bind.Attach(context);
        qte = new QteProbe();
        qte.Attach(context);
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
        qte?.Dispose();
        qte = null;
        bind?.Dispose();
        bind = null;
        Constraints.ReleaseAll();
        RopeCreature.Clear();
    }
}
