using Godot;

namespace SlackOfDescent;

public partial class NativeMotionOverlay : Control
{
    private NativeBone[] nodes = [];
    private MotionIkChain[] chains = [];
    private MotionView view;
    private Vector2 actor;
    private string? selected;
    private static readonly Color BoneColor = new(0.85f, 0.85f, 0.9f, 0.6f);
    private static readonly Color RootColor = new(0.3f, 0.9f, 1f, 0.9f);
    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;
    public void Sync(IReadOnlyList<NativeBone> bones, string? selectedBone, IEnumerable<MotionIkChain>? ikChains, MotionView frame, Vector2 actorPosition)
    {
        nodes = bones.ToArray(); selected = selectedBone; chains = ikChains?.Where(chain => chain.Enabled).ToArray() ?? []; view = frame; actor = actorPosition; QueueRedraw();
    }
    public override void _Draw()
    {
        if (nodes.Length == 0 || Size.X <= 0 || Size.Y <= 0) return;
        var byInstance = nodes.GroupBy(node => node.Instance).ToDictionary(group => group.Key, group => group.First());
        foreach (var node in nodes)
        {
            if (!byInstance.TryGetValue(node.Parent, out var parent)) continue;
            var color = BoneColor;
            for (int i = 0; i < chains.Length; i++)
                if (chains[i].Mid == node.Name || chains[i].End == node.Name) color = ChainColor(i);
            DrawLine(Surface(parent.X, parent.Y), Surface(node.X, node.Y), color, 1.5f, true);
        }
        foreach (var node in nodes)
        {
            bool highlight = node.Name == selected;
            DrawCircle(Surface(node.X, node.Y), highlight ? 5 : 3, highlight ? Colors.Yellow : Colors.White);
        }
        var root = Surface(actor.X, actor.Y);
        DrawLine(root - new Vector2(6, 0), root + new Vector2(6, 0), RootColor, 2);
        DrawLine(root - new Vector2(0, 6), root + new Vector2(0, 6), RootColor, 2);
    }
    private Vector2 Surface(double x, double y)
    {
        var (surfaceX, surfaceY) = MotionViewport.ToSurface(x, y, Size.X, Size.Y, view);
        return new((float)surfaceX, (float)surfaceY);
    }
    private static Color ChainColor(int index) => Color.FromHsv(index * 0.16f, 0.75f, 1f, 0.85f);
}
