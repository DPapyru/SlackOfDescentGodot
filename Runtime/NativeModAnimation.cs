namespace SlackOfDescent;

public readonly record struct NativePartPose(double X, double Y, double Angle, double ScaleX = 1, double ScaleY = 1)
{
    public static NativePartPose Lerp(NativePartPose from, NativePartPose to, double weight)
    {
        double t = Math.Clamp(weight, 0, 1), turn = (to.Angle - from.Angle + 540) % 360 - 180;
        return new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t, from.Angle + turn * t,
            from.ScaleX + (to.ScaleX - from.ScaleX) * t, from.ScaleY + (to.ScaleY - from.ScaleY) * t);
    }
    public (double X, double Y) Transform(double x, double y)
    {
        double radians = Angle * Math.PI / 180, px = x * ScaleX, py = y * ScaleY;
        return (X + px * Math.Cos(radians) + py * Math.Sin(radians), Y - px * Math.Sin(radians) + py * Math.Cos(radians));
    }
}

public sealed record NativeAnimationFrame(double Time, NativePartPose Pose);
public sealed class NativeAnimationClip
{
    public IReadOnlyList<NativeAnimationFrame> Frames { get; }
    public double Duration => Frames.Count == 0 ? 0 : Frames[^1].Time;
    public NativeAnimationClip(params NativeAnimationFrame[] frames)
    {
        if (frames.Any(f => f.Time < 0 || !double.IsFinite(f.Time))) throw new ArgumentOutOfRangeException(nameof(frames));
        Frames = frames.OrderBy(f => f.Time).ToArray();
    }
    public NativePartPose Sample(double time)
    {
        if (Frames.Count == 0) return new(0, 0, 0);
        if (time <= Frames[0].Time) return Frames[0].Pose;
        for (int i = 1; i < Frames.Count; i++)
            if (time <= Frames[i].Time) return NativePartPose.Lerp(Frames[i - 1].Pose, Frames[i].Pose, (time - Frames[i - 1].Time) / (Frames[i].Time - Frames[i - 1].Time));
        return Frames[^1].Pose;
    }
    public NativeAnimationClip Then(NativeAnimationClip next) => new(Frames.Concat(next.Frames.Select(f => new NativeAnimationFrame(f.Time + Duration, f.Pose))).ToArray());
}

public static class NativeAnimationService
{
    public static NativePartPose Pose(ActorContext part) => new(part.X, part.Y, part.Read("image_angle") * part.Instance.Scope.ReadOr("Direction", 1).Number, part.Instance.Scope.ReadOr("ScaleX", 1).Number * part.Instance.Scope.ReadOr("Direction", 1).Number, part.Instance.Scope.ReadOr("ScaleY", 1).Number);
    public static void Apply(ActorContext actor, string name, NativePartPose pose)
    {
        var part = actor.Part(name) ?? throw new InvalidDataException("角色部位不存在：" + name);
        part.X = pose.X; part.Y = pose.Y; part.Write("image_angle", pose.Angle / part.Instance.Scope.ReadOr("Direction", 1).Number); part.Write("ScaleX", pose.ScaleX / part.Instance.Scope.ReadOr("Direction", 1).Number); part.Write("ScaleY", pose.ScaleY);
    }
    public static NativePartPose Mount(ActorContext actor, string partName, string resource, string id, double originX, double originY, double localX = 0, double localY = 0, double angleOffset = 0)
    {
        var part = actor.Part(partName) ?? actor;
        var pose = Pose(part); var point = pose.Transform(localX, localY);
        pose = pose with { X = point.X, Y = point.Y, Angle = pose.Angle + angleOffset };
        actor.Services.Presentation.Sprite(actor.Id, id, resource, pose.X, pose.Y, pose.Angle, pose.ScaleX, pose.ScaleY, originX, originY, part.Read("depth") - 1);
        return pose;
    }
}
