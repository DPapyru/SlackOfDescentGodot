namespace SlackOfDescent;

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

    public double Multiplier(double factorPerLegacyStep)
    {
        if (!double.IsFinite(factorPerLegacyStep) || factorPerLegacyStep < 0)
            throw new ArgumentOutOfRangeException(nameof(factorPerLegacyStep));
        return Math.Pow(factorPerLegacyStep, LegacyUnitsPerTick);
    }

    public double Probability(double probabilityPerLegacyStep)
    {
        if (!double.IsFinite(probabilityPerLegacyStep) || probabilityPerLegacyStep < 0 || probabilityPerLegacyStep > 1)
            throw new ArgumentOutOfRangeException(nameof(probabilityPerLegacyStep));
        if (probabilityPerLegacyStep is 0 or 1) return probabilityPerLegacyStep;
        return 1 - Math.Pow(1 - probabilityPerLegacyStep, LegacyUnitsPerTick);
    }

    public GmNativeAlarmStep Alarm(double remaining)
    {
        if (!double.IsFinite(remaining)) throw new ArgumentOutOfRangeException(nameof(remaining));
        if (remaining < 0) return new(remaining, false);
        if (remaining == 0) return new(-1, false);
        double next = remaining - LegacyUnitsPerTick;
        return next <= 1e-12 ? new(0, true) : new(next, false);
    }

    public GmNativeMotionStep Accelerate(double velocity, double acceleration)
    {
        double units = LegacyUnitsPerTick;
        return new(velocity + acceleration * units,
            velocity * units + acceleration * units * (units + 1) / 2);
    }

    public GmNativeAnimationStep Animate(double index, double speed, int frameCount)
    {
        if (frameCount <= 0) return new(index, 0);
        double next = index + Increment(speed);
        if (!double.IsFinite(next)) throw new ArgumentOutOfRangeException(nameof(index));
        long wraps = checked((long)Math.Floor(next / frameCount));
        return new(next - wraps * frameCount, wraps);
    }

    public static bool Crossed(double previous, double current, double threshold) =>
        previous < threshold && current >= threshold || previous > threshold && current <= threshold;
}

public readonly record struct GmNativeAlarmStep(double Remaining, bool Fired);
public readonly record struct GmNativeMotionStep(double Velocity, double Displacement);
public readonly record struct GmNativeAnimationStep(double Index, long Wraps);

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
