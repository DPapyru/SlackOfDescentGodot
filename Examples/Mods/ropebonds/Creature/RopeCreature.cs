using System;
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
    private const double SegmentLength = 6;
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
    private Random random = new();
    private ActorContext? victim;
    private BindPart hand;
    private int direction = 1, phaseFrame, jumpFrame;
    private double headVelocity;
    private bool jumping;

    public string Id => "ropebonds.creature";

    public void Created(ActorContext entity) => Reset(entity);
    public void RoomEntered(ActorContext entity) => Reset(entity);
    public void RoomExited(ActorContext entity) => Release();
    public void Detach(ActorContext entity) => Release();
    public void Destroyed(ActorContext entity) => Release();

    public void Update(ActorContext entity, FrameContext frame)
    {
        if (chain.Count == 0) Reset(entity);
        if (victim is not null)
        {
            if (victim.Instance.Destroyed || !entity.World.Instances.Contains(victim.Instance)) Release();
            else return;
        }
        var player = Player(entity);
        double headX = entity.X, headY = entity.Y;
        bool grounded = Solid(entity, headX, headY + 1);
        if (player is null) jumping = false;
        else if (!jumping && grounded)
        {
            double distance = player.X - headX;
            if (Math.Abs(distance) <= DetectRange && Math.Sign(distance) is int facing && facing != 0) direction = facing;
            if (Math.Abs(distance) <= JumpRange)
            {
                jumping = true; jumpFrame = 0; headVelocity = JumpVelocity;
            }
        }
        if (jumping)
        {
            jumpFrame++;
            headX += direction * JumpStep;
            headVelocity = Math.Min(headVelocity + chain.Gravity, VerletChain.MaxVelocity);
            headY += headVelocity;
            if (player is not null && Math.Abs(player.X - headX) <= BindRange && Bind(player)) jumping = false;
            else if (jumpFrame > JumpTimeout || Solid(entity, headX, headY + 1)) jumping = false;
        }
        else
        {
            phaseFrame = (phaseFrame + 1) % (ExtendFrames + ContractFrames);
            if (phaseFrame <= ExtendFrames)
            {
                double next = headX + direction * ExtendSpeed;
                if (Solid(entity, next + direction * 2, headY)) direction = -direction;
                else headX = next;
            }
            headVelocity = Solid(entity, headX, headY + 1) ? 0 : Math.Min(headVelocity + chain.Gravity, VerletChain.MaxVelocity);
            if (!Solid(entity, headX, headY + headVelocity + 1)) headY += headVelocity;
        }
        entity.X = headX; entity.Y = headY;
        chain.Step(headX, headY);
        chain.Collide((x, y) => Solid(entity, x, y));
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

    private static bool Solid(ActorContext entity, double x, double y)
    {
        foreach (var instance in entity.World.Instances)
        {
            if (instance.Destroyed || !instance.Active || instance.Id == entity.Id || !instance.Scope.ReadOr("solid").Truth) continue;
            var bounds = entity.World.Bounds(instance);
            if (!bounds.Empty && x >= bounds.Left && x <= bounds.Right && y >= bounds.Top && y <= bounds.Bottom) return true;
        }
        return false;
    }
}
