using System;
using System.Collections.Generic;
using SlackOfDescent;

namespace DoubleDash;

public sealed class DoubleDashMod : IMod
{
    public string Id => "example.double-dash";
    public void Load(ModContext context)
    {
        var definition = DoubleDashAction.CreateDefinition();
        context.Content.RegisterAction(definition.Id, definition, values => new DoubleDashAction(values));
        context.Content.RegisterCharacter(new CharacterDefinition {
            Id = "example.double-dash-nana", Name = "Nana · 二段冲刺", BasePreset = "Nana", Appearance = "Character-Nana",
            Actions = new() { [CharacterActionSlot.Dash] = definition.Id }, Dependencies = new() { Id } });
    }
    public void Unload() { }
}

public sealed class DoubleDashAction : ICharacterAction, IActionConfigurable
{
    public const string ActionId = "example.double-dash";
    private readonly NativeActionDefinition definition;
    private readonly DashSettings settings = new();
    private readonly DoubleDashState state;
    private bool active;
    private bool directionTaken;
    private double originalDirection;
    private double runDistance;
    public DoubleDashAction() : this(null) { }
    public DoubleDashAction(IReadOnlyDictionary<string, GmValue>? values) : this(values, CreateDefinition()) { }
    public DoubleDashAction(IReadOnlyDictionary<string, GmValue>? values, NativeActionDefinition definition)
    {
        this.definition = definition;
        state = new(settings);
        Configure(values ?? new Dictionary<string, GmValue>());
    }
    public static NativeActionDefinition CreateDefinition() => new()
    {
        FormatVersion = 1, Id = ActionId, Name = "二段冲刺", Description = "冲刺结束后在窗口内再次按下方向键可以追加第二段冲刺，方向可不同。",
        Slot = CharacterActionSlot.Dash, Mode = CharacterActionMode.Replace, PreviewMotion = "DASH",
        Parameters =
        [
            new() { Name = "firstDistance", Description = "第一段冲刺的距离", Type = NativeActionParameterType.Number, Default = "72", Min = 8, Max = 400 },
            new() { Name = "firstFrames", Description = "第一段冲刺持续的帧数", Type = NativeActionParameterType.Number, Default = "10", Min = 1, Max = 60 },
            new() { Name = "secondDistance", Description = "第二段冲刺的距离", Type = NativeActionParameterType.Number, Default = "72", Min = 8, Max = 400 },
            new() { Name = "tapFrames", Description = "判定双击的帧数窗口", Type = NativeActionParameterType.Number, Default = "7", Min = 1, Max = 30 },
            new() { Name = "secondWindow", Description = "第一段结束后可以追加第二段的帧数", Type = NativeActionParameterType.Number, Default = "12", Min = 1, Max = 60 },
            new() { Name = "cooldown", Description = "两段结束后重新蓄能的帧数", Type = NativeActionParameterType.Number, Default = "10", Min = 0, Max = 120 },
            new() { Name = "allowAirSecond", Description = "第二段是否允许在空中触发", Type = NativeActionParameterType.Bool, Default = "true" }
        ]
    };
    public NativeActionDefinition Definition => definition;
    public string Id => definition.Id;
    public CharacterActionSlot Slot => definition.Slot;
    public CharacterActionMode Mode => definition.Mode;
    public DashSettings Settings => settings;
    public int Stages => state.Stages;
    public void Configure(IReadOnlyDictionary<string, GmValue> values)
    {
        settings.FirstDistance = Number(values, "firstDistance", settings.FirstDistance);
        settings.FirstFrames = Number(values, "firstFrames", settings.FirstFrames);
        settings.SecondDistance = Number(values, "secondDistance", settings.SecondDistance);
        settings.TapFrames = Number(values, "tapFrames", settings.TapFrames);
        settings.SecondWindow = Number(values, "secondWindow", settings.SecondWindow);
        settings.Cooldown = Number(values, "cooldown", settings.Cooldown);
        settings.AllowAirSecond = values.TryGetValue("allowAirSecond", out var air) ? air.Truth : settings.AllowAirSecond;
    }
    public void Update(ActorContext actor, FrameContext frame)
    {
        var step = state.Step(frame.Delta, frame.Input.JustPressed(NativeActorActions.Key(actor, 0)), frame.Input.JustPressed(NativeActorActions.Key(actor, 1)),
            NativeActorActions.Grounded(actor), NativeActorActions.CanAct(actor));
        if (step.Begin)
        {
            if (!directionTaken) { originalDirection = actor.Read("Direction"); directionTaken = true; }
            if (step.Stage == 1) runDistance = 0;
            actor.Write("Direction", -step.Direction);
            actor.HorizontalSpeed = 0;
            NativeActorActions.Motion(actor, "DASH");
            active = true;
        }
        if (step.Distance > 0)
        {
            double before = actor.X;
            bool clear = NativeActorActions.MoveHorizontal(actor, step.Direction * step.Distance);
            runDistance += Math.Abs(actor.X - before);
            actor.Write("nativeDashDistance", runDistance);
            if (!clear) state.Block();
        }
        if (step.End || active && !state.Dashing) Stop(actor);
    }
    public void Detach(ActorContext actor)
    {
        active = false;
        runDistance = 0;
        actor.HorizontalSpeed = 0;
        actor.Write("nativeDashDistance", 0);
        if (directionTaken) { actor.Write("Direction", originalDirection); directionTaken = false; }
        state.Reset();
    }
    private void Stop(ActorContext actor)
    {
        if (!active) return;
        active = false;
        actor.HorizontalSpeed = 0;
        NativeActorActions.Motion(actor, NativeActorActions.Grounded(actor) ? "STAND_0" : "JUMPDOWN");
    }
    private static double Number(IReadOnlyDictionary<string, GmValue> values, string name, double fallback) => values.TryGetValue(name, out var value) ? value.Number : fallback;
}
