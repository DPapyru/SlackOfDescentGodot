using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed class HandConstraint : IDisposable
{
    private const string Handler = "ropebonds.hand.constraint", VerifyHandler = "ropebonds.hand.verify", PrepareHandler = "ropebonds.hand.prepare";
    private const string ClipGraph = "ropebonds.bound", ClipId = "bound";
    public static readonly string[] Hands = MotionSkeleton.Bones.Where(name => name.StartsWith("HAND", StringComparison.Ordinal)).ToArray();
    private sealed record Bone(string Name, GmInstance Instance, GmInstance Parent);
    private sealed record Entry(ActorContext Actor, List<Bone> Bones, NativePoseService.Snapshot? Pose, MotionClip? Clip)
    {
        public double Frame;
    }
    private readonly Dictionary<int, Entry> entries = new();
    private ContentRegistry? registry;
    private readonly Dictionary<int, (double X, double Y, double Angle)> written = new();
    private readonly Dictionary<(int Actor, string Bone), (double X, double Y)> placed = new();
    private readonly bool trace = Environment.GetEnvironmentVariable("ROPEBONDS_TRACE") is { Length: > 0 };

    public void Attach(ModContext context)
    {
        registry = context.Content;
        context.Subscribe(Handler, NativeEventNames.StepEnd, NativeEventPhase.After, _ => Tick());
        context.Subscribe(VerifyHandler, NativeEventNames.StepBegin, NativeEventPhase.Before, _ => Verify());
        context.Subscribe(PrepareHandler, NativeEventNames.FramePrepare, NativeEventPhase.After, _ => Prepare());
    }

    public bool Bind(ActorContext actor, IEnumerable<string>? names = null)
    {
        if (actor.Instance.Destroyed || entries.ContainsKey(actor.Id)) return false;
        List<Bone> bones = [];
        MotionSkeletonDescriptor skeleton = MotionSkeletonDescriptor.FromActor(actor.World, actor.Instance);
        foreach (string name in (names ?? Hands).Distinct(StringComparer.Ordinal))
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
        bones.Sort((left, right) => Depth(left, bones).CompareTo(Depth(right, bones)));
        entries[actor.Id] = new Entry(actor, bones, actor.Services.Poses.Capture(actor, bones.Select(bone => bone.Name).ToArray()), BoundClip());
        Console.WriteLine("ROPE_BIND_START bones=" + bones.Count + " actor=" + actor.Id);
        return true;
    }

    public bool Release(ActorContext actor)
    {
        if (!entries.Remove(actor.Id, out var entry)) return false;
        Restore(entry);
        if (trace) foreach (Bone bone in entry.Bones) written.Remove(bone.Instance.Id);
        foreach (Bone bone in entry.Bones) placed.Remove((entry.Actor.Id, bone.Name));
        Console.WriteLine("ROPE_BIND_RELEASE actor=" + actor.Id);
        return true;
    }

    public void ReleaseAll()
    {
        foreach (var entry in entries.Values) Restore(entry);
        entries.Clear();
    }

    public bool IsBound(int actorId) => entries.ContainsKey(actorId);

    public (double X, double Y)? BoundHands(int actorId) =>
        placed.TryGetValue((actorId, "HAND3_ABOVE"), out var above) && placed.TryGetValue((actorId, "HAND3_BELOW"), out var below)
            ? ((above.X + below.X) / 2, (above.Y + below.Y) / 2)
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
        }
    }

    private void Prepare()
    {
        foreach (Entry entry in entries.Values)
            if (!entry.Actor.Instance.Destroyed) Restore(entry);
    }

    private MotionClip? BoundClip()
    {
        if (registry is null || !registry.MotionGraphs.TryGetValue(ClipGraph, out var graph) || graph.Clips.Count == 0) return null;
        return graph.Clips.FirstOrDefault(clip => clip.Id == ClipId) ?? graph.Clips[0];
    }

    private static int Depth(Bone bone, List<Bone> bones)
    {
        int depth = 0;
        GmInstance? parent = bone.Parent;
        while (parent is not null)
        {
            Bone? match = bones.FirstOrDefault(candidate => ReferenceEquals(candidate.Instance, parent));
            if (match is null) break;
            depth++;
            parent = match.Parent;
        }
        return depth;
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

public sealed class BindProbe : IDisposable
{
    private const string CreatedHandler = "ropebonds.bind.created";
    private const string StepHandler = "ropebonds.bind.step";
    private readonly HandConstraint constraints;
    private readonly bool enabled = Environment.GetEnvironmentVariable("ROPEBONDS_BIND_TEST") is { Length: > 0 };
    private readonly bool loop = Environment.GetEnvironmentVariable("ROPEBONDS_BIND_LOOP") is { Length: > 0 };
    private GmWorld? world;
    private ActorContext? player;
    private double waited;
    private double boundAt;
    private bool bound;
    private bool done;

    public BindProbe(HandConstraint constraints) => this.constraints = constraints;

    public void Attach(ModContext context)
    {
        if (!enabled) return;
        context.Subscribe(CreatedHandler, NativeEventNames.InstanceCreated, NativeEventPhase.After, Created);
        context.Subscribe(StepHandler, NativeEventNames.StepEnd, NativeEventPhase.After, Step);
    }

    private void Created(NativeGameEvent value)
    {
        if (value.Actor is not { } actor) return;
        world ??= actor.World;
        if (RopeBondsMod.IsPlayer(actor)) player = actor;
    }

    private void Step(NativeGameEvent value)
    {
        if (world is null || done) return;
        if (player is null || player.Instance.Destroyed || !world.Instances.Contains(player.Instance)) player = RopeBondsMod.FindPlayer(world);
        if (player is null) return;
        if (!bound)
        {
            waited += player.Services.Frame.Delta;
            if (waited < 2) return;
            if (constraints.Bind(player)) { bound = true; boundAt = player.Services.Frame.GameTime; }
            return;
        }
        if (player.Services.Frame.GameTime - boundAt < 2) return;
        constraints.Release(player);
        bound = false;
        if (!loop) { done = true; return; }
        waited = 0;
    }

    public void Dispose()
    {
        if (bound && player is not null && !player.Instance.Destroyed) constraints.Release(player);
        bound = false;
        done = true;
    }
}
