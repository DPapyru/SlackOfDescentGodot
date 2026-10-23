using SlackOfDescent;

public static class NativeTimingTests
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            count++;
        }
        void Near(double actual, double expected, string name) =>
            Check(Math.Abs(actual - expected) < 1e-9, $"{name}: expected {expected}, got {actual}");

        var timing = new GmNativeTiming();
        Near(timing.LegacyUnitsPerTick, .5, "native ticks retain half an original step");
        double alarm = 30;
        int fired = 0;
        for (int tick = 1; tick <= 120; tick++)
        {
            var next = timing.Alarm(alarm); alarm = next.Remaining;
            if (next.Fired) { fired++; Check(tick == 60, "one-second alarm fires at native tick sixty"); }
        }
        Check(fired == 1 && alarm == -1, "alarm dispatches once and reaches disabled sentinel");
        Check(timing.Alarm(0) == new GmNativeAlarmStep(-1, false), "zero alarm is disabled without dispatch");
        Check(timing.Alarm(-1) == new GmNativeAlarmStep(-1, false), "disabled alarm is unchanged");
        Check(timing.Alarm(.25) == new GmNativeAlarmStep(0, true), "fractional alarm crosses zero without truncation");
        alarm = 2;
        for (int tick = 1; tick <= 8; tick++)
        {
            var next = timing.Alarm(alarm); alarm = next.Remaining;
            if (next.Fired) { Check(tick is 4 or 8, "rearmed alarm retains duration"); alarm = 2; }
        }

        double legacyPosition = 0, nativePosition = 0, legacyVelocity = -8, nativeVelocity = -8;
        for (int step = 1; step <= 60; step++)
        {
            legacyVelocity += .35; legacyPosition += legacyVelocity;
            for (int tick = 0; tick < 2; tick++)
            {
                var motion = timing.Accelerate(nativeVelocity, .35);
                nativeVelocity = motion.Velocity; nativePosition += motion.Displacement;
            }
            Near(nativeVelocity, legacyVelocity, "native gravity preserves legacy velocity at step " + step);
            Near(nativePosition, legacyPosition, "native gravity preserves legacy position at step " + step);
        }
        var moved = timing.Accelerate(6, 0);
        Near(moved.Displacement, 3, "movement advances on every native tick");

        double fade = 1;
        for (int tick = 0; tick < 60; tick++) fade *= timing.Multiplier(.9);
        Near(fade, Math.Pow(.9, 30), "multiplicative fading retains one-second endpoint");
        double timer = 45;
        for (int tick = 0; tick < 60; tick++) timer -= timing.Increment(1);
        Near(timer, 15, "non-motion timer retains original elapsed time");
        foreach (double p in new[] { 0.0, .01, .2, .5, .99, 1.0 })
        {
            double halfProbability = timing.Probability(p);
            Near(1 - Math.Pow(1 - halfProbability, 2), p, "two native random trials retain legacy probability " + p);
        }

        double animation = 0;
        long animationEnds = 0;
        for (int tick = 1; tick <= 120; tick++)
        {
            var next = timing.Animate(animation, .5, 10);
            animation = next.Index; animationEnds += next.Wraps;
            Check(next.Wraps == (tick % 40 == 0 ? 1 : 0), "animation end occurs once at tick " + tick);
        }
        Check(animationEnds == 3 && animation == 0, "animation duration and wrap count retain two-second endpoint");
        Check(timing.Animate(1, 18, 4) == new GmNativeAnimationStep(2, 2), "fast animation reports every crossed cycle");
        Check(timing.Animate(0, -1, 4) == new GmNativeAnimationStep(3.5, -1), "reverse animation wraps explicitly");
        Check(timing.Animate(2, 1, 0) == new GmNativeAnimationStep(2, 0), "missing sprite cannot generate animation-end events");
        Check(GmNativeTiming.Crossed(2.75, 3.25, 3), "fractional timer detects forward threshold crossing");
        Check(GmNativeTiming.Crossed(3.25, 2.75, 3), "fractional timer detects backward threshold crossing");
        Check(!GmNativeTiming.Crossed(3, 3.5, 3), "threshold departure does not repeat discrete event");

        var cadence = new GmNativeEventCadence<(int Self, int Other, string Event)>();
        var contact = (1, 2, "damage");
        int contacts = 0;
        for (int tick = 0; tick < 60; tick++)
        {
            double time = tick * GmNativeTiming.Delta;
            if (cadence.TryDispatch(contact, time, 1.0 / 30)) contacts++;
            Check(!cadence.TryDispatch(contact, time, 1.0 / 30), "same contact cannot dispatch twice in native tick " + tick);
        }
        Check(contacts == 30, "continuous contact retains thirty damage events per second");
        Check(cadence.TryDispatch((1, 3, "damage"), 0, 1.0 / 30), "different collision partner retains its own event");
        Check(cadence.TryDispatch((1, 2, "sound"), 0, 1.0 / 30), "different effect retains its own event");
        Check(cadence.Remove(contact) && cadence.TryDispatch(contact, 0, 1.0 / 30), "destroyed contact state can be removed");
        cadence.Clear();
        Check(cadence.TryDispatch((1, 3, "damage"), 0, 1.0 / 30), "room reset clears event cadence");

        bool loadingRejected = false;
        try { _ = new GmNativeTiming(3000); } catch (ArgumentOutOfRangeException) { loadingRejected = true; }
        Check(loadingRejected, "loading frequency is not treated as gameplay delta");
        return count;
    }
}
