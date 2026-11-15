using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed partial class RopeBondsMod
{
    partial void RegisterCreatures(ModContext context)
    {
        context.Subscribe("ropebonds.anchor", NativeEventNames.StepEnd, NativeEventPhase.After, _ => RopeCreature.Anchor());
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
    private const double SegmentLength = 9;
    private const double RopeDepth = -100000;
    private const double BoundAngle = 35;
    private const double ExtendSpeed = .5;
    private const double JumpStep = 1.6;
    private const double DetectRange = 96;
    private const double JumpRange = 48;
    private const double BindRange = 12;
    private const double JumpVelocity = -8.5;
    private const int ExtendFrames = 20;
    private const int ContractFrames = 14;
    private const int JumpTimeout = 120;
    private const double WrapDuration = .6;
    private const double HalfSize = 5;
    private const int StuckFrames = 30;
    private const int LoopPoints = 5;
    private const int WrapSegments = LoopPoints * 2 + 2;
    private const double LoopStep = Math.PI * 2 / LoopPoints;
    private const double RightBase = (LoopPoints - 1) * LoopStep;
    private const double CoilRadius = 5;
    private const double CoilLift = 4;
    private readonly VerletChain chain = new(SegmentLength, .35);
    private readonly List<GmBounds> solids = new();
    private ActorContext? victim;
    private int direction = 1, phaseFrame, jumpFrame;
    private double headVelocity;
    private bool jumping;
    private bool render = true;
    private bool reported;
    private bool qte;
    private int bindCounter;
    private double qteThreshold = 10;
    private double qteTime;
    private double qteCooldown;
    private int previousBound;
    private bool wrapping;
    private double wrapTime;
    private static readonly List<RopeCreature> All = new();
    private double lastX, lastY;
    private int stuckFrames;
    private ActorContext? frozen;
    private ActorContext? self;
    private readonly Dictionary<int, int> hiddenPickups = new();
    private double previousCamera = 1;
    private double frozenX, frozenY;
    private bool reportedBones;
    private bool reportedCoil;

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
        self = entity;
        var traced = Player(entity);
        Trace(entity, traced);
        if (TickBound(entity)) return;
        var player = traced;
        if (qte) { TickQte(entity, frame, player); return; }
        if (wrapping) { TickWrapping(entity, frame, player); return; }
        bool grounded = Solid(entity.X, entity.Y + HalfSize + 1, 1);
        double headX = entity.X, headY = entity.Y;
        if (player is not null) ApplyFacing(entity, player);
        if (player is not null && ShouldJump(entity, player, grounded)) JumpMotion(player, ref headX, ref headY);
        else Crawl(ref headX, ref headY);
        Commit(entity, ref headX, ref headY);
        Render(entity);
    }

    private static void Log(string message)
    {
        if (RopeBondsMod.TraceOn) Console.Error.WriteLine(message);
    }

    private static long traceTick;
    private long boundTick;
    private double struggleFrame;
    private static readonly bool trace = Environment.GetEnvironmentVariable("SLACK_ROPE_TRACE") is { Length: > 0 };

    private void Trace(ActorContext entity, ActorContext? player)
    {
        if (player is null || !trace || traceTick++ % 3 != 0) return;
        var state = player.MotionState;
        Log("ROPE_STATE phase=" + (victim is not null ? "bound" : qte ? "qte" : wrapping ? "wrapping" : "free")
            + " state=" + (state?.State ?? "-") + " frame=" + (state?.Frame ?? -1) + " cycle=" + (state?.Cycle ?? -1) + " suspended=" + (state?.Suspended ?? false)
            + " binded=" + player.Read("BINDED") + " half=" + player.Read("HALFBINDED")
            + " motion=" + player.Read("MotionNum") + " change=" + player.Read("ChangeMotion") + " changeNum=" + player.Read("ChangeMotionNum") + " key=" + player.Read("KeyFrmNum")
            + " qte=" + qteTime + " seek=" + struggleFrame + " dir=" + player.Read("Direction") + " x=" + player.X + " y=" + player.Y + " hand=" + (player.Part("HAND3_ABOVE") is { } h ? h.X + "," + h.Y : "missing"));
    }

    private bool TickBound(ActorContext entity)
    {
        if (victim is null) return false;
        if (victim.Instance.Destroyed || !victim.Instance.Active || !entity.World.Instances.Contains(victim.Instance)) { Release(); return true; }
        if (chain.Count != WrapSegments) chain.Reset(WrapSegments, chain[0].X, chain[0].Y);
        Coil(entity, victim, 1);
        RenderBound(victim);
        BlockPickups(victim);
        if (boundTick++ % 3 == 0)
        {
            var upper = victim.Part("HAND3_ABOVE");
            var lower = victim.Part("HAND3_BELOW");
            Log("ROPE_BOUND frozen=" + (frozen is null ? "null" : "set") + " count=" + chain.Count
                + " d0a=" + Delta(0, upper) + " d5b=" + Delta(5, lower) + " d6b=" + Delta(6, lower) + " d11b=" + Delta(11, lower) + " radius=" + Radius(victim));
        }
        Render(entity);
        return true;
    }

    private void TickQte(ActorContext entity, FrameContext frame, ActorContext? player)
    {
        Pin();
        qteTime += frame.Delta;
        if (player is not null)
        {
            if (player.MotionState?.State != "struggle") Play(player, "ropebonds.struggle");
            else
            {
                struggleFrame = (struggleFrame + 1) % 40;
                try { player.Services.Motions.Seek(player, struggleFrame); }
                catch (InvalidOperationException) { }
            }
        }
        if (Pressed(Controller(player))) bindCounter++;
        if (player is not null) { Coil(entity, player, 1); RenderBound(player); }
        if (qteTime < 2.5) return;
        qte = false;
        Unfreeze();
        if (player is not null) Stop(player);
        if (player is not null) { Unembed(player); ReportGeometry(player); }
        if (player is null || bindCounter >= qteThreshold)
        {
            Retreat(entity, player);
            chain.Reset(Segments, entity.X, entity.Y);
            return;
        }
        if (!Bind(player)) { chain.Reset(Segments, entity.X, entity.Y); qteCooldown = 1.5; }
    }

    private void StartQte(ActorContext player)
    {
        qte = true; bindCounter = 0; qteTime = 0; jumping = false;
        Play(player, "ropebonds.struggle");
        qteThreshold = player.World.Globals.ReadOr("EASY_QS").Truth ? 12 : 25;
    }

    private void Freeze(ActorContext player)
    {
        previousBound = (int)player.Read("BINDED");
        previousCamera = player.World.Globals.ReadOr("Camera", 1).Number;
        player.Write("BINDED", 1);
        frozenX = player.X; frozenY = player.Y;
        frozen = player;
        Log("ROPE_FREEZE camera=" + previousCamera + "->0 binded=" + previousBound + "->1");
    }

    private void Unfreeze()
    {
        if (frozen is not null && !frozen.Instance.Destroyed)
        {
            frozen.Write("BINDED", previousBound);
            Log("ROPE_UNFREEZE camera=" + previousCamera + " binded=1->" + previousBound);
        }
        frozen = null;
    }

    private void Retreat(ActorContext entity, ActorContext? player)
    {
        qteCooldown = 12;
        if (player is not null) direction = player.X > entity.X ? -1 : 1;
    }

    private void BlockPickups(ActorContext victim)
    {
        int blocked = 0;
        foreach (var instance in victim.World.Instances)
        {
            if (instance.Destroyed || instance.Scope.ReadOr("image_index").Int != 3) continue;
            if (!(victim.Services.Instances.ObjectName(instance.Object)?.Contains("Gun", StringComparison.Ordinal) ?? false)) continue;
            if (Math.Abs(instance.Scope.ReadOr("x").Number - victim.X) > 64 || Math.Abs(instance.Scope.ReadOr("y").Number - victim.Y) > 64) continue;
            hiddenPickups.TryAdd(instance.Id, 3);
            instance.Scope.Write("image_index", 2);
            blocked++;
        }
        if (boundTick % 30 == 0) Log("ROPE_GUN blocked=" + blocked + " bound=1");
    }

    private void RestorePickups()
    {
        if (self is { } entity)
            foreach (var pair in hiddenPickups)
                foreach (var instance in entity.World.Instances)
                    if (instance.Id == pair.Key && !instance.Destroyed) instance.Scope.Write("image_index", pair.Value);
        hiddenPickups.Clear();
    }

    private static void Stop(ActorContext player)
    {
        try { player.StopMotion(); }
        catch (Exception) { }
    }

    private static void Play(ActorContext player, string id)
    {
        try { player.PlayMotion(id); }
        catch (Exception) { }
    }

    private static GmInstance? Controller(ActorContext? player) => player is null ? null : NativeActorActions.Controller(player);

    private static bool Pressed(GmInstance? controller) => controller is not null
        && (controller.Scope.ReadOr("left").Int == 2 || controller.Scope.ReadOr("right").Int == 2 || controller.Scope.ReadOr("up").Int == 2 || controller.Scope.ReadOr("down").Int == 2
            || controller.Scope.ReadOr("b1").Int == 2 || controller.Scope.ReadOr("b2").Int == 2 || controller.Scope.ReadOr("b3").Int == 2);

    private void BeginAttach(ActorContext player)
    {
        if (frozen is not null || player.Read("BINDED") != 0 || constraints.IsBound(player.Id) || All.Any(creature => creature.victim?.Id == player.Id)) { jumping = false; qteCooldown = 2; return; }
        jumping = false;
        wrapping = true; wrapTime = 0;
        Freeze(player);
        chain.Reset(WrapSegments, chain[0].X, chain[0].Y);
        DropWeapon(player);
        if (!reportedBones)
        {
            reportedBones = true;
            Log("ROPEBONDS attach wrists " + Wrist(player, "HAND3_ABOVE") + " " + Wrist(player, "HAND3_BELOW") + " player " + player.X + "," + player.Y);
        }
    }

    private double Delta(int index, ActorContext? bonepart) => bonepart is null ? -1 : Math.Round(Math.Sqrt(Math.Pow(chain[index].X - bonepart.X, 2) + Math.Pow(chain[index].Y - bonepart.Y, 2)), 2);

    private static string Wrist(ActorContext player, string name) => player.Part(name) is { } bone ? name + "=" + bone.X + "," + bone.Y : name + "=missing";

    private void Pin()
    {
        if (frozen is null || frozen.Instance.Destroyed) return;
        frozen.X = frozenX; frozen.Y = frozenY;
        frozen.HorizontalSpeed = 0; frozen.VerticalSpeed = 0;
    }

    private void TickWrapping(ActorContext entity, FrameContext frame, ActorContext? player)
    {
        if (player is null || !player.Instance.Active) { wrapping = false; Unfreeze(); chain.Reset(Segments, entity.X, entity.Y); return; }
        Pin();
        wrapTime += frame.Delta;
        double progress = Math.Min(1, wrapTime / WrapDuration);
        Coil(entity, player, progress);
        RenderBound(player);
        if (progress < 1) return;
        wrapping = false;
        Log("ROPEBONDS coil head=" + chain[0].X + "," + chain[0].Y + " mid=" + chain[5].X + "," + chain[5].Y + " tail=" + chain[WrapSegments - 1].X + "," + chain[WrapSegments - 1].Y
            + " " + Wrist(player, "HAND3_ABOVE") + " " + Wrist(player, "HAND3_BELOW"));
        StartQte(player);
    }

    private void Render(ActorContext entity)
    {
        if (chain.Count < 2 || !render || !chain.IsFinite) return;
        try
        {
            var presentation = entity.Services.Presentation;
            presentation.Sprite(entity.Id, "rope.head", "ropebonds.rope_head", chain[0].X, chain[0].Y, Angle(chain[0], chain[1]), 2, 2, 4, 2, RopeDepth);
            int tail = chain.Count - 1;
            presentation.Sprite(entity.Id, "rope.head.tail", "ropebonds.rope_head", chain[tail].X, chain[tail].Y, Angle(chain[tail], chain[tail - 1]), 2, 2, 4, 2, RopeDepth);
            for (int index = 1; index < chain.Count; index++)
            {
                var from = chain[index - 1]; var to = chain[index];
                double dx = to.X - from.X, dy = to.Y - from.Y, distance = Math.Sqrt(dx * dx + dy * dy);
                presentation.Sprite(entity.Id, "rope.body." + (index - 1), "ropebonds.rope_body9", (from.X + to.X) / 2, (from.Y + to.Y) / 2, Angle(from, to), Math.Clamp(distance / 9, .9, 1.1), 2, 4.5, 2, RopeDepth);
            }
            for (int stale = chain.Count - 1; stale <= WrapSegments - 2; stale++) presentation.Remove(entity.Id, "rope.body." + stale);
            if (reported) return;
            reported = true;
            int count = presentation.Sprites.Count(sprite => sprite.Owner == entity.Id);
            if (count != chain.Count + 1) Log("ROPEBONDS sprite count " + count + " expected " + (chain.Count + 1));
        }
        catch (Exception error)
        {
            render = false;
            Log("ROPEBONDS render failed: " + error.Message);
        }
    }

    private const double BoundLift = 4;

    private void RenderBound(ActorContext entity)
    {
        if (!render) return;
        var left = entity.Part("HAND3_ABOVE") ?? entity.Part("HAND3_BELOW");
        var right = entity.Part("HAND3_BELOW") ?? left;
        if (left is null || right is null) return;
        double leftX = left.X, leftY = left.Y - BoundLift, rightX = right.X, rightY = right.Y - BoundLift;
        double dx = rightX - leftX, dy = rightY - leftY, distance = Math.Sqrt(dx * dx + dy * dy), angle = Math.Atan2(-dy, dx) * 180 / Math.PI;
        var presentation = entity.Services.Presentation;
        if (boundTick % 3 == 0) Log("ROPE_PICTURE above=" + left.Instance.Scope.ReadOr("PartPicture").Int + "/" + left.Instance.Scope.ReadOr("image_index").Number
            + " below=" + right.Instance.Scope.ReadOr("PartPicture").Int + "/" + right.Instance.Scope.ReadOr("image_index").Number);
        presentation.Sprite(entity.Id, "rope.head", "ropebonds.rope_head", leftX, leftY, angle, 2, 2, 4, 2, RopeDepth);
        presentation.Sprite(entity.Id, "rope.body.0", "ropebonds.rope_body9", (leftX + rightX) / 2, (leftY + rightY) / 2, angle, Math.Clamp(distance / 9, .9, 1.1), 2, 4.5, 2, RopeDepth);
        presentation.Sprite(entity.Id, "rope.head.tail", "ropebonds.rope_head", rightX, rightY, angle + 180, 2, 2, 4, 2, RopeDepth);
        for (int index = 1; index < WrapSegments - 1; index++) presentation.Remove(entity.Id, "rope.body." + index);
    }

    internal static void Anchor()
    {
        foreach (var creature in All.ToArray())
            if (creature.victim is { } victim && creature.self is { } entity && !victim.Instance.Destroyed)
            {
                creature.Coil(entity, victim, 1);
                creature.RenderBound(victim);
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
        if (chain.Count != WrapSegments) chain.Reset(WrapSegments, chain[0].X, chain[0].Y);
        var left = player.Part("HAND3_ABOVE");
        var right = player.Part("HAND3_BELOW");
        double leftX = left?.X ?? entity.X, leftY = left?.Y ?? entity.Y;
        double rightX = right?.X ?? leftX, rightY = right?.Y ?? leftY;
        double radius = Radius(player);
        double bridgeStartX = leftX + Math.Cos(Math.PI - (LoopPoints - 1) * LoopStep) * radius;
        double bridgeStartY = leftY + Math.Sin(Math.PI - (LoopPoints - 1) * LoopStep) * radius;
        double bridgeEndX = rightX + Math.Cos(RightBase) * radius;
        double bridgeEndY = rightY + Math.Sin(RightBase) * radius;
        for (int index = 0; index < chain.Count; index++)
        {
            (double X, double Y) target = index < LoopPoints ? Orbit(leftX, leftY, Math.PI, index, radius)
                : index < LoopPoints + 2 ? (bridgeStartX + (bridgeEndX - bridgeStartX) * (index - LoopPoints + 1) / 3.0, bridgeStartY + (bridgeEndY - bridgeStartY) * (index - LoopPoints + 1) / 3.0)
                : Orbit(rightX, rightY, RightBase, index - LoopPoints - 2, radius);
            var (x, y) = chain[index];
            chain.Place(index, x + (target.X - x) * progress, y + (target.Y - CoilLift - y) * progress);
        }
        if (!reportedCoil && progress >= 1)
        {
            reportedCoil = true;
            var text = new System.Text.StringBuilder("ROPE_COIL radius=" + radius + " lw=" + leftX + "," + leftY + " rw=" + rightX + "," + rightY + " pts=");
            for (int index = 0; index < chain.Count; index++) text.Append(index + ":" + Math.Round(chain[index].X, 1) + "," + Math.Round(chain[index].Y, 1) + " ");
            Log(text.ToString());
        }
        entity.X = lastX = chain[0].X;
        entity.Y = lastY = chain[0].Y;
    }

    private static double Radius(ActorContext player)
    {
        double above = Reach(player, "HAND3_ABOVE", "HAND2_ABOVE"), below = Reach(player, "HAND3_BELOW", "HAND2_BELOW");
        double width = above > 0 && below > 0 ? (above + below) / 2 : Math.Max(above, below);
        return width > 0 ? Math.Clamp(width * .35 + 1, 3, 6) : CoilRadius;
    }

    private void Unembed(ActorContext player)
    {
        var bounds = player.World.Bounds(player.Instance);
        if (!Hit((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2)) return;
        double x = player.X, y = player.Y;
        foreach (var (dx, dy) in new[] { (0.0, -8.0), (0.0, -16.0), (0.0, -24.0), (0.0, -32.0), (-16.0, 0.0), (16.0, 0.0), (-32.0, 0.0), (32.0, 0.0), (0.0, 16.0), (0.0, 32.0) })
        {
            var probe = player.World.Bounds(player.Instance, x + dx, y + dy);
            if (Hit((probe.Left + probe.Right) / 2, (probe.Top + probe.Bottom) / 2)) continue;
            player.X = x + dx; player.Y = y + dy;
            Log("ROPE_UNEMBED from=" + x.ToString("0.0") + "," + y.ToString("0.0") + " to=" + player.X.ToString("0.0") + "," + player.Y.ToString("0.0"));
            return;
        }
        Log("ROPE_UNEMBED failed from=" + x.ToString("0.0") + "," + y.ToString("0.0"));
    }

    private void ReportGeometry(ActorContext player)
    {
        var bounds = player.World.Bounds(player.Instance);
        double width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        Log("ROPE_QTE_END pos=" + player.X.ToString("0.0") + "," + player.Y.ToString("0.0")
            + " box=" + bounds.Left.ToString("0.0") + "," + bounds.Top.ToString("0.0") + "," + bounds.Right.ToString("0.0") + "," + bounds.Bottom.ToString("0.0")
            + " center=" + Hit((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2)
            + " tl=" + Hit(bounds.Left, bounds.Top) + " tr=" + Hit(bounds.Right, bounds.Top)
            + " bl=" + Hit(bounds.Left, bounds.Bottom) + " br=" + Hit(bounds.Right, bounds.Bottom) + " w=" + width.ToString("0") + " h=" + height.ToString("0"));
    }

    private bool Hit(double x, double y)
    {
        foreach (var solid in solids)
            if (x >= solid.Left && x <= solid.Right && y >= solid.Top && y <= solid.Bottom) return true;
        return false;
    }

    private static double Reach(ActorContext player, string from, string to) => player.Part(from) is { } a && player.Part(to) is { } b
        ? Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) : 0;

    private static (double X, double Y) Orbit(double centerX, double centerY, double baseAngle, int index, double radius)
    {
        double angle = baseAngle - index * LoopStep;
        return (centerX + Math.Cos(angle) * radius, centerY + Math.Sin(angle) * radius);
    }

    private void ApplyFacing(ActorContext entity, ActorContext player)
    {
        if (qteCooldown > 0) return;
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
        if (Solid(nextX, headY, HalfSize)) direction = -direction;
        else headX = nextX;
        headVelocity = Math.Min(headVelocity + chain.Gravity, VerletChain.MaxVelocity);
        double nextY = headY + headVelocity;
        if (!Solid(headX, nextY, HalfSize)) headY = nextY;
        else { headVelocity = 0; jumping = false; }
        if (Math.Abs(player.X - headX) <= BindRange) BeginAttach(player);
        else if (jumpFrame > JumpTimeout) jumping = false;
    }

    private void Crawl(ref double headX, ref double headY)
    {
        phaseFrame = (phaseFrame + 1) % (ExtendFrames + ContractFrames);
        if (phaseFrame <= ExtendFrames)
        {
            double next = headX + direction * ExtendSpeed;
            if (Solid(next, headY, HalfSize)) direction = -direction;
            else headX = next;
        }
        headVelocity = Solid(headX, headY + 1, HalfSize) ? 0 : Math.Min(headVelocity + chain.Gravity, VerletChain.MaxVelocity);
        double nextY = headY + headVelocity;
        if (!Solid(headX, nextY, HalfSize)) headY = nextY;
    }

    private void Commit(ActorContext entity, ref double headX, ref double headY)
    {
        double width = entity.World.Engine.Read("room_width").Number, height = entity.World.Engine.Read("room_height").Number;
        headX = Math.Clamp(headX, HalfSize, width - HalfSize); headY = Math.Clamp(headY, HalfSize, height - HalfSize);
        if (double.IsFinite(headX) && double.IsFinite(headY) && !Solid(headX, headY, HalfSize)) { lastX = headX; lastY = headY; stuckFrames = 0; }
        else if (++stuckFrames > StuckFrames) { stuckFrames = 0; Escape(ref headX, ref headY); }
        else { headX = lastX; headY = lastY; headVelocity = 0; }
        entity.X = headX; entity.Y = headY;
        chain.Step(headX, headY);
        chain.Collide((x, y) => Solid(x, y, 2));
        chain.Clamp(headX, headY, 512, width, height);
    }

    private void Escape(ref double x, ref double y)
    {
        foreach (var (dx, dy) in new[] { (0.0, -8.0), (-direction * 8.0, -4.0), (direction * 8.0, -4.0), (0.0, -16.0) })
            if (!Solid(x + dx, y + dy, HalfSize)) { x += dx; y += dy; lastX = x; lastY = y; headVelocity = 0; return; }
        x = lastX; y = lastY; headVelocity = 0;
        for (int attempts = 0; attempts < 8 && Solid(x, y, HalfSize); attempts++) y -= 2;
        if (Solid(x, y, HalfSize)) { Log("ROPEBONDS escape failed at " + x + "," + y); return; }
        lastX = x; lastY = y;
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

    private bool Solid(double x, double y, double radius = 0)
    {
        foreach (var bounds in solids)
            if (x + radius >= bounds.Left && x - radius <= bounds.Right && y + radius >= bounds.Top && y - radius <= bounds.Bottom) return true;
        return false;
    }

    private static void DropWeapon(ActorContext player)
    {
        if (!player.World.Globals.Values.ContainsKey("CurrentWeapon")) player.World.Globals.Write("CurrentWeapon", 0);
        if (!player.World.Globals.ReadOr("config_DropWeapon", 1).Truth) return;
        try
        {
            player.Call("dropWeapon");
            double direction = player.Read("Direction") == 0 ? 1 : Math.Sign(player.Read("Direction"));
            GmInstance? flying = null;
            foreach (var instance in player.World.Instances)
                if (!instance.Destroyed && (player.Services.Instances.ObjectName(instance.Object) ?? "").Contains("Gun", StringComparison.Ordinal)
                    && Math.Abs(instance.Scope.ReadOr("x").Number - player.X) < 64 && Math.Abs(instance.Scope.ReadOr("y").Number - player.Y) < 64) flying = instance;
            if (flying is not null)
            {
                flying.Scope.Write("x", player.X + direction * 50);
                flying.Scope.Write("y", player.Y - 24);
                flying.Scope.Write("hspeed", direction * 6);
                flying.Scope.Write("vspeed", -4);
                Log("ROPE_DROP gun=" + flying.Id + " to=" + (player.X + direction * 50) + "," + (player.Y - 24));
            }
        }
        catch (Exception error) { Log("ROPEBONDS dropWeapon failed: " + error.Message); }
    }

    private void Reset(ActorContext entity)
    {
        if (!All.Contains(this)) All.Add(this);
        CollectSolids(entity);
        double x = entity.X, y = entity.Y;
        for (int attempts = 0; attempts < 64 && Solid(x, y, HalfSize); attempts++) y -= 1;
        entity.X = x; entity.Y = y;
        lastX = x; lastY = y;
        jumping = false; phaseFrame = 0; headVelocity = 0; wrapping = false; qte = false; Unfreeze();
        chain.Reset(Segments, x, y);
    }

    private bool Bind(ActorContext player)
    {
        if (!constraints.Bind(player, BindPart.Hands)) return false;
        constraints.Hang(player);
        constraints.Offset(player, BoundAngle);
        victim = player;
        player.Write("ropeBound", 1);
        return true;
    }

    private void Release()
    {
        All.Remove(this);
        wrapping = false;
        Unfreeze();
        if (victim is null) return;
        constraints.Release(victim);
        if (!victim.Instance.Destroyed)
        {
            foreach (string name in new[] { "HAND3_ABOVE", "HAND3_BELOW" }) if (victim.Part(name) is { } bone) bone.Instance.Scope.Write("PartPictureOverride", -1);
        }
        if (!victim.Instance.Destroyed && !constraints.IsBound(victim.Id)) victim.Write("ropeBound", 0);
        RestorePickups();
        victim = null;
        double x = lastX, y = lastY;
        for (int attempts = 0; attempts < 32 && Solid(x, y, HalfSize); attempts++) y -= 2;
        lastX = x; lastY = y;
        chain.Reset(Segments, x, y);
    }

    private static ActorContext? Player(ActorContext entity)
    {
        if (!entity.World.Constants.TryGetValue("objPlayerDoll", out var kind)) return null;
        foreach (var instance in entity.World.Instances)
            if (!instance.Destroyed && instance.Active && entity.World.IsObject(instance.Object, kind.Int)) return new(entity.World, instance);
        return null;
    }
}
