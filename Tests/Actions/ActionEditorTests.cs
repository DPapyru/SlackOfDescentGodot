namespace SlackOfDescent;

public static class ActionEditorTests
{
    public static int Run(string? temporary = null)
    {
        bool owned = temporary is null;
        temporary ??= Path.Combine(Path.GetTempPath(), "slack-action-tests-" + Guid.NewGuid().ToString("N"));
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Action editor: " + message); }
        var samples = NativeActionSamples.All();
        Check(samples.Count == 3, "three sample actions expose schemas");
        Check(samples.Select(sample => sample.Definition.Id).Distinct().Count() == 3, "sample ids stay unique");
        foreach (var sample in samples) NativeActionValues.ValidateDefinition(sample.Definition);
        var pulse = samples.Select(sample => sample.Definition).Single(value => value.Id == "sample.pulse");
        Check(pulse.Slot == CharacterActionSlot.Attack && pulse.Mode == CharacterActionMode.Additive, "sample declares slot and mode");
        Check(pulse.Parameter("mode")!.Type == NativeActionParameterType.Enum && pulse.Parameter("mode")!.Options.Count == 3, "enum parameter carries options");
        Check(pulse.Parameter("label")!.Type == NativeActionParameterType.String && pulse.Parameter("amount")!.Type == NativeActionParameterType.Number, "string and number parameters are typed");
        var defaults = NativeActionValues.Resolve(pulse, null);
        Check(defaults["amount"].Number == 1 && defaults["label"].ToString() == "pulse" && defaults["mode"].ToString() == "normal", "defaults resolve without input");
        var supplied = NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["amount"] = "7.5", ["label"] = "tap", ["mode"] = "hard" });
        Check(supplied["amount"].Number == 7.5 && supplied["label"].ToString() == "tap" && supplied["mode"].ToString() == "hard", "supplied values override defaults");
        var formatted = NativeActionValues.Format(pulse, supplied);
        Check(formatted["amount"] == "7.5" && formatted["label"] == "tap" && formatted["mode"] == "hard", "values format back to text");
        Check(Throws(() => NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["missing"] = "1" }), "未知参数"), "unknown parameter key is rejected");
        Check(Throws(() => NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["amount"] = "101" }), "上限"), "value above maximum is rejected");
        Check(Throws(() => NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["amount"] = "-1" }), "下限"), "value below minimum is rejected");
        Check(Throws(() => NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["amount"] = "NaN" }), "有限数值"), "NaN is rejected");
        Check(Throws(() => NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["amount"] = "Infinity" }), "有限数值"), "infinity is rejected");
        Check(Throws(() => NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["mode"] = "wrong" }), "可选项"), "enum outside options is rejected");
        Check(Throws(() => NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["label"] = new string('x', 5000) }), "文本过长"), "oversized text is rejected");
        Check(!Throws(() => NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["amount"] = "0" }), ""), "boundary minimum is accepted");
        Check(!Throws(() => NativeActionValues.Resolve(pulse, new Dictionary<string, string> { ["amount"] = "100" }), ""), "boundary maximum is accepted");
        var gravity = samples.Select(sample => sample.Definition).Single(value => value.Id == "sample.gravity");
        Check(!Throws(() => NativeActionValues.Resolve(gravity, new Dictionary<string, string> { ["strength"] = "-4" }), ""), "negative boundary is accepted");
        Check(Throws(() => NativeActionValues.Resolve(gravity, new Dictionary<string, string> { ["strength"] = "-5" }), "下限"), "negative boundary is enforced");
        var boost = samples.Select(sample => sample.Definition).Single(value => value.Id == "sample.boost");
        Check(Throws(() => NativeActionValues.Resolve(boost, new Dictionary<string, string> { ["frames"] = "0" }), "下限"), "frame count below one is rejected");
        Check(Throws(() => NativeActionValues.ValidateDefinition(new NativeActionDefinition { Id = "audit.dup", Parameters = [new() { Name = "a" }, new() { Name = "a" }] }), "重复"), "duplicate parameter names are rejected");
        Check(Throws(() => NativeActionValues.ValidateDefinition(new NativeActionDefinition { Id = "" }), "稳定标识"), "empty id is rejected");
        Check(Throws(() => NativeActionValues.ValidateDefinition(new NativeActionDefinition { Id = "audit.enum", Parameters = [new() { Name = "m", Type = NativeActionParameterType.Enum }] }), "选项"), "enum without options is rejected");
        Check(Throws(() => NativeActionValues.ValidateDefinition(new NativeActionDefinition { Id = "audit.range", Parameters = [new() { Name = "n", Min = 5, Max = 1 }] }), "颠倒"), "inverted bounds are rejected");
        var config = new NativeActionConfig { Action = pulse.Id, Slot = pulse.Slot, Mode = pulse.Mode, Target = NativeActionTargets.Enemy, Parameters = formatted };
        config.Save(temporary);
        string path = NativeActionConfig.Path(temporary, pulse.Id);
        Check(File.Exists(path) && Path.GetFileName(path) == "sample.pulse.action.json", "saved config uses the action file suffix");
        var loaded = NativeActionConfig.Load(path);
        Check(loaded.Action == pulse.Id && loaded.Slot == pulse.Slot && loaded.Mode == pulse.Mode && loaded.Target == NativeActionTargets.Enemy, "config round trip keeps metadata");
        Check(loaded.Parameters["amount"] == "7.5" && loaded.Parameters["mode"] == "hard", "config round trip keeps parameters");
        loaded.Validate(pulse);
        Check(Throws(() => new NativeActionConfig { Action = pulse.Id, Slot = CharacterActionSlot.Jump, Mode = pulse.Mode }.Validate(pulse), "槽位"), "config with wrong slot is rejected");
        Check(Throws(() => new NativeActionConfig { Action = pulse.Id, Slot = pulse.Slot, Mode = pulse.Mode, Target = "nobody" }.Validate(pulse), "目标"), "config with unknown target is rejected");
        Check(Throws(() => new NativeActionConfig { Action = "sample.missing", Slot = pulse.Slot, Mode = pulse.Mode }.Validate(pulse), "标识"), "config with wrong id is rejected");
        Check(Throws(() => NativeActionConfig.Path(temporary, "../escape"), "标识"), "unsafe id is rejected");
        Check(!Directory.Exists(NativeActionConfig.Path(temporary, "a")) && Directory.GetFiles(temporary, "*.action.json").Length == 1, "configs never escape the action directory");
        var gravityAction = new NativeSampleGravity();
        gravityAction.Configure(NativeActionValues.Resolve(gravity, new Dictionary<string, string> { ["strength"] = "2.5" }));
        Check(gravityAction.Strength == 2.5 && gravityAction.Slot == CharacterActionSlot.Jump && gravityAction.Mode == CharacterActionMode.Replace, "configured action keeps slot and mode");
        var boostAction = new NativeSampleBoost();
        boostAction.Configure(NativeActionValues.Resolve(boost, new Dictionary<string, string> { ["speed"] = "9", ["frames"] = "3" }));
        Check(boostAction.Speed == 9 && boostAction.Frames == 3, "configured action applies every parameter");
        var pulseAction = new NativeSamplePulse();
        pulseAction.Configure(NativeActionValues.Resolve(pulse, formatted));
        Check(pulseAction.ModeName == "hard", "enum parameter reaches the action");
        var registry = new ContentRegistry();
        registry.RegisterAction(pulse.Id, pulse, values => { var action = new NativeSamplePulse(); action.Configure(values); return action; });
        Check(registry.Actions.ContainsKey(pulse.Id) && registry.ActionSchema(pulse.Id)?.Id == pulse.Id, "registry registers the schema beside the factory");
        var created = registry.CreateAction(pulse.Id, new Dictionary<string, string> { ["amount"] = "3", ["mode"] = "soft" });
        Check(created is NativeSamplePulse { ModeName: "soft" }, "registry creates a configured action");
        Check(Throws(() => registry.CreateAction(pulse.Id, new Dictionary<string, string> { ["amount"] = "999" }), "上限"), "registry rejects out of range values");
        Check(Throws(() => registry.CreateAction("audit.missing"), "未加载"), "registry reports unknown actions");
        var plain = new ContentRegistry();
        plain.RegisterAction("audit.plain", () => new PlainAction());
        Check(plain.CreateAction("audit.plain") is PlainAction, "legacy factories still build unconfigured actions");
        Check(Throws(() => plain.CreateAction("audit.plain", new Dictionary<string, string> { ["speed"] = "2" }), "不接受参数"), "actions without parameters are rejected");
        plain.RegisterAction("audit.schema", () => new NativeSampleBoost());
        Check(plain.CreateAction("audit.schema", new Dictionary<string, string> { ["frames"] = "4" }) is NativeSampleBoost { Frames: 4 }, "actions carry their own schema for parameters");
        if (owned) Directory.Delete(temporary, true);
        return checks;
    }
    private sealed class PlainAction : ICharacterAction
    {
        public string Id => "audit.plain";
        public CharacterActionSlot Slot => CharacterActionSlot.Jump;
        public void Update(ActorContext actor, FrameContext frame) { }
    }
    private static bool Throws(Action action, string expected)
    {
        try { action(); return false; }
        catch (Exception error) { return error.GetBaseException().Message.Contains(expected, StringComparison.Ordinal); }
    }
}
