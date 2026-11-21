using System.Text.Json;
using SlackOfDescent;

public static class GmCombatSdkTests
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
            Id = "sdk.combat.doll",
            BehaviorId = "sdk.combat.doll",
            Width = 10,
            Height = 10,
            BaseObject = "objEnemyDoll",
            Parameters = new()
            {
                ["HP"] = new() { Default = "10000" },
                ["INV"] = new() { Default = "0" },
                ["WEAK"] = new() { Default = "0" },
                ["MotionNum"] = new() { Default = "0" },
                ["DEAD_MOTION"] = new() { Default = "1" },
                ["DAMAGE_ACTIVE"] = new() { Default = "1" }
            }
        }, () => new Stub());
        var doll = services.Entities.Spawn("sdk.combat.doll", 100, 100);
        world.Globals.Write("controllMode", world.Constants["CTRL_KEYMOUSE"]);
        foreach (string script in new[] { "Quake", "Spark" })
            world.Code[world.Scripts[script]] = JsonSerializer.SerializeToElement(new object[] { "group", Array.Empty<object>() });
        int bulletObject = world.Constants["objBullet"].Int;
        int Fire()
        {
            var bullet = world.Create(bulletObject, 110, 100, run: false);
            bullet.Scope.Write("preX", 90.0);
            bullet.Scope.Write("preY", 100.0);
            bullet.Scope.Write("fireAngle", 0.0);
            world.Evaluator.ResetBudget();
            world.Event(bullet, 3, 1);
            return bullet.Id;
        }
        void Subscribe(string id, Action<NativeGameEvent> handle)
        {
            registry.EventHandlers.Add(id, new NativeEventHandler("combat-tests", "combat.damage", NativeEventPhase.Before, handle));
            registry.Refresh();
        }
        void Unsubscribe(string id)
        {
            registry.EventHandlers.Remove(id);
            registry.Refresh();
        }
        int target = -1, source = -1, calls = 0;
        double damage = -1;
        Subscribe("combat-tests:observe", value =>
        {
            calls++;
            target = value.Actor?.Id ?? -1;
            source = value.Arguments["source"].Int;
            damage = value.Arguments["damage"].Number;
        });
        int bulletId = Fire();
        Expect(target == doll.Id && source == bulletId && damage == 100, "S2a the handler receives target, source and original damage");
        doll.Write("HP", 10000);
        Unsubscribe("combat-tests:observe");
        Subscribe("combat-tests:x10", value => { value.Arguments["damage"] = value.Arguments["damage"].Number * 10; });
        Fire();
        Expect(doll.Read("HP") == 9000, "S2b a x10 modifier subtracts ten times the original damage");
        doll.Write("HP", 10000);
        Unsubscribe("combat-tests:x10");
        Fire();
        Expect(doll.Read("HP") == 9900, "S2c without a handler the original damage applies unchanged");
        doll.Write("HP", 10000);
        Subscribe("combat-tests:throw", _ => throw new InvalidOperationException("handler failure"));
        Fire();
        Unsubscribe("combat-tests:throw");
        Expect(doll.Read("HP") == 9900, "S2d a throwing handler falls back to the original damage");
        var bare = new GmWorld(shared.Root, shared.SourceRoot);
        var bareContext = new GmContext(new GmInstance { Id = 910001, Object = -1 });
        Expect(bare.Call("native_damage", [7.0], bareContext).Number == 7, "S2e native_damage passes the original amount through without services");
        doll.Write("HP", 10000);
        calls = 0;
        Subscribe("combat-tests:count", _ => calls++);
        var hit = new NativeRayHit(doll, doll.X, doll.Y, 0, false);
        services.Combat.Damage(new ActorContext(world, doll.Instance), hit, 300, 0);
        Unsubscribe("combat-tests:count");
        Expect(calls == 1 && doll.Read("HP") == 9700, "S2f the replayed damage block dispatches combat.damage once");
        return assertions;
    }

    private sealed class Stub : IEntityBehavior
    {
        public string Id => "sdk.combat";
        public void Update(ActorContext entity, FrameContext frame) { }
    }
}
