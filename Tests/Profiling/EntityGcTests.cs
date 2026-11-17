using System.Text.Json;
using SlackOfDescent;

public static class EntityGcTests
{
    public static int Run(GmWorld shared)
    {
        int assertions = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; }
        var world = new GmWorld(shared.Root, shared.SourceRoot);
        var registry = new ContentRegistry();
        registry.RegisterEntity(new NativeEntityDefinition { Id = "perf.entity", BehaviorId = "perf.entity", Width = 8, Height = 8 }, () => new Probe());
        using var bridge = new GmNativeBridge(world, registry);
        for (int index = 0; index < 64; index++) bridge.Services.Entities.Spawn("perf.entity", 32 + index, 32);
        long tick = 0;
        void Update() => bridge.Update(new(FrameContext.FixedDelta, tick++ / 60.0, tick, InputSnapshot.Empty));
        for (int frame = 0; frame < 30; frame++) Update();
        int updates = bridge.EntityUpdates;
        int collections = GC.CollectionCount(2);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 200; frame++) Update();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check(bridge.EntityUpdates - updates == 200 * 64, "entity updates keep running through the measurement window");
        Check(GC.CollectionCount(2) == collections, "entity runtime runs no forced collection");
        Check(allocated < 4_000_000, "entity runtime allocation stays bounded: " + allocated + " bytes over 12800 updates");

        var loadWorld = new GmWorld(shared.Root, shared.SourceRoot);
        int room = 900778;
        loadWorld.Rooms[room] = JsonSerializer.SerializeToElement(new
        {
            name = "gc-test-room", width = 64, height = 64, speed = 30, colour = 0, viewsEnabled = false, clearScreen = false,
            tiles = Array.Empty<object>(), backgrounds = Array.Empty<object>(), views = Array.Empty<object>(),
            instances = Array.Empty<object>(), code = loadWorld.Compile("return 0;")
        });
        int loadCollections = GC.CollectionCount(2);
        loadWorld.LoadRoom(room);
        Check(GC.CollectionCount(2) > loadCollections, "room load forces a full collection");
        int started = GC.CollectionCount(2);
        for (int frame = 0; frame < 30; frame++) loadWorld.Step();
        Check(GC.CollectionCount(2) == started, "room start-up runs no further forced collection");
        return assertions;
    }

    private sealed class Probe : IEntityBehavior
    {
        public string Id => "perf.entity";
        public void Update(ActorContext entity, FrameContext frame) { }
    }
}
