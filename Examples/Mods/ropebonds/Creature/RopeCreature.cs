using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed class RopeCreature : IEntityBehavior
{
    private enum Phase
    {
        Roam,
        Catch,
        Fall,
        LegQte,
        Hold
    }
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
    private const double QteWindow = 2.5;
    private const double LegQteWindow = 5;
    public const double StaggerSettle = .3;
    public const double FallFallback = 1.5;
    private const double RollMotionTime = 1;
    private const double RetreatCooldown = 2;
    private const int QtePresses = 25, QteEasyPresses = 12;
    private const double GishiInterval = .5;
    private const double FaceInterval = .4;
    private static readonly string[] GishiFiles = ["gishi.wav", "gishi_low.wav", "gishi_high.wav"];
    private static readonly Dictionary<string, int> GishiSounds = new(StringComparer.Ordinal);
    private readonly ConstraintService constraints;
    private readonly VerletChain chain = new(SegmentLength, 2);
    private readonly List<GmBounds> solids = [];
    private static readonly List<RopeCreature> Live = [];
    private ActorContext? victim, self, rolling;
    private GmInstance? controller;
    private double ropeDepth = -100000;
    private double startX, startY, direction = 1, headVelocity;
    private double qteTime, qteStart, phaseStart, landedAt, frozenX, frozenY, frozenDirection, cooldownUntil, nextGishi, lastCounter, rollUntil;
    private NativePoseService.Snapshot? feetEntry;
    private int gishiIndex, savedBindBy, faceStep = -1, autoQte;
    private BindLayer qteLayer;
    private double qteWindow = QteWindow;
    private bool muguPlayed, preBind, anchorSeen;
    private bool grounded, jumping;
    private Phase phase;
    private int jumpFrame;

    public RopeCreature(ConstraintService constraints) => this.constraints = constraints;
    public string Id => "ropebonds.creature";

    public void Created(ActorContext entity) { Live.Add(this); entity.Write("HP", 10000); Reset(entity); }
    public void RoomEntered(ActorContext entity) => Reset(entity);
    public void RoomExited(ActorContext entity) => Release(entity);
    public void Destroyed(ActorContext entity) { Live.Remove(this); Release(entity); }
    public void Detach(ActorContext entity) { Live.Remove(this); Release(entity); }

    public void Update(ActorContext entity, FrameContext frame)
    {
        self = entity;
        if (chain.Count == 0)
        {
            Reset(entity);
        }
        if (solids.Count == 0)
        {
            CollectSolids(entity);
        }
        if (victim is not null && (victim.Instance.Destroyed || !victim.Instance.Active))
        {
            Release(entity);
        }
        if (phase == Phase.Roam && victim is not null && !constraints.Bound(victim.Id))
        {
            Release(entity);
        }
        if (victim is not null)
        {
            ropeDepth = victim.Instance.Scope.ReadOr("depth").Number - PlayerLayerOffset;
        }
        if (phase is Phase.Catch or Phase.Fall or Phase.LegQte) return;
        if (victim is not null)
        {
            Follow(entity);
            return;
        }
        ActorContext? player = Player(entity);
        if (player is not null)
        {
            ropeDepth = player.Instance.Scope.ReadOr("depth").Number - PlayerLayerOffset;
        }
        double previousX = entity.X;
        if (Approach(entity, frame, player)) return;
        Fall(entity, previousX);
        chain.Step(entity.X, entity.Y);
        chain.Clamp(entity.X, entity.Y, 256, entity.World.Engine.Read("room_width").Number, entity.World.Engine.Read("room_height").Number);
        ResolveSolids();
    }

    private bool Approach(ActorContext entity, FrameContext frame, ActorContext? player)
    {
        (double X, double Y)? target = player is null ? null : Target(player);
        if (target is { } point && frame.GameTime >= cooldownUntil
            && Math.Abs(point.X - entity.X) < DetectRange && Math.Abs(point.Y - entity.Y) < 140)
        {
            int facing = Math.Sign(point.X - entity.X);
            if (facing != 0)
            {
                direction = facing;
            }
            if (Math.Abs(point.X - entity.X) <= AttachRange && Math.Abs(point.Y - entity.Y) <= AttachRange)
            {
                Attach(entity, player!);
                return true;
            }
            if (grounded && !jumping)
            {
                Jump();
            }
            entity.X += jumping ? Math.Clamp(point.X - entity.X, -JumpSpeed, JumpSpeed) : direction * Speed;
            return false;
        }
        if (jumping) return false;
        entity.X += direction * Speed * .5;
        if (entity.X < startX - PatrolRange || entity.X > startX + PatrolRange)
        {
            direction = -direction;
        }
        return false;
    }

    private (double X, double Y) Target(ActorContext player) =>
        constraints.Bound(player.Id, BindLayer.Hands) ? Feet(player) : Hands(player);

    private void Fall(ActorContext entity, double previousX)
    {
        double previousY = entity.Y;
        if (jumping && ++jumpFrame > MaxJumpFrames)
        {
            jumping = false;
        }
        headVelocity = Math.Min(headVelocity + (jumping ? JumpGravity : HeadGravity), MaxFall);
        entity.Y += headVelocity;
        grounded = false;
        if (ResolveHead(entity, previousX, previousY))
        {
            grounded = true;
            jumping = false;
        }
    }

    private void Attach(ActorContext entity, ActorContext player)
    {
        if (player.Read("BINDED") != 0 || Busy(player)) return;
        if (!NativeActorActions.Grounded(player)) return;
        if (constraints.Bound(player.Id, BindLayer.Feet)) return;
        if (preBind && !constraints.Bound(player.Id, BindLayer.Hands))
        {
            if (!constraints.Bind(player, BindLayer.Hands)) return;
            Console.WriteLine("ROPE_PREBIND actor=" + player.Id);
        }
        if (entity.Read("LegsOnly") != 0)
        {
            if (!constraints.Bind(player, BindLayer.Feet)) return;
            victim = player;
            phase = Phase.Hold;
            headVelocity = 0;
            jumping = false;
            Console.WriteLine("ROPE_FEET_ATTACH actor=" + player.Id);
            return;
        }
        if (constraints.Bound(player.Id, BindLayer.Hands))
        {
            StartFall(entity, player);
            return;
        }
        StartCatch(entity, player);
    }

    private void StartCatch(ActorContext entity, ActorContext player)
    {
        GmInstance? target = NativeActorActions.Controller(player);
        if (target is null || !constraints.BindQte(player)) return;
        controller = target;
        victim = player;
        phase = Phase.Catch;
        qteLayer = BindLayer.Hands;
        qteWindow = QteWindow;
        frozenX = player.X;
        frozenY = player.Y;
        frozenDirection = player.Read("Direction");
        savedBindBy = (int)player.Read("BINDED_BY");
        qteTime = 0;
        qteStart = entity.Services.Frame.GameTime;
        nextGishi = 0;
        lastCounter = 0;
        gishiIndex = 0;
        muguPlayed = false;
        faceStep = -1;
        anchorSeen = false;
        headVelocity = 0;
        jumping = false;
        PlayVoice(player, "V_UHI", 0);
        Hold(player);
        ApplyFace(player);
        Console.WriteLine("ROPE_FACE_START actor=" + player.Id + " bind_by=" + savedBindBy);
        controller.Scope.Write("BindCounter", 0);
        player.World.Globals.Write("BindMax", BindMax(player, qteLayer));
        (entity.X, entity.Y) = constraints.BoundHands(player.Id) ?? Hands(player);
        chain.Reset(Segments, entity.X, entity.Y);
        Console.WriteLine("ROPE_QTE_START actor=" + player.Id + " max=" + BindMax(player, qteLayer) + " x=" + entity.X + " y=" + entity.Y);
    }

    private void StartFall(ActorContext entity, ActorContext player)
    {
        victim = player;
        phase = Phase.Fall;
        qteLayer = BindLayer.Feet;
        phaseStart = entity.Services.Frame.GameTime;
        frozenX = player.X;
        frozenY = player.Y;
        frozenDirection = player.Read("Direction");
        savedBindBy = (int)player.Read("BINDED_BY");
        feetEntry = player.Services.Poses.Capture(player, ConstraintService.FootBones);
        landedAt = 0;
        faceStep = -1;
        anchorSeen = false;
        headVelocity = 0;
        jumping = false;
        Hold(player);
        NativeActorActions.Motion(player, "DOWN_0");
        Console.WriteLine("ROPE_FALL_START actor=" + player.Id);
    }

    private void StartLegQte(ActorContext player)
    {
        if (!constraints.Bind(player, BindLayer.Feet, "ropebonds.legqte", "legqte", feetEntry))
        {
            AbortQte();
            return;
        }
        phase = Phase.LegQte;
        qteLayer = BindLayer.Feet;
        qteWindow = LegQteWindow;
        qteTime = 0;
        qteStart = player.Services.Frame.GameTime;
        nextGishi = 0;
        lastCounter = 0;
        gishiIndex = 0;
        muguPlayed = false;
        controller ??= NativeActorActions.Controller(player);
        if (controller is { Destroyed: false } current) current.Scope.Write("BindCounter", 0);
        ApplyFace(player);
        player.World.Globals.Write("BindMax", BindMax(player, qteLayer));
        Console.WriteLine("ROPE_LEG_QTE_START actor=" + player.Id + " max=" + BindMax(player, qteLayer));
    }

    public static void TickQte()
    {
        foreach (RopeCreature creature in Live.ToArray()) creature.TickQteStep();
    }

    private void TickQteStep()
    {
        if (rolling is not null) TickRoll();
        if (phase == Phase.Fall) TickFall();
        else if (phase is Phase.Catch or Phase.LegQte) TickStruggle();
    }

    private void TickFall()
    {
        if (victim is not { } player)
        {
            AbortQte();
            return;
        }
        if (player.Instance.Destroyed || !player.Instance.Active)
        {
            AbortQte();
            return;
        }
        Hold(player);
        double now = player.Services.Frame.GameTime;
        if (landedAt <= 0 && StaggerLanded(player)) landedAt = now;
        if (StaggerFinished(landedAt, phaseStart, now)) StartLegQte(player);
    }

    public static bool StaggerLanded(ActorContext player) =>
        player.Read("ChangeMotion") == 0 && player.World.Constants.TryGetValue("DOWN_0", out GmValue down) && player.Read("MotionNum") == down.Number;

    public static bool StaggerFinished(double landedAt, double startedAt, double now) =>
        landedAt > 0 ? now - landedAt >= StaggerSettle : now - startedAt >= FallFallback;

    private void TickStruggle()
    {
        if (victim is not { } player)
        {
            AbortQte();
            return;
        }
        if (player.Instance.Destroyed || !player.Instance.Active
            || player.Read("BINDED") != 0 || player.World.Globals.ReadOr("PlayerHP", 1).Number <= 0)
        {
            AbortQte();
            return;
        }
        controller ??= NativeActorActions.Controller(player);
        if (controller is null || controller.Destroyed)
        {
            AbortQte();
            return;
        }
        Hold(player);
        AutoStruggle(player);
        double bindMax = BindMax(player, qteLayer);
        player.World.Globals.Write("BindMax", bindMax);
        qteTime = player.Services.Frame.GameTime - qteStart;
        double counter = controller.Scope.ReadOr("BindCounter").Number;
        if (counter >= bindMax)
        {
            FinishQte(player, true);
            return;
        }
        if (qteTime >= qteWindow)
        {
            FinishQte(player, false);
            return;
        }
        StruggleVoice(player, counter);
        lastCounter = counter;
    }

    private void StruggleVoice(ActorContext player, double counter)
    {
        if (!muguPlayed && qteTime >= .5)
        {
            muguPlayed = true;
            PlayVoice(player, "V_MUGU", 2);
        }
        if (counter > lastCounter && qteTime >= nextGishi)
        {
            nextGishi = qteTime + GishiInterval;
            PlayGishi(player, gishiIndex++ % GishiFiles.Length);
        }
    }

    private void AutoStruggle(ActorContext player)
    {
        if (autoQte == 0 || controller is null || controller.Destroyed) return;
        if (autoQte == 2)
        {
            controller.Scope.Write("BindCounter", 0);
            return;
        }
        double bindMax = BindMax(player, qteLayer);
        double ramped = Math.Min(bindMax, controller.Scope.ReadOr("BindCounter").Number + Math.Max(1, bindMax / 12));
        controller.Scope.Write("BindCounter", ramped);
    }

    private static bool Busy(ActorContext player) =>
        Live.Any(creature => creature.victim?.Id == player.Id && creature.phase is Phase.Catch or Phase.Fall or Phase.LegQte);

    private void FinishQte(ActorContext player, bool success)
    {
        BindLayer layer = qteLayer;
        double counter = controller?.Scope.ReadOr("BindCounter").Number ?? 0;
        qteTime = player.Services.Frame.GameTime - qteStart;
        constraints.Release(player, layer);
        if (success)
        {
            if (layer == BindLayer.Feet) StartRoll(player);
            victim = null;
            cooldownUntil = (self?.Services.Frame.GameTime ?? 0) + RetreatCooldown;
            direction = startX >= player.X ? 1 : -1;
        }
        else if (constraints.Bind(player, layer, layer == BindLayer.Feet ? feetEntry : null))
        {
            victim = player;
        }
        else
        {
            victim = null;
        }
        phase = victim is null ? Phase.Roam : Phase.Hold;
        EndHold(player);
        Console.WriteLine("ROPE_QTE_" + (success ? "SUCCESS" : "FAIL") + " layer=" + layer + " actor=" + player.Id + " counter=" + counter + " time=" + qteTime.ToString("0.00"));
    }

    private void StartRoll(ActorContext player)
    {
        rolling = player;
        rollUntil = player.Services.Frame.GameTime + RollMotionTime;
        NativeActorActions.Motion(player, "FLYING_SPIN");
        Console.WriteLine("ROPE_ROLL_START actor=" + player.Id);
    }

    private void TickRoll()
    {
        if (rolling is not { } player) return;
        if (!player.Instance.Destroyed && player.Services.Frame.GameTime < rollUntil) return;
        rolling = null;
        if (player.Instance.Destroyed) return;
        NativeActorActions.Motion(player, "STAND_0");
        Console.WriteLine("ROPE_ROLL_END actor=" + player.Id);
    }

    private void Hold(ActorContext player)
    {
        player.Write("HALFBINDED", 1);
        player.Write("BINDED_BY", 1);
        player.X = frozenX;
        player.Y = frozenY;
        player.Write("Direction", frozenDirection);
        player.HorizontalSpeed = 0;
        player.VerticalSpeed = 0;
    }

    private static void PlayGishi(ActorContext player, int index)
    {
        if (RopeBondsMod.AssetDirectory is { } root)
        {
            string full = System.IO.Path.Combine(root, GishiFiles[index]);
            string relative = System.IO.Path.GetRelativePath(player.World.UserRoot, full);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative) && System.IO.File.Exists(full))
            {
                try
                {
                    if (!GishiSounds.TryGetValue(relative, out int sound))
                        GishiSounds[relative] = sound = player.Call("sound_add", relative, 0, 0).Int;
                    player.Call("sound_volume", sound, 1);
                    player.Call("sound_play", sound);
                    return;
                }
                catch (Exception) { }
            }
        }
        player.Services.Audio.PlayBuiltin(player, "sndGishi00");
    }

    internal static void PlayVoice(ActorContext player, string kindName, int index)
    {
        if (!player.World.Constants.TryGetValue(kindName, out GmValue kind)) return;
        GmValue voice = player.World.Globals.ReadOr(GmScope.Key("Voice", new[] { kind.Int, index }));
        if (voice.Int > 0) player.Call("sound_play2", voice);
    }

    private void AbortQte()
    {
        if (phase is not (Phase.Catch or Phase.Fall or Phase.LegQte)) return;
        BindLayer layer = qteLayer;
        phase = Phase.Roam;
        cooldownUntil = (self?.Services.Frame.GameTime ?? 0) + RetreatCooldown;
        if (victim is { } player)
        {
            if (!player.Instance.Destroyed)
            {
                constraints.Release(player, layer);
                EndHold(player);
            }
            victim = null;
        }
        controller = null;
    }

    public static void BindFlag(int controllerId, bool bound)
    {
        foreach (RopeCreature creature in Live)
            if (creature.phase is Phase.Catch or Phase.LegQte && creature.controller is { } controller && controller.Id == controllerId
                && creature.victim is { } player && !player.Instance.Destroyed)
                player.Write("HALFBINDED", bound || creature.constraints.Bound(player.Id) ? 1 : 0);
    }

    private void EndHold(ActorContext player)
    {
        faceStep = -1;
        if (!player.Instance.Destroyed)
        {
            player.Write("HALFBINDED", constraints.Bound(player.Id) ? 1 : 0);
            player.Write("BINDED_BY", savedBindBy);
        }
        if (controller is { Destroyed: false } current) current.Scope.Write("BindCounter", 0);
        controller = null;
        Console.WriteLine("ROPE_FACE_END actor=" + player.Id + " bind_by=" + savedBindBy);
    }

    private void ApplyFace(ActorContext player)
    {
        int step = (int)(qteTime / FaceInterval) % 4;
        if (step == faceStep) return;
        faceStep = step;
        int eye = step is 1 or 2
            ? Constant(player, "FACE_EYE_CLOSE", 1)
            : Constant(player, "FACE_EYE_HALF", 3);
        int mouth = step is 2 or 3
            ? Constant(player, "FACE_MOUTH_A", 1)
            : Constant(player, "FACE_MOUTH_I", 3);
        GmInstance? head = Head(player);
        if (head is null) return;
        head.Scope.Write("Eye_Num", eye);
        head.Scope.Write("Mouth_Num", mouth);
        Console.WriteLine("ROPE_FACE_STEP actor=" + player.Id + " head=" + head.Id + " step=" + step + " eye=" + eye + " mouth=" + mouth);
    }

    private static GmInstance? Head(ActorContext player)
    {
        foreach (GmInstance instance in player.World.Instances)
        {
            if (instance.Destroyed || instance.Scope.ReadOr("DrawTarget").Int != player.Id) continue;
            string? name = player.Services.Instances.ObjectName(instance.Object);
            if (name is not null && name.EndsWith("_Head", StringComparison.Ordinal)) return instance;
        }
        return null;
    }

    private static int Constant(ActorContext player, string name, int fallback) =>
        player.World.Constants.TryGetValue(name, out GmValue value) ? value.Int : fallback;

    private static double BindMax(ActorContext player, BindLayer layer)
    {
        double presses = player.World.Globals.ReadOr("EASY_QS").Truth ? QteEasyPresses : QtePresses;
        return layer == BindLayer.Feet ? presses * 2 : presses;
    }

    public static void Clear()
    {
        foreach (RopeCreature creature in Live.ToArray()) creature.AbortQte();
        Live.Clear();
    }

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
        (ActorContext Above, ActorContext Below)? pair = null;
        if (victim is { } player && !player.Instance.Destroyed) pair = Pair(player);
        if (pair is { } bound)
        {
            ActorContext above = bound.Above, below = bound.Below;
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

    private (ActorContext Above, ActorContext Below)? Pair(ActorContext player)
    {
        if (phase is Phase.LegQte or Phase.Hold && constraints.Bound(player.Id, BindLayer.Feet) && !constraints.Bound(player.Id, BindLayer.Hands))
            return Parts(player, "FOOT3_ABOVE", "FOOT3_BELOW");
        if (phase is Phase.LegQte) return Parts(player, "FOOT3_ABOVE", "FOOT3_BELOW");
        if (constraints.Bound(player.Id, BindLayer.Hands)) return Parts(player, "HAND3_ABOVE", "HAND3_BELOW");
        if (constraints.Bound(player.Id, BindLayer.Feet)) return Parts(player, "FOOT3_ABOVE", "FOOT3_BELOW");
        return null;
    }

    private static (ActorContext Above, ActorContext Below)? Parts(ActorContext player, string above, string below) =>
        player.Part(above) is { } left && player.Part(below) is { } right
            ? (left, right)
            : null;

    private (double X, double Y)? Anchor(ActorContext player) =>
        phase is Phase.LegQte or Phase.Hold && constraints.Bound(player.Id, BindLayer.Feet) && !constraints.Bound(player.Id, BindLayer.Hands)
            ? constraints.BoundFeet(player.Id)
            : constraints.BoundHands(player.Id) ?? constraints.BoundFeet(player.Id);

    private void RenderBound(ActorContext entity, ActorContext above, ActorContext below)
    {
        double leftX = above.X, leftY = above.Y, rightX = below.X, rightY = below.Y;
        double dx = rightX - leftX, dy = rightY - leftY, distance = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(-dy, dx) * 180 / Math.PI;
        var presentation = entity.Services.Presentation;
        presentation.Sprite(entity.Id, "rope.head", "ropebonds.rope_head", leftX, leftY, angle, 2, 2, 2, 2, ropeDepth);
        presentation.Sprite(entity.Id, "rope.body.0", "ropebonds.rope_body", (leftX + rightX) / 2, (leftY + rightY) / 2, angle, Math.Max(.4, distance / 9), 2, 4.5, 2, ropeDepth);
        presentation.Sprite(entity.Id, "rope.head.tail", "ropebonds.rope_head", rightX, rightY, angle + 180, 2, 2, 2, 2, ropeDepth);
        for (int index = 1; index < chain.Count - 1; index++) presentation.Remove(entity.Id, "rope.body." + index);
    }

    private void Follow(ActorContext entity)
    {
        (double X, double Y)? bound = Anchor(victim!);
        (entity.X, entity.Y) = bound ?? Target(victim!);
        if (bound is not null && !anchorSeen)
        {
            anchorSeen = true;
            Console.WriteLine("ROPE_LOCK source=clip x=" + entity.X.ToString("0.0") + " y=" + entity.Y.ToString("0.0"));
        }
        headVelocity = 0;
        jumping = false;
        chain.Step(entity.X, entity.Y);
        ResolveSolids();
    }

    private void Release(ActorContext entity)
    {
        if (victim is { } carried)
        {
            if (!carried.Instance.Destroyed)
            {
                constraints.Release(carried);
                EndHold(carried);
            }
            victim = null;
        }
        phase = Phase.Roam;
        controller = null;
        headVelocity = 0;
        jumping = false;
        anchorSeen = false;
        if (!entity.Instance.Destroyed)
        {
            entity.X = startX;
            entity.Y = startY;
        }
        chain.Reset(Segments, startX, startY);
    }

    private void Reset(ActorContext entity)
    {
        AbortQte();
        if (phase == Phase.Hold && victim is { } held && !held.Instance.Destroyed) constraints.Release(held);
        phase = Phase.Roam;
        startX = entity.X;
        startY = entity.Y;
        victim = null;
        autoQte = (int)entity.Read("AutoQte");
        preBind = entity.Read("PreBind") != 0;
        direction = 1;
        headVelocity = 0;
        grounded = false;
        jumping = false;
        jumpFrame = 0;
        anchorSeen = false;
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

    private static (double X, double Y) Feet(ActorContext player)
    {
        ActorContext? left = player.Part("FOOT3_ABOVE");
        ActorContext? right = player.Part("FOOT3_BELOW");
        if (left is not null && right is not null) return ((left.X + right.X) / 2, (left.Y + right.Y) / 2);
        if (left is not null) return (left.X, left.Y);
        return right is not null ? (right.X, right.Y) : (player.X, player.Y);
    }

    private bool ResolveHead(ActorContext entity, double previousX, double previousY)
    {
        bool grounded = false;
        foreach (GmBounds bounds in solids)
        {
            double x = entity.X, y = entity.Y;
            if (x < bounds.Left - HeadRadius || x > bounds.Right + HeadRadius
                || y < bounds.Top - HeadRadius || y > bounds.Bottom + HeadRadius) continue;
            (entity.X, entity.Y) = PushOut(x, y, previousX, previousY, bounds, HeadRadius);
            if (entity.Y < y)
            {
                headVelocity = 0;
                grounded = true;
            }
            else if (entity.Y > y) headVelocity = 0;
        }
        return grounded;
    }

    private static (double X, double Y) PushOut(double x, double y, double previousX, double previousY, GmBounds bounds, double thickness)
    {
        double left = bounds.Left - thickness, right = bounds.Right + thickness;
        double top = bounds.Top - thickness, bottom = bounds.Bottom + thickness;
        bool horizontal = Math.Abs(x - previousX) > Math.Abs(y - previousY);
        if (horizontal && previousX <= left) return (left - .01, y);
        if (horizontal && previousX >= right) return (right + .01, y);
        if (!horizontal && previousY <= top) return (x, top - .01);
        if (!horizontal && previousY >= bottom) return (x, bottom + .01);
        double smallest = Math.Min(Math.Min(x - left, right - x), Math.Min(y - top, bottom - y));
        if (smallest == x - left) return (left - .01, y);
        if (smallest == right - x) return (right + .01, y);
        if (smallest == y - top) return (x, top - .01);
        return (x, bottom + .01);
    }

    private void ResolveSolids()
    {
        for (int index = 1; index < chain.Count; index++)
        {
            var (x, y) = chain[index];
            var (previousX, previousY) = chain.Previous(index);
            foreach (GmBounds bounds in solids)
            {
                if (x < bounds.Left - HalfThickness || x > bounds.Right + HalfThickness
                    || y < bounds.Top - HalfThickness || y > bounds.Bottom + HalfThickness) continue;
                (x, y) = PushOut(x, y, previousX, previousY, bounds, HalfThickness);
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
