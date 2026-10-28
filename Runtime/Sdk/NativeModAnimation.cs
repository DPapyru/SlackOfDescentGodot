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
