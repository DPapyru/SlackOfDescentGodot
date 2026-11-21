using System;
using SlackOfDescent;

namespace RopeBonds;

public sealed class BindProbe : IDisposable
{
    private const string CreatedHandler = "ropebonds.bind.created";
    private const string StepHandler = "ropebonds.bind.step";
    private readonly ConstraintService constraints;
    private readonly bool enabled = Environment.GetEnvironmentVariable("ROPEBONDS_BIND_TEST") is { Length: > 0 };
    private readonly bool loop = Environment.GetEnvironmentVariable("ROPEBONDS_BIND_LOOP") is { Length: > 0 };
    private GmWorld? world;
    private ActorContext? player;
    private double waited;
    private double boundAt;
    private bool bound;
    private bool done;

    public BindProbe(ConstraintService constraints) => this.constraints = constraints;

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
        if (world is null || done) return;
        if (player is null || player.Instance.Destroyed || !world.Instances.Contains(player.Instance)) player = RopeBondsMod.FindPlayer(world);
        if (player is null) return;
        if (!bound)
        {
            waited += player.Services.Frame.Delta;
            if (waited < 2) return;
            if (constraints.Bind(player, BindLayer.Hands)) { bound = true; boundAt = player.Services.Frame.GameTime; }
            return;
        }
        if (player.Services.Frame.GameTime - boundAt < 2) return;
        constraints.Release(player, BindLayer.Hands);
        bound = false;
        if (!loop) { done = true; return; }
        waited = 0;
    }

    public void Dispose()
    {
        if (bound && player is not null && !player.Instance.Destroyed) constraints.Release(player, BindLayer.Hands);
        bound = false;
        done = true;
    }
}
