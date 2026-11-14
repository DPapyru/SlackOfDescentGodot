using System;
using SlackOfDescent;

namespace RopeBonds;

public sealed partial class RopeBondsMod : IMod
{
    public string Id => "example.ropebonds";
    public ConstraintService Constraints { get; } = new();
    public void Load(ModContext context)
    {
        Constraints.Attach(context);
        RegisterMotions(context);
        RegisterActions(context);
        RegisterMaps(context);
        RegisterCreatures(context);
        if (Environment.GetEnvironmentVariable("ROPEBONDS_SELFCHECK") is { Length: > 0 }) SelfCheck.Run();
    }
    public void Unload() => Constraints.ReleaseAll();
    partial void RegisterMotions(ModContext context);
    partial void RegisterActions(ModContext context);
    partial void RegisterMaps(ModContext context);
    partial void RegisterCreatures(ModContext context);
}
