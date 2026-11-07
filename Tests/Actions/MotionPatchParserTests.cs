using SinkKnee;
using SlackOfDescent;

public static class MotionPatchParserTests
{
    public static int Run(string path)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception("Motion patch: " + message); }

        var patch = MotionPatchParser.Load(path);
        Check(patch.Id == "mod.sink.pull-knee", "id");
        Check(patch.Base == "PlayerCommon/StandFem1_Sinking", "base preset");
        Check(patch.Duration == 40, "duration");
        Check(patch.Frames.Count == 3, "frame count");
        Check(patch.Frames[0].T == 0 && patch.Frames[1].T == 20 && patch.Frames[2].T == 40, "frame times");
        foreach (var frame in patch.Frames)
            foreach (string bone in frame.Bones.Keys)
                Check(MotionSkeleton.IsNodeName(bone), "unknown bone " + bone);
        var knee = new[] { new MotionPatchBone(6, 10, -7), new MotionPatchBone(6, 7, -2), new MotionPatchBone(6, 4, 3) };
        var hand = new[] { new MotionPatchBone(7, -13, 19), new MotionPatchBone(7, -10, 24), new MotionPatchBone(7, -7, 29) };
        for (int i = 0; i < 3; i++)
        {
            Check(patch.Frames[i].Bones["FOOT1_ABOVE"] == knee[i], "knee pose " + i);
            Check(patch.Frames[i].Bones["HAND1_ABOVE"] == hand[i], "hand pose " + i);
        }
        Check(patch.Frames[0].Bones["FOOT1_ABOVE"].Y > patch.Frames[1].Bones["FOOT1_ABOVE"].Y && patch.Frames[1].Bones["FOOT1_ABOVE"].Y > patch.Frames[2].Bones["FOOT1_ABOVE"].Y, "knee sinks across frames");
        return checks;
    }
}
