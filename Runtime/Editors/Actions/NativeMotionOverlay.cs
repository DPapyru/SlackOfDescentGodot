using Godot;

namespace SlackOfDescent;

public partial class NativeMotionOverlay : Control
{
    private const float RingRadius = 54f, ArrowRadius = 76f, BallRadius = 7f;
    private static readonly Color RingColor = new(0.25f, 1f, 0.35f, 0.35f), BallColor = new(1f, 1f, 1f, 0.7f), ArrowColor = new(0.6f, 1f, 0.7f, 0.8f);
    private NativeBone[] nodes = [];
    private MotionIkChain[] chains = [];
    private MotionView view;
    private Vector2 actor;
    private string? selected;
    private double? ringAngle;
    private bool showBones = true, showNames;
    private string legend = "", playback = "";
    public bool ShowBones { get => showBones; set { showBones = value; QueueRedraw(); } }
    public bool ShowNames { get => showNames; set { showNames = value; QueueRedraw(); } }
    public string Legend { get => legend; set { legend = value; QueueRedraw(); } }
    public string Playback { get => playback; set { playback = value; QueueRedraw(); } }
    private static readonly Color BoneColor = new(0.85f, 0.85f, 0.9f, 0.6f);
    private static readonly Color RootColor = new(0.3f, 0.9f, 1f, 0.9f);
    internal float RingPixelRadius => RingRadius;
    internal bool RingVisible => showBones && selected is not null && nodes.Any(node => node.Name == selected);
    internal double? BallAngle
    {
        get
        {
            if (selected is null) return null;
            var node = nodes.FirstOrDefault(item => item.Name == selected);
            return node is null ? null : ringAngle ?? node.Angle;
        }
    }
    internal Vector2? BallPoint
    {
        get
        {
            if (selected is null) return null;
            var node = nodes.FirstOrDefault(item => item.Name == selected);
            return node is null ? null : Surface(node.X, node.Y) + Direction(ringAngle ?? node.Angle) * RingRadius;
        }
    }
    internal IReadOnlyList<Vector2> ArrowPoints
    {
        get
        {
            var points = new Vector2[8];
            if (selected is null || nodes.FirstOrDefault(item => item.Name == selected) is not { } node) return points;
            var ring = Surface(node.X, node.Y);
            for (int i = 0; i < 8; i++) points[i] = ring + Direction(90 - i * 45) * ArrowRadius;
            return points;
        }
    }
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
    internal string? RingHit(Vector2 surfacePoint)
    {
        if (selected is null || nodes.FirstOrDefault(node => node.Name == selected) is not { } node) return null;
        var center = Surface(node.X, node.Y);
        if (surfacePoint.DistanceTo(center + Direction(ringAngle ?? node.Angle) * RingRadius) <= 10) return "ball";
        for (int i = 0; i < 8; i++) if (surfacePoint.DistanceTo(center + Direction(90 - i * 45) * ArrowRadius) <= 10) return "arrow:" + i;
        return null;
    }
    public override void _Draw()
    {
        if (Size.X <= 0 || Size.Y <= 0) return;
        if (legend.Length > 0) DrawString(ThemeDB.FallbackFont, new Vector2(8, Size.Y - 8), legend, HorizontalAlignment.Left, -1, 12, new Color(1f, 1f, 1f, .9f));
        if (playback.Length > 0) DrawString(ThemeDB.FallbackFont, new Vector2(8, 16), playback, HorizontalAlignment.Left, -1, 12, new Color(1f, 1f, 1f, .9f));
        if (!showBones || nodes.Length == 0) return;
        var byInstance = nodes.GroupBy(node => node.Instance).ToDictionary(group => group.Key, group => group.First());
        var root = Surface(actor.X, actor.Y);
        int focus = Array.FindIndex(chains, chain => chain.Root == selected || chain.Mid == selected || chain.End == selected);
        foreach (var node in nodes)
        {
            var color = BoneColor; float width = 1.5f;
            for (int i = 0; i < chains.Length; i++)
                if (chains[i].Mid == node.Name || chains[i].End == node.Name) { color = ChainColor(i); if (i == focus) width = 3f; }
            if (byInstance.TryGetValue(node.Parent, out var parent)) DrawLine(Surface(parent.X, parent.Y), Surface(node.X, node.Y), color, width, true);
            else DrawLine(root, Surface(node.X, node.Y), RootColor, 1f, true);
        }
        foreach (var node in nodes)
        {
            bool highlight = node.Name == selected;
            bool chainBone = focus >= 0 && (chains[focus].Root == node.Name || chains[focus].Mid == node.Name || chains[focus].End == node.Name);
            var point = Surface(node.X, node.Y);
            if (highlight) DrawCircle(point, 5, Colors.Yellow);
            else if (chainBone) DrawCircle(point, 5, ChainColor(focus));
            else DrawCircle(point, 3, Colors.White);
            if (chainBone && chains[focus].End == node.Name) DrawCircle(point, 6, ChainColor(focus));
            if (showNames) DrawString(ThemeDB.FallbackFont, point + new Vector2(6, -6), node.Name, HorizontalAlignment.Left, -1, 10, Colors.White);
        }
        DrawLine(root - new Vector2(6, 0), root + new Vector2(6, 0), RootColor, 2);
        DrawLine(root - new Vector2(0, 6), root + new Vector2(0, 6), RootColor, 2);
        if (selected is null || nodes.FirstOrDefault(node => node.Name == selected) is not { } focusNode) return;
        var ring = Surface(focusNode.X, focusNode.Y);
        DrawArc(ring, RingRadius, 0, Mathf.Tau, 64, RingColor, 3, true);
        DrawCircle(ring + Direction(ringAngle ?? focusNode.Angle) * RingRadius, BallRadius, BallColor);
        for (int i = 0; i < 8; i++)
        {
            var direction = Direction(90 - i * 45); var across = new Vector2(-direction.Y, direction.X);
            var tip = ring + direction * (ArrowRadius + 7);
            var left = ring + direction * (ArrowRadius - 3) + across * 5;
            var right = ring + direction * (ArrowRadius - 3) - across * 5;
            DrawColoredPolygon([tip, left, right], ArrowColor);
        }
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
