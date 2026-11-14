using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed class ConstraintService
{
    private const string HandlerId = "ropebonds.constraint";
    private sealed class Entry
    {
        public required ActorContext Actor { get; init; }
        public Dictionary<string, BoneLocal> Pose { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Bones { get; } = new(StringComparer.Ordinal);
    }
    private readonly Dictionary<int, Entry> entries = new();
    public void Attach(ModContext context)
    {
        BoneSets.Validate();
        context.Subscribe(HandlerId, NativeEventNames.StepEnd, NativeEventPhase.After, _ => Tick());
    }
    public bool Bind(ActorContext actor, params BindPart[] parts)
    {
        if (actor.Instance.Destroyed) return false;
        string[] bones = BindPose.Union(parts.SelectMany(part => BoneSets.Of(part)));
        if (bones.Length == 0) return false;
        if (!entries.TryGetValue(actor.Id, out var entry)) entries[actor.Id] = entry = new Entry { Actor = actor };
        foreach (string name in bones)
        {
            if (entry.Bones.Contains(name)) continue;
            if (actor.Part(name) is not { Instance: { Destroyed: false } } part) continue;
            entry.Bones.Add(name);
            entry.Pose[name] = BoneLocal.Read(part.Instance);
        }
        if (entry.Bones.Count == 0) entries.Remove(actor.Id);
        return entries.ContainsKey(actor.Id);
    }
    public bool Release(ActorContext actor)
    {
        if (!entries.Remove(actor.Id, out var entry)) return false;
        Restore(entry);
        return true;
    }
    public bool Release(ActorContext actor, params BindPart[] parts)
    {
        if (!entries.TryGetValue(actor.Id, out var entry)) return false;
        string[] released = BindPose.Union(parts.SelectMany(part => BoneSets.Of(part)));
        Restore(entry.Actor, released, entry.Pose);
        RemoveParts(entry.Bones, entry.Pose, released);
        if (entry.Bones.Count == 0) entries.Remove(actor.Id);
        return true;
    }
    public void ReleaseAll()
    {
        foreach (var entry in entries.Values.ToArray()) Restore(entry);
        entries.Clear();
    }
    public bool IsBound(int actorId) => entries.ContainsKey(actorId);
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
    private void Tick()
    {
        if (entries.Count == 0) return;
        foreach (int id in entries.Keys.ToArray())
        {
            var entry = entries[id];
            if (entry.Actor.Instance.Destroyed) { entries.Remove(id); continue; }
            Restore(entry);
        }
    }
    private static void Restore(Entry entry) => Restore(entry.Actor, entry.Bones, entry.Pose);
    private static void Restore(ActorContext actor, IEnumerable<string> names, IReadOnlyDictionary<string, BoneLocal> pose)
    {
        var bones = new List<(string Name, GmInstance Bone)>();
        foreach (string name in names)
            if (actor.Part(name) is { Instance: { Destroyed: false } } part) bones.Add((name, part.Instance));
        if (bones.Count == 0) return;
        var constrained = new Dictionary<int, string>();
        foreach (var (name, bone) in bones) constrained[bone.Id] = name;
        bones.Sort((left, right) => Depth(left.Bone, constrained, actor).CompareTo(Depth(right.Bone, constrained, actor)));
        var parts = new List<(string Name, GmInstance Bone, double ParentAngle)>();
        foreach (var (name, bone) in bones) parts.Add((name, bone, ParentAngle(actor, bone)));
        BindPose.Restore(parts, pose);
    }
    private static int Depth(GmInstance bone, Dictionary<int, string> constrained, ActorContext actor)
    {
        int depth = 0, parent = bone.Scope.ReadOr("parent", actor.Id).Int;
        var seen = new HashSet<int>();
        while (constrained.ContainsKey(parent) && seen.Add(parent))
        {
            depth++;
            var ancestor = actor.World.Instances.FirstOrDefault(candidate => candidate.Id == parent);
            if (ancestor is null) break;
            parent = ancestor.Scope.ReadOr("parent", actor.Id).Int;
        }
        return depth;
    }
    private static double ParentAngle(ActorContext actor, GmInstance bone)
    {
        int parent = bone.Scope.ReadOr("parent", actor.Id).Int;
        foreach (var instance in actor.World.Instances)
            if (instance.Id == parent && !instance.Destroyed) return instance.Scope.ReadOr("image_angle").Number;
        return actor.Read("image_angle");
    }
}
