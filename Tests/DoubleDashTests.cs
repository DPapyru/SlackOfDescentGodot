using DoubleDash;

public static class DoubleDashTests
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        const double delta = 1.0 / 60;
        double Play(DoubleDashState state, int ticks, int leftAt = -1, int rightAt = -1, bool grounded = true, bool canAct = true)
        {
            double distance = 0;
            for (int i = 0; i < ticks; i++) distance += state.Step(delta, i == leftAt, i == rightAt, grounded, canAct).Distance;
            return distance;
        }
        int Tap(DoubleDashState state, bool right, bool grounded = true)
        {
            var step = state.Step(delta, !right, right, grounded, true);
            return step.Begin ? step.Stage : 0;
        }
        DoubleDashState Fresh() => new(new DashSettings());
        int First(DoubleDashState state, bool right)
        {
            Play(state, 8);
            Play(state, 1, right ? -1 : 0, right ? 0 : -1);
            Play(state, 1);
            return Tap(state, right);
        }
        var idle = Fresh();
        Check(idle.Step(delta, false, false, true, true).Equals(DashStep.None) && idle.Stages == 0, "idle state does nothing");
        var loner = Fresh();
        Play(loner, 6, rightAt: 0);
        Check(!loner.Dashing && loner.Stages == 0, "a lone tap never starts a dash");

        var first = Fresh();
        Check(First(first, true) == 1, "double tap starts stage one");
        Check(Math.Abs(Play(first, 20) - first.Settings.FirstDistance) < 1e-6, "first stage covers the configured distance");
        Check(first.Stages == 1 && !first.Dashing && first.WindowOpen, "first stage opens the second stage window");
        Check(Tap(first, false) == 2, "single press inside the window starts stage two");
        Check(first.Direction == -1, "second stage may reverse direction");
        Check(Math.Abs(Play(first, 20) - first.Settings.SecondDistance) < 1e-6, "second stage covers its own distance");
        Check(!first.WindowOpen, "second stage closes the window");
        Check(Tap(first, true) == 0, "single press after two stages never starts a third stage");
        Check(first.Stages == 0 && !first.Dashing, "grounded chain resets after the second stage");
        Check(First(first, true) == 1, "fresh double tap after landing starts a new chain");

        var exhausted = Fresh();
        First(exhausted, true);
        Play(exhausted, 20, grounded: false);
        Tap(exhausted, false, grounded: false);
        Play(exhausted, 20, grounded: false);
        Check(exhausted.Stages == 2, "two stages consume the chain budget");
        Check(Tap(exhausted, true, grounded: false) == 0, "third input while airborne stays rejected");
        Play(exhausted, 12);
        Check(exhausted.Stages == 0, "landing clears the spent chain");
        Check(First(exhausted, true) == 1, "the chain re-arms as a fresh first stage");

        var late = Fresh();
        First(late, true);
        Play(late, 20);
        Play(late, 13);
        Check(!late.WindowOpen, "window closes after its configured duration");
        Check(Tap(late, false) == 0, "press after the window never starts a second stage");

        var landing = Fresh();
        First(landing, true);
        Play(landing, 20, grounded: false);
        Check(landing.Stages == 1 && landing.WindowOpen, "airborne stage keeps the window open");
        Play(landing, 1);
        Check(landing.Stages == 0 && !landing.WindowOpen, "landing clears the stage budget");

        var falling = Fresh();
        First(falling, true);
        Play(falling, 20, grounded: false);
        Check(Tap(falling, false, grounded: false) == 2, "second stage works while falling");

        var restricted = new DoubleDashState(new DashSettings { AllowAirSecond = false });
        First(restricted, true);
        Play(restricted, 20, grounded: false);
        Check(Tap(restricted, false, grounded: false) == 0, "allowAirSecond false rejects an airborne second stage");
        First(restricted, true);
        Play(restricted, 20);
        Check(Tap(restricted, false) == 2, "allowAirSecond false still allows a grounded second stage");

        var tuned = new DoubleDashState(new DashSettings { FirstDistance = 24, SecondDistance = 48, FirstFrames = 6, SecondWindow = 30 });
        First(tuned, true);
        Check(Math.Abs(Play(tuned, 12) - 24) < 1e-6, "first stage honours firstDistance and firstFrames");
        Check(Tap(tuned, false) == 2, "tuned window accepts stage two");
        Check(Math.Abs(Play(tuned, 12) - 48) < 1e-6, "second stage honours secondDistance");

        var cancel = Fresh();
        First(cancel, true);
        Check(Play(cancel, 3, canAct: false) == 0 && !cancel.Dashing, "losing control cancels the dash");

        var blocked = Fresh();
        First(blocked, true);
        Play(blocked, 4);
        Check(blocked.Dashing, "dash runs before the obstruction");
        blocked.Block();
        Check(!blocked.Dashing && blocked.Stages == 0, "obstruction unlatches without spending the budget");

        var reset = Fresh();
        First(reset, true);
        Play(reset, 20);
        reset.Settings.FirstDistance = 999;
        reset.Reset();
        Check(reset.Stages == 0 && !reset.Dashing && !reset.WindowOpen && reset.CooldownLeft == 0, "reset clears every field");
        Check(Math.Abs(reset.Settings.FirstDistance - 999) < 1e-6, "reset never rewrites configured parameters");
        return count;
    }
}
