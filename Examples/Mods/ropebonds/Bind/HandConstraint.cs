using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed class HandConstraint : IDisposable
{
    private const string Handler = "ropebonds.hand.constraint", VerifyHandler = "ropebonds.hand.verify", PrepareHandler = "ropebonds.hand.prepare";
    private const string ClipGraph = "ropebonds.bound", ClipId = "bound";
    public static readonly string[] Hands =
    [
        "HAND1_ABOVE", "HAND2_ABOVE", "HAND3_ABOVE",
        "HAND1_BELOW", "HAND2_BELOW", "HAND3_BELOW"
    ];
    private sealed record Bone(string Name, GmInstance Instance, GmInstance Parent);
    private sealed record Snapshot(double Angle, double ScaleX, double ScaleY, int Picture, double ImageIndex, double PX, double PY);
    private sealed record Entry(ActorContext Actor, List<Bone> Bones, Dictionary<string, Snapshot> Pose, MotionClip? Clip)
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
        Dictionary<string, Snapshot> pose = new(StringComparer.Ordinal);
        double direction = Direction(actor);
        foreach (string name in (names ?? Hands).Distinct(StringComparer.Ordinal))
        {
            if (actor.Part(name) is not { } part) continue;
            GmInstance instance = part.Instance;
            int parentId = instance.Scope.ReadOr("parent", actor.Id).Int;
            GmInstance? parent = parentId == actor.Id
                ? actor.Instance
                : actor.World.Instances.FirstOrDefault(candidate => candidate.Id == parentId && !candidate.Destroyed);
            if (parent is null || parent.Destroyed) continue;
            bones.Add(new Bone(name, instance, parent));
            pose[name] = SnapshotOf(instance, parent, actor, direction);
        }
        if (bones.Count == 0) return false;
        bones.Sort((left, right) => Depth(left, bones).CompareTo(Depth(right, bones)));
        entries[actor.Id] = new Entry(actor, bones, pose, BoundClip());
        Console.WriteLine("ROPE_BIND_START bones=" + bones.Count + " actor=" + actor.Id);
        return true;
    }

    public bool Release(ActorContext actor)
    {
        if (!entries.Remove(actor.Id, out var entry)) return false;
        Restore(entry, Direction(actor));
        if (trace) foreach (Bone bone in entry.Bones) written.Remove(bone.Instance.Id);
        foreach (Bone bone in entry.Bones) placed.Remove((entry.Actor.Id, bone.Name));
        Console.WriteLine("ROPE_BIND_RELEASE actor=" + actor.Id);
        return true;
    }

    public void ReleaseAll()
    {
        foreach (var entry in entries.Values) Restore(entry, Direction(entry.Actor));
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
            Restore(entry, Direction(entry.Actor));
        }
    }

    private void Prepare()
    {
        foreach (Entry entry in entries.Values)
            if (!entry.Actor.Instance.Destroyed) Restore(entry, Direction(entry.Actor));
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

    private void Restore(Entry entry, double direction)
    {
        IReadOnlyDictionary<string, MotionPose>? sample = entry.Clip?.Sample(entry.Frame);
        foreach (Bone bone in entry.Bones)
        {
            if (bone.Instance.Destroyed || bone.Parent.Destroyed) continue;
            Snapshot? pose = null;
            if (sample is not null && sample.TryGetValue(bone.Name, out var key)) pose = new(key.Angle, key.ScaleX, key.ScaleY, key.Picture, key.Picture, key.X, key.Y);
            else pose = entry.Pose.GetValueOrDefault(bone.Name);
            if (pose is null) continue;
            Apply(bone.Instance, bone.Parent, pose, entry.Actor, direction);
            placed[(entry.Actor.Id, bone.Name)] = (bone.Instance.Scope.ReadOr("x").Number, bone.Instance.Scope.ReadOr("y").Number);
            if (trace) written[bone.Instance.Id] = (bone.Instance.Scope.ReadOr("x").Number, bone.Instance.Scope.ReadOr("y").Number, bone.Instance.Scope.ReadOr("Angle").Number);
        }
    }

    private static void Apply(GmInstance bone, GmInstance parent, Snapshot pose, ActorContext actor, double direction)
    {
        double parentX = ParentX(actor, parent), parentY = ParentY(actor, parent), parentAngle = ParentAngle(actor, parent);
        double radians = parentAngle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        bone.Scope.Write("Angle", pose.Angle);
        bone.Scope.Write("ScaleX", pose.ScaleX);
        bone.Scope.Write("ScaleY", pose.ScaleY);
        bone.Scope.Write("PartPicture", pose.Picture);
        bone.Scope.Write("image_index", pose.ImageIndex);
        bone.Scope.Write("x", parentX + (pose.PX * cos + pose.PY * sin) * direction);
        bone.Scope.Write("y", parentY - pose.PX * sin + pose.PY * cos);
        bone.Scope.Write("Direction", direction);
        bone.Scope.Write("image_angle", pose.Angle + parentAngle);
    }

    private static Snapshot SnapshotOf(GmInstance bone, GmInstance parent, ActorContext actor, double direction)
    {
        double parentAngle = ParentAngle(actor, parent);
        double radians = parentAngle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        double dx = (bone.Scope.ReadOr("x").Number - ParentX(actor, parent)) / direction;
        double dy = bone.Scope.ReadOr("y").Number - ParentY(actor, parent);
        return new(
            bone.Scope.ReadOr("Angle").Number,
            bone.Scope.ReadOr("ScaleX", 1).Number,
            bone.Scope.ReadOr("ScaleY", 1).Number,
            bone.Scope.ReadOr("PartPicture").Int,
            bone.Scope.ReadOr("image_index").Number,
            dx * cos - dy * sin,
            dx * sin + dy * cos);
    }

    private static double ParentX(ActorContext actor, GmInstance parent) =>
        ReferenceEquals(parent, actor.Instance) ? actor.X : parent.Scope.ReadOr("x").Number;
    private static double ParentY(ActorContext actor, GmInstance parent) =>
        ReferenceEquals(parent, actor.Instance) ? actor.Y : parent.Scope.ReadOr("y").Number;
    private static double ParentAngle(ActorContext actor, GmInstance parent) =>
        ReferenceEquals(parent, actor.Instance) ? actor.Read("image_angle") : parent.Scope.ReadOr("image_angle").Number;
    private static double Direction(ActorContext actor)
    {
        double direction = actor.Read("Direction");
        return direction == 0 ? 1 : direction;
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
        if (IsPlayer(actor)) player = actor;
    }

    private void Step(NativeGameEvent value)
    {
        if (world is null || done) return;
        if (player is null || player.Instance.Destroyed || !world.Instances.Contains(player.Instance)) player = FindPlayer();
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

    private ActorContext? FindPlayer()
    {
        if (world is null || !world.Constants.TryGetValue("objPlayerDoll", out var kind)) return null;
        foreach (var instance in world.Instances)
            if (!instance.Destroyed && instance.Active && world.IsObject(instance.Object, kind.Int)) return new(world, instance);
        return null;
    }

    private static bool IsPlayer(ActorContext actor) =>
        actor.World.Constants.TryGetValue("objPlayerDoll", out var kind) && actor.World.IsObject(actor.Instance.Object, kind.Int);

    public void Dispose()
    {
        if (bound && player is not null && !player.Instance.Destroyed) constraints.Release(player);
        bound = false;
        done = true;
    }
}
