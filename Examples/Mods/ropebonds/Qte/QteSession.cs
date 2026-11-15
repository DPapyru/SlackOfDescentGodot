using System;
using SlackOfDescent;

namespace RopeBonds;

public sealed class QteProbe : IDisposable
{
    private const string CreatedHandler = "ropebonds.qte.created";
    private const string StepHandler = "ropebonds.qte.step";
    private const double StartDelay = 1.5;
    private const double Window = 2.5;
    private readonly bool enabled = Environment.GetEnvironmentVariable("ROPEBONDS_QTE_TEST") is { Length: > 0 };
    private readonly bool loop = Environment.GetEnvironmentVariable("ROPEBONDS_QTE_LOOP") is { Length: > 0 };
    private GmWorld? world;
    private ActorContext? player;
    private QteSession? session;
    private double waited;
    private bool started;

    public void Attach(ModContext context)
    {
        if (!enabled) return;
        context.Subscribe(CreatedHandler, NativeEventNames.InstanceCreated, NativeEventPhase.After, Created);
        context.Subscribe(StepHandler, NativeEventNames.StepEnd, NativeEventPhase.After, Step);
    }

    private void Created(NativeGameEvent value)
    {
        if (value.Actor is not { } actor) return;
        world ??= actor.World;
        if (RopeBondsMod.IsPlayer(actor)) player = actor;
    }

    private void Step(NativeGameEvent value)
    {
        if (world is null) return;
        if (player is null || player.Instance.Destroyed || !world.Instances.Contains(player.Instance)) player = RopeBondsMod.FindPlayer(world);
        if (player is null) return;
        if (session is not null)
        {
            session.Tick();
            if (!session.Done) return;
            session = null;
            if (!loop) return;
            started = false;
            waited = 0;
            return;
        }
        if (started) return;
        if (player.Read("BINDED") != 0 || player.Read("HALFBINDED") != 0) return;
        waited += player.Services.Frame.Delta;
        if (waited < StartDelay) return;
        var controller = NativeActorActions.Controller(player);
        if (controller is null) return;
        started = true;
        session = new QteSession(player, controller, Window);
        session.Start();
    }

    public void Dispose()
    {
        session?.Abort();
        session = null;
    }
}

public sealed class QteSession
{
    private readonly ActorContext player;
    private readonly GmInstance controller;
    private readonly double started;
    private readonly double window;
    private double nextLog;
    public bool Done { get; private set; }

    public QteSession(ActorContext player, GmInstance controller, double window)
    {
        this.player = player;
        this.controller = controller;
        this.window = window;
        started = player.Services.Frame.GameTime;
        nextLog = 0.5;
    }

    public void Start()
    {
        player.Write("HALFBINDED", 1);
        controller.Scope.Write("BindCounter", 0);
        player.World.Globals.Write("BindMax", BindMax());
        Console.WriteLine("ROPE_QTE_START room=" + player.Services.Instances.CurrentRoomName + " hp=" + Hp() + " bindMax=" + BindMax()
            + " gui=" + player.World.Globals.ReadOr("GUI", 1).Truth + " binded=" + player.Read("BINDED") + " half=" + player.Read("HALFBINDED"));
    }

    public void Tick()
    {
        if (player.Instance.Destroyed || controller.Destroyed) { Abort(); return; }
        player.World.Globals.Write("BindMax", BindMax());
        double counter = controller.Scope.ReadOr("BindCounter").Number;
        double elapsed = player.Services.Frame.GameTime - started;
        if (elapsed >= nextLog && counter <= BindMax() && elapsed < window)
        {
            nextLog += 0.5;
            Console.WriteLine("ROPE_QTE_TICK counter=" + counter + " bindMax=" + BindMax() + " elapsed=" + elapsed.ToString("0.00"));
        }
        if (counter > BindMax())
        {
            Console.WriteLine("ROPE_QTE_SUCCESS counter=" + counter + " bindMax=" + BindMax() + " elapsed=" + elapsed.ToString("0.00"));
            Finish();
        }
        else if (elapsed >= window)
        {
            Console.WriteLine("ROPE_QTE_FAIL counter=" + counter + " bindMax=" + BindMax() + " elapsed=" + elapsed.ToString("0.00"));
            Finish();
        }
    }

    private double Hp() => player.World.Globals.ReadOr("PlayerHP", 100).Number;
    private double BindMax() => 30 + 0.1 * (100 - Hp());

    private void Finish()
    {
        if (Done) return;
        Done = true;
        if (!player.Instance.Destroyed) player.Write("HALFBINDED", 0);
        if (!controller.Destroyed) controller.Scope.Write("BindCounter", 0);
    }

    public void Abort()
    {
        if (Done) return;
        Done = true;
        if (!player.Instance.Destroyed) player.Write("HALFBINDED", 0);
        if (!controller.Destroyed) controller.Scope.Write("BindCounter", 0);
    }
}