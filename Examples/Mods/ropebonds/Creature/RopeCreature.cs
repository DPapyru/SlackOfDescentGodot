using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SlackOfDescent;

namespace RopeBonds;

public sealed partial class RopeBondsMod
{
    partial void RegisterCreatures(ModContext context) => context.Content.RegisterEntity(new NativeEntityDefinition
    {
        Id = "ropebonds.creature",
        Name = "RopeBonds creature",
        Description = "Inchworm rope creature that binds one hand of the nearby player.",
        BehaviorId = "ropebonds.creature",
        Width = 10,
        Height = 10
    }, () => new RopeCreature(Constraints));
}

public sealed class RopeCreature(ConstraintService constraints) : IEntityBehavior
{
    private const int Segments = 8;
    private const double SegmentLength = 11;
    private const double RopeDepth = -100000;
    private const double ExtendSpeed = .5;
    private const double JumpStep = .8;
    private const double DetectRange = 96;
    private const double JumpRange = 48;
    private const double BindRange = 12;
    private const double JumpVelocity = -3.2;
    private const int ExtendFrames = 20;
    private const int ContractFrames = 14;
    private const int JumpTimeout = 90;
    private readonly VerletChain chain = new(SegmentLength, .35);
    private readonly List<GmBounds> solids = new();
    private Random random = new();
    private ActorContext? victim;
    private BindPart hand;
    private int direction = 1, phaseFrame, jumpFrame;
    private double headVelocity;
    private bool jumping;
    private bool render = true;
    private bool diagnosed;

    public string Id => "ropebonds.creature";

    public void Created(ActorContext entity) => Reset(entity);
    public void RoomEntered(ActorContext entity) => Reset(entity);
    public void RoomExited(ActorContext entity) => Release();
    public void Detach(ActorContext entity) => Release();
    public void Destroyed(ActorContext entity) => Release();

    public void Update(ActorContext entity, FrameContext frame)
    {
        if (chain.Count == 0) Reset(entity);
        if (DropInvalidVictim()) return;
        var player = Player(entity);
        CollectSolids(entity);
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
        if (chain.Count < 2) return;
        try
        {
            var presentation = entity.Services.Presentation;
            for (int index = 1; index < chain.Count; index++)
            {
                var from = chain[index - 1]; var to = chain[index];
                presentation.Line(entity.Id, "rope.line." + (index - 1), from.X, from.Y, to.X, to.Y, 1.0 / 30, 0xff6600, 2);
            }
            if (render)
            {
                presentation.Sprite(entity.Id, "rope.head", "ropebonds.rope_head", chain[0].X, chain[0].Y, Angle(chain[1], chain[0]), 2, 2, 2, 2, RopeDepth);
                for (int index = 1; index < chain.Count; index++)
                {
                    var from = chain[index - 1]; var to = chain[index];
                    presentation.Sprite(entity.Id, "rope.body." + (index - 1), "ropebonds.rope_body", (from.X + to.X) / 2, (from.Y + to.Y) / 2, Angle(from, to), chain.Spacing(index) / 11, 2, 5.5, 2, RopeDepth);
                }
            }
        }
        catch (Exception error)
        {
            render = false;
            Console.Error.WriteLine("ROPEBONDS render failed: " + error.Message);
        }
        Diagnose(entity);
    }

    private void Diagnose(ActorContext entity)
    {
        if (diagnosed) return;
        diagnosed = true;
        var sprites = entity.Services.Presentation.Sprites.Where(sprite => sprite.Owner == entity.Id).ToArray();
        string first = sprites.Length == 0 ? "none" : sprites[0].X + "," + sprites[0].Y + "," + sprites[0].Angle + "," + sprites[0].ScaleX;
        Console.WriteLine("ROPEBONDS_RENDER owner=" + entity.Id + " active=" + entity.Instance.Active + " sprites=" + sprites.Length
            + " head=" + Resolve(entity.Services, "ropebonds.rope_head") + " body=" + Resolve(entity.Services, "ropebonds.rope_body")
            + " first=" + first);
    }

    private static string Resolve(NativeModServices services, string id)
    {
        var registry = typeof(NativeModServices).GetField("Registry", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(services) as ContentRegistry;
        return registry is not null && registry.Resources.TryGetValue(id, out var path) ? path : "(unresolved)";
    }

    private static double Angle((double X, double Y) from, (double X, double Y) to) => Math.Atan2(-(to.Y - from.Y), to.X - from.X) * 180 / Math.PI;

    private bool DropInvalidVictim()
    {
        if (victim is null) return false;
        if (!victim.Instance.Destroyed) return true;
        Release();
        return false;
    }

    private void ApplyFacing(ActorContext entity, ActorContext player)
    {
        double distance = player.X - entity.X;
        if (Math.Abs(distance) <= DetectRange && Math.Sign(distance) is int facing && facing != 0) direction = facing;
    }

    private bool ShouldJump(ActorContext entity, ActorContext player, bool grounded)
    {
        if (jumping) return true;
        if (!grounded || Math.Abs(player.X - entity.X) > JumpRange) return false;
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
        if (Math.Abs(player.X - headX) <= BindRange && Bind(player)) jumping = false;
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
        entity.X = headX; entity.Y = headY;
        chain.Step(headX, headY);
        chain.Collide((x, y) => Solid(x, y));
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

    private void Reset(ActorContext entity)
    {
        random = new Random(entity.Id);
        jumping = false; phaseFrame = 0; headVelocity = 0;
        chain.Reset(Segments, entity.X, entity.Y);
    }

    private bool Bind(ActorContext player)
    {
        hand = random.Next(2) == 0 ? BindPart.HandAbove : BindPart.HandBelow;
        if (!constraints.Bind(player, hand)) return false;
        victim = player;
        player.Write("ropeBound", 1);
        return true;
    }

    private void Release()
    {
        if (victim is null) return;
        constraints.Release(victim, hand);
        if (!victim.Instance.Destroyed && !constraints.IsBound(victim.Id)) victim.Write("ropeBound", 0);
        victim = null;
    }

    private static ActorContext? Player(ActorContext entity)
    {
        if (!entity.World.Constants.TryGetValue("objPlayerDoll", out var kind)) return null;
        foreach (var instance in entity.World.Instances)
            if (!instance.Destroyed && instance.Active && entity.World.IsObject(instance.Object, kind.Int)) return new(entity.World, instance);
        return null;
    }
}
