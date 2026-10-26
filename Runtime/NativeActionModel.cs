using System.Globalization;

namespace SlackOfDescent;

public enum NativeActionParameterType { Number, Bool, String, Enum }

public sealed class NativeActionParameter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public NativeActionParameterType Type { get; set; } = NativeActionParameterType.Number;
    public string Default { get; set; } = "0";
    public double? Min { get; set; }
    public double? Max { get; set; }
    public List<string> Options { get; set; } = new();
}

public sealed class NativeActionDefinition
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public CharacterActionSlot Slot { get; set; }
    public CharacterActionMode Mode { get; set; }
    public List<NativeActionParameter> Parameters { get; set; } = new();
    public string PreviewMotion { get; set; } = "";
    public NativeActionParameter? Parameter(string name) => Parameters.FirstOrDefault(parameter => parameter.Name == name);
}

public interface IActionSchema
{
    NativeActionDefinition Definition { get; }
}

public interface IActionConfigurable : IActionSchema
{
    void Configure(IReadOnlyDictionary<string, GmValue> values);
}

public static class NativeActionValues
{
    private static readonly Dictionary<string, string> Empty = new(StringComparer.Ordinal);
    public static void ValidateDefinition(NativeActionDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id)) throw new InvalidDataException("动作需要稳定标识。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name)) throw new InvalidDataException("动作参数需要稳定名称：" + definition.Id);
            if (!names.Add(parameter.Name)) throw new InvalidDataException("动作参数重复：" + parameter.Name);
            if (parameter.Type == NativeActionParameterType.Enum && parameter.Options.Count == 0) throw new InvalidDataException("枚举参数缺少选项：" + parameter.Name);
            if (parameter.Min is { } min && parameter.Max is { } max && min > max) throw new InvalidDataException("参数上下限颠倒：" + parameter.Name);
            Parse(parameter, parameter.Default);
        }
    }
    public static Dictionary<string, GmValue> Resolve(NativeActionDefinition definition, IReadOnlyDictionary<string, string>? supplied)
    {
        ValidateDefinition(definition);
        var raw = supplied ?? Empty;
        foreach (string key in raw.Keys)
            if (definition.Parameter(key) is null) throw new InvalidDataException("未知参数：" + key);
        var values = new Dictionary<string, GmValue>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters)
            values[parameter.Name] = Parse(parameter, raw.TryGetValue(parameter.Name, out string? text) ? text : parameter.Default);
        return values;
    }
    public static Dictionary<string, string> Format(NativeActionDefinition definition, IReadOnlyDictionary<string, GmValue> values)
    {
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters)
            text[parameter.Name] = values.TryGetValue(parameter.Name, out var value) ? Format(parameter, value) : parameter.Default;
        return text;
    }
    private static string Format(NativeActionParameter parameter, GmValue value) => parameter.Type switch
    {
        NativeActionParameterType.Bool => value.Truth ? "true" : "false",
        NativeActionParameterType.Number => value.Number.ToString("R", CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
    public static GmValue Parse(NativeActionParameter parameter, string? text)
    {
        string raw = (text ?? "").Trim();
        switch (parameter.Type)
        {
            case NativeActionParameterType.Number:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                    throw new InvalidDataException("参数需要有限数值：" + parameter.Name);
                if (parameter.Min is { } min && number < min) throw new InvalidDataException("参数低于下限 " + min.ToString("R", CultureInfo.InvariantCulture) + "：" + parameter.Name);
                if (parameter.Max is { } max && number > max) throw new InvalidDataException("参数高于上限 " + max.ToString("R", CultureInfo.InvariantCulture) + "：" + parameter.Name);
                return number;
            case NativeActionParameterType.Bool:
                if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1") return 1;
                if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0") return 0;
                throw new InvalidDataException("参数需要 true 或 false：" + parameter.Name);
            case NativeActionParameterType.Enum:
                string match = parameter.Options.FirstOrDefault(option => option == raw)
                    ?? parameter.Options.FirstOrDefault(option => string.Equals(option, raw, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("参数不在可选项中：" + parameter.Name);
                return match;
            default:
                if (raw.Length > 4096) throw new InvalidDataException("参数文本过长：" + parameter.Name);
                return text ?? "";
        }
    }
}

public static class NativeActionTargets
{
    public const string Player = "player";
    public const string Enemy = "enemy";
    public static readonly string[] All = [Player, Enemy];
}

public sealed class NativeActionConfig
{
    public int FormatVersion { get; set; } = 1;
    public string Action { get; set; } = "";
    public CharacterActionSlot Slot { get; set; }
    public CharacterActionMode Mode { get; set; }
    public string Target { get; set; } = NativeActionTargets.Player;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
    public static string Path(string directory, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new InvalidDataException("动作配置需要有效标识。");
        return System.IO.Path.Combine(directory, id + ".action.json");
    }
    public void Save(string directory) => NativeContentStore.Save(Path(directory, Action), this);
    public static NativeActionConfig Load(string path) => NativeContentStore.Load<NativeActionConfig>(path);
    public void Validate(NativeActionDefinition definition)
    {
        if (Action != definition.Id) throw new InvalidDataException("动作配置与动作标识不一致：" + Action);
        if (Slot != definition.Slot) throw new InvalidDataException("动作配置与动作槽位不一致：" + Action);
        if (Mode != definition.Mode) throw new InvalidDataException("动作配置与动作模式不一致：" + Action);
        if (!NativeActionTargets.All.Contains(Target)) throw new InvalidDataException("动作目标无效：" + Target);
        NativeActionValues.Resolve(definition, Parameters);
    }
}

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
