using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed class BoneLayer(string prefix, bool drains = false)
{
    private const double DrainInterval = .5;
    private const double BloodDamage = 5;
    private const double StaminaDamage = 5;
    private const double ExhaustedDamage = 2;
    private sealed record Bone(string Name, GmInstance Instance, GmInstance Parent);
    private sealed record Entry(ActorContext Actor, List<Bone> Bones, NativePoseService.Snapshot? Pose, MotionClip? Clip)
    {
        public double Frame;
        public double NextDrain;
    }
    private readonly bool drains = drains;
    private readonly Dictionary<int, Entry> entries = new();
    private readonly Dictionary<int, (double X, double Y, double Angle)> written = new();
    private readonly Dictionary<(int Actor, string Bone), (double X, double Y)> placed = new();
    private readonly bool trace = Environment.GetEnvironmentVariable("ROPEBONDS_TRACE") is { Length: > 0 };
    private ContentRegistry? registry;

    public void Attach(ModContext context)
    {
        registry = context.Content;
        context.Subscribe(prefix + ".constraint", NativeEventNames.StepEnd, NativeEventPhase.After, _ => Tick());
        context.Subscribe(prefix + ".verify", NativeEventNames.StepBegin, NativeEventPhase.Before, _ => Verify());
        context.Subscribe(prefix + ".prepare", NativeEventNames.FramePrepare, NativeEventPhase.After, _ => Prepare());
    }

    public bool Bind(ActorContext actor, string graph, string clip, IEnumerable<string> names, NativePoseService.Snapshot? pose = null)
    {
        if (actor.Instance.Destroyed || entries.ContainsKey(actor.Id)) return false;
        List<Bone> bones = [];
        MotionSkeletonDescriptor skeleton = MotionSkeletonDescriptor.FromActor(actor.World, actor.Instance);
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            if (!skeleton.ByName.TryGetValue(name, out var node)) continue;
            GmInstance? instance = actor.World.Instances.FirstOrDefault(candidate => candidate.Id == node.Instance && !candidate.Destroyed);
            if (instance is null) continue;
            GmInstance? parent = node.Parent == actor.Id
                ? actor.Instance
                : actor.World.Instances.FirstOrDefault(candidate => candidate.Id == node.Parent && !candidate.Destroyed);
            if (parent is null || parent.Destroyed) continue;
            bones.Add(new Bone(name, instance, parent));
        }
        if (bones.Count == 0) return false;
        bones.Sort((left, right) => Array.IndexOf(MotionSkeleton.Bones, left.Name).CompareTo(Array.IndexOf(MotionSkeleton.Bones, right.Name)));
        NativePoseService.Snapshot? snapshot = pose ?? actor.Services.Poses.Capture(actor, bones.Select(bone => bone.Name).ToArray());
        entries[actor.Id] = new Entry(actor, bones, snapshot, Clip(graph, clip));
        Console.WriteLine("ROPE_BIND_START bones=" + bones.Count + " actor=" + actor.Id);
        return true;
    }

    public bool Release(ActorContext actor)
    {
        if (!entries.Remove(actor.Id, out var entry)) return false;
        Revert(entry);
        if (trace) foreach (Bone bone in entry.Bones) written.Remove(bone.Instance.Id);
        foreach (Bone bone in entry.Bones) placed.Remove((entry.Actor.Id, bone.Name));
        Console.WriteLine("ROPE_BIND_RELEASE actor=" + actor.Id);
        return true;
    }

    public void ReleaseAll()
    {
        foreach (var entry in entries.Values) Revert(entry);
        entries.Clear();
    }

    public bool Holds(int actorId) => entries.ContainsKey(actorId);

    public string? ClipId(int actorId) => entries.TryGetValue(actorId, out var entry) ? entry.Clip?.Id : null;

    public bool Holds(int actorId, IEnumerable<string> names) =>
        entries.TryGetValue(actorId, out var entry) && entry.Bones.Any(bone => names.Contains(bone.Name, StringComparer.Ordinal));

    public (double X, double Y)? Center(int actorId, string above, string below) =>
        placed.TryGetValue((actorId, above), out var left) && placed.TryGetValue((actorId, below), out var right)
            ? ((left.X + right.X) / 2, (left.Y + right.Y) / 2)
            : null;

    private void Tick()
    {
        if (entries.Count == 0) return;
        foreach (int id in entries.Keys.ToArray())
        {
            var entry = entries[id];
            if (entry.Actor.Instance.Destroyed) { entries.Remove(id); continue; }
            if (entry.Clip is { } clip) entry.Frame = clip.Loop ? (entry.Frame + 1) % Math.Max(1, clip.Duration) : Math.Min(entry.Frame + 1, clip.Duration);
            Restore(entry);
            if (drains) Drain(entry);
        }
    }

    private void Drain(Entry entry)
    {
        ActorContext player = entry.Actor;
        if (player.Instance.Destroyed || !player.Instance.Active) return;
        double now = player.Services.Frame.GameTime;
        if (entry.NextDrain <= 0)
        {
            entry.NextDrain = now + DrainInterval;
            return;
        }
        if (now < entry.NextDrain) return;
        entry.NextDrain = now + DrainInterval;
        DrainOnce(player);
    }

    private static void DrainOnce(ActorContext player)
    {
        if (player.World.Globals.ReadOr("PlayerHP").Number <= 0) return;
        if (player.World.Globals.ReadOr("config_Blood").Truth)
        {
            player.World.Globals.Write("PlayerHP", Math.Max(0, player.World.Globals.ReadOr("PlayerHP").Number - BloodDamage));
            RopeCreature.PlayVoice(player, "V_UHI", 0);
            return;
        }
        double stamina = player.World.Globals.ReadOr("PlayerST").Number;
        if (stamina > 0) player.World.Globals.Write("PlayerST", Math.Max(0, stamina - StaminaDamage));
        else player.World.Globals.Write("PlayerHP", Math.Max(0, player.World.Globals.ReadOr("PlayerHP").Number - ExhaustedDamage));
    }

    private void Prepare()
    {
        foreach (Entry entry in entries.Values)
            if (!entry.Actor.Instance.Destroyed) Restore(entry);
    }

    private MotionClip? Clip(string graphId, string clipId)
    {
        if (registry is null || !registry.MotionGraphs.TryGetValue(graphId, out var graph) || graph.Clips.Count == 0) return null;
        return graph.Clips.FirstOrDefault(clip => clip.Id == clipId) ?? graph.Clips[0];
    }

    private void Verify()
    {
        if (!trace) return;
        foreach (Entry entry in entries.Values)
            foreach (Bone bone in entry.Bones)
            {
                if (bone.Instance.Destroyed || bone.Parent.Destroyed)
                {
                    if (written.Remove(bone.Instance.Id)) Console.WriteLine("ROPE_BONE_LOST bone=" + bone.Name + " actor=" + entry.Actor.Id);
                    continue;
                }
                if (!written.TryGetValue(bone.Instance.Id, out var pose)) continue;
                double x = bone.Instance.Scope.ReadOr("x").Number, y = bone.Instance.Scope.ReadOr("y").Number, angle = bone.Instance.Scope.ReadOr("Angle").Number;
                if (Math.Abs(x - pose.X) < .01 && Math.Abs(y - pose.Y) < .01 && Math.Abs(angle - pose.Angle) < .01) continue;
                Console.WriteLine("ROPE_POSE_MOVED bone=" + bone.Name + " actor=" + entry.Actor.Id + " dx=" + (x - pose.X).ToString("0.00") + " dy=" + (y - pose.Y).ToString("0.00") + " da=" + (angle - pose.Angle).ToString("0.00"));
                written.Remove(bone.Instance.Id);
            }
    }

    private void Revert(Entry entry)
    {
        if (entry.Pose is not { } snapshot) return;
        foreach (Bone bone in entry.Bones)
            if (!bone.Instance.Destroyed && !bone.Parent.Destroyed) entry.Actor.Services.Poses.Restore(snapshot, bone.Name);
    }

    private void Restore(Entry entry)
    {
        IReadOnlyDictionary<string, MotionPose>? sample = entry.Clip?.Sample(entry.Frame);
        foreach (Bone bone in entry.Bones)
        {
            if (bone.Instance.Destroyed || bone.Parent.Destroyed) continue;
            if (sample is not null && sample.TryGetValue(bone.Name, out var key)) entry.Actor.Services.Poses.Apply(entry.Actor, bone.Name, key);
            else if (entry.Pose is { } snapshot) entry.Actor.Services.Poses.Restore(snapshot, bone.Name);
            placed[(entry.Actor.Id, bone.Name)] = (bone.Instance.Scope.ReadOr("x").Number, bone.Instance.Scope.ReadOr("y").Number);
            if (trace) written[bone.Instance.Id] = (bone.Instance.Scope.ReadOr("x").Number, bone.Instance.Scope.ReadOr("y").Number, bone.Instance.Scope.ReadOr("Angle").Number);
        }
    }

    public void Dispose() => ReleaseAll();
}
