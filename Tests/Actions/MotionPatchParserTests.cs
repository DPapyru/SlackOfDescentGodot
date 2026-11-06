using SinkKnee;

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
        Check(patch.Frames.Count == 4, "frame count");
        Check(patch.Frames[0].T == 0, "first frame time");
        Check(patch.Frames[0].Bones["KNEE"].Angle == 0, "first knee angle");
        Check(patch.Frames[2].T == 24, "third frame time");
        Check(patch.Frames[2].Bones["KNEE"] == new MotionPatchBone(0, -12, 20), "third knee pose");
        Check(patch.Frames[^1].Bones["HAND_ABOVE"].Angle == 35, "last hand angle");
        return checks;
    }
}
