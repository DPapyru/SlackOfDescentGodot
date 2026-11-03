using Godot;

namespace SlackOfDescent;

public partial class NativeMotionOverlay : Control
{
    private NativeBone[] nodes = [];
    private MotionIkChain[] chains = [];
    private MotionView view;
    private Vector2 actor;
    private string? selected;
    private bool showBones = true, showNames;
    public bool ShowBones { get => showBones; set { showBones = value; QueueRedraw(); } }
    public bool ShowNames { get => showNames; set { showNames = value; QueueRedraw(); } }
    private static readonly Color BoneColor = new(0.85f, 0.85f, 0.9f, 0.6f);
    private static readonly Color RootColor = new(0.3f, 0.9f, 1f, 0.9f);
    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;
    public void Sync(IReadOnlyList<NativeBone> bones, string? selectedBone, IEnumerable<MotionIkChain>? ikChains, MotionView frame, Vector2 actorPosition)
    {
        nodes = bones.ToArray(); selected = selectedBone; chains = ikChains?.Where(chain => chain.Enabled).ToArray() ?? []; view = frame; actor = actorPosition; QueueRedraw();
    }
    public override void _Draw()
    {
        if (!showBones || nodes.Length == 0 || Size.X <= 0 || Size.Y <= 0) return;
        var byInstance = nodes.GroupBy(node => node.Instance).ToDictionary(group => group.Key, group => group.First());
        var root = Surface(actor.X, actor.Y);
        foreach (var node in nodes)
        {
            var color = BoneColor;
            for (int i = 0; i < chains.Length; i++)
                if (chains[i].Mid == node.Name || chains[i].End == node.Name) color = ChainColor(i);
            if (byInstance.TryGetValue(node.Parent, out var parent)) DrawLine(Surface(parent.X, parent.Y), Surface(node.X, node.Y), color, 1.5f, true);
            else DrawLine(root, Surface(node.X, node.Y), RootColor, 1f, true);
        }
        foreach (var node in nodes)
        {
            bool highlight = node.Name == selected;
            var point = Surface(node.X, node.Y);
            DrawCircle(point, highlight ? 5 : 3, highlight ? Colors.Yellow : Colors.White);
            if (showNames) DrawString(ThemeDB.FallbackFont, point + new Vector2(6, -6), node.Name, HorizontalAlignment.Left, -1, 10, Colors.White);
        }
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
