using Godot;

namespace SlackOfDescent;

public static class NativeCharacterRuntime
{
    public static async Task BindAsync(GmWorld world, GmNativeBridge bridge, CharacterDefinition definition)
    {
        var actor = world.Instances.FirstOrDefault(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objPlayerDoll"].Int)) ?? throw new InvalidOperationException("没有可控制的角色");
        if (definition.BasePreset is not ("Nana" or "Yuki" or "Ryoko")) throw new InvalidDataException("未知基础行为：" + definition.BasePreset);
        world.Evaluator.ResetBudget();
        var previous = actor.Scope.Values.Where(p => p.Key == "PartsList" || p.Key.StartsWith("PartsList[", StringComparison.Ordinal)).Select(p => p.Value.Int).Where(id => id >= 100000).Distinct().ToArray();
        foreach (int id in previous)
        {
            var part = world.Instances.FirstOrDefault(i => i.Id == id && !i.Destroyed);
            if (part is not null) await world.Evaluator.CallAsync("instance_destroy", [], new(part));
        }
        actor.Scope.Write("PartsNumber", 0); actor.Scope.Write("PartsList", world.Constants["noone"]);
        await world.Evaluator.CallAsync(definition.BasePreset + "Load", [], new(actor));
        actor.Scope.Write("HEAD_EXT", world.Constants["obj" + definition.BasePreset + "_Head"]);
        actor.Scope.Write("MotionNum", actor.Scope.ReadOr("STAND_0")); actor.Scope.Write("KeyFrmNum", 0);
        await world.Evaluator.CallAsync("LoadCharacter", [definition.Appearance, false], new(actor));
        bridge.BindCharacter(actor, definition);
    }
}
