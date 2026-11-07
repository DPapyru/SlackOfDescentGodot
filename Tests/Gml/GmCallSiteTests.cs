using SlackOfDescent;

public static class GmCallSiteTests
{
    public static int Run(GmWorld source)
    {
        int assertions = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; }
        static bool Same(GmValue left, GmValue right) =>
            left.IsString == right.IsString
            && (left.IsString ? left.Text == right.Text : double.IsNaN(left.Number) && double.IsNaN(right.Number) || left.AlmostEquals(right));
        var world = new GmWorld(source.Root, source.SourceRoot);
        world.Evaluator.ResetBudget();

        var self = new GmInstance { Id = 999997, Object = -1 };
        world.Instances.Add(self);
        var context = new GmContext(self);
        foreach (var name in new[] { "abs", "floor", "ceil", "round", "sign", "sqrt", "sqr", "sin", "cos" })
        {
            var site = world.CallSite(name, 1);
            Check(site is not null, "one argument math site resolves: " + name);
            foreach (double value in new[] { 2.5, -3.25, 0.0 })
            {
                var generic = world.CallFixed(name, value, default, 1, context);
                var specialized = site!(new GmValue[] { value }, context);
                Check(Same(specialized, generic), name + " matches the generic path for " + value);
            }
        }
        foreach (var name in new[] { "min", "max", "power" })
        {
            var site = world.CallSite(name, 2);
            Check(site is not null, "two argument math site resolves: " + name);
            foreach (var (left, right) in new[] { (1.0, 2.0), (5.0, -1.0), (double.NaN, 3.0) })
            {
                var generic = world.CallFixed(name, left, right, 2, context);
                var specialized = site!(new GmValue[] { left, right }, context);
                Check(Same(specialized, generic), name + " matches the generic path for " + left + "," + right);
            }
        }

        var color = world.CallSite("make_color_rgb", 3);
        Check(color is not null, "make_color_rgb site resolves");
        Check(
            color!(new GmValue[] { 55, 44, 17 }, context).Equals(world.Call("make_color_rgb", new GmValue[] { 55, 44, 17 }, context)),
            "make_color_rgb matches the generic path"
        );

        Check(world.CallSite("not_a_builtin", 1) is null, "unknown names keep the generic path");
        string shadowed = world.Scripts.Keys.First();
        Check(world.CallSite(shadowed, 0) is null, "script names keep the generic path");

        string key = world.Compile("site_probe = min(3, 8) + abs(-2)");
        world.Evaluator.Run(key, context);
        Check(self.Scope.Read("site_probe").Number == 5, "compiled specialized call site computes min + abs");
        return assertions;
    }
}
