using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed partial class RopeBondsMod
{
    partial void RegisterActions(ModContext context) => new SinkDriver(context.Content).Attach(context);
}

public sealed class SinkDriver
{
    private const string TickHandler = "ropebonds.sink.tick", SpawnHandler = "ropebonds.sink.spawn";
    private readonly ContentRegistry content;
    private readonly Dictionary<int, double> frames = new();
    private MotionClip? clip;
    private GmWorld? world;
    private ActorContext? player;
    public SinkDriver(ContentRegistry content) => this.content = content;
    public void Attach(ModContext context)
    {
        context.Subscribe(SpawnHandler, NativeEventNames.InstanceCreated, NativeEventPhase.After, Capture);
        context.Subscribe(TickHandler, NativeEventNames.StepEnd, NativeEventPhase.Before, _ => Tick());
    }
    private void Capture(NativeGameEvent value)
    {
        if (value.Actor is not { } actor) return;
        world = actor.World;
        if (IsPlayer(actor)) player = actor;
    }
    private MotionClip? Resolve()
    {
        if (clip is not null) return clip;
        if (!content.MotionGraphs.TryGetValue("ropebonds.sink", out var graph)) return null;
        graph.Validate();
        return clip = graph.Clips.Single(item => item.Id == graph.States.Single(state => state.Id == graph.Entry).Clip);
    }
    private void Tick()
    {
        if (world is null) return;
        var active = Resolve();
        if (active is null) return;
        ActorContext? actor = player;
        if (actor is null || actor.Instance.Destroyed) actor = FindPlayer();
        player = actor;
        if (actor is null) { frames.Clear(); return; }
        foreach (int id in frames.Keys.Where(id => id != actor.Id).ToArray()) frames.Remove(id);
        if (actor.Read("touchingQS") == 0 || !NativeActorActions.MotionMatches(actor, "STAND_0_QUICKSAND"))
        {
            frames.Remove(actor.Id);
            return;
        }
        double frame = frames.TryGetValue(actor.Id, out var previous) ? Math.Min(active.Duration, previous + 2) : 0;
        Write(active, actor, frame);
        frames[actor.Id] = frame;
    }
    private static void Write(MotionClip clip, ActorContext actor, double frame)
    {
        var sample = clip.Sample(frame);
        var parts = Parts(actor);
        var byId = parts.ToDictionary(part => part.Bone.Id, part => part.Bone);
        double direction = actor.Instance.Scope.ReadOr("Direction", 1).Number;
        if (direction == 0) direction = 1;
        foreach (var (name, bone) in Ordered(actor, parts))
        {
            if (!sample.TryGetValue(name, out var local)) continue;
            int parentId = bone.Scope.ReadOr("parent", actor.Id).Int;
            GmInstance? parent = parentId == actor.Id ? actor.Instance : byId.GetValueOrDefault(parentId);
            if (parent is null || parent.Destroyed) continue;
            double parentAngle = ParentAngle(actor, parent);
            double radians = parentAngle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
            bone.Scope.Write("Angle", local.Angle);
            bone.Scope.Write("ScaleX", local.ScaleX);
            bone.Scope.Write("ScaleY", local.ScaleY);
            bone.Scope.Write("PartPicture", local.Picture);
            bone.Scope.Write("image_index", local.Picture);
            bone.Scope.Write("x", ParentX(actor, parent) + (local.X * cos + local.Y * sin) * direction);
            bone.Scope.Write("y", ParentY(actor, parent) - local.X * sin + local.Y * cos);
            bone.Scope.Write("Direction", direction);
            bone.Scope.Write("image_angle", local.Angle + parentAngle);
        }
    }
    private static List<(string Name, GmInstance Bone)> Parts(ActorContext actor)
    {
        var parts = new List<(string, GmInstance)>();
        foreach (string name in NativeCharacterBones.Order)
            if (actor.Part(name) is { Instance: { Destroyed: false } } part) parts.Add((name, part.Instance));
        return parts;
    }
    private static IEnumerable<(string Name, GmInstance Bone)> Ordered(ActorContext actor, List<(string Name, GmInstance Bone)> parts)
    {
        var byId = parts.ToDictionary(part => part.Bone.Id, part => part.Bone);
        int Depth(GmInstance bone)
        {
            int depth = 0, parent = bone.Scope.ReadOr("parent", actor.Id).Int;
            while (byId.TryGetValue(parent, out var above)) { depth++; parent = above.Scope.ReadOr("parent", actor.Id).Int; }
            return depth;
        }
        return parts.OrderBy(part => Depth(part.Bone));
    }
    private static double ParentX(ActorContext actor, GmInstance parent) => ReferenceEquals(parent, actor.Instance) ? actor.X : parent.Scope.ReadOr("x").Number;
    private static double ParentY(ActorContext actor, GmInstance parent) => ReferenceEquals(parent, actor.Instance) ? actor.Y : parent.Scope.ReadOr("y").Number;
    private static double ParentAngle(ActorContext actor, GmInstance parent) => ReferenceEquals(parent, actor.Instance) ? actor.Read("image_angle") : parent.Scope.ReadOr("image_angle").Number;
    private ActorContext? FindPlayer()
    {
        if (world is null || !world.Constants.TryGetValue("objPlayerDoll", out var doll)) return null;
        var instance = world.Instances.FirstOrDefault(candidate => !candidate.Destroyed && world.IsObject(candidate.Object, doll.Int));
        return instance is null ? null : new(world, instance);
    }
    private static bool IsPlayer(ActorContext actor) => actor.World.Constants.TryGetValue("objPlayerDoll", out var doll) && actor.World.IsObject(actor.Instance.Object, doll.Int);
}
