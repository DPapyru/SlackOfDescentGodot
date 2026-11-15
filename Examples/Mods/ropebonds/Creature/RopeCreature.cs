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
    private const double RopeDepth = -100000;
    private readonly HandConstraint constraints;
    private readonly VerletChain chain = new(SegmentLength, 2);
    private readonly List<GmBounds> solids = [];
    private ActorContext? victim;
    private double startX, startY, direction = 1, headVelocity;

    public RopeCreature(HandConstraint constraints) => this.constraints = constraints;
    public string Id => "ropebonds.creature";

    public void Created(ActorContext entity) { entity.Write("HP", 10000); Reset(entity); }
    public void RoomEntered(ActorContext entity) => Reset(entity);
    public void RoomExited(ActorContext entity) => Release(entity);
    public void Destroyed(ActorContext entity) => Release(entity);
    public void Detach(ActorContext entity) => Release(entity);

    public void Update(ActorContext entity, FrameContext frame)
    {
        if (chain.Count == 0) Reset(entity);
        if (solids.Count == 0) CollectSolids(entity);
        if (victim is not null && (victim.Instance.Destroyed || !victim.Instance.Active)) Release(entity);
        if (victim is not null && !constraints.IsBound(victim.Id)) Release(entity);
        if (victim is not null) { Follow(entity); return; }
        ActorContext? player = Player(entity);
        if (player is not null && Math.Abs(player.X - entity.X) < DetectRange && Math.Abs(player.Y - entity.Y) < 140)
        {
            int toward = Math.Sign(player.X - entity.X);
            if (toward != 0) direction = toward;
            if (Math.Abs(player.X - entity.X) <= AttachRange) { Attach(entity, player); return; }
            entity.X += direction * Speed;
        }
        else
        {
            entity.X += direction * Speed * .5;
            if (entity.X < startX - PatrolRange || entity.X > startX + PatrolRange) direction = -direction;
        }
        headVelocity = Math.Min(headVelocity + HeadGravity, MaxFall);
        entity.Y += headVelocity;
        ResolveHead(entity);
        chain.Step(entity.X, entity.Y);
        chain.Clamp(entity.X, entity.Y, 256, entity.World.Engine.Read("room_width").Number, entity.World.Engine.Read("room_height").Number);
        ResolveSolids();
        Render(entity);
    }

    private void Attach(ActorContext entity, ActorContext player)
    {
        if (!constraints.Bind(player)) return;
        victim = player;
        headVelocity = 0;
        entity.X = player.X;
        entity.Y = player.Y;
        chain.Reset(Segments, entity.X, entity.Y);
        Console.WriteLine("ROPE_ATTACH actor=" + player.Id + " x=" + entity.X + " y=" + entity.Y);
    }

    private void Follow(ActorContext entity)
    {
        ActorContext player = victim!;
        ActorContext? left = player.Part("HAND3_ABOVE");
        ActorContext? right = player.Part("HAND3_BELOW");
        if (left is not null && right is not null) { entity.X = (left.X + right.X) / 2; entity.Y = (left.Y + right.Y) / 2; }
        else if (left is not null) { entity.X = left.X; entity.Y = left.Y; }
        headVelocity = 0;
        chain.Step(entity.X, entity.Y);
        ResolveSolids();
        Render(entity);
    }

    private void Release(ActorContext entity)
    {
        if (victim is { } player)
        {
            if (!player.Instance.Destroyed) constraints.Release(player);
            victim = null;
        }
        headVelocity = 0;
        if (!entity.Instance.Destroyed)
        {
            RemoveSprites(entity);
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

    private void ResolveHead(ActorContext entity)
    {
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
            else if (smallest == top) { entity.Y = bounds.Top - HeadRadius - .01; headVelocity = 0; }
            else { entity.Y = bounds.Bottom + HeadRadius + .01; headVelocity = 0; }
        }
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
        presentation.Sprite(entity.Id, "rope.head", "ropebonds.rope_head", chain[0].X, chain[0].Y, Angle(chain[0], chain[1]), 2, 2, 2, 2, RopeDepth);
        int tail = chain.Count - 1;
        presentation.Sprite(entity.Id, "rope.head.tail", "ropebonds.rope_head", chain[tail].X, chain[tail].Y, Angle(chain[tail], chain[tail - 1]), 2, 2, 2, 2, RopeDepth);
        for (int index = 1; index < chain.Count; index++)
        {
            var from = chain[index - 1];
            var to = chain[index];
            double distance = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
            presentation.Sprite(entity.Id, "rope.body." + (index - 1), "ropebonds.rope_body",
                (from.X + to.X) / 2, (from.Y + to.Y) / 2, Angle(from, to), Math.Max(.4, distance / 9), 2, 4.5, 2, RopeDepth);
        }
    }

    private void RemoveSprites(ActorContext entity)
    {
        var presentation = entity.Services.Presentation;
        presentation.Remove(entity.Id, "rope.head");
        presentation.Remove(entity.Id, "rope.head.tail");
        for (int index = 0; index < Segments; index++) presentation.Remove(entity.Id, "rope.body." + index);
    }

    private static double Angle((double X, double Y) from, (double X, double Y) to) => Math.Atan2(-(to.Y - from.Y), to.X - from.X) * 180 / Math.PI;

    private static ActorContext? Player(ActorContext entity)
    {
        if (!entity.World.Constants.TryGetValue("objPlayerDoll", out var kind)) return null;
        foreach (GmInstance instance in entity.World.Instances)
            if (!instance.Destroyed && instance.Active && entity.World.IsObject(instance.Object, kind.Int)) return new(entity.World, instance);
        return null;
    }
}
