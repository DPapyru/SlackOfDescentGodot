using System;
using System.Collections.Generic;

namespace SinkKnee;

public sealed class MotionPatch
{
    public string Id { get; set; } = "";
    public string Base { get; set; } = "";
    public int Duration { get; set; }
    public List<MotionPatchFrame> Frames { get; set; } = new();
}

public sealed class MotionPatchFrame
{
    public int T { get; set; }
    public Dictionary<string, MotionPatchBone> Bones { get; set; } = new(StringComparer.Ordinal);
}

public readonly record struct MotionPatchBone(double X, double Y, double Angle);

public static class MotionPatchParser
{
    public static MotionPatch Load(string path) => throw new NotImplementedException("动作解析器留给 #12。");
}
