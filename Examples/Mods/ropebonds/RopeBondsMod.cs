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
        bind = new BindProbe(Constraints);
        bind.Attach(context);
        qte = new QteProbe();
        qte.Attach(context);
    }

    public void Unload()
    {
        qte?.Dispose();
        qte = null;
        bind?.Dispose();
        bind = null;
        Constraints.ReleaseAll();
    }
}
