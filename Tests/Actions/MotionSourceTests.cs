namespace SlackOfDescent;

public static class MotionSourceTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Motion source: " + message); }
        var debounce = new MotionSourceDebounce(.4);
        debounce.Restart();
        Check(!debounce.Tick(.2) && debounce.Pending, "a pause shorter than the delay keeps waiting");
        Check(debounce.Tick(.3) && !debounce.Pending, "the debounce fires once the delay elapses");
        Check(!debounce.Tick(.5), "the debounce fires only once per burst");
        debounce.Restart();
        Check(!debounce.Tick(.2), "a new edit starts a new wait");
        debounce.Restart();
        Check(!debounce.Tick(.35) && debounce.Pending, "the latest edit replaces the pending wait");
        Check(debounce.Tick(.1), "the latest edit fires after its own delay");
        debounce.Restart();
        debounce.Cancel();
        Check(!debounce.Tick(.5) && !debounce.Pending, "cancelling drops the pending update");
        return checks;
    }
}
