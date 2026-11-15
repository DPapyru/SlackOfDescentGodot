using SlackOfDescent;

namespace RopeBonds;

public sealed partial class RopeBondsMod
{
    partial void RegisterMotions(ModContext context) => context.Content.RegisterCharacter(new CharacterDefinition
    {
        Id = "ropebonds.nana", Name = "Nana · RopeBonds", BasePreset = "Nana", Appearance = "Character-Nana", MotionGraph = "ropebonds.locomotion"
    });
}
