namespace SlackOfDescent;

public enum MotionTargetKind { PlayerPreset, PlayerCharacter, Entity }

public readonly record struct MotionProjectTarget(MotionTargetKind Kind, string Id)
{
    public string Skeleton(string basePreset) => Kind switch
    {
        MotionTargetKind.PlayerPreset => Id,
        MotionTargetKind.PlayerCharacter => basePreset,
        _ => "legacy:" + Id
    };
}

public sealed class MotionProject(MotionProjectTarget target, MotionGraphDocument document)
{
    public MotionProjectTarget Target { get; set; } = target;
    public MotionGraphDocument Document { get; set; } = document;
    public string? Path { get; set; }
    public int ClipIndex { get; set; }
    public double Frame { get; set; }
    public bool Dirty => Document.Dirty;
}
