using System;

namespace DoubleDash;

public sealed class DashSettings
{
    public double FirstDistance = 72;
    public double FirstFrames = 10;
    public double SecondDistance = 72;
    public double TapFrames = 7;
    public double SecondWindow = 12;
    public double Cooldown = 10;
    public bool AllowAirSecond = true;
    public double StageDuration => Math.Max(1, FirstFrames) / 60;
    public double TapWindow => Math.Max(0, TapFrames) / 60;
    public double WindowDuration => Math.Max(0, SecondWindow) / 60;
    public double CooldownDuration => Math.Max(0, Cooldown) / 60;
}

public readonly record struct DashStep(bool Begin, bool End, int Direction, int Stage, double Distance)
{
    public static readonly DashStep None = new(false, false, 0, 0, 0);
}

public sealed class DoubleDashState
{
    public const int MaxStages = 2;
    private readonly DashSettings settings;
    private double lastLeft = double.NegativeInfinity;
    private double lastRight = double.NegativeInfinity;
    private double cooldown;
    private double window;
    private double elapsed;
    private double moved;
    private double clock;
    private int direction;
    private int stages;
    private bool dashing;
    private bool airborne;

    public DoubleDashState(DashSettings settings) { this.settings = settings; }
    public DashSettings Settings => settings;
    public int Stages => stages;
    public bool Dashing => dashing;
    public bool WindowOpen => window > 0;
    public int Direction => direction;
    public double CooldownLeft => cooldown;

    public DashStep Step(double delta, bool leftPressed, bool rightPressed, bool grounded, bool canAct)
    {
        cooldown = Math.Max(0, cooldown - delta);
        window = Math.Max(0, window - delta);
        clock += delta;
        if (!canAct) { Cancel(); return DashStep.None; }
        if (!dashing)
        {
            if (grounded)
            {
                if (airborne) { stages = 0; window = 0; airborne = false; }
                if (stages > 0 && window <= 0) stages = 0;
            }
            int requested = 0;
            if (leftPressed) { if (clock - lastLeft <= settings.TapWindow) requested = -1; lastLeft = clock; }
            if (rightPressed) { if (clock - lastRight <= settings.TapWindow) requested = 1; lastRight = clock; }
            if (stages == 1 && window > 0 && (leftPressed || rightPressed) && (grounded || settings.AllowAirSecond)) return Begin(leftPressed ? -1 : 1);
            if (stages == 0 && cooldown <= 0 && requested != 0) return Begin(requested);
            return DashStep.None;
        }
        double before = moved;
        elapsed += delta;
        moved = Distance(stages + 1) * Math.Min(elapsed / settings.StageDuration, 1);
        double distance = Math.Max(0, moved - before);
        if (elapsed >= settings.StageDuration - 1e-9) return Finish(distance, grounded);
        return new DashStep(false, false, direction, stages + 1, distance);
    }

    public void Block()
    {
        if (!dashing) return;
        dashing = false;
        elapsed = 0;
        moved = 0;
    }

    public void Reset()
    {
        dashing = false;
        stages = 0;
        cooldown = 0;
        window = 0;
        elapsed = 0;
        moved = 0;
        clock = 0;
        direction = 0;
        airborne = false;
        lastLeft = double.NegativeInfinity;
        lastRight = double.NegativeInfinity;
    }

    private double Distance(int stage) => stage <= 1 ? settings.FirstDistance : settings.SecondDistance;

    private DashStep Begin(int request)
    {
        direction = request;
        dashing = true;
        elapsed = 0;
        moved = 0;
        window = 0;
        return new DashStep(true, false, direction, stages + 1, 0);
    }

    private DashStep Finish(double distance, bool grounded)
    {
        int completed = stages + 1;
        dashing = false;
        elapsed = 0;
        moved = 0;
        stages = completed;
        airborne = !grounded;
        cooldown = settings.CooldownDuration;
        if (stages < MaxStages) window = settings.WindowDuration;
        return new DashStep(false, true, direction, completed, distance);
    }

    private void Cancel()
    {
        dashing = false;
        elapsed = 0;
        moved = 0;
    }
}
