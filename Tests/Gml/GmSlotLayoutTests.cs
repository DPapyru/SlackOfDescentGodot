using SlackOfDescent;

public static class GmSlotLayoutTests
{
    public static int Run(GmWorld source)
    {
        int assertions = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; }
        GmWorld.SlotExperiment = true;
        try
        {
            var world = new GmWorld(source.Root, source.SourceRoot);
            world.Evaluator.ResetBudget();
            int obj = world.Constants["objSwampSurface"].Int;
            var plan = world.SlotLayout(obj);
            Check(plan is not null && plan.Scalars.Count > 0, "swamp surface yields a slot layout");
            var layout = plan!.Scalars;
            Check(layout.ContainsKey("x"), "plain instance variables become slots: " + string.Join(",", layout.Keys));
            Check(!layout.ContainsKey("moss_m"), "array base names stay out of the scalar layout");
            Check(plan.Arrays is not null && plan.Arrays.ContainsKey("moss_m"), "array base names become array slots");
            Check(plan.Arrays!.ContainsKey("moss") && plan.Arrays.ContainsKey("moss_a"), "every moss array gets a container slot");
            Check(!plan.Arrays.ContainsKey("view_xview"), "engine arrays stay off the layout");
            Check(!layout.ContainsKey("view_xview"), "engine variables stay off the layout");
            Check(!layout.ContainsKey("c_white"), "constants stay off the layout");
            Check(!layout.ContainsKey("hspeed"), "motion variables keep the world write path");

            var instance = world.Create(obj, 0, 0, run: false);
            Check(ReferenceEquals(instance.Scope.Layout, layout), "instances of the object share its layout");
            Check(ReferenceEquals(instance.Scope.ArrayLayout, plan.Arrays), "array slots share one layout");
            instance.Scope.Write("x", 7);
            Check(instance.Scope.Read("x").Number == 7, "slot read returns the written value");
            Check(instance.Scope.ReadOr("x").Number == 7, "ReadOr sees slot values");
            Check(
                instance.Scope.Values.TryGetValue("x", out var enumerated) && enumerated.Number == 7,
                "slot values stay enumerable"
            );

            string undefined = layout.Keys.First(name => name != "x");
            Check(!instance.Scope.Values.ContainsKey(undefined), "unwritten slot stays undefined");
            bool threw = false;
            try { instance.Scope.Read(undefined); } catch (KeyNotFoundException) { threw = true; }
            Check(threw, "uninitialized slot read still throws");

            var context = new GmContext(instance);
            string read = world.Compile("slot_probe = x + 1");
            world.Evaluator.Run(read, context);
            Check(instance.Scope.Read("slot_probe").Number == 8, "compiled slot read sees the slot");
            instance.Scope.Write("x", 3);
            world.Evaluator.Run(read, context);
            Check(instance.Scope.Read("slot_probe").Number == 4, "compiled slot read follows later writes");

            var scope = instance.Scope;
            scope.Write("moss_m", 5, 2);
            Check(scope.Read("moss_m", 2).Number == 5, "array slot write is readable by index");
            Check(scope.Read("moss_m", 1).Number == 0, "entries inside the array length read as zero");
            bool beyond = false;
            try { scope.Read("moss_m", 9); } catch (KeyNotFoundException) { beyond = true; }
            Check(beyond, "reading past an array slot length still throws");
            bool range = false;
            try { scope.Write("moss_m", 1, 40000); } catch (IndexOutOfRangeException) { range = true; }
            Check(range, "array slot writes keep the index range check");
            scope.Write("moss_m", 7, 1, 3);
            Check(scope.Read("moss_m", 1, 3).Number == 7, "two dimensional array slots keep the row");
            scope.Write("moss_m", "slime", 0);
            Check(scope.Read("moss_m", 0).IsString && scope.Read("moss_m", 0).Text == "slime", "string array elements keep their type");
            Check(scope.Values.TryGetValue("moss_m[2]", out var arrayValue) && arrayValue.Number == 5, "array slot entries stay enumerable");
            Check(scope.Values.Count > 0, "array slot entries keep the value count");

            long readsBefore = GmEvaluator.SlotArrayReads;
            long writesBefore = GmEvaluator.SlotArrayWrites;
            string arrayRead = world.Compile("slot_array_probe = moss_m[2] + 1");
            world.Evaluator.Run(arrayRead, context);
            Check(scope.Read("slot_array_probe").Number == 6, "compiled array read uses the container");
            Check(GmEvaluator.SlotArrayReads > readsBefore, "compiled array read takes the slot fast path");
            string arrayWrite = world.Compile("moss_m[4] = 11");
            world.Evaluator.Run(arrayWrite, context);
            Check(scope.Read("moss_m", 4).Number == 11, "compiled array write lands in the container");
            Check(GmEvaluator.SlotArrayWrites > writesBefore, "compiled array write takes the slot fast path");
            scope.Write("dynamic_array", 3, 2);
            Check(scope.Read("dynamic_array", 2).Number == 3, "domains outside the layout keep the dictionary path");

            assertions += CharacterSlotChecks(world);

            scope.UseLayout(null);
            Check(scope.Layout is null && scope.ArrayLayout is null, "layout can be dropped");
            Check(scope.ReadOr("x").Number == 3, "dropping the layout keeps slot values");
            Check(scope.Read("moss_m", 2).Number == 5, "dropping the layout keeps array slot values");
            Check(scope.Read("moss_m", 0).Text == "slime", "dropping the layout keeps string array entries");
        }
        finally
        {
            GmWorld.SlotExperiment = false;
        }
        return assertions;
    }

    private static int CharacterSlotChecks(GmWorld world)
    {
        int assertions = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; }
        int part = world.Constants["objNana_Foot_Below"].Int;
        var partPlan = world.SlotLayout(part);
        Check(partPlan is not null && partPlan.Scalars.ContainsKey("Socks0"), "character parts yield a slot layout with their event variables");
        foreach (string inherited in new[] { "objRyoko_Foot_Below", "objYuki_Foot_Below", "objPlayerParts", "objEnemyParts" })
        {
            var inheritedPlan = world.SlotLayout(world.Constants[inherited].Int);
            Check(inheritedPlan is not null && inheritedPlan.Scalars.Count > 0, inherited + " inherits a slot layout from its character ancestry");
        }
        var partInstance = world.Create(part, 0, 0, run: false);
        Check(ReferenceEquals(partInstance.Scope.Layout, partPlan!.Scalars), "character part instances share their layout");
        partInstance.Scope.Write("Socks0", 7);
        Check(partInstance.Scope.Read("Socks0").Number == 7, "character part slot writes round-trip");
        var partContext = new GmContext(partInstance);
        string partRead = world.Compile("part_probe = Socks0 + 1");
        world.Evaluator.Run(partRead, partContext);
        Check(partInstance.Scope.Read("part_probe").Number == 8, "compiled character code reads slots");
        var inheritedInstance = world.Create(world.Constants["objRyoko_Foot_Below"].Int, 0, 0, run: false);
        Check(inheritedInstance.Scope.Layout is not null, "instances of inherited character parts use the slot layout");
        int outside = world.Constants["objTarString"].Int;
        Check(world.SlotLayout(outside) is null, "objects outside the slot families keep the dictionary path");
        return assertions;
    }
}
