using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public readonly record struct BoneLocal(double Angle, double ScaleX, double ScaleY, int Picture, double ImageIndex)
{
    public static BoneLocal Read(GmInstance bone) => new(
        bone.Scope.ReadOr("Angle").Number,
        bone.Scope.ReadOr("ScaleX", 1).Number,
        bone.Scope.ReadOr("ScaleY", 1).Number,
        bone.Scope.ReadOr("PartPicture").Int,
        bone.Scope.ReadOr("image_index").Number);
    public void Write(GmInstance bone)
    {
        bone.Scope.Write("Angle", Angle);
        bone.Scope.Write("ScaleX", ScaleX);
        bone.Scope.Write("ScaleY", ScaleY);
        bone.Scope.Write("PartPicture", Picture);
        bone.Scope.Write("image_index", ImageIndex);
    }
}

public static class BindPose
{
    public static Dictionary<string, BoneLocal> Snapshot(IEnumerable<(string Name, GmInstance Bone)> bones)
    {
        var pose = new Dictionary<string, BoneLocal>(StringComparer.Ordinal);
        foreach (var (name, bone) in bones) pose[name] = BoneLocal.Read(bone);
        return pose;
    }
    public static void Restore(IEnumerable<(string Name, GmInstance Bone, double ParentAngle)> bones, IReadOnlyDictionary<string, BoneLocal> pose)
    {
        foreach (var (name, bone, parentAngle) in bones)
            if (pose.TryGetValue(name, out var local))
            {
                local.Write(bone);
                bone.Scope.Write("image_angle", local.Angle + parentAngle);
            }
    }
    public static string[] Union(params IEnumerable<string>[] sets)
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets) union.UnionWith(set);
        return union.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }
}
