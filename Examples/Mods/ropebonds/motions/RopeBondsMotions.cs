using System.IO;
using SlackOfDescent;

namespace RopeBonds;

public sealed partial class RopeBondsMod
{
    partial void RegisterMotions(ModContext context)
    {
        var graph = MotionGraph.Load(Path.Combine(context.Directory, "motions", "locomotion.motion.json"));
        context.Content.RegisterMotion(graph);
        context.Content.RegisterCharacter(new CharacterDefinition
        {
            Id = "ropebonds.nana", Name = "Nana · RopeBonds", BasePreset = "Nana", Appearance = "Character-Nana", MotionGraph = graph.Id
        });
    }
}
