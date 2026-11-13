using Godot;

namespace SlackOfDescent;

public partial class NativeMotionOverlay : Control
{
    private const float RingRadius = 72f, BallRadius = 9f, BallHit = 14f, JointRadius = 5f, SelectedRadius = 8f, HandleRadius = 9f, JointHit = 12f;
    private static readonly Color RingColor = new(0.25f, 1f, 0.35f, 0.35f), BallColor = new(1f, 1f, 1f, 0.7f), BoneColor = new(0.85f, 0.85f, 0.9f, 0.6f), RootColor = new(0.3f, 0.9f, 1f, 0.9f);
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
    internal MotionView View => view;
    internal float RingPixelRadius => RingRadius;
    internal bool RingVisible => showBones && SelectedNode() is not null;
    internal double? BallAngle => SelectedNode() is { } node ? ringAngle ?? node.Angle : null;
    internal Vector2? BallPoint => SelectedNode() is { } node ? SurfacePoint(node.X, node.Y) + Direction(ringAngle ?? node.Angle) * RingRadius : null;
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
    internal Vector2 SurfacePoint(double x, double y)
    {
        var (surfaceX, surfaceY) = MotionViewport.ToSurface(x, y, Size.X, Size.Y, view);
        return new((float)surfaceX, (float)surfaceY);
    }
    internal (double X, double Y) WorldPoint(Vector2 surfacePoint) => MotionViewport.ToWorld(surfacePoint.X, surfacePoint.Y, Size.X, Size.Y, view);
    internal string? PickBone(Vector2 surfacePoint)
    {
        if (Size.X <= 0 || Size.Y <= 0 || nodes.Length == 0) return null;
        string? best = null; float bestDistance = JointHit;
        foreach (var node in nodes)
        {
            float distance = SurfacePoint(node.X, node.Y).DistanceTo(surfacePoint);
            if (distance <= bestDistance) { best = node.Name; bestDistance = distance; }
        }
        return best;
    }
    internal string? RingHit(Vector2 surfacePoint) =>
        SelectedNode() is { } node && surfacePoint.DistanceTo(SurfacePoint(node.X, node.Y) + Direction(ringAngle ?? node.Angle) * RingRadius) <= BallHit ? "ball" : null;
    private NativeBone? SelectedNode() => selected is null ? null : nodes.FirstOrDefault(node => node.Name == selected);
    public override void _Draw()
    {
        if (Size.X <= 0 || Size.Y <= 0) return;
        DrawLabels();
        if (!showBones || nodes.Length == 0) return;
        var byInstance = nodes.GroupBy(node => node.Instance).ToDictionary(group => group.Key, group => group.First());
        var root = SurfacePoint(actor.X, actor.Y);
        int focus = Array.FindIndex(chains, chain => chain.Root == selected || chain.Mid == selected || chain.End == selected);
        DrawBoneLines(byInstance, root, focus);
        DrawJoints(focus);
        DrawRootCross(root);
        DrawRing();
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
            if (byInstance.TryGetValue(node.Parent, out var parent)) DrawLine(SurfacePoint(parent.X, parent.Y), SurfacePoint(node.X, node.Y), color, width, true);
            else DrawLine(root, SurfacePoint(node.X, node.Y), RootColor, 1f, true);
        }
    }
    private void DrawJoints(int focus)
    {
        foreach (var node in nodes)
        {
            bool highlight = node.Name == selected;
            bool chainBone = focus >= 0 && (chains[focus].Root == node.Name || chains[focus].Mid == node.Name || chains[focus].End == node.Name);
            var point = SurfacePoint(node.X, node.Y);
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
    private void DrawRing()
    {
        if (SelectedNode() is not { } focusNode) return;
        var ring = SurfacePoint(focusNode.X, focusNode.Y);
        DrawArc(ring, RingRadius, 0, Mathf.Tau, 64, RingColor, 3, true);
        DrawCircle(ring + Direction(ringAngle ?? focusNode.Angle) * RingRadius, BallRadius, BallColor);
    }
    private static Vector2 Direction(double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new((float)Math.Cos(radians), (float)-Math.Sin(radians));
    }
    private static Color ChainColor(int index) => Color.FromHsv(index * 0.16f, 0.75f, 1f, 0.85f);
}
