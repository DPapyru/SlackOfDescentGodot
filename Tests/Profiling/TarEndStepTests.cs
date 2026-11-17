using System.Text.Json;
using SlackOfDescent;

public static class TarEndStepTests
{
    public static int Run(GmWorld shared)
    {
        int assertions = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; }

        var world = new GmWorld(shared.Root, shared.SourceRoot);
        var context = new GmContext(new GmInstance());
        int room = 900778;
        world.Rooms[room] = JsonSerializer.SerializeToElement(new
        {
            name = "tar-endstep", width = 64, height = 64, speed = 30, colour = 0, viewsEnabled = false, clearScreen = false,
            tiles = Array.Empty<object>(), backgrounds = Array.Empty<object>(), views = Array.Empty<object>(),
            instances = Array.Empty<object>(), code = world.Compile("return 0;")
        });
        world.LoadRoom(room);

        int Obj(string name)
        {
            foreach (var o in world.Manifest.GetProperty("objects").EnumerateArray())
                if (o.GetProperty("name").GetString() == name) return o.GetProperty("id").GetInt32();
            throw new Exception("missing object " + name);
        }
        int sprite = world.RegisterSprite(JsonSerializer.SerializeToElement(new
        {
            originX = 0, originY = 0,
            frames = new[] { new { width = 32, height = 32, path = "unused" } },
            masks = new[] { new { width = 32, height = 32, left = 0, right = 31, top = 0, bottom = 31, data = new string('1', 32 * 32) } }
        }));
        int playerObj = Obj("objPlayerDoll"), tarObj = Obj("objTarString");
        int blockObj = world.Call("object_add", [], context).Int;
        world.Call("object_set_sprite", [blockObj, sprite], context);
        var player = world.Create(playerObj, 1000, 1000, run: false);
        player.Scope.Write("sprite_index", sprite);
        player.Scope.Write("mask_index", sprite);
        player.Scope.Write("xprevious", 1000);
        player.Scope.Write("yprevious", 1000);
        player.Scope.Write("hspeed", 0);
        player.Scope.Write("vspeed", 0);
        player.Scope.Write("stgl", 0);
        var block = world.Create(blockObj, 1000, 1024, run: false);
        block.Scope.Write("solid", 1);
        block.Scope.Write("sprite_index", sprite);
        var tar = world.Create(tarObj, 1000, 1010, run: true);
        tar.Scope.Write("moveObject", player.Id);
        tar.Scope.Write("acting", 1);

        Check(world.SlotLayoutFor(tarObj) is not null, "tar end step runs on the slot layout fast path");
        for (int i = 0; i < 50; i++)
        {
            player.Scope.Write("x", 1000);
            player.Scope.Write("y", 1000);
            world.Evaluator.ResetBudget();
            world.EventAsync(tar, 3, 2).GetAwaiter().GetResult();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            player.Scope.Write("x", 1000);
            player.Scope.Write("y", 1000);
            world.Evaluator.ResetBudget();
            world.EventAsync(tar, 3, 2).GetAwaiter().GetResult();
        }
        long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        Check(allocated < 4096, "tar end step allocation per call stays bounded: " + allocated + " bytes");
        Check(tar.Scope.ReadOr("alpha").Number > 0.2, "tar end step still executes");
        return assertions;
    }
}
