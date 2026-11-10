using SlackOfDescent;

namespace SinkKnee;

public sealed class SinkKneeMod : IMod
{
    public string Id => "example.sink-pull-knee";
    public void Load(ModContext context)
    {
        context.Content.RegisterAction(SinkKneeAction.ActionId, () => new SinkKneeAction());
        context.Content.RegisterCharacter(new CharacterDefinition
        {
            Id = "example.sink-pull-knee-nana",
            Name = "Nana · 抓膝拔腿",
            BasePreset = "Nana",
            Appearance = "Character-Nana",
            Actions = new() { [CharacterActionSlot.Controller] = SinkKneeAction.ActionId },
            Dependencies = new() { Id }
        });
    }
    public void Unload() { }
}
