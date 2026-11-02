using System.Text.Json;
using SlackOfDescent;

// Pins instance_activate/deactivate_region and the object variants against the collision index:
// inside/outside, notme, empty-bounds (point) instances and object selectors must be unchanged.
public static class RegionActivationTests
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
            frames = new[] { new { width = 8, height = 8, path = "unused" } },
            masks = new[] { new { width = 8, height = 8, left = 0, right = 7, top = 0, bottom = 7, data = new string('1', 64) } }
        }));
        int masked = world.Call("object_add", [], context).Int;
        int empty = world.Call("object_add", [], context).Int;
        world.Call("object_set_sprite", [masked, sprite], context);
        world.Evaluator.ResetBudget();
        var maskedInside = world.Create(masked, 110, 110);
        var maskedOutside = world.Create(masked, 10, 10);
        var maskedEdge = world.Create(masked, 145, 145);
        var emptyInside = world.Create(empty, 120, 120);
        var emptyOutside = world.Create(empty, 0, 0);
        void Region(string name, params int[] args) => world.Call(name, args.Select(v => new GmValue(v)).ToArray(), context);
        bool Active(params GmInstance[] instances) => instances.All(i => i.Active);
        bool Inactive(params GmInstance[] instances) => instances.All(i => !i.Active);
        Region("instance_deactivate_region", 100, 100, 50, 50, 1);
        Expect(Inactive(maskedInside, maskedEdge, emptyInside), "inside region deactivates overlapping bounds and empty-bounds points");
        Expect(Active(maskedOutside, emptyOutside), "inside region leaves outside instances active");
        Region("instance_activate_region", 100, 100, 50, 50, 1);
        Expect(Active(maskedInside, maskedEdge, emptyInside), "inside region reactivates the same instances");
        Region("instance_deactivate_region", 100, 100, 50, 50, 0);
        Expect(Inactive(maskedOutside, emptyOutside), "outside region deactivates non-overlapping instances");
        Expect(Active(maskedInside, maskedEdge, emptyInside), "outside region keeps overlapping instances active");
        Region("instance_activate_region", 100, 100, 50, 50, 0);
        Expect(Active(maskedOutside, emptyOutside), "outside region reactivates non-overlapping instances");
        var selfContext = new GmContext(maskedInside);
        world.Call("instance_deactivate_region", [100, 100, 50, 50, 1, 1], selfContext);
        Expect(maskedInside.Active, "notme keeps the calling instance active");
        Expect(Inactive(maskedEdge), "notme still deactivates the other overlapping instances");
        world.Call("instance_activate_region", [0, 0, 400, 400, 1], context);
        Expect(Active(maskedInside, maskedEdge, emptyInside), "activation region restores every instance");
        Region("instance_deactivate_object", empty);
        Expect(Inactive(emptyInside, emptyOutside), "object deactivation hits every instance of that object");
        Expect(Active(maskedInside, maskedEdge), "object deactivation leaves other objects active");
        Region("instance_activate_object", empty);
        Expect(Active(emptyInside, emptyOutside), "object activation restores every instance of that object");
        world.Write(maskedInside, "x", 10);
        world.Write(maskedInside, "y", 10);
        Region("instance_deactivate_region", 100, 100, 50, 50, 1);
        Expect(maskedInside.Active, "moved instance leaves the region on the next query");
        Expect(Inactive(maskedEdge), "moved instance no longer shields the region hit");
        Region("instance_activate_region", 100, 100, 50, 50, 1);
        Expect(Active(maskedEdge), "moved instance can be reactivated again");
        return assertions;
    }
}
