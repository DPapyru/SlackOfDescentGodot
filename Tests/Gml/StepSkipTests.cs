using System.Text.Json;
using SlackOfDescent;

// Pins the StepAsync per-instance skips: a motion pass that would write identical values, a
// one-frame animation without an Animation End event, and repeated xprevious/yprevious writes.
public static class StepSkipTests
{
    public static int Run(GmWorld shared)
    {
        int assertions = 0;
        void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + message);
            assertions++;
        }
        var world = new GmWorld(shared.Root, shared.SourceRoot);
        var context = new GmContext(new GmInstance());
        int Sprite(int frames)
        {
            var list = Enumerable.Range(0, frames).Select(_ => new { width = 8, height = 8, path = "unused" }).ToArray();
            return world.RegisterSprite(JsonSerializer.SerializeToElement(new
            {
                originX = 0, originY = 0, frames = list,
                masks = new[] { new { width = 8, height = 8, left = 0, right = 7, top = 0, bottom = 7, data = new string('1', 64) } }
            }));
        }
        int oneFrame = Sprite(1), twoFrames = Sprite(2);
        int Plain() => world.Call("object_add", [], context).Int;
        int plain = Plain(), moverObject = Plain();
        world.Call("object_event_add", [moverObject, 0, 0, "hspeed = 1;"], context);
        world.Evaluator.ResetBudget();
        var still = world.Create(plain, 10, 10);
        var mover = world.Create(moverObject, 0, 0);
        for (int step = 0; step < 3; step++) world.Step();
        Expect(still.Scope.Read("x").Number == 10 && still.Scope.Read("y").Number == 10, "resting instance stays put");
        Expect(still.MotionRest, "resting instance leaves the motion pass at rest");
        Expect(still.Scope.Read("xprevious").Number == 10 && still.Scope.Read("yprevious").Number == 10, "resting instance keeps its previous position");
        Expect(mover.Scope.Read("x").Number == 3 && mover.Scope.Read("xprevious").Number == 2, "moving instance keeps integrating and records the last step position");
        world.Write(still, "x", 50);
        world.Write(still, "y", 60);
        world.Step();
        Expect(still.Scope.Read("x").Number == 50 && still.Scope.Read("y").Number == 60, "teleport survives the skipped motion pass");
        Expect(still.Scope.Read("xprevious").Number == 50 && still.Scope.Read("yprevious").Number == 60, "teleport refreshes the previous position");
        world.Write(still, "hspeed", 2);
        world.Step();
        Expect(still.Scope.Read("x").Number == 52 && still.Scope.Read("xprevious").Number == 50, "physics write wakes the motion pass");
        world.Write(still, "hspeed", 0);
        world.Step();
        world.Step();
        Expect(still.Scope.Read("x").Number == 52, "zeroed velocity settles the motion pass again");
        int animObject = Plain();
        world.Call("object_set_sprite", [animObject, oneFrame], context);
        var anim = world.Create(animObject, 0, 0);
        for (int step = 0; step < 3; step++) world.Step();
        Expect(anim.Scope.Read("image_index").Number == 0, "one-frame sprite keeps image_index at zero");
        Expect(!anim.AnimationSettled, "one-frame sprite keeps the settled flag clear");
        anim.Scope.Write("hits", 0);
        world.Call("object_event_add", [animObject, 7, 7, "hits = hits + 1;"], context);
        world.Step();
        Expect(anim.Scope.Read("hits").Number == 1, "one-frame sprite with an Animation End event still fires it");
        int twoFrameObject = Plain();
        world.Call("object_set_sprite", [twoFrameObject, twoFrames], context);
        var twoFrame = world.Create(twoFrameObject, 0, 0);
        world.Step();
        Expect(twoFrame.Scope.Read("image_index").Number == 1, "two-frame sprite still advances");
        int fractionalObject = Plain();
        world.Call("object_set_sprite", [fractionalObject, oneFrame], context);
        var fractional = world.Create(fractionalObject, 0, 0);
        fractional.Scope.Write("image_index", 0.5);
        world.Step();
        Expect(fractional.Scope.Read("image_index").Number == 0.5, "fractional one-frame image keeps its frame");
        int previousObject = Plain();
        var previous = world.Create(previousObject, 20, 20);
        previous.Scope.Write("mark", 0);
        world.Call("object_event_add", [previousObject, 3, 1, "if (mark == 0) { mark = 1; xprevious = 123; }"], context);
        world.Step();
        Expect(previous.Scope.Read("xprevious").Number == 123, "event write to xprevious is preserved for the step");
        world.Step();
        Expect(previous.Scope.Read("xprevious").Number == 20, "xprevious cache is invalidated by an event write");
        return assertions;
    }
}
