using SlackOfDescent;

namespace SinkKnee;

public sealed class SinkKneeAction : ICharacterAction
{
    public const string ActionId = "mod.sink.pull-knee";
    public string Id => ActionId;
    public CharacterActionSlot Slot => CharacterActionSlot.Controller;
    public void Update(ActorContext actor, FrameContext frame) { }
}
