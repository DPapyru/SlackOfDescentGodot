using System.Globalization;
using System.Text.RegularExpressions;

namespace SlackOfDescent;

public sealed class MotionIkChain
{
    public string Root { get; set; } = "";
    public string Mid { get; set; } = "";
    public string End { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public readonly record struct MotionEntitySlot(string Bone, string Target)
{
    public override string ToString() => Bone + "=" + Target;
}

public readonly record struct MotionSkeletonNode(string Name, int Instance, int Parent, double X, double Y, double Angle, int Process, double LocalX = 0, double LocalY = 0)
{
    public bool IsRoot => Parent < 0;
}

public sealed class MotionSkeletonDescriptor
{
    public string Id { get; }
    public IReadOnlyList<MotionSkeletonNode> Nodes { get; }
    public IReadOnlyDictionary<string, MotionSkeletonNode> ByName { get; }

    public MotionSkeletonDescriptor(string id, IEnumerable<MotionSkeletonNode> nodes)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new InvalidDataException("Skeleton id is required.") : id;
        Nodes = nodes.Where(node => node.Instance >= 0).GroupBy(node => node.Name, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        ByName = Nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        if (Nodes.Count == 0) throw new InvalidDataException("Skeleton has no process nodes.");
        foreach (var node in Nodes)
            if (node.Parent == node.Instance) throw new InvalidDataException("Skeleton contains a self parent: " + node.Name);
    }

    public bool Contains(string name) => ByName.ContainsKey(name);
    public MotionSkeletonNode Node(string name) => ByName.TryGetValue(name, out var node) ? node : throw new InvalidDataException("Skeleton node is missing: " + name);

    public static MotionSkeletonDescriptor FromActor(GmWorld world, GmInstance actor, string? id = null)
    {
        var nodes = new List<MotionSkeletonNode>();
        if (world.Constants.TryGetValue("objPlayerDoll", out var player) && world.IsObject(actor.Object, player.Int))
        {
            foreach (string name in MotionSkeleton.Bones)
                if (NativeCharacterBones.Resolve(world, actor, name) is { } part)
                    nodes.Add(Node(name, part, actor.Id, -1));
            if (nodes.Count > 0) return new MotionSkeletonDescriptor(id ?? world.Globals.ReadOr("Chara", "Nana").ToString(), nodes);
        }
        var seen = new HashSet<int>();
        var usedInstances = new HashSet<int>();
        foreach (var entry in actor.Scope.Values.Keys)
        {
            if (!TryProcessKey(entry, out int process) || !actor.Scope.Values.TryGetValue(entry, out var target)) continue;
            if (!seen.Add(process) || target.Int < 0) continue;
            var candidates = world.Select(target.Int, new GmContext(actor)).Where(i => i.Id != actor.Id && !usedInstances.Contains(i.Id)).ToArray();
            var part = candidates.FirstOrDefault(i => i.Scope.ReadOr("parent", actor.Id).Int == actor.Id) ?? candidates.FirstOrDefault();
            if (part is null) continue;
            usedInstances.Add(part.Id);
            nodes.Add(Node(MotionSkeleton.Process(process), part, actor.Id, process));
        }
        if (nodes.Count == 0) throw new InvalidDataException("目标实例没有可编辑的 Process/parent 骨架。");
        string resource = world.Objects.TryGetValue(actor.Object, out var definition) ? definition.GetProperty("name").GetString() ?? actor.Object.ToString(CultureInfo.InvariantCulture) : actor.Object.ToString(CultureInfo.InvariantCulture);
        return new MotionSkeletonDescriptor(id ?? "legacy:" + resource, nodes);

        static MotionSkeletonNode Node(string name, GmInstance part, int root, int process)
        {
            double localX = 0, localY = 0;
            if (part.Scope.Values.TryGetValue("PartPosDir", out var direction) && part.Scope.Values.TryGetValue("PartPosDis", out var distance))
            {
                double radians = direction.Number * Math.PI / 180;
                localX = Math.Cos(radians) * distance.Number;
                localY = -Math.Sin(radians) * distance.Number;
            }
            return new(name, part.Id, part.Scope.ReadOr("parent", root).Int, part.Scope.ReadOr("x").Number, part.Scope.ReadOr("y").Number, part.Scope.ReadOr("image_angle").Number, process, localX, localY);
        }

        static bool TryProcessKey(string key, out int process)
        {
            process = -1;
            if (key == "Process") { process = 0; return true; }
            if (!key.StartsWith("Process[", StringComparison.Ordinal) || !key.EndsWith(']')) return false;
            return int.TryParse(key.AsSpan(8, key.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out process) && process >= 0;
        }
    }
}

public readonly record struct MotionIkResult(
    bool Success,
    bool Clamped,
    double RootAngle,
    double MidAngle,
    double EndAngle,
    double Reach,
    string Reason)
{
    public double TargetX { get; init; }
    public double TargetY { get; init; }
    public static MotionIkResult Invalid(string reason) => new(false, false, 0, 0, 0, 0, reason);
}

public static class MotionIk
{
    public static MotionIkResult Solve(
        double rootX, double rootY, double midX, double midY, double endX, double endY,
        double targetX, double targetY, bool bendPositive = true)
    {
        double first = Math.Sqrt((midX - rootX) * (midX - rootX) + (midY - rootY) * (midY - rootY));
        double second = Math.Sqrt((endX - midX) * (endX - midX) + (endY - midY) * (endY - midY));
        if (!double.IsFinite(first) || !double.IsFinite(second) || first <= 1e-9 || second <= 1e-9)
            return MotionIkResult.Invalid("IK 链段长度必须大于零。");
        double dx = targetX - rootX, dy = targetY - rootY;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(distance)) return MotionIkResult.Invalid("IK 目标必须是有限坐标。");
        double minimum = Math.Abs(first - second), maximum = first + second;
        double reach = Math.Clamp(distance, minimum, maximum);
        bool clamped = Math.Abs(reach - distance) > 1e-8;
        if (reach <= 1e-9) return MotionIkResult.Invalid("目标与链根重合时没有唯一弯曲方向。");
        double direction = Math.Atan2(dy, dx);
        double rootCos = Math.Clamp((first * first + reach * reach - second * second) / (2 * first * reach), -1, 1);
        double midCos = Math.Clamp((first * first + second * second - reach * reach) / (2 * first * second), -1, 1);
        double rootOffset = Math.Acos(rootCos) * (bendPositive ? 1 : -1);
        double elbow = Math.PI - Math.Acos(midCos);
        double rootAngle = direction + rootOffset;
        double midAngle = rootAngle + (bendPositive ? -elbow : elbow);
        return new(true, clamped, Degrees(rootAngle), Degrees(midAngle), Degrees(midAngle), reach,
            clamped ? "目标超出链长，已夹到可达范围。" : "") { TargetX = rootX + Math.Cos(direction) * reach, TargetY = rootY + Math.Sin(direction) * reach };
    }

    public static MotionIkResult Solve((double X, double Y) root, (double X, double Y) mid, (double X, double Y) end, (double X, double Y) target, bool bendPositive = true) =>
        Solve(root.X, root.Y, mid.X, mid.Y, end.X, end.Y, target.X, target.Y, bendPositive);

    private static double Degrees(double radians)
    {
        double value = radians * 180 / Math.PI;
        value %= 360;
        return value < -180 ? value + 360 : value > 180 ? value - 360 : value;
    }
}

public static class MotionIkPresets
{
    public static readonly IReadOnlyDictionary<string, (string Root, string Mid, string End)> Limbs =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            ["left_hand"] = ("BODY_TOP", "HAND1_ABOVE", "HAND2_ABOVE"),
            ["right_hand"] = ("BODY_TOP", "HAND1_BELOW", "HAND2_BELOW"),
            ["left_foot"] = ("BODY_BOTTOM", "FOOT1_ABOVE", "FOOT2_ABOVE"),
            ["right_foot"] = ("BODY_BOTTOM", "FOOT1_BELOW", "FOOT2_BELOW")
        };

    public static IReadOnlyList<MotionIkChain> For(MotionGraph graph) =>
        graph.IkChains is not null ? graph.IkChains : MotionSkeleton.IsLegacy(graph.Skeleton) ? [] : Limbs.Values.Select(preset => new MotionIkChain { Root = preset.Root, Mid = preset.Mid, End = preset.End }).ToArray();

    public static void Materialize(MotionGraph graph) => graph.IkChains ??= For(graph).ToList();
}

public static class MotionRigCoordinates
{
    public static (double X, double Y) ToLocal(double worldX, double worldY, double parentX, double parentY, double parentAngle, double direction = 1)
    {
        if (!double.IsFinite(worldX) || !double.IsFinite(worldY) || !double.IsFinite(parentX) || !double.IsFinite(parentY) || !double.IsFinite(parentAngle) || !double.IsFinite(direction) || direction == 0)
            throw new InvalidDataException("坐标变换需要有限值和非零镜像方向。");
        double radians = parentAngle * Math.PI / 180, dx = (worldX - parentX) * Math.Sign(direction), dy = worldY - parentY;
        return (dx * Math.Cos(radians) - dy * Math.Sin(radians), dx * Math.Sin(radians) + dy * Math.Cos(radians));
    }

    public static (double X, double Y) Rotate(double x, double y, double angle)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(angle)) throw new InvalidDataException("旋转需要有限数值。");
        double radians = angle * Math.PI / 180;
        return (x * Math.Cos(radians) + y * Math.Sin(radians), -x * Math.Sin(radians) + y * Math.Cos(radians));
    }
}

public readonly record struct MotionIkFrame(double ParentAngle, double RootX, double RootY);

public static class MotionPoseEditor
{
    public static MotionIkResult Drag(MotionGraph graph, string clipId, string bone, double frame, double targetX, double targetY, bool? bendPositive = null, bool forceFk = false, MotionIkFrame? ikFrame = null)
    {
        var clip = graph.Clips.SingleOrDefault(item => item.Id == clipId) ?? throw new InvalidDataException("Motion clip is missing: " + clipId);
        if (!double.IsFinite(frame) || !double.IsFinite(targetX) || !double.IsFinite(targetY)) return MotionIkResult.Invalid("拖动坐标必须是有限数值。");
        if (bone.Length == 0) return MotionIkResult.Invalid("请选择骨骼节点。");
        var chain = MotionIkPresets.For(graph).FirstOrDefault(item => item.Enabled && item.End == bone);
        if (chain is not null && !forceFk)
        {
            var sample = clip.Sample(frame);
            var root = sample.GetValueOrDefault(chain.Root);
            var mid = sample.GetValueOrDefault(chain.Mid);
            var end = sample.GetValueOrDefault(chain.End);
            double parentAngle = ikFrame?.ParentAngle ?? 0;
            double rootX = ikFrame?.RootX ?? root.X, rootY = ikFrame?.RootY ?? root.Y;
            var midOffset = MotionRigCoordinates.Rotate(mid.X, mid.Y, root.Angle + parentAngle);
            double midX = rootX + midOffset.X, midY = rootY + midOffset.Y;
            var endOffset = MotionRigCoordinates.Rotate(end.X, end.Y, root.Angle + mid.Angle + parentAngle);
            double endX = midX + endOffset.X, endY = midY + endOffset.Y;
            bool bend = bendPositive ?? Bend(graph, clipId, bone, frame, ikFrame) ?? true;
            var result = MotionIk.Solve(rootX, rootY, midX, midY, endX, endY, targetX, targetY, bend);
            if (!result.Success) return result;
            double firstLength = Math.Sqrt(mid.X * mid.X + mid.Y * mid.Y), secondLength = Math.Sqrt(end.X * end.X + end.Y * end.Y);
            SetKey(clip, chain.Root, frame, new MotionPose(root.X, root.Y, Normalize(-result.RootAngle - parentAngle), root.ScaleX, root.ScaleY, root.Picture));
            SetKey(clip, chain.Mid, frame, new MotionPose(firstLength, 0, Normalize(result.RootAngle - result.MidAngle), mid.ScaleX, mid.ScaleY, mid.Picture));
            SetKey(clip, chain.End, frame, new MotionPose(secondLength, 0, end.Angle, end.ScaleX, end.ScaleY, end.Picture));
            return result;
        }
        var current = clip.Sample(frame).GetValueOrDefault(bone);
        double length = Math.Sqrt(current.X * current.X + current.Y * current.Y);
        if (bone == "root" || bone == "__root__")
        {
            SetRootKey(clip, frame, new MotionPose(targetX, targetY, current.Angle, current.ScaleX, current.ScaleY, current.Picture));
            return new(true, false, current.Angle, current.Angle, current.Angle, 0, "");
        }
        if (length <= 1e-9) return MotionIkResult.Invalid("未绑定 IK 的节点长度为零，请先设置参考偏移。");
        double angle = Math.Atan2(-targetY, targetX) * 180 / Math.PI;
        SetKey(clip, bone, frame, new MotionPose(Math.Cos(angle * Math.PI / 180) * length, -Math.Sin(angle * Math.PI / 180) * length, angle, current.ScaleX, current.ScaleY, current.Picture));
        return new(true, false, angle, angle, angle, length, "");
    }

    public static bool? Bend(MotionGraph graph, string clipId, string bone, double frame, MotionIkFrame? ikFrame = null)
    {
        var clip = graph.Clips.SingleOrDefault(item => item.Id == clipId);
        var chain = MotionIkPresets.For(graph).FirstOrDefault(item => item.Enabled && item.End == bone);
        if (clip is null || chain is null) return null;
        var sample = clip.Sample(frame);
        var root = sample.GetValueOrDefault(chain.Root);
        var mid = sample.GetValueOrDefault(chain.Mid);
        var end = sample.GetValueOrDefault(chain.End);
        double parentAngle = ikFrame?.ParentAngle ?? 0;
        double rootX = ikFrame?.RootX ?? root.X, rootY = ikFrame?.RootY ?? root.Y;
        var midOffset = MotionRigCoordinates.Rotate(mid.X, mid.Y, root.Angle + parentAngle);
        double midX = rootX + midOffset.X, midY = rootY + midOffset.Y;
        var endOffset = MotionRigCoordinates.Rotate(end.X, end.Y, root.Angle + mid.Angle + parentAngle);
        double endX = midX + endOffset.X, endY = midY + endOffset.Y;
        return (midX - rootX) * (endY - midY) - (midY - rootY) * (endX - midX) >= 0;
    }

    public static void Translate(MotionGraph graph, string clipId, string bone, double frame, double x, double y)
    {
        var clip = graph.Clips.SingleOrDefault(item => item.Id == clipId) ?? throw new InvalidDataException("Motion clip is missing: " + clipId);
        if (!double.IsFinite(frame) || !double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidDataException("拖动坐标必须是有限数值。");
        if (bone.Length == 0) throw new InvalidDataException("请选择骨骼节点。");
        var current = clip.Sample(frame).GetValueOrDefault(bone);
        SetKey(clip, bone is "root" or "__root__" ? null : bone, frame, new MotionPose(x, y, current.Angle, current.ScaleX, current.ScaleY, current.Picture));
    }

    public static void Rotate(MotionGraph graph, string clipId, string bone, double frame, double angle)
    {
        var clip = graph.Clips.SingleOrDefault(item => item.Id == clipId) ?? throw new InvalidDataException("Motion clip is missing: " + clipId);
        if (!double.IsFinite(frame) || !double.IsFinite(angle)) throw new InvalidDataException("旋转角度必须是有限数值。");
        if (bone.Length == 0) throw new InvalidDataException("请选择骨骼节点。");
        var current = bone is "root" or "__root__" ? clip.RootPose(frame) : clip.Sample(frame).GetValueOrDefault(bone);
        SetKey(clip, bone is "root" or "__root__" ? null : bone, frame, new MotionPose(current.X, current.Y, Normalize(angle), current.ScaleX, current.ScaleY, current.Picture));
    }

    private static void SetRootKey(MotionClip clip, double frame, MotionPose pose)
    {
        SetKey(clip, null, frame, pose);
    }

    private static void SetKey(MotionClip clip, string? bone, double frame, MotionPose pose)
    {
        var keys = bone is null ? clip.Root : clip.Tracks.FirstOrDefault(track => track.Bone == bone)?.Keys;
        if (keys is null)
        {
            if (bone is null) keys = clip.Root;
            else { var track = new MotionTrack { Bone = bone }; clip.Tracks.Add(track); keys = track.Keys; }
        }
        keys.RemoveAll(key => Math.Abs(key.Frame - frame) < 1e-9);
        keys.Add(new MotionKey { Frame = frame, X = pose.X, Y = pose.Y, Angle = pose.Angle, ScaleX = pose.ScaleX, ScaleY = pose.ScaleY, Picture = pose.Picture });
        keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
    }

    private static double Normalize(double angle)
    {
        angle %= 360;
        return angle <= -180 ? angle + 360 : angle > 180 ? angle - 360 : angle;
    }
}

public static class MotionSkeleton
{
    public static readonly string[] Bones = ["BODY_BOTTOM", "BODY_TOP", "HEAD", "HAND1_ABOVE", "HAND2_ABOVE", "HAND3_ABOVE", "HAND1_BELOW", "HAND2_BELOW", "HAND3_BELOW", "FOOT1_ABOVE", "FOOT2_ABOVE", "FOOT3_ABOVE", "FOOT1_BELOW", "FOOT2_BELOW", "FOOT3_BELOW"];
    private static readonly Regex ProcessName = new("^process:[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static bool IsNodeName(string name) => Bones.Contains(name, StringComparer.Ordinal) || ProcessName.IsMatch(name);
    public static string Process(int index) => "process:" + index.ToString(CultureInfo.InvariantCulture);
    public static bool TryProcess(string name, out int index)
    {
        if (!name.StartsWith("process:", StringComparison.Ordinal) || !int.TryParse(name[8..], NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 0) { index = -1; return false; }
        return true;
    }
    public static bool IsLegacy(string skeleton) => skeleton.StartsWith("legacy:", StringComparison.Ordinal) && skeleton.Length > 7;
}
