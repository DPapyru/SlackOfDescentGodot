namespace SlackOfDescent;

/// <summary>A bounded fixed clock. Modal/focus pauses discard wall time explicitly.</summary>
public sealed class GmFixedClock
{
    public const double Delta = 1.0 / 60;
    private double accumulated;
    private double legacyAccumulated;
    public long Tick { get; private set; }
    public double GameTime => Tick * Delta;
    public int Advance(double wallDelta)
    {
        if (!double.IsFinite(wallDelta) || wallDelta < 0) throw new ArgumentOutOfRangeException(nameof(wallDelta));
        accumulated += wallDelta;
        // Limit work per callback while preserving the unprocessed simulation time.
        // Only explicit modal/focus suspension discards the backlog.
        int ticks = Math.Min(15, (int)Math.Floor((accumulated + 1e-12) / Delta));
        accumulated = Math.Max(0, accumulated - ticks * Delta);
        return ticks;
    }
    public void BeginTick() => Tick++;
    public bool LegacyDue(double frequency)
    {
        if (frequency > 240) throw new InvalidOperationException("加载任务不能作为玩法频率执行");
        legacyAccumulated += Delta;
        double period = 1 / Math.Max(1, frequency);
        if (legacyAccumulated + 1e-12 < period) return false;
        legacyAccumulated = Math.Max(0, legacyAccumulated - period);
        return true;
    }
    public void Pause() { accumulated = 0; }
    public void RoomChanged() { accumulated = 0; legacyAccumulated = 0; }
}
