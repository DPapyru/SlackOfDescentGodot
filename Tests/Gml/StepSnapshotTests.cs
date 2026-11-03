using System.Text.Json;
using SlackOfDescent;

public static class StepSnapshotTests
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
        int sprite = world.RegisterSprite(JsonSerializer.SerializeToElement(new
        {
            originX = 0, originY = 0,
            frames = new[] { new { width = 8, height = 8, path = "unused" }, new { width = 8, height = 8, path = "unused" } },
            masks = new[] { new { width = 8, height = 8, left = 0, right = 7, top = 0, bottom = 7, data = new string('1', 64) } }
        }));
        int Plain() => world.Call("object_add", [], context).Int;
        int spawner = Plain(), moved = Plain(), beginBorn = Plain(), endBorn = Plain(), victimObject = Plain();
        world.Call("object_event_add", [moved, 0, 0, "hspeed = 1;"], context);
        world.Call("object_event_add", [beginBorn, 0, 0, "hspeed = 2;"], context);
        world.Call("object_event_add", [endBorn, 0, 0, "hspeed = 3; image_speed = 1;"], context);
        world.Call("object_set_sprite", [endBorn, sprite], context);
        world.Evaluator.ResetBudget();
        var spawnerInstance = world.Create(spawner, 0, 0);
        spawnerInstance.Scope.Write("spawnBegun", 0);
        spawnerInstance.Scope.Write("spawnEnded", 0);
        var mover = world.Create(moved, 0, 0);
        var victim = world.Create(victimObject, 5, 5);
        spawnerInstance.Scope.Write("victim", victim.Id);
        world.Call("object_event_add", [spawner, 3, 1, "if (spawnBegun == 0) { spawnBegun = 1; instance_create(20, 20, " + beginBorn + "); with (victim) instance_destroy(); }"], context);
        world.Call("object_event_add", [spawner, 3, 2, "if (spawnEnded == 0) { spawnEnded = 1; instance_create(40, 40, " + endBorn + "); }"], context);
        world.Step();
        GmInstance Unique(int obj) => world.Instances.Single(i => i.Object == obj);
        var beginInstance = Unique(beginBorn);
        var endInstance = Unique(endBorn);
        Expect(mover.Scope.Read("x").Number == 1, "pre-existing instance moves once per step");
        Expect(beginInstance.Scope.Read("x").Number == 22, "instance created in Begin Step enters the motion pass");
        Expect(endInstance.Scope.Read("x").Number == 40, "instance created in End Step skips the motion pass");
        Expect(endInstance.Scope.Read("image_index").Number == 1, "instance created in End Step enters the animation pass");
        Expect(victim.Destroyed, "instance destroyed by Begin Step is destroyed");
        Expect(!world.Instances.Contains(victim), "destroyed instance leaves the live list");
        Expect(victim.Scope.Read("x").Number == 5, "instance destroyed by Begin Step skips the motion pass");
        return assertions;
    }
}
