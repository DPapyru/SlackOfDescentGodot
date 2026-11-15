using SlackOfDescent;

namespace RopeBonds;

public sealed class RopeBondsMod : IMod
{
    public string Id => "example.ropebonds";
    private QteProbe? probe;
    public void Load(ModContext context)
    {
        context.Content.RegisterEntity(new NativeEntityDefinition
        {
            Id = "ropebonds.creature",
            Name = "RopeBonds creature",
            Description = "Rope creature placeholder.",
            BehaviorId = "ropebonds.creature",
            Width = 10,
            Height = 10
        }, () => new RopeCreaturePlaceholder());
        probe = new QteProbe();
        probe.Attach(context);
    }
    public void Unload()
    {
        probe?.Dispose();
        probe = null;
    }
}

public sealed class RopeCreaturePlaceholder : IEntityBehavior
{
    public string Id => "ropebonds.creature";
    public void Update(ActorContext entity, FrameContext frame) { }
}
