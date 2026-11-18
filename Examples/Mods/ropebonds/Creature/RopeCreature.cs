using System;
using System.Collections.Generic;
using SlackOfDescent;

namespace RopeBonds;

public sealed class RopeCreature : IEntityBehavior
{
    private const int Segments = 8;
    private const double SegmentLength = 9;
    private const double Speed = .6;
    private const double DetectRange = 160;
    private const double AttachRange = 18;
    private const double PatrolRange = 64;
    private const double HalfThickness = 2;
    private const double HeadRadius = 4;
    private const double HeadGravity = 1.5;
    private const double MaxFall = 12;
    private const double JumpSpeed = 2.2;
    private const double JumpImpulse = -11;
    private const double JumpGravity = .5;
    private const int MaxJumpFrames = 90;
    private const double PlayerLayerOffset = 1000.5;
    private readonly HandConstraint constraints;
    private readonly VerletChain chain = new(SegmentLength, 2);
    private readonly List<GmBounds> solids = [];
    private static readonly List<RopeCreature> Live = [];
    private ActorContext? victim, self;
    private double ropeDepth = -100000;
    private double startX, startY, direction = 1, headVelocity;
    private bool grounded, jumping, locked, bodiesCleared;
    private int jumpFrame;

    public RopeCreature(HandConstraint constraints) => this.constraints = constraints;
    public string Id => "ropebonds.creature";

    public void Created(ActorContext entity) { Live.Add(this); entity.Write("HP", 10000); Reset(entity); }
    public void RoomEntered(ActorContext entity) => Reset(entity);
    public void RoomExited(ActorContext entity) => Release(entity);
    public void Destroyed(ActorContext entity) { Live.Remove(this); Release(entity); }
    public void Detach(ActorContext entity) => Release(entity);

    public void Update(ActorContext entity, FrameContext frame)
    {
        self = entity;
        if (chain.Count == 0) Reset(entity);
        if (solids.Count == 0) CollectSolids(entity);
        if (victim is not null && (victim.Instance.Destroyed || !victim.Instance.Active)) Release(entity);
        if (victim is not null && !constraints.IsBound(victim.Id)) Release(entity);
        if (victim is not null) { Follow(entity); return; }
        ActorContext? player = Player(entity);
        if (player is not null) ropeDepth = player.Instance.Scope.ReadOr("depth").Number - PlayerLayerOffset;
        (double X, double Y)? hands = player is null ? null : Hands(player);
        if (hands is { } target
            && Math.Abs(target.X - entity.X) < DetectRange && Math.Abs(target.Y - entity.Y) < 140)
        {
            int facing = Math.Sign(target.X - entity.X);
            if (facing != 0) direction = facing;
            if (Math.Abs(target.X - entity.X) <= AttachRange && Math.Abs(target.Y - entity.Y) <= AttachRange) { Attach(entity, player!); return; }
            if (grounded && !jumping) Jump();
            entity.X += jumping ? Math.Clamp(target.X - entity.X, -JumpSpeed, JumpSpeed) : direction * Speed;
        }
        else if (!jumping)
        {
            entity.X += direction * Speed * .5;
            if (entity.X < startX - PatrolRange || entity.X > startX + PatrolRange) direction = -direction;
        }
        if (jumping && ++jumpFrame > MaxJumpFrames) jumping = false;
        headVelocity = Math.Min(headVelocity + (jumping ? JumpGravity : HeadGravity), MaxFall);
        entity.Y += headVelocity;
        grounded = false;
        if (ResolveHead(entity)) { grounded = true; jumping = false; }
        chain.Step(entity.X, entity.Y);
        chain.Clamp(entity.X, entity.Y, 256, entity.World.Engine.Read("room_width").Number, entity.World.Engine.Read("room_height").Number);
        ResolveSolids();
    }

    private void Attach(ActorContext entity, ActorContext player)
    {
        if (!constraints.Bind(player)) return;
        victim = player;
        headVelocity = 0;
        jumping = false;
        locked = false;
        (entity.X, entity.Y) = constraints.BoundHands(player.Id) ?? Hands(player);
        chain.Reset(Segments, entity.X, entity.Y);
        Console.WriteLine("ROPE_ATTACH actor=" + player.Id + " x=" + entity.X + " y=" + entity.Y);
    }

    public static void Clear() => Live.Clear();

    public static void Prepare()
    {
        foreach (RopeCreature creature in Live)
        {
            if (creature.self is not { } entity || entity.Instance.Destroyed) continue;
            creature.Draw(entity);
        }
    }

    private void Draw(ActorContext entity)
    {
        if (victim is { } player && !player.Instance.Destroyed
            && player.Part("HAND3_ABOVE") is { } above && player.Part("HAND3_BELOW") is { } below)
        {
            double x = (above.X + below.X) / 2, y = (above.Y + below.Y) / 2;
            double dx = x - entity.X, dy = y - entity.Y;
            if (Math.Abs(dx) >= .01 || Math.Abs(dy) >= .01)
            {
                entity.X = x;
                entity.Y = y;
                for (int index = 0; index < chain.Count; index++) { var (px, py) = chain[index]; chain.Place(index, px + dx, py + dy); }
            }
            RenderBound(entity, above, below);
            return;
        }
        Render(entity);
    }

    private void RenderBound(ActorContext entity, ActorContext above, ActorContext below)
    {
        double leftX = above.X, leftY = above.Y, rightX = below.X, rightY = below.Y;
        double dx = rightX - leftX, dy = rightY - leftY, distance = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(-dy, dx) * 180 / Math.PI;
        var presentation = entity.Services.Presentation;
        presentation.Sprite(entity.Id, "rope.head", "ropebonds.rope_head", leftX, leftY, angle, 2, 2, 4, 2, ropeDepth);
        presentation.Sprite(entity.Id, "rope.body.0", "ropebonds.rope_body", (leftX + rightX) / 2, (leftY + rightY) / 2, angle, Math.Clamp(distance / 9, .9, 1.1), 2, 4.5, 2, ropeDepth);
        presentation.Sprite(entity.Id, "rope.head.tail", "ropebonds.rope_head", rightX, rightY, angle + 180, 2, 2, 4, 2, ropeDepth);
        if (bodiesCleared) return;
        bodiesCleared = true;
        for (int index = 1; index < chain.Count - 1; index++) presentation.Remove(entity.Id, "rope.body." + index);
    }

    private void Follow(ActorContext entity)
    {
        (double X, double Y)? bound = constraints.BoundHands(victim!.Id);
        (entity.X, entity.Y) = bound ?? Hands(victim!);
        if (bound is not null && !locked) { locked = true; Console.WriteLine("ROPE_LOCK source=clip x=" + entity.X.ToString("0.0") + " y=" + entity.Y.ToString("0.0")); }
        headVelocity = 0;
        jumping = false;
        chain.Step(entity.X, entity.Y);
        ResolveSolids();
    }

    private void Release(ActorContext entity)
    {
        if (victim is { } player)
        {
            if (!player.Instance.Destroyed) constraints.Release(player);
            victim = null;
        }
        headVelocity = 0;
        jumping = false;
        bodiesCleared = false;
        if (!entity.Instance.Destroyed)
        {
            entity.X = startX;
            entity.Y = startY;
        }
        chain.Reset(Segments, startX, startY);
    }

    private void Reset(ActorContext entity)
    {
        startX = entity.X;
        startY = entity.Y;
        victim = null;
        direction = 1;
        headVelocity = 0;
        grounded = false;
        jumping = false;
        jumpFrame = 0;
        chain.Reset(Segments, startX, startY);
        CollectSolids(entity);
    }

    private void CollectSolids(ActorContext entity)
    {
        solids.Clear();
        foreach (GmInstance instance in entity.World.Instances)
        {
            if (instance.Destroyed || !instance.Active || instance.Id == entity.Id || !instance.Scope.ReadOr("solid").Truth) continue;
            GmBounds bounds = entity.World.Bounds(instance);
            if (!bounds.Empty) solids.Add(bounds);
        }
    }

    private void Jump()
    {
        jumping = true;
        jumpFrame = 0;
        headVelocity = JumpImpulse;
    }

    private static (double X, double Y) Hands(ActorContext player)
    {
        ActorContext? left = player.Part("HAND3_ABOVE");
        ActorContext? right = player.Part("HAND3_BELOW");
        if (left is not null && right is not null) return ((left.X + right.X) / 2, (left.Y + right.Y) / 2);
        if (left is not null) return (left.X, left.Y);
        return right is not null ? (right.X, right.Y) : (player.X, player.Y);
    }

    private bool ResolveHead(ActorContext entity)
    {
        bool grounded = false;
        foreach (GmBounds bounds in solids)
        {
            double x = entity.X, y = entity.Y;
            if (x < bounds.Left - HeadRadius || x > bounds.Right + HeadRadius
                || y < bounds.Top - HeadRadius || y > bounds.Bottom + HeadRadius) continue;
            double left = x - (bounds.Left - HeadRadius);
            double right = bounds.Right + HeadRadius - x;
            double top = y - (bounds.Top - HeadRadius);
            double bottom = bounds.Bottom + HeadRadius - y;
            double smallest = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
            if (smallest == left) entity.X = bounds.Left - HeadRadius - .01;
            else if (smallest == right) entity.X = bounds.Right + HeadRadius + .01;
            else if (smallest == top) { entity.Y = bounds.Top - HeadRadius - .01; headVelocity = 0; grounded = true; }
            else { entity.Y = bounds.Bottom + HeadRadius + .01; headVelocity = 0; }
        }
        return grounded;
    }

    private void ResolveSolids()
    {
        for (int index = 1; index < chain.Count; index++)
        {
            var (x, y) = chain[index];
            foreach (GmBounds bounds in solids)
            {
                if (x < bounds.Left - HalfThickness || x > bounds.Right + HalfThickness
                    || y < bounds.Top - HalfThickness || y > bounds.Bottom + HalfThickness) continue;
                double left = x - (bounds.Left - HalfThickness);
                double right = bounds.Right + HalfThickness - x;
                double top = y - (bounds.Top - HalfThickness);
                double bottom = bounds.Bottom + HalfThickness - y;
                double smallest = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
                if (smallest == left) x = bounds.Left - HalfThickness - .01;
                else if (smallest == right) x = bounds.Right + HalfThickness + .01;
                else if (smallest == top) y = bounds.Top - HalfThickness - .01;
                else y = bounds.Bottom + HalfThickness + .01;
            }
            chain.Place(index, x, y);
        }
    }

    private void Render(ActorContext entity)
    {
        if (chain.Count < 2) return;
        var presentation = entity.Services.Presentation;
        presentation.Sprite(entity.Id, "rope.head", "ropebonds.rope_head", chain[0].X, chain[0].Y, Angle(chain[0], chain[1]), 2, 2, 2, 2, ropeDepth);
        int tail = chain.Count - 1;
        presentation.Sprite(entity.Id, "rope.head.tail", "ropebonds.rope_head", chain[tail].X, chain[tail].Y, Angle(chain[tail], chain[tail - 1]), 2, 2, 2, 2, ropeDepth);
        for (int index = 1; index < chain.Count; index++)
        {
            var from = chain[index - 1];
            var to = chain[index];
            double distance = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
            presentation.Sprite(entity.Id, "rope.body." + (index - 1), "ropebonds.rope_body",
                (from.X + to.X) / 2, (from.Y + to.Y) / 2, Angle(from, to), Math.Max(.4, distance / 9), 2, 4.5, 2, ropeDepth);
        }
    }

    private static double Angle((double X, double Y) from, (double X, double Y) to) => Math.Atan2(-(to.Y - from.Y), to.X - from.X) * 180 / Math.PI;

    private static ActorContext? Player(ActorContext entity) =>
        entity.World.Constants.ContainsKey("objPlayerDoll") ? entity.Services.Instances.FindObject("objPlayerDoll") : null;
}
