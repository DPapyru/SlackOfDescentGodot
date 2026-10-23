using System.Text.Json;
using SlackOfDescent;

public static class CollisionOptimizationTests
{
    private readonly record struct Mask(int Width, int Height, int OriginX, int OriginY,
        double Left, double Top, double Right, double Bottom, string Pixels);
    private readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public bool Empty => Right < Left || Bottom < Top;
    }

    private static Mask? ReadMask(GmWorld world, GmInstance instance)
    {
        int sprite = instance.Scope.ReadOr("mask_index", -1).Int;
        if (sprite < 0) sprite = instance.Scope.ReadOr("sprite_index", -1).Int;
        if (!world.Sprites.TryGetValue(sprite, out var definition)) return null;
        var masks = definition.GetProperty("masks");
        if (masks.GetArrayLength() == 0) return null;
        int index = (int)Math.Floor(instance.Scope.ReadOr("image_index").Number);
        index = (index % masks.GetArrayLength() + masks.GetArrayLength()) % masks.GetArrayLength();
        var mask = masks[index];
        int originX = definition.GetProperty("originX").GetInt32();
        int originY = definition.GetProperty("originY").GetInt32();
        return new(mask.GetProperty("width").GetInt32(), mask.GetProperty("height").GetInt32(), originX, originY,
            mask.GetProperty("left").GetDouble() - originX, mask.GetProperty("top").GetDouble() - originY,
            mask.GetProperty("right").GetDouble() + 1 - originX, mask.GetProperty("bottom").GetDouble() + 1 - originY,
            mask.GetProperty("data").GetString()!);
    }

    private static Box ReferenceBounds(Mask mask, GmInstance instance, double x, double y)
    {
        double sx = instance.Scope.ReadOr("image_xscale", 1).Number;
        double sy = instance.Scope.ReadOr("image_yscale", 1).Number;
        double angle = instance.Scope.ReadOr("image_angle").Number * Math.PI / 180;
        double cs = Math.Cos(angle), sn = Math.Sin(angle);
        double x1 = x + mask.Left * sx * cs + mask.Top * sy * sn;
        double x2 = x + mask.Left * sx * cs + mask.Bottom * sy * sn;
        double x3 = x + mask.Right * sx * cs + mask.Top * sy * sn;
        double x4 = x + mask.Right * sx * cs + mask.Bottom * sy * sn;
        double y1 = y - mask.Left * sx * sn + mask.Top * sy * cs;
        double y2 = y - mask.Left * sx * sn + mask.Bottom * sy * cs;
        double y3 = y - mask.Right * sx * sn + mask.Top * sy * cs;
        double y4 = y - mask.Right * sx * sn + mask.Bottom * sy * cs;
        return new(Math.Floor(Math.Min(Math.Min(x1, x2), Math.Min(x3, x4))),
            Math.Floor(Math.Min(Math.Min(y1, y2), Math.Min(y3, y4))),
            Math.Ceiling(Math.Max(Math.Max(x1, x2), Math.Max(x3, x4))) - 1,
            Math.Ceiling(Math.Max(Math.Max(y1, y2), Math.Max(y3, y4))) - 1);
    }

    private static bool ReferenceOpaque(Mask mask, GmInstance instance, double px, double py, double x, double y)
    {
        double sx = instance.Scope.ReadOr("image_xscale", 1).Number;
        double sy = instance.Scope.ReadOr("image_yscale", 1).Number;
        double angle = instance.Scope.ReadOr("image_angle").Number * Math.PI / 180;
        double cs = Math.Cos(angle), sn = Math.Sin(angle);
        double xx = cs / sx, xy = -sn / sx, yx = sn / sy, yy = cs / sy;
        double tx = mask.OriginX - x * xx - y * xy;
        double ty = mask.OriginY - x * yx - y * yy;
        double localX = px * xx + py * xy + tx;
        double localY = px * yx + py * yy + ty;
        if (!(localX >= 0 && localY >= 0 && localX < mask.Width && localY < mask.Height)) return false;
        return mask.Pixels[(int)Math.Floor(localY) * mask.Width + (int)Math.Floor(localX)] == '1';
    }

    private static bool ReferenceTouches(GmWorld world, GmInstance self, GmInstance other, double x, double y)
    {
        var first = ReadMask(world, self); var second = ReadMask(world, other);
        if (first is not Mask a || second is not Mask b) return false;
        double otherX = other.Scope.ReadOr("x").Number, otherY = other.Scope.ReadOr("y").Number;
        var aa = ReferenceBounds(a, self, x, y); var bb = ReferenceBounds(b, other, otherX, otherY);
        if (aa.Empty || bb.Empty) return false;
        for (double row = Math.Max(aa.Top, bb.Top); row <= Math.Min(aa.Bottom, bb.Bottom); row++)
        for (double col = Math.Max(aa.Left, bb.Left); col <= Math.Min(aa.Right, bb.Right); col++)
            if (ReferenceOpaque(a, self, col + .5, row + .5, x, y) &&
                ReferenceOpaque(b, other, col + .5, row + .5, otherX, otherY)) return true;
        return false;
    }

    private static bool ReferenceObject(GmWorld world, int actual, int target)
    {
        for (int depth = 0; depth < 128 && world.Objects.TryGetValue(actual, out var definition); depth++)
        {
            if (actual == target) return true;
            actual = definition.GetProperty("parent").GetInt32();
        }
        return false;
    }

    private static int ReferenceAt(GmWorld world, GmInstance self, double x, double y, int target)
    {
        foreach (var other in world.Instances)
        {
            if (ReferenceEquals(self, other) || other.Destroyed || !other.Active) continue;
            if (target != -3 && other.Id != target && !ReferenceObject(world, other.Object, target)) continue;
            if (ReferenceTouches(world, self, other, x, y)) return other.Id;
        }
        return -4;
    }

    public static int Run(string project, string source, string? userRoot = null)
    {
        var world = new GmWorld(Path.Combine(project, "GameData"), source, userRoot);
        var context = new GmContext(new GmInstance());
        int count = 0, positiveQueries = 0, negativeQueries = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            count++;
        }
        int AddSprite(int width, int height, int originX, int originY, params string[] frames)
        {
            foreach (string pixels in frames) Check(pixels.Length == width * height, "test mask dimensions match pixel data");
            var masks = frames.Select(pixels =>
            {
                var opaque = Enumerable.Range(0, pixels.Length).Where(i => pixels[i] == '1').ToArray();
                return new
                {
                    width, height, data = pixels,
                    left = opaque.Length == 0 ? width : opaque.Min(i => i % width),
                    right = opaque.Length == 0 ? -1 : opaque.Max(i => i % width),
                    top = opaque.Length == 0 ? height : opaque.Min(i => i / width),
                    bottom = opaque.Length == 0 ? -1 : opaque.Max(i => i / width)
                };
            }).ToArray();
            return world.RegisterSprite(JsonSerializer.SerializeToElement(new
            {
                originX, originY,
                frames = Enumerable.Range(0, Math.Max(1, frames.Length)).Select(_ => new { width, height, path = "unused" }).ToArray(),
                masks
            }));
        }
        int AddObject(int sprite, int parent = -1)
        {
            int obj = world.Call("object_add", [], context).Int;
            world.Call("object_set_sprite", [obj, sprite], context);
            if (parent >= 0) world.Call("object_set_parent", [obj, parent], context);
            return obj;
        }
        void Transform(GmInstance instance, double x, double y, double scaleX = 1, double scaleY = 1, double angle = 0)
        {
            instance.Scope.Write("x", x); instance.Scope.Write("y", y);
            instance.Scope.Write("image_xscale", scaleX); instance.Scope.Write("image_yscale", scaleY);
            instance.Scope.Write("image_angle", angle);
        }
        void Compare(GmInstance self, double x, double y, int target, string name)
        {
            int expected = ReferenceAt(world, self, x, y, target);
            if (expected == -4) negativeQueries++; else positiveQueries++;
            for (int warm = 0; warm < 2; warm++)
            {
                int actual = world.Call("instance_place", [x, y, target], new(self)).Int;
                Check(actual == expected, $"{name}, cache pass {warm}: expected instance {expected}, got {actual}");
                Check(world.Call("place_meeting", [x, y, target], new(self)).Truth == (expected != -4),
                    $"{name}, cache pass {warm}: boolean result differs from pixel oracle");
            }
        }

        int point = AddSprite(1, 1, 0, 0, "1");
        int solid = AddSprite(7, 5, 3, 2, new string('1', 35));
        int sparse = AddSprite(7, 5, 3, 2, "10001000010100001000010001000001000");
        int hollow = AddSprite(7, 5, 3, 2, "11111111000001100000110000011111111");
        int empty = AddSprite(7, 5, 3, 2, new string('0', 35));
        int absent = AddSprite(7, 5, 3, 2);
        int animated = AddSprite(3, 2, 1, 1, "100011", "011100", "000000");
        int actorType = AddObject(solid), targetType = AddObject(solid);
        var actor = world.Create(actorType, 0, 0, run: false);
        var target = world.Create(targetType, 0, 0, run: false);
        int[] sprites = [point, solid, sparse, hollow, empty, absent, animated];

        double[] angles = [0, 90, 180, 270, 360, -90, .001, 17, 44.5, 89.999, 135, -271.25];
        double[] scales = [0, .25, .5, 1, 1.5, 2, -1, -2, -.75];
        var random = new Random(0x4c5344);
        for (int sample = 0; sample < 1200; sample++)
        {
            actor.Scope.Write("sprite_index", sprites[random.Next(sprites.Length)]);
            target.Scope.Write("sprite_index", sprites[random.Next(sprites.Length)]);
            actor.Scope.Write("image_index", (random.Next(9) - 4) + .75);
            target.Scope.Write("image_index", (random.Next(9) - 4) + .25);
            double x = random.Next(-3, 4) * 128 + random.Next(4) * .25;
            double y = random.Next(-3, 4) * 128 + random.Next(4) * .25;
            Transform(actor, x, y, scales[random.Next(scales.Length)], scales[random.Next(scales.Length)], angles[random.Next(angles.Length)]);
            Transform(target, x + random.Next(-8, 9) + random.Next(4) * .25,
                y + random.Next(-8, 9) + random.Next(4) * .25,
                scales[random.Next(scales.Length)], scales[random.Next(scales.Length)], angles[random.Next(angles.Length)]);
            Compare(actor, x, y, targetType, "transformed mask sample " + sample);
            Compare(actor, x + .5, y - .25, target.Id, "hypothetical position sample " + sample);
        }

        actor.Scope.Write("sprite_index", solid); target.Scope.Write("sprite_index", solid);
        foreach (double x in new[] { -256.0, -128.25, -.5, 0, .25, .5, 127.75, 128, 256.5 })
        foreach (double delta in new[] { -7.0, -6.5, -1, 0, .25, 1, 6.5, 7 })
        {
            Transform(actor, x, .25); Transform(target, x + delta, .25);
            Compare(actor, x, .25, targetType, $"unit-scale edge at {x}, offset {delta}");
        }

        foreach (int width in new[] { 1, 2, 7, 31, 32, 33, 63, 64, 65, 127, 128, 129 })
        {
            string row = new string('0', width - 1) + '1';
            int edge = AddSprite(width, 3, width / 2, 1, row + new string('0', width) + '1' + new string('0', width - 1));
            actor.Scope.Write("sprite_index", edge); target.Scope.Write("sprite_index", edge);
            foreach (double shift in new[] { -width - .25, -width + 1.0, -1, 0, .25, .5, 1, width - 1.0, width + .25 })
            foreach (double mirror in new[] { 1.0, -1.0 })
            {
                Transform(actor, -.25, -.5); Transform(target, shift - .25, -.5, mirror, 1);
                Compare(actor, -.25, -.5, targetType, $"row width {width}, shift {shift}, mirror {mirror}");
            }
        }

        Transform(actor, 4096, 4096); Transform(target, -4096, -4096);
        actor.Scope.Write("sprite_index", solid);
        int parent = AddObject(solid), child = AddObject(solid, parent), grandchild = AddObject(solid, child);
        int unrelated = AddObject(solid);
        var first = world.Create(grandchild, 4096, 4096, id: 8999999, run: false);
        var second = world.Create(child, 4096, 4096, id: 8999997, run: false);
        var third = world.Create(parent, 4096, 4096, id: 8999998, run: false);
        Compare(actor, 4096, 4096, parent, "parent returns earliest grandchild");
        Check(world.Call("instance_place", [4096, 4096, parent], new(actor)).Int == first.Id,
            "first match is creation order rather than object depth or ID sorting");
        foreach (var candidate in new[] { first, second, third })
        {
            candidate.Active = false;
            Compare(actor, 4096, 4096, parent, "deactivate candidate " + candidate.Id);
        }
        first.Active = true; second.Active = true; third.Active = true;
        Compare(actor, 4096, 4096, -3, "all includes reactivated inherited objects");
        first.Destroyed = true;
        Compare(actor, 4096, 4096, parent, "destroyed earliest candidate is skipped");
        Transform(second, -1024, -512);
        Compare(actor, 4096, 4096, parent, "moved candidate leaves old spatial cell");
        Compare(actor, -1024, -512, parent, "moved candidate enters negative spatial cell");
        Transform(second, 4096, 4096);
        world.Call("instance_change", [unrelated, false], new(second));
        Compare(actor, 4096, 4096, parent, "instance type change invalidates cached inherited selection");
        world.Call("object_set_parent", [child, -1], context);
        Compare(actor, 4096, 4096, parent, "parent change invalidates inherited selection");
        world.Call("object_set_parent", [child, parent], context);
        world.Call("instance_change", [child, false], new(second));
        Compare(actor, 4096, 4096, parent, "restored ancestry and instance type");

        actor.Scope.Write("sprite_index", point); Transform(actor, 8192, 8192);
        var mutable = world.Create(targetType, 8192, 8192, run: false);
        Transform(mutable, 8192, 8192); mutable.Scope.Write("sprite_index", point);
        Compare(actor, 8192, 8192, mutable.Id, "mutable collider baseline");
        for (int repeat = 0; repeat < 4; repeat++)
        {
            mutable.Scope.Write("mask_index", empty);
            Compare(actor, 8192, 8192, targetType, "temporary empty target mask " + repeat);
            mutable.Scope.Write("mask_index", -1);
            Compare(actor, 8192, 8192, targetType, "restored target mask " + repeat);
            actor.Scope.Write("mask_index", empty);
            Compare(actor, 8192, 8192, targetType, "temporary empty query mask " + repeat);
            actor.Scope.Write("mask_index", -1);
            Compare(actor, 8192, 8192, targetType, "restored query mask " + repeat);
            mutable.Scope.Write("mask_index", solid); mutable.Scope.Write("mask_index", -1);
            mutable.Scope.Write("x", 8192 + repeat + 1);
            Compare(actor, 8192, 8192, targetType, "coalesced mask restoration with movement " + repeat);
            mutable.Scope.Write("x", 8192);
        }
        actor.Scope.Write("sprite_index", solid); mutable.Scope.Write("sprite_index", animated);
        foreach (double frame in new[] { 0.0, .75, 1, 1.99, 2, 3, -1, -2.5 })
        {
            mutable.Scope.Write("image_index", frame);
            Compare(actor, 8192, 8192, targetType, "per-frame collision mask " + frame);
        }
        mutable.Scope.Write("sprite_index", point); mutable.Scope.Write("image_index", 0);
        world.Call("sprite_set_offset", [point, -130, 0], context);
        Compare(actor, 8192, 8192, targetType, "sprite offset mutation invalidates spatial membership");
        world.Call("sprite_set_offset", [point, 0, 0], context);
        Compare(actor, 8192, 8192, targetType, "sprite offset restored");
        int revised = AddSprite(7, 5, 3, 2, new string('1', 35));
        mutable.Scope.Write("sprite_index", revised);
        Compare(actor, 8192, 8192, targetType, "dynamic collision mask before rewrite");
        world.Call("sprite_collision_mask", [revised, false, 2, 0, 0, 0, 0, 1], context);
        Compare(actor, 8192, 8192, targetType, "dynamic collision mask after rewrite");
        world.Call("sprite_delete", [revised], context);
        Compare(actor, 8192, 8192, targetType, "deleted sprite cannot reuse cached mask");

        mutable.Scope.Write("sprite_index", point);
        Transform(mutable, -32768, -32768, 40000, 1);
        Compare(actor, -8192, -32768, targetType, "large collider side index");
        Transform(mutable, 8192, 8192);
        Compare(actor, -8192, -32768, targetType, "shrunk collider removes old large membership");
        actor.Scope.Write("sprite_index", point);
        Transform(mutable, 8272, 8192, 40000, 1);
        Compare(actor, 8193, 8192, targetType, "cached region first misses large collider");
        Compare(actor, 8292, 8192, targetType, "same cached region includes distant edge of large collider");
        Transform(mutable, 8192, 8192);
        Transform(actor, 8192, 8192);
        actor.Scope.Write("mask_index", empty);
        Compare(actor, 8192, 8192, targetType, "query self keeps temporary mask pending");
        Compare(mutable, 8192, 8192, actorType, "next query observes pending target mask");
        actor.Scope.Write("mask_index", -1);
        actor.Scope.Write("x", 8320);
        Compare(mutable, 8320, 8192, actorType, "next query observes restored mask and moved target");
        mutable.Destroyed = true;
        world.Step();
        var replacement = world.Create(targetType, 8192, 8192, run: false);
        replacement.Scope.Write("sprite_index", point);
        Compare(actor, 8192, 8192, targetType, "removed collider replaced without changing final count");
        world.Instances.Clear();
        Compare(actor, 8192, 8192, targetType, "clearing the scene discards cached region members");
        Check(positiveQueries > 100 && negativeQueries > 100, "differential samples exercise both hits and misses");
        return count;
    }
}
