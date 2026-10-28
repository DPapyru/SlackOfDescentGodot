using System.Globalization;

namespace SlackOfDescent;

public static class NativeActionSamples
{
    private static readonly Dictionary<string, Func<IActionConfigurable>> Factories = new(StringComparer.Ordinal)
    {
        ["sample.gravity"] = () => new NativeSampleGravity(),
        ["sample.boost"] = () => new NativeSampleBoost(),
        ["sample.pulse"] = () => new NativeSamplePulse()
    };
    public static IReadOnlyList<IActionConfigurable> All() => Factories.Values.Select(create => create()).ToArray();
    public static Func<IActionConfigurable>? Factory(string id) => Factories.GetValueOrDefault(id);
}

public sealed class NativeSampleGravity : ICharacterAction, IActionConfigurable
{
    private readonly NativeActionDefinition definition;
    private double strength = .5;
    public NativeSampleGravity()
    {
        definition = new()
        {
            Id = "sample.gravity", Name = "悬浮重力", Description = "每帧追加竖直速度。", Slot = CharacterActionSlot.Jump, Mode = CharacterActionMode.Replace,
            Parameters = [new() { Name = "strength", Description = "每帧附加的竖直速度", Type = NativeActionParameterType.Number, Default = "0.5", Min = -4, Max = 4 }]
        };
    }
    public NativeActionDefinition Definition => definition;
    public string Id => definition.Id;
    public CharacterActionSlot Slot => definition.Slot;
    public CharacterActionMode Mode => definition.Mode;
    public double Strength => strength;
    public void Configure(IReadOnlyDictionary<string, GmValue> values) => strength = values.TryGetValue("strength", out var value) ? value.Number : strength;
    public void Update(ActorContext actor, FrameContext frame) { actor.VerticalSpeed += strength; actor.Write("actionGravity", strength); }
}

public sealed class NativeSampleBoost : ICharacterAction, IActionConfigurable
{
    private readonly NativeActionDefinition definition;
    private double speed = 3, frames = 8, remaining;
    public NativeSampleBoost()
    {
        definition = new()
        {
            Id = "sample.boost", Name = "推进冲刺", Description = "按帧数追加水平速度。", Slot = CharacterActionSlot.Dash, Mode = CharacterActionMode.Additive,
            Parameters =
            [
                new() { Name = "speed", Description = "水平速度增量", Type = NativeActionParameterType.Number, Default = "3", Min = 0, Max = 40 },
                new() { Name = "frames", Description = "生效帧数", Type = NativeActionParameterType.Number, Default = "8", Min = 1, Max = 120 }
            ]
        };
    }
    public NativeActionDefinition Definition => definition;
    public string Id => definition.Id;
    public CharacterActionSlot Slot => definition.Slot;
    public CharacterActionMode Mode => definition.Mode;
    public double Speed => speed;
    public double Frames => frames;
    public void Configure(IReadOnlyDictionary<string, GmValue> values)
    {
        speed = values.TryGetValue("speed", out var value) ? value.Number : speed;
        frames = remaining = values.TryGetValue("frames", out var count) ? count.Number : frames;
    }
    public void Update(ActorContext actor, FrameContext frame)
    {
        if (remaining <= 0) return;
        actor.HorizontalSpeed += speed; remaining--;
    }
}

public sealed class NativeSamplePulse : ICharacterAction, IActionConfigurable
{
    private readonly NativeActionDefinition definition;
    private double amount = 1;
    private string label = "pulse", mode = "normal";
    public NativeSamplePulse()
    {
        definition = new()
        {
            Id = "sample.pulse", Name = "脉冲标记", Description = "逐帧累加可见标记。", Slot = CharacterActionSlot.Attack, Mode = CharacterActionMode.Additive,
            Parameters =
            [
                new() { Name = "amount", Description = "每次累加的数量", Type = NativeActionParameterType.Number, Default = "1", Min = 0, Max = 100 },
                new() { Name = "label", Description = "标记文本", Type = NativeActionParameterType.String, Default = "pulse" },
                new() { Name = "mode", Description = "脉冲模式", Type = NativeActionParameterType.Enum, Default = "normal", Options = ["normal", "soft", "hard"] }
            ]
        };
    }
    public NativeActionDefinition Definition => definition;
    public string Id => definition.Id;
    public CharacterActionSlot Slot => definition.Slot;
    public CharacterActionMode Mode => definition.Mode;
    public string ModeName => mode;
    public void Configure(IReadOnlyDictionary<string, GmValue> values)
    {
        amount = values.TryGetValue("amount", out var value) ? value.Number : amount;
        if (values.TryGetValue("label", out var text)) label = text.ToString();
        if (values.TryGetValue("mode", out var option)) mode = option.ToString();
    }
    public void Update(ActorContext actor, FrameContext frame)
    {
        actor.Write("actionPulse", actor.Read("actionPulse") + amount);
        actor.Instance.Scope.Write("actionPulseLabel", label);
        actor.Instance.Scope.Write("actionPulseMode", mode);
    }
}
