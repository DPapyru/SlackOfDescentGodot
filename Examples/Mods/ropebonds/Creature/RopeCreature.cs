using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed partial class RopeBondsMod
{
    partial void RegisterCreatures(ModContext context)
    {
        context.Content.RegisterInput(new() { Id = "ropebonds.qte", Name = "绳索挣脱", Description = "被绳索生物缠住时连续按键挣脱。", DefaultKey = 90 });
        context.Content.RegisterEntity(new NativeEntityDefinition
    {
        Id = "ropebonds.creature",
        Name = "RopeBonds creature",
        Description = "Inchworm rope creature that binds one hand of the nearby player.",
        BehaviorId = "ropebonds.creature",
        Width = 10,
        Height = 10
        }, () => new RopeCreature(Constraints));
    }
}

public sealed class RopeCreature(ConstraintService constraints) : IEntityBehavior
{
    private const int Segments = 8;
    private const double SegmentLength = 11;
    private const double RopeDepth = -100000;
    private const double BoundAngle = 35;
    private const double ExtendSpeed = .5;
    private const double JumpStep = .8;
    private const double DetectRange = 96;
    private const double JumpRange = 48;
    private const double BindRange = 12;
    private const double JumpVelocity = -3.2;
    private const int ExtendFrames = 20;
    private const int ContractFrames = 14;
    private const int JumpTimeout = 90;
    private const double WrapDuration = .6;
    private const double CoilStep = 1.05;
    private readonly VerletChain chain = new(SegmentLength, .35);
    private readonly List<GmBounds> solids = new();
    private ActorContext? victim;
    private int direction = 1, phaseFrame, jumpFrame;
    private double headVelocity;
    private bool jumping;
    private bool render = true;
    private bool reported;
    private bool qte;
    private int qteHits;
    private double qteTime;
    private double qteCooldown;
    private int previousBound;
    private bool wrapping;
    private double wrapTime;
    private static readonly List<RopeCreature> All = new();
    private double lastX, lastY;

    public string Id => "ropebonds.creature";

    public void Created(ActorContext entity)
    {
        entity.Write("HP", 10000);
        Reset(entity);
    }
    public void RoomEntered(ActorContext entity) => Reset(entity);
    public void RoomExited(ActorContext entity) => Release();
    public void Detach(ActorContext entity) => Release();
    public void Destroyed(ActorContext entity) => Release();

    public void Update(ActorContext entity, FrameContext frame)
    {
        if (chain.Count == 0) Reset(entity);
        CollectSolids(entity);
        qteCooldown = Math.Max(0, qteCooldown - frame.Delta);
        if (victim is not null)
        {
            if (victim.Instance.Destroyed) Release();
            else { Coil(entity, victim, 1); Render(entity); }
            return;
        }
        var player = Player(entity);
        if (qte)
        {
            qteTime += frame.Delta;
            if (entity.Services.Input.Pressed("ropebonds.qte", frame.Input)) qteHits++;
            entity.Services.Presentation.Text(entity.Id, "qte", "QTE x" + Math.Max(0, 8 - qteHits), 12, 96);
            if (qteTime < 2.5) return;
            qte = false;
            entity.Services.Presentation.Remove(entity.Id, "qte");
            if (qteHits >= 8) qteCooldown = 3;
            else if (player is not null) { wrapping = true; wrapTime = 0; }
            return;
        }
        if (wrapping)
        {
            if (player is null) { wrapping = false; return; }
            wrapTime += frame.Delta;
            double progress = Math.Min(1, wrapTime / WrapDuration);
            Coil(entity, player, progress);
            Render(entity);
            if (progress < 1) return;
            wrapping = false;
            if (!Bind(player)) qteCooldown = 1.5;
            return;
        }
        bool grounded = Solid(entity.X, entity.Y + 1);
        double headX = entity.X, headY = entity.Y;
        if (player is not null) ApplyFacing(entity, player);
        if (player is not null && ShouldJump(entity, player, grounded)) JumpMotion(player, ref headX, ref headY);
        else Crawl(ref headX, ref headY);
        Commit(entity, ref headX, ref headY);
        Render(entity);
    }

    private void Render(ActorContext entity)
    {
        if (chain.Count < 2 || !render || !chain.IsFinite) return;
        try
        {
            var presentation = entity.Services.Presentation;
            presentation.Sprite(entity.Id, "rope.head", "ropebonds.rope_head", chain[0].X, chain[0].Y - 6, Angle(chain[0], chain[1]), 2, 2, 4, 2, RopeDepth);
            int tail = chain.Count - 1;
            presentation.Sprite(entity.Id, "rope.head.tail", "ropebonds.rope_head", chain[tail].X, chain[tail].Y - 6, Angle(chain[tail], chain[tail - 1]), 2, 2, 4, 2, RopeDepth);
            for (int index = 1; index < chain.Count; index++)
            {
                var from = chain[index - 1]; var to = chain[index];
                presentation.Sprite(entity.Id, "rope.body." + (index - 1), "ropebonds.rope_body", (from.X + to.X) / 2, (from.Y + to.Y) / 2 - 6, Angle(from, to), chain.Spacing(index) / 9, 2, 5.5, 2, RopeDepth);
            }
            if (reported) return;
            reported = true;
            int count = presentation.Sprites.Count(sprite => sprite.Owner == entity.Id);
            if (count != chain.Count + 1) Console.Error.WriteLine("ROPEBONDS sprite count " + count + " expected " + (chain.Count + 1));
        }
        catch (Exception error)
        {
            render = false;
            Console.Error.WriteLine("ROPEBONDS render failed: " + error.Message);
        }
    }

    internal static void ReleaseAll()
    {
        foreach (var creature in All.ToArray()) creature.Release();
        All.Clear();
    }

    private static double Angle((double X, double Y) from, (double X, double Y) to) => Math.Atan2(-(to.Y - from.Y), to.X - from.X) * 180 / Math.PI;

    private void Coil(ActorContext entity, ActorContext player, double progress)
    {
        var (centerX, centerY) = Grasp(player);
        for (int index = 0; index < chain.Count; index++)
        {
            var (x, y) = chain[index];
            double angle = (direction > 0 ? 0 : Math.PI) + index * CoilStep;
            double radius = 4 + index * 1.1;
            double targetX = centerX + Math.Cos(angle) * radius;
            double targetY = centerY + Math.Sin(angle) * radius * .6;
            chain.Place(index, x + (targetX - x) * progress, y + (targetY - y) * progress);
        }
        entity.X = lastX = chain[0].X;
        entity.Y = lastY = chain[0].Y;
    }

    private static (double X, double Y) Grasp(ActorContext player)
    {
        foreach (var names in new[] { ConstraintService.Hands, ConstraintService.Feet, ConstraintService.Body })
        {
            double x = 0, y = 0; int count = 0;
            foreach (string name in names)
                if (player.Part(name) is { } part) { x += part.X; y += part.Y; count++; }
            if (count > 0) return (x / count, y / count);
        }
        return (player.X, player.Y);
    }

    private void ApplyFacing(ActorContext entity, ActorContext player)
    {
        double distance = player.X - entity.X;
        if (Math.Abs(distance) <= DetectRange && Math.Sign(distance) is int facing && facing != 0) direction = facing;
    }

    private bool ShouldJump(ActorContext entity, ActorContext player, bool grounded)
    {
        if (jumping) return true;
        if (qteCooldown > 0 || !grounded || Math.Abs(player.X - entity.X) > JumpRange) return false;
        jumping = true; jumpFrame = 0; headVelocity = JumpVelocity;
        return true;
    }

    private void JumpMotion(ActorContext player, ref double headX, ref double headY)
    {
        jumpFrame++;
        double nextX = headX + direction * JumpStep;
        if (Solid(nextX + direction * 2, headY)) direction = -direction;
        else headX = nextX;
        headVelocity = Math.Min(headVelocity + chain.Gravity, VerletChain.MaxVelocity);
        double nextY = headY + headVelocity;
        if (!Solid(headX, nextY + 2)) headY = nextY;
        else { headVelocity = 0; jumping = false; }
        if (Math.Abs(player.X - headX) <= BindRange) { qte = true; qteHits = 0; qteTime = 0; jumping = false; }
        else if (jumpFrame > JumpTimeout) jumping = false;
    }

    private void Crawl(ref double headX, ref double headY)
    {
        phaseFrame = (phaseFrame + 1) % (ExtendFrames + ContractFrames);
        if (phaseFrame <= ExtendFrames)
        {
            double next = headX + direction * ExtendSpeed;
            if (Solid(next + direction * 2, headY)) direction = -direction;
            else headX = next;
        }
        headVelocity = Solid(headX, headY + 1) ? 0 : Math.Min(headVelocity + chain.Gravity, VerletChain.MaxVelocity);
        double nextY = headY + headVelocity;
        if (!Solid(headX, nextY + 1)) headY = nextY;
    }

    private void Commit(ActorContext entity, ref double headX, ref double headY)
    {
        double width = entity.World.Engine.Read("room_width").Number, height = entity.World.Engine.Read("room_height").Number;
        headX = Math.Clamp(headX, 2, width - 2); headY = Math.Clamp(headY, 2, height - 2);
        if (!double.IsFinite(headX) || !double.IsFinite(headY) || Solid(headX, headY)) { headX = lastX; headY = lastY; headVelocity = 0; }
        else { lastX = headX; lastY = headY; }
        entity.X = headX; entity.Y = headY;
        chain.Step(headX, headY);
        chain.Collide((x, y) => Solid(x, y));
        chain.Clamp(headX, headY, 512, width, height);
    }

    private void CollectSolids(ActorContext entity)
    {
        solids.Clear();
        foreach (var instance in entity.World.Instances)
        {
            if (instance.Destroyed || !instance.Active || instance.Id == entity.Id || !instance.Scope.ReadOr("solid").Truth) continue;
            var bounds = entity.World.Bounds(instance);
            if (!bounds.Empty) solids.Add(bounds);
        }
    }

    private bool Solid(double x, double y)
    {
        foreach (var bounds in solids)
            if (x >= bounds.Left && x <= bounds.Right && y >= bounds.Top && y <= bounds.Bottom) return true;
        return false;
    }

    private static void DropWeapon(ActorContext player)
    {
        if (!player.World.Globals.Values.ContainsKey("CurrentWeapon")) player.World.Globals.Write("CurrentWeapon", 0);
        player.Call("dropWeapon");
    }

    private void Reset(ActorContext entity)
    {
        if (!All.Contains(this)) All.Add(this);
        CollectSolids(entity);
        double x = entity.X, y = entity.Y;
        for (int attempts = 0; attempts < 64 && Solid(x, y); attempts++) y -= 1;
        entity.X = x; entity.Y = y;
        lastX = x; lastY = y;
        jumping = false; phaseFrame = 0; headVelocity = 0; wrapping = false; qte = false;
        chain.Reset(Segments, x, y);
    }

    private bool Bind(ActorContext player)
    {
        if (!constraints.Bind(player, BindPart.Hands)) return false;
        constraints.Offset(player, BoundAngle);
        previousBound = (int)player.Read("BINDED");
        player.Write("BINDED", 1);
        victim = player;
        player.Write("ropeBound", 1);
        DropWeapon(player);
        return true;
    }

    private void Release()
    {
        All.Remove(this);
        wrapping = false;
        if (victim is null) return;
        constraints.Release(victim);
        if (!victim.Instance.Destroyed)
        {
            victim.Write("BINDED", previousBound);
            if (!constraints.IsBound(victim.Id)) victim.Write("ropeBound", 0);
        }
        victim = null;
        chain.Reset(Segments, chain[0].X, chain[0].Y);
        lastX = chain[0].X; lastY = chain[0].Y;
    }

    private static ActorContext? Player(ActorContext entity)
    {
        if (!entity.World.Constants.TryGetValue("objPlayerDoll", out var kind)) return null;
        foreach (var instance in entity.World.Instances)
            if (!instance.Destroyed && instance.Active && entity.World.IsObject(instance.Object, kind.Int)) return new(entity.World, instance);
        return null;
    }
}
