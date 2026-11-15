using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public enum BindPart { Hands, HandAbove, HandBelow, Feet, Body }

public sealed class ConstraintService
{
    private const string HandlerId = "ropebonds.constraint";
    internal readonly record struct BoneLocal(double Angle, double ScaleX, double ScaleY, int Picture, double ImageIndex, double PX = 0, double PY = 0)
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
    public static readonly string[] HandAbove = ["HAND1_ABOVE", "HAND2_ABOVE", "HAND3_ABOVE"];
    public static readonly string[] HandBelow = ["HAND1_BELOW", "HAND2_BELOW", "HAND3_BELOW"];
    public static readonly string[] Hands = [.. HandAbove, .. HandBelow];
    public static readonly string[] Feet = ["FOOT1_ABOVE", "FOOT2_ABOVE", "FOOT3_ABOVE", "FOOT1_BELOW", "FOOT2_BELOW", "FOOT3_BELOW"];
    public static readonly string[] Body = ["BODY_BOTTOM", "BODY_TOP", "HEAD"];
    public static IReadOnlyList<string> Of(BindPart part) => part switch
    {
        BindPart.Hands => Hands,
        BindPart.HandAbove => HandAbove,
        BindPart.HandBelow => HandBelow,
        BindPart.Feet => Feet,
        _ => Body
    };
    internal static void Validate()
    {
        foreach (string name in HandAbove.Concat(HandBelow).Concat(Feet).Concat(Body))
            if (Array.IndexOf(NativeCharacterBones.Order, name) < 0) throw new InvalidOperationException("Unknown bone name: " + name);
    }
    internal static string[] Union(params IEnumerable<string>[] sets)
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets) union.UnionWith(set);
        return union.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }
    internal static Dictionary<string, BoneLocal> Snapshot(IEnumerable<(string Name, GmInstance Bone)> bones)
    {
        var pose = new Dictionary<string, BoneLocal>(StringComparer.Ordinal);
        foreach (var (name, bone) in bones) pose[name] = BoneLocal.Read(bone);
        return pose;
    }
    internal static string[] RemoveParts(HashSet<string> bones, Dictionary<string, BoneLocal> pose, IEnumerable<string> names)
    {
        var removed = new List<string>();
        foreach (string name in names)
        {
            if (!bones.Remove(name)) continue;
            pose.Remove(name);
            removed.Add(name);
        }
        return removed.ToArray();
    }
    private readonly Dictionary<int, (ActorContext Actor, List<(string Name, GmInstance Bone, GmInstance? Parent)> Parts, Dictionary<string, BoneLocal> Pose)> entries = new();
    public void Attach(ModContext context)
    {
        Validate();
        context.Subscribe(HandlerId, NativeEventNames.StepEnd, NativeEventPhase.After, _ => Tick());
    }
    public bool Bind(ActorContext actor, params BindPart[] parts)
    {
        if (actor.Instance.Destroyed) return false;
        string[] names = Union(parts.SelectMany(part => Of(part)));
        if (names.Length == 0) return false;
        var byId = Instances(actor);
        if (!entries.TryGetValue(actor.Id, out var entry))
        {
            entry = (actor, new List<(string Name, GmInstance Bone, GmInstance? Parent)>(), new Dictionary<string, BoneLocal>(StringComparer.Ordinal));
            entries[actor.Id] = entry;
        }
        double direction = Direction(actor);
        foreach (string name in names)
        {
            if (entry.Pose.ContainsKey(name)) continue;
            int id = actor.Instance.Scope.ReadOr(name, -4).Int;
            if (!byId.TryGetValue(id, out var bone)) continue;
            int parentId = bone.Scope.ReadOr("parent", actor.Id).Int;
            GmInstance? parent = parentId == actor.Id ? actor.Instance : byId.GetValueOrDefault(parentId);
            if (parent is null || parent.Destroyed)
            {
                Console.Error.WriteLine("ROPEBONDS parent missing, bone not constrained: " + name);
                continue;
            }
            (double px, double py) = Offset(bone, parent, actor, direction);
            entry.Pose[name] = new BoneLocal(
                bone.Scope.ReadOr("Angle").Number,
                bone.Scope.ReadOr("ScaleX", 1).Number,
                bone.Scope.ReadOr("ScaleY", 1).Number,
                bone.Scope.ReadOr("PartPicture").Int,
                bone.Scope.ReadOr("image_index").Number,
                px, py);
            entry.Parts.Add((name, bone, parent));
        }
        if (entry.Parts.Count == 0) { entries.Remove(actor.Id); return false; }
        SortByDepth(entry.Parts);
        return true;
    }
    public bool Release(ActorContext actor)
    {
        if (!entries.Remove(actor.Id, out var entry)) return false;
        Restore(entry, Direction(actor));
        return true;
    }
    public bool Release(ActorContext actor, params BindPart[] parts)
    {
        if (!entries.TryGetValue(actor.Id, out var entry)) return false;
        var released = new HashSet<string>(Union(parts.SelectMany(part => Of(part))), StringComparer.Ordinal);
        double direction = Direction(actor);
        foreach (var part in entry.Parts.ToArray())
        {
            if (!released.Contains(part.Name) || !entry.Pose.TryGetValue(part.Name, out var local)) continue;
            if (part.Parent is null || part.Parent.Destroyed)
                Console.Error.WriteLine("ROPEBONDS parent missing, bone released without restore: " + part.Name);
            else
                Apply(part.Bone, part.Parent, local, entry.Actor, direction);
        }
        entry.Parts.RemoveAll(part => released.Contains(part.Name));
        foreach (string name in released) entry.Pose.Remove(name);
        if (entry.Parts.Count == 0) entries.Remove(actor.Id);
        return true;
    }
    public void ReleaseAll()
    {
        foreach (var entry in entries.Values.ToArray()) Restore(entry, Direction(entry.Actor));
        entries.Clear();
    }
    public bool IsBound(int actorId) => entries.ContainsKey(actorId);
    private void Tick()
    {
        if (entries.Count == 0) return;
        foreach (int id in entries.Keys.ToArray())
        {
            var entry = entries[id];
            if (entry.Actor.Instance.Destroyed) { entries.Remove(id); continue; }
            Restore(entry, Direction(entry.Actor));
            if (entry.Parts.Count == 0) entries.Remove(id);
        }
    }
    private static void Restore((ActorContext Actor, List<(string Name, GmInstance Bone, GmInstance? Parent)> Parts, Dictionary<string, BoneLocal> Pose) entry, double direction)
    {
        List<string>? stale = null;
        foreach (var part in entry.Parts.ToArray())
        {
            if (part.Bone.Destroyed || part.Parent is null || part.Parent.Destroyed)
            {
                Console.Error.WriteLine("ROPEBONDS parent missing, bone released: " + part.Name);
                (stale ??= new List<string>()).Add(part.Name);
                continue;
            }
            if (entry.Pose.TryGetValue(part.Name, out var local)) Apply(part.Bone, part.Parent, local, entry.Actor, direction);
        }
        if (stale is null) return;
        entry.Parts.RemoveAll(part => stale.Contains(part.Name));
        foreach (string name in stale) entry.Pose.Remove(name);
    }
    private static void Apply(GmInstance bone, GmInstance parent, BoneLocal local, ActorContext actor, double direction)
    {
        double parentX = ParentX(actor, parent), parentY = ParentY(actor, parent), parentAngle = ParentAngle(actor, parent);
        double radians = parentAngle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        local.Write(bone);
        bone.Scope.Write("x", parentX + (local.PX * cos + local.PY * sin) * direction);
        bone.Scope.Write("y", parentY - local.PX * sin + local.PY * cos);
        bone.Scope.Write("Direction", direction);
        bone.Scope.Write("image_angle", local.Angle + parentAngle);
    }
    private static Dictionary<int, GmInstance> Instances(ActorContext actor)
    {
        var map = new Dictionary<int, GmInstance>();
        foreach (var instance in actor.World.Instances)
            if (!instance.Destroyed) map[instance.Id] = instance;
        return map;
    }
    private static void SortByDepth(List<(string Name, GmInstance Bone, GmInstance? Parent)> parts)
    {
        int Depth((string Name, GmInstance Bone, GmInstance? Parent) part)
        {
            int depth = 0;
            GmInstance? parent = part.Parent;
            while (parent is not null)
            {
                var match = parts.FirstOrDefault(candidate => ReferenceEquals(candidate.Bone, parent));
                if (match.Bone is null) break;
                depth++;
                parent = match.Parent;
            }
            return depth;
        }
        parts.Sort((left, right) => Depth(left).CompareTo(Depth(right)));
    }
    private static (double PX, double PY) Offset(GmInstance bone, GmInstance parent, ActorContext actor, double direction)
    {
        double parentAngle = ParentAngle(actor, parent);
        double radians = parentAngle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        double dx = (bone.Scope.ReadOr("x").Number - ParentX(actor, parent)) / direction;
        double dy = bone.Scope.ReadOr("y").Number - ParentY(actor, parent);
        return (dx * cos - dy * sin, dx * sin + dy * cos);
    }
    private static double ParentX(ActorContext actor, GmInstance parent) => ReferenceEquals(parent, actor.Instance) ? actor.X : parent.Scope.ReadOr("x").Number;
    private static double ParentY(ActorContext actor, GmInstance parent) => ReferenceEquals(parent, actor.Instance) ? actor.Y : parent.Scope.ReadOr("y").Number;
    private static double ParentAngle(ActorContext actor, GmInstance parent) => ReferenceEquals(parent, actor.Instance) ? actor.Read("image_angle") : parent.Scope.ReadOr("image_angle").Number;
    private static double Direction(ActorContext actor)
    {
        double direction = actor.Instance.Scope.ReadOr("Direction", 1).Number;
        return direction == 0 ? 1 : direction;
    }
}
