using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public static class SelfCheck
{
    private static int assertions;
    private static void Expect(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("ROPEBONDS_SELFCHECK FAILED: " + name);
        assertions++;
    }
    public static void Run()
    {
        BoneSets.Validate();
        foreach (string name in BoneSets.Hands.Concat(BoneSets.Feet).Concat(BoneSets.Body))
            Expect(NativeCharacterBones.Order.Contains(name, StringComparer.Ordinal), "bone name is canonical: " + name);
        var bone = new GmInstance();
        bone.Scope.Write("Angle", 17.5);
        bone.Scope.Write("ScaleX", 1.5);
        bone.Scope.Write("ScaleY", .5);
        bone.Scope.Write("PartPicture", 3);
        bone.Scope.Write("image_index", 2.25);
        bone.Scope.Write("x", 40);
        bone.Scope.Write("y", 50);
        var pose = BindPose.Snapshot(new[] { ("HAND1_ABOVE", bone) });
        bone.Scope.Write("Angle", -90);
        bone.Scope.Write("ScaleX", 9);
        bone.Scope.Write("ScaleY", 9);
        bone.Scope.Write("PartPicture", 8);
        bone.Scope.Write("image_index", 0);
        bone.Scope.Write("image_angle", 200);
        bone.Scope.Write("x", 999);
        bone.Scope.Write("y", 888);
        BindPose.Restore(new[] { ("HAND1_ABOVE", bone, 90d) }, pose);
        Expect(bone.Scope.ReadOr("Angle").Number == 17.5, "restore Angle");
        Expect(bone.Scope.ReadOr("ScaleX").Number == 1.5 && bone.Scope.ReadOr("ScaleY").Number == .5, "restore ScaleX/ScaleY");
        Expect(bone.Scope.ReadOr("PartPicture").Int == 3 && bone.Scope.ReadOr("image_index").Number == 2.25, "restore PartPicture/image_index");
        Expect(bone.Scope.ReadOr("image_angle").Number == 107.5, "image_angle follows current parent");
        Expect(bone.Scope.ReadOr("x").Number == 999 && bone.Scope.ReadOr("y").Number == 888, "world coordinates are left to the player");
        var single = BindPose.Union(BoneSets.Hands);
        Expect(single.Length == BoneSets.Hands.Length && single.SequenceEqual(BoneSets.Hands.OrderBy(name => name, StringComparer.Ordinal)), "single set union");
        var union = BindPose.Union(BoneSets.Hands, BoneSets.Feet, BoneSets.Hands);
        Expect(union.Length == BoneSets.Hands.Length + BoneSets.Feet.Length, "union deduplicates overlapping sets");
        Expect(union.Distinct(StringComparer.Ordinal).Count() == union.Length, "union has no duplicates");
        Expect(BoneSets.Hands.All(union.Contains) && BoneSets.Feet.All(union.Contains), "union contains every hand and foot bone");
        Expect(!BoneSets.Body.Any(union.Contains), "union excludes the body set");
        Expect(BindPose.Union(BoneSets.HandAbove, BoneSets.HandBelow).Length == BoneSets.Hands.Length, "hands are the union of both sides");
        var bones = new HashSet<string>(BindPose.Union(BoneSets.Of(BindPart.HandAbove), BoneSets.Of(BindPart.HandBelow)), StringComparer.Ordinal);
        var snapshots = new Dictionary<string, BoneLocal>(StringComparer.Ordinal);
        foreach (string name in bones) snapshots[name] = new BoneLocal(5, 1, 1, 0, 0);
        var released = ConstraintService.RemoveParts(bones, snapshots, BindPose.Union(BoneSets.Of(BindPart.HandBelow)));
        Expect(released.Length == BoneSets.HandBelow.Length, "release removes only the requested hand");
        Expect(bones.SetEquals(BindPose.Union(BoneSets.Of(BindPart.HandAbove))), "interleaved release keeps the other hand constrained");
        Expect(BindPose.Union(BoneSets.Of(BindPart.HandBelow)).All(name => !snapshots.ContainsKey(name)), "released bones leave the snapshot");
        var chain = new VerletChain(6, 0);
        chain.Reset(8, 100, 40);
        for (int step = 0; step < 600; step++) chain.Step(100 + step * .4, 40);
        Expect(chain.IsFinite, "verlet stays finite over 600 driven steps");
        Expect(Math.Abs(chain[0].X - 339.6) < 1e-6, "verlet head is driven by the caller");
        bool bounded = true;
        for (int index = 1; index < chain.Count; index++)
            if (Math.Abs(chain[index].X - 339.6) > 100 || Math.Abs(chain[index].Y - 40) > 100) bounded = false;
        Expect(bounded, "verlet stays bounded");
        bool stable = true;
        for (int index = 1; index < chain.Count; index++)
            if (Math.Abs(chain.Spacing(index) - chain.RestLength) > 1e-6) stable = false;
        Expect(stable, "verlet segment lengths are stable");
        var falling = new VerletChain(6, .35);
        falling.Reset(8, 100, 100);
        for (int step = 0; step < 600; step++) { falling.Step(100, 100); falling.Collide((x, y) => y > 100); }
        Expect(falling.IsFinite, "verlet gravity with floor stays finite");
        Console.WriteLine("ROPEBONDS_SELFCHECK PASS assertions=" + assertions);
    }
}
