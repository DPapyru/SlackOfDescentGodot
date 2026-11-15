using System;
using System.Collections.Generic;
using System.Linq;
using SlackOfDescent;

namespace RopeBonds;

public sealed partial class RopeBondsMod : IMod
{
    public string Id => "example.ropebonds";
    public ConstraintService Constraints { get; } = new();
    public void Load(ModContext context)
    {
        Constraints.Attach(context);
        RegisterMotions(context);
        RegisterActions(context);
        RegisterMaps(context);
        RegisterCreatures(context);
        if (Environment.GetEnvironmentVariable("ROPEBONDS_SELFCHECK") is { Length: > 0 }) RunSelfCheck();
    }
    public void Unload()
    {
        RopeCreature.ReleaseAll();
        Constraints.ReleaseAll();
    }
    partial void RegisterMotions(ModContext context);
    partial void RegisterActions(ModContext context);
    partial void RegisterMaps(ModContext context);
    partial void RegisterCreatures(ModContext context);

    private static int selfCheckAssertions;
    private static void Expect(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("ROPEBONDS_SELFCHECK FAILED: " + name);
        selfCheckAssertions++;
    }
    private static void RunSelfCheck()
    {
        ConstraintService.Validate();
        foreach (string name in ConstraintService.Hands.Concat(ConstraintService.Feet).Concat(ConstraintService.Body))
            Expect(NativeCharacterBones.Order.Contains(name, StringComparer.Ordinal), "bone name is canonical: " + name);
        var bone = new GmInstance();
        bone.Scope.Write("Angle", 17.5);
        bone.Scope.Write("ScaleX", 1.5);
        bone.Scope.Write("ScaleY", .5);
        bone.Scope.Write("PartPicture", 3);
        bone.Scope.Write("image_index", 2.25);
        var pose = ConstraintService.Snapshot(new[] { ("HAND1_ABOVE", bone) });
        Expect(pose["HAND1_ABOVE"].Angle == 17.5 && pose["HAND1_ABOVE"].ScaleX == 1.5 && pose["HAND1_ABOVE"].ScaleY == .5
            && pose["HAND1_ABOVE"].Picture == 3 && pose["HAND1_ABOVE"].ImageIndex == 2.25, "snapshot captures local pose");
        var single = ConstraintService.Union(ConstraintService.Hands);
        Expect(single.Length == ConstraintService.Hands.Length && single.SequenceEqual(ConstraintService.Hands.OrderBy(name => name, StringComparer.Ordinal)), "single set union");
        var union = ConstraintService.Union(ConstraintService.Hands, ConstraintService.Feet, ConstraintService.Hands);
        Expect(union.Length == ConstraintService.Hands.Length + ConstraintService.Feet.Length, "union deduplicates overlapping sets");
        Expect(union.Distinct(StringComparer.Ordinal).Count() == union.Length, "union has no duplicates");
        Expect(ConstraintService.Hands.All(union.Contains) && ConstraintService.Feet.All(union.Contains), "union contains every hand and foot bone");
        Expect(!ConstraintService.Body.Any(union.Contains), "union excludes the body set");
        Expect(ConstraintService.Union(ConstraintService.HandAbove, ConstraintService.HandBelow).Length == ConstraintService.Hands.Length, "hands are the union of both sides");
        Expect(ConstraintService.BoundAngle(10, 35) - 10 == ConstraintService.BoundAngle(70, 35) - 70, "both hands share one bound angle delta");
        var bones = new HashSet<string>(ConstraintService.Union(ConstraintService.Of(BindPart.HandAbove), ConstraintService.Of(BindPart.HandBelow)), StringComparer.Ordinal);
        var snapshots = ConstraintService.Snapshot(bones.Select(name => (name, bone)));
        var released = ConstraintService.RemoveParts(bones, snapshots, ConstraintService.Union(ConstraintService.Of(BindPart.HandBelow)));
        Expect(released.Length == ConstraintService.HandBelow.Length, "release removes only the requested hand");
        Expect(bones.SetEquals(ConstraintService.Union(ConstraintService.Of(BindPart.HandAbove))), "interleaved release keeps the other hand constrained");
        Expect(ConstraintService.Union(ConstraintService.Of(BindPart.HandBelow)).All(name => !snapshots.ContainsKey(name)), "released bones leave the snapshot");
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
        Console.WriteLine("ROPEBONDS_SELFCHECK PASS assertions=" + selfCheckAssertions);
    }
}
