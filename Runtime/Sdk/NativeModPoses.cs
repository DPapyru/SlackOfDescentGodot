using System;
using System.Collections.Generic;
using System.Linq;

namespace SlackOfDescent;

public sealed class NativePoseService(NativeModServices services)
{
    public sealed record BoneState(string Bone, double X, double Y, double ImageAngle, double Angle, double ScaleX, double ScaleY, int Picture, double ImageIndex, double Direction);

    public sealed record Snapshot(int Actor, IReadOnlyList<BoneState> Bones)
    {
        public BoneState? Find(string bone) => Bones.FirstOrDefault(item => item.Bone == bone);
    }

    public Snapshot? Capture(ActorContext actor, IEnumerable<string> bones)
    {
        List<BoneState> captured = [];
        foreach (string name in bones)
        {
            if (Part(actor, name) is not { } part) continue;
            captured.Add(new(name, part.Scope.ReadOr("x").Number, part.Scope.ReadOr("y").Number, part.Scope.ReadOr("image_angle").Number, part.Scope.ReadOr("Angle").Number,
                part.Scope.ReadOr("ScaleX", 1).Number, part.Scope.ReadOr("ScaleY", 1).Number, part.Scope.ReadOr("PartPicture").Int, part.Scope.ReadOr("image_index").Number, part.Scope.ReadOr("Direction", 1).Number));
        }
        return captured.Count == 0 ? null : new(actor.Id, captured);
    }

    public void Apply(ActorContext actor, string bone, MotionPose pose)
    {
        if (Part(actor, bone) is not { } part) return;
        double direction = actor.Instance.Scope.ReadOr("Direction", 1).Number;
        int parentId = part.Scope.ReadOr("parent", actor.Id).Int;
        GmInstance parent = parentId == actor.Id ? actor.Instance : actor.World.Instances.FirstOrDefault(candidate => candidate.Id == parentId && !candidate.Destroyed) ?? actor.Instance;
        bool root = ReferenceEquals(parent, actor.Instance);
        double parentX = root ? actor.X : parent.Scope.ReadOr("x").Number;
        double parentY = root ? actor.Y : parent.Scope.ReadOr("y").Number;
        double parentAngle = root ? actor.Read("image_angle") : parent.Scope.ReadOr("image_angle").Number;
        double radians = parentAngle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        actor.World.Write(part, "x", parentX + (pose.X * cos + pose.Y * sin) * direction);
        actor.World.Write(part, "y", parentY - pose.X * sin + pose.Y * cos);
        part.Scope.Write("Direction", direction);
        part.Scope.Write("image_angle", pose.Angle + parentAngle);
        part.Scope.Write("Angle", pose.Angle);
        part.Scope.Write("ScaleX", pose.ScaleX);
        part.Scope.Write("ScaleY", pose.ScaleY);
        part.Scope.Write("PartPicture", pose.Picture);
        part.Scope.Write("image_index", pose.Picture);
    }

    public void Restore(Snapshot snapshot, string? bone = null)
    {
        if (services.Instances.Find(snapshot.Actor) is not { } actor) return;
        foreach (BoneState state in snapshot.Bones)
        {
            if (bone is not null && state.Bone != bone) continue;
            if (Part(actor, state.Bone) is not { } part) continue;
            actor.World.Write(part, "x", state.X);
            actor.World.Write(part, "y", state.Y);
            part.Scope.Write("image_angle", state.ImageAngle);
            part.Scope.Write("Angle", state.Angle);
            part.Scope.Write("ScaleX", state.ScaleX);
            part.Scope.Write("ScaleY", state.ScaleY);
            part.Scope.Write("PartPicture", state.Picture);
            part.Scope.Write("image_index", state.ImageIndex);
            part.Scope.Write("Direction", state.Direction);
        }
    }

    private static GmInstance? Part(ActorContext actor, string name) => NativeCharacterBones.Resolve(actor.World, actor.Instance, name);
}
