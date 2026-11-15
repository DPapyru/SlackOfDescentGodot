using SlackOfDescent;

namespace RopeBonds;

public sealed class RopeBondsMod : IMod
{
    public string Id => "example.ropebonds";
    private QteProbe? probe;
    public void Load(ModContext context)
    {
        probe = new QteProbe();
        probe.Attach(context);
    }
    public void Unload()
    {
        probe?.Dispose();
        probe = null;
    }
}
