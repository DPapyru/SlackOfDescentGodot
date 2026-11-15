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
    private static void Log(string message)
    {
        if (RopeBondsMod.TraceOn) Console.Error.WriteLine(message);
    }

    private const string TickHandler = "ropebonds.sink.tick", SpawnHandler = "ropebonds.sink.spawn";
    private readonly ContentRegistry content;
    private readonly Dictionary<int, double> frames = new();
    private readonly HashSet<string> warned = new();
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
        if (!InSand(actor)) { frames.Remove(actor.Id); return; }
        double half = active.Duration * .5;
        bool deep = actor.Read("KOF1") > 10;
        double frame = frames.TryGetValue(actor.Id, out var previous) ? previous + 2 : 0;
        if (deep) frame = frame < half ? half : frame > active.Duration ? half : frame;
        else if (frame > half) frame = 0;
        if (NativeActorActions.MotionMatches(actor, "STAND_0_QUICKSAND") || NativeActorActions.MotionMatches(actor, "STAND_1_QUICKSAND"))
        {
            Write(active, actor, frame);
            Press(actor);
        }
        frames[actor.Id] = frame;
    }
    private void Write(MotionClip clip, ActorContext actor, double frame)
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
            if (parent is null || parent.Destroyed)
            {
                if (warned.Add(name)) Log("ROPEBONDS sink parent missing, bone skipped: " + name);
                continue;
            }
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
    private void Press(ActorContext actor)
    {
        double depth = actor.Read("KOF1");
        double surface = actor.Y + 59 - 4 * depth;
        double target = surface - 2;
        double direction = actor.Instance.Scope.ReadOr("Direction", 1).Number;
        if (direction == 0) direction = 1;
        foreach (string side in new[] { "ABOVE", "BELOW" })
        {
            var upper = actor.Part("HAND1_" + side);
            var elbow = actor.Part("HAND2_" + side);
            var hand = actor.Part("HAND3_" + side);
            if (upper is null || elbow is null || hand is null) continue;
            double l1 = Length(upper, elbow), l2 = Length(elbow, hand), sx = upper.X, sy = upper.Y;
            double tx = sx + direction * 6, ty = Math.Min(target, sy + l1 + l2 - .01);
            double d = Math.Clamp(Math.Sqrt((tx - sx) * (tx - sx) + (ty - sy) * (ty - sy)), .01, l1 + l2 - .01);
            double k = (l1 * l1 - l2 * l2 + d * d) / (2 * d), h = Math.Sqrt(Math.Max(0, l1 * l1 - k * k));
            double ux = (tx - sx) / d, uy = (ty - sy) / d;
            double ex = sx + ux * k - uy * h, ey = sy + uy * k + ux * h;
            double fx = sx + ux * k + uy * h, fy = sy + uy * k - ux * h;
            if (fy > ey) { ex = fx; ey = fy; }
            Place(elbow, ex, ey);
            Place(hand, tx, ty);
            double bend = Bend(sx, sy, ex, ey, tx, ty);
            Log("ROPE_SINK side=" + side + " palm=" + ty.ToString("0.0") + " surface=" + surface.ToString("0.0") + " err=" + (ty - surface).ToString("0.0") + " elbow=" + bend.ToString("0") + " raw=" + Angle(upper, elbow).ToString("0"));
        }
    }

    private static bool InSand(ActorContext actor) => actor.Read("touchingQS") != 0 || actor.Read("KOF1") > 0;

    private static double Length(ActorContext a, ActorContext b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Angle(ActorContext a, ActorContext b) => Math.Atan2(-(b.Y - a.Y), b.X - a.X) * 180 / Math.PI;

    private static double Bend(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double ux = ax - bx, uy = ay - by, vx = cx - bx, vy = cy - by;
        double dot = ux * vx + uy * vy;
        double lengths = Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy);
        if (lengths < 1e-9) return 0;
        return Math.Acos(Math.Clamp(dot / lengths, -1, 1)) * 180 / Math.PI;
    }

    private static void Place(ActorContext bone, double x, double y)
    {
        bone.X = x; bone.Y = y;
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
