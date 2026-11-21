using System.Text.Json;
using SlackOfDescent;

public static class GmEntitySdkTests
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
        var registry = new ContentRegistry();
        using var services = new NativeModServices(world, registry);
        registry.RegisterEntity(new NativeEntityDefinition
        {
            Id = "sdk.entity.doll",
            BehaviorId = "sdk.entity.doll",
            Width = 10,
            Height = 10,
            BaseObject = "objEnemyDoll",
            Parameters = new()
            {
                ["HP"] = new() { Default = "10000" },
                ["INV"] = new() { Default = "0" },
                ["WEAK"] = new() { Default = "0" },
                ["MotionNum"] = new() { Default = "0" },
                ["DEAD_MOTION"] = new() { Default = "1" }
            }
        }, () => new Stub());
        registry.RegisterEntity(new NativeEntityDefinition
        {
            Id = "sdk.entity.plain",
            BehaviorId = "sdk.entity.plain",
            Width = 10,
            Height = 10
        }, () => new Stub());
        var doll = services.Entities.Spawn("sdk.entity.doll", 100, 100);
        var plain = services.Entities.Spawn("sdk.entity.plain", 40, 40);
        int enemy = world.Constants["objEnemyDoll"].Int;
        int player = world.Constants["objPlayerDoll"].Int;
        Expect(services.Entities.Query("sdk.entity.doll").Count == 1, "S1a a definition declaring a base object spawns");
        Expect(world.IsObject(doll.Instance.Object, enemy), "S1a the declared base object joins the original family");
        Expect(services.Instances.IsObjectOf(doll, "objEnemyDoll"), "S1a the instance service reports the declared family");
        Expect(world.Select(enemy, new GmContext(doll.Instance)).Contains(doll.Instance), "S1a family selection includes the declared entity");
        Expect(!world.IsObject(doll.Instance.Object, player), "S1b the declared entity stays out of other families");
        Expect(!world.HasEvent(doll.Instance.Object, 3, 0), "S1b the declared entity runs no original step event");
        Expect(plain.Instance.Object == -100001, "S1c an undeclared entity keeps the synthetic native object");
        Expect(!world.IsObject(plain.Instance.Object, enemy), "S1c an undeclared entity stays out of the original family");
        Expect(doll.Instance.Object != plain.Instance.Object, "S1c declared and undeclared entities use distinct object definitions");
        var bounds = world.Bounds(doll.Instance);
        Expect(bounds.Left == 95 && bounds.Top == 95 && bounds.Right == 105 && bounds.Bottom == 105, "S1d entity bounds keep the declared width and height");
        world.Globals.Write("controllMode", world.Constants["CTRL_KEYMOUSE"]);
        foreach (string script in new[] { "Quake", "Spark" })
            world.Code[world.Scripts[script]] = JsonSerializer.SerializeToElement(new object[] { "group", Array.Empty<object>() });
        var bullet = world.Create(world.Constants["objBullet"].Int, 110, 100, run: false);
        bullet.Scope.Write("preX", 90.0);
        bullet.Scope.Write("preY", 100.0);
        bullet.Scope.Write("fireAngle", 0.0);
        world.Evaluator.ResetBudget();
        world.Event(bullet, 3, 1);
        Expect(doll.Read("HP") == 9900, "S1e a native bullet hit subtracts the original 100 damage");
        bool rejected = false;
        try
        {
            registry.RegisterEntity(new NativeEntityDefinition
            {
                Id = "sdk.entity.missing",
                BehaviorId = "sdk.entity.missing",
                BaseObject = "objMissingBase"
            }, () => new Stub());
            services.Entities.Spawn("sdk.entity.missing", 0, 0);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Expect(rejected, "S1f an unknown base object is rejected");
        return assertions;
    }

    private sealed class Stub : IEntityBehavior
    {
        public string Id => "sdk.entity";
        public void Update(ActorContext entity, FrameContext frame) { }
    }
}
