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
    private readonly Dictionary<int, (ActorContext Actor, Dictionary<string, ConstraintService.BoneLocal> Pose, double Frame)> entries = new();
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
        if (actor is null) { entries.Clear(); return; }
        foreach (int id in entries.Keys.Where(id => id != actor.Id).ToArray())
            if (entries.Remove(id, out var stale)) Restore(stale.Actor, stale.Pose);
        if (actor.Read("touchingQS") == 0)
        {
            if (entries.Remove(actor.Id, out var leaving)) Restore(leaving.Actor, leaving.Pose);
            return;
        }
        if (!entries.TryGetValue(actor.Id, out var entry))
        {
            var snapped = Parts(actor);
            entry = (actor, ConstraintService.Snapshot(snapped.Select(part => (part.Name, part.Bone))), 0);
        }
        Write(active, entry.Actor, entry.Frame);
        entries[actor.Id] = (entry.Actor, entry.Pose, (entry.Frame + 2) % active.Duration);
    }
    private static void Write(MotionClip clip, ActorContext actor, double frame)
    {
        var sample = clip.Sample(frame);
        var parts = Parts(actor);
        var byId = parts.ToDictionary(part => part.Bone.Id, part => part.Bone);
        foreach (var (name, bone) in Ordered(actor, parts))
        {
            if (!sample.TryGetValue(name, out var local)) continue;
            new ConstraintService.BoneLocal(local.Angle, local.ScaleX, local.ScaleY, local.Picture, local.Picture).Write(bone);
            bone.Scope.Write("image_angle", local.Angle + ParentAngle(actor, bone, byId));
        }
    }
    private static void Restore(ActorContext actor, Dictionary<string, ConstraintService.BoneLocal> pose)
    {
        var parts = Parts(actor);
        var byId = parts.ToDictionary(part => part.Bone.Id, part => part.Bone);
        foreach (var (name, bone) in Ordered(actor, parts))
        {
            if (!pose.TryGetValue(name, out var local)) continue;
            local.Write(bone);
            bone.Scope.Write("image_angle", local.Angle + ParentAngle(actor, bone, byId));
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
    private static double ParentAngle(ActorContext actor, GmInstance bone, Dictionary<int, GmInstance> byId)
    {
        int parent = bone.Scope.ReadOr("parent", actor.Id).Int;
        return byId.TryGetValue(parent, out var above) ? above.Scope.ReadOr("image_angle").Number : actor.Read("image_angle");
    }
    private ActorContext? FindPlayer()
    {
        if (world is null || !world.Constants.TryGetValue("objPlayerDoll", out var doll)) return null;
        var instance = world.Instances.FirstOrDefault(candidate => !candidate.Destroyed && world.IsObject(candidate.Object, doll.Int));
        return instance is null ? null : new(world, instance);
    }
    private static bool IsPlayer(ActorContext actor) => actor.World.Constants.TryGetValue("objPlayerDoll", out var doll) && actor.World.IsObject(actor.Instance.Object, doll.Int);
}
