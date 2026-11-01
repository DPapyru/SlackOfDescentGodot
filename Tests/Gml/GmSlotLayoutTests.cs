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
            var layout = world.SlotLayout(obj);
            Check(layout is not null && layout.Count > 0, "swamp surface yields a slot layout");
            Check(layout!.ContainsKey("x"), "plain instance variables become slots: " + string.Join(",", layout.Keys));
            Check(!layout.ContainsKey("moss_m"), "indexed array names stay on the dictionary path");
            Check(!layout.ContainsKey("view_xview"), "engine variables stay off the layout");
            Check(!layout.ContainsKey("c_white"), "constants stay off the layout");
            Check(!layout.ContainsKey("hspeed"), "motion variables keep the world write path");

            var instance = world.Create(obj, 0, 0, run: false);
            Check(ReferenceEquals(instance.Scope.Layout, layout), "instances of the object share its layout");
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

            instance.Scope.UseLayout(null);
            Check(instance.Scope.Layout is null, "layout can be dropped");
            Check(instance.Scope.ReadOr("x").Number == 3, "dropping the layout keeps slot values");
        }
        finally
        {
            GmWorld.SlotExperiment = false;
        }
        return assertions;
    }
}
