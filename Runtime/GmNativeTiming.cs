namespace SlackOfDescent;

/// <summary>
/// Converts values measured in original GameMaker steps to a 60 Hz simulation.
/// These helpers do not change script execution frequency or infer script semantics.
/// </summary>
public sealed class GmNativeTiming
{
    public const int Frequency = 60;
    public const double Delta = 1.0 / Frequency;
    public double LegacyFrequency { get; }
    public double LegacyUnitsPerTick { get; }

    public GmNativeTiming(double legacyFrequency = 30)
    {
        if (!double.IsFinite(legacyFrequency) || legacyFrequency <= 0 || legacyFrequency > 240)
            throw new ArgumentOutOfRangeException(nameof(legacyFrequency), "Loading rooms must run as loading work, outside the gameplay clock.");
        LegacyFrequency = legacyFrequency;
        LegacyUnitsPerTick = legacyFrequency * Delta;
    }

    public double Increment(double amountPerLegacyStep) => amountPerLegacyStep * LegacyUnitsPerTick;

    /// <summary>Preserves the elapsed time of positive multiplicative decay or growth.</summary>
    public double Multiplier(double factorPerLegacyStep)
    {
        if (!double.IsFinite(factorPerLegacyStep) || factorPerLegacyStep < 0)
            throw new ArgumentOutOfRangeException(nameof(factorPerLegacyStep));
        return Math.Pow(factorPerLegacyStep, LegacyUnitsPerTick);
    }

    /// <summary>
    /// Preserves the probability of at least one event over an original step.
    /// Use only for independent per-step trials, never for random sizes or choices.
    /// </summary>
    public double Probability(double probabilityPerLegacyStep)
    {
        if (!double.IsFinite(probabilityPerLegacyStep) || probabilityPerLegacyStep < 0 || probabilityPerLegacyStep > 1)
            throw new ArgumentOutOfRangeException(nameof(probabilityPerLegacyStep));
        if (probabilityPerLegacyStep is 0 or 1) return probabilityPerLegacyStep;
        return 1 - Math.Pow(1 - probabilityPerLegacyStep, LegacyUnitsPerTick);
    }

    /// <summary>
    /// Alarms retain original-step units in scripts, including the disabled -1
    /// sentinel and the distinct zero state after an alarm has fired.
    /// </summary>
    public GmNativeAlarmStep Alarm(double remaining)
    {
        if (!double.IsFinite(remaining)) throw new ArgumentOutOfRangeException(nameof(remaining));
        if (remaining < 0) return new(remaining, false);
        if (remaining == 0) return new(-1, false);
        double next = remaining - LegacyUnitsPerTick;
        return next <= 1e-12 ? new(0, true) : new(next, false);
    }

    /// <summary>
    /// Advances constant acceleration while retaining the original semi-implicit
    /// Euler positions at whole legacy-step boundaries. Collision impulses and
    /// friction clamps must be handled by the caller before the next integration.
    /// </summary>
    public GmNativeMotionStep Accelerate(double velocity, double acceleration)
    {
        double units = LegacyUnitsPerTick;
        return new(velocity + acceleration * units,
            velocity * units + acceleration * units * (units + 1) / 2);
    }

    /// <summary>Returns signed wrap count separately so animation-end events can be dispatched once per wrap.</summary>
    public GmNativeAnimationStep Animate(double index, double speed, int frameCount)
    {
        if (frameCount <= 0) return new(index, 0);
        double next = index + Increment(speed);
        if (!double.IsFinite(next)) throw new ArgumentOutOfRangeException(nameof(index));
        long wraps = checked((long)Math.Floor(next / frameCount));
        return new(next - wraps * frameCount, wraps);
    }

    /// <summary>Uses an open start and a closed end so a threshold fires once in either direction.</summary>
    public static bool Crossed(double previous, double current, double threshold) =>
        previous < threshold && current >= threshold || previous > threshold && current <= threshold;
}

public readonly record struct GmNativeAlarmStep(double Remaining, bool Fired);
public readonly record struct GmNativeMotionStep(double Velocity, double Displacement);
public readonly record struct GmNativeAnimationStep(double Index, long Wraps);

/// <summary>
/// Limits explicitly identified repeated side effects (for example contact damage)
/// without reducing movement or collision sampling frequency. Keys must identify
/// the semantic event, including both instances when it is a collision event.
/// </summary>
public sealed class GmNativeEventCadence<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, double> lastDispatch = new();

    public bool TryDispatch(TKey key, double gameTime, double intervalSeconds)
    {
        if (!double.IsFinite(gameTime) || gameTime < 0) throw new ArgumentOutOfRangeException(nameof(gameTime));
        if (!double.IsFinite(intervalSeconds) || intervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
        if (lastDispatch.TryGetValue(key, out double previous) && gameTime - previous + 1e-12 < intervalSeconds) return false;
        lastDispatch[key] = gameTime;
        return true;
    }

    public bool Remove(TKey key) => lastDispatch.Remove(key);
    public void Clear() => lastDispatch.Clear();
}
