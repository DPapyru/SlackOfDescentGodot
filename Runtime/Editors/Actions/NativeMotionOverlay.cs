using Godot;

namespace SlackOfDescent;

public partial class NativeMotionOverlay : Control
{
    private const float RingRadius = 72f, BallRadius = 9f, BallHit = 14f, BarGap = 24f, BarThickness = 8f, BarHit = 10f, JointRadius = 5f, SelectedRadius = 8f, HandleRadius = 9f;
    internal const float BarHalf = 72f;
    private static readonly Color RingColor = new(0.25f, 1f, 0.35f, 0.35f), BallColor = new(1f, 1f, 1f, 0.7f), BarColor = new(0.6f, 1f, 0.7f, 0.8f), KnobColor = new(1f, 1f, 1f, 0.9f);
    private NativeBone[] nodes = [];
    private MotionIkChain[] chains = [];
    private MotionView view;
    private Vector2 actor;
    private string? selected;
    private double? ringAngle;
    private Vector2? barOffset;
    private bool showBones = true, showNames;
    private string legend = "", playback = "";
    public bool ShowBones { get => showBones; set { showBones = value; QueueRedraw(); } }
    public bool ShowNames { get => showNames; set { showNames = value; QueueRedraw(); } }
    public string Legend { get => legend; set { legend = value; QueueRedraw(); } }
    public string Playback { get => playback; set { playback = value; QueueRedraw(); } }
    private static readonly Color BoneColor = new(0.85f, 0.85f, 0.9f, 0.6f);
    private static readonly Color RootColor = new(0.3f, 0.9f, 1f, 0.9f);
    internal MotionView View => view;
    internal float RingPixelRadius => RingRadius;
    internal bool RingVisible => showBones && SelectedNode() is not null;
    internal double? BallAngle => SelectedNode() is { } node ? ringAngle ?? node.Angle : null;
    internal Vector2? BallPoint => SelectedNode() is { } node ? Surface(node.X, node.Y) + Direction(ringAngle ?? node.Angle) * RingRadius : null;
    internal IReadOnlyList<Vector2> BarPoints => new[] { BarCenter(0) ?? Vector2.Zero, BarCenter(1) ?? Vector2.Zero };
    internal Vector2? BarOffset => barOffset;
    internal Rect2? BarRect(int axis) => BarCenter(axis) is { } center ? BarRect(center, axis == 0 ? new(1, 0) : new(0, 1)) : null;
    internal int ChainFocusIndex => selected is null ? -1 : Array.FindIndex(chains, chain => chain.Root == selected || chain.Mid == selected || chain.End == selected);
    internal string[] FocusChainBones => ChainFocusIndex is var focus && focus >= 0 ? [chains[focus].Root, chains[focus].Mid, chains[focus].End] : [];
    internal bool EndHandleVisible => ChainFocusIndex >= 0;
    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;
    public void Sync(IReadOnlyList<NativeBone> bones, string? selectedBone, IEnumerable<MotionIkChain>? ikChains, MotionView frame, Vector2 actorPosition)
    {
        nodes = bones.ToArray(); selected = selectedBone; chains = ikChains?.Where(chain => chain.Enabled).ToArray() ?? []; view = frame; actor = actorPosition; QueueRedraw();
    }
    internal void SetRingAngle(double? degrees)
    {
        ringAngle = degrees; QueueRedraw();
    }
    internal void SetBarOffset(Vector2? offset)
    {
        barOffset = offset; QueueRedraw();
    }
    internal string? RingHit(Vector2 surfacePoint)
    {
        if (SelectedNode() is not { } node) return null;
        var center = Surface(node.X, node.Y);
        if (surfacePoint.DistanceTo(center + Direction(ringAngle ?? node.Angle) * RingRadius) <= BallHit) return "ball";
        var xBar = BarCenter(0);
        if (xBar is { } x && Math.Abs(surfacePoint.X - x.X) <= BarHalf && Math.Abs(surfacePoint.Y - x.Y) <= BarHit) return "bar:x";
        var yBar = BarCenter(1);
        if (yBar is { } y && Math.Abs(surfacePoint.Y - y.Y) <= BarHalf && Math.Abs(surfacePoint.X - y.X) <= BarHit) return "bar:y";
        return null;
    }
    private NativeBone? SelectedNode() => selected is null ? null : nodes.FirstOrDefault(node => node.Name == selected);
    private Vector2? BarCenter(int axis)
    {
        if (SelectedNode() is not { } node) return null;
        var ring = Surface(node.X, node.Y);
        return axis == 0 ? ring + new Vector2(0, RingRadius + BarGap) : ring + new Vector2(RingRadius + BarGap, 0);
    }
    public override void _Draw()
    {
        if (Size.X <= 0 || Size.Y <= 0) return;
        DrawLabels();
        if (!showBones || nodes.Length == 0) return;
        var byInstance = nodes.GroupBy(node => node.Instance).ToDictionary(group => group.Key, group => group.First());
        var root = Surface(actor.X, actor.Y);
        int focus = Array.FindIndex(chains, chain => chain.Root == selected || chain.Mid == selected || chain.End == selected);
        DrawBoneLines(byInstance, root, focus);
        DrawJoints(focus);
        DrawRootCross(root);
        DrawRingAndBars();
    }
    private void DrawLabels()
    {
        if (legend.Length > 0) DrawString(ThemeDB.FallbackFont, new Vector2(8, Size.Y - 8), legend, HorizontalAlignment.Left, -1, 12, new Color(1f, 1f, 1f, .9f));
        if (playback.Length > 0) DrawString(ThemeDB.FallbackFont, new Vector2(8, 16), playback, HorizontalAlignment.Left, -1, 12, new Color(1f, 1f, 1f, .9f));
    }
    private void DrawBoneLines(Dictionary<int, NativeBone> byInstance, Vector2 root, int focus)
    {
        foreach (var node in nodes)
        {
            var color = BoneColor; float width = 1.5f;
            for (int i = 0; i < chains.Length; i++)
                if (chains[i].Mid == node.Name || chains[i].End == node.Name) { color = ChainColor(i); if (i == focus) width = 3f; }
            if (byInstance.TryGetValue(node.Parent, out var parent)) DrawLine(Surface(parent.X, parent.Y), Surface(node.X, node.Y), color, width, true);
            else DrawLine(root, Surface(node.X, node.Y), RootColor, 1f, true);
        }
    }
    private void DrawJoints(int focus)
    {
        foreach (var node in nodes)
        {
            bool highlight = node.Name == selected;
            bool chainBone = focus >= 0 && (chains[focus].Root == node.Name || chains[focus].Mid == node.Name || chains[focus].End == node.Name);
            var point = Surface(node.X, node.Y);
            if (highlight) DrawCircle(point, SelectedRadius, Colors.Yellow);
            else if (chainBone) DrawCircle(point, JointRadius + 1, ChainColor(focus));
            else DrawCircle(point, JointRadius, Colors.White);
            if (chainBone && chains[focus].End == node.Name) DrawCircle(point, HandleRadius, ChainColor(focus));
            if (showNames) DrawString(ThemeDB.FallbackFont, point + new Vector2(6, -6), node.Name, HorizontalAlignment.Left, -1, 10, Colors.White);
        }
    }
    private void DrawRootCross(Vector2 root)
    {
        DrawLine(root - new Vector2(6, 0), root + new Vector2(6, 0), RootColor, 2);
        DrawLine(root - new Vector2(0, 6), root + new Vector2(0, 6), RootColor, 2);
    }
    private void DrawRingAndBars()
    {
        if (SelectedNode() is not { } focusNode) return;
        var ring = Surface(focusNode.X, focusNode.Y);
        DrawArc(ring, RingRadius, 0, Mathf.Tau, 64, RingColor, 3, true);
        DrawCircle(ring + Direction(ringAngle ?? focusNode.Angle) * RingRadius, BallRadius, BallColor);
        if (BarCenter(0) is { } xBar) DrawBar(xBar, new(1, 0), barOffset?.X ?? 0);
        if (BarCenter(1) is { } yBar) DrawBar(yBar, new(0, 1), barOffset?.Y ?? 0);
    }
    private static Rect2 BarRect(Vector2 center, Vector2 axis) =>
        new(center - axis * BarHalf - new Vector2(Math.Abs(axis.Y), Math.Abs(axis.X)) * BarThickness / 2, new(axis.X == 0 ? BarThickness : BarHalf * 2, axis.X == 0 ? BarHalf * 2 : BarThickness));
    private void DrawBar(Vector2 center, Vector2 axis, float offset)
    {
        DrawRect(BarRect(center, axis), BarColor);
        var knob = center + axis * Mathf.Clamp(offset, -1f, 1f) * BarHalf;
        DrawCircle(knob, BarThickness, KnobColor);
    }
    private static Vector2 Direction(double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new((float)Math.Cos(radians), (float)-Math.Sin(radians));
    }
    private Vector2 Surface(double x, double y)
    {
        var (surfaceX, surfaceY) = MotionViewport.ToSurface(x, y, Size.X, Size.Y, view);
        return new((float)surfaceX, (float)surfaceY);
    }
    private static Color ChainColor(int index) => Color.FromHsv(index * 0.16f, 0.75f, 1f, 0.85f);
}
