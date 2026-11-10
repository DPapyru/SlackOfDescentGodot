using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public static class MotionTomlTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Motion TOML: " + message); }
        void Throws(string text, string fragment)
        {
            checks++;
            try { MotionToml.Parse(text); throw new Exception("Motion TOML: invalid text was accepted: " + text); }
            catch (InvalidDataException error) { if (!error.Message.Contains(fragment, StringComparison.Ordinal)) throw new Exception("Motion TOML: wrong rejection for " + text + ": " + error.Message); }
        }
        void ThrowsFrame(string text, string clipId, double frame, string fragment)
        {
            checks++;
            try { MotionToml.ParseFrame(text, clipId, frame); throw new Exception("Motion TOML: invalid frame text was accepted: " + text); }
            catch (InvalidDataException error) { if (!error.Message.Contains(fragment, StringComparison.Ordinal)) throw new Exception("Motion TOML: wrong frame rejection for " + text + ": " + error.Message); }
        }
        JsonNode Normal(MotionGraph graph)
        {
            var copy = JsonSerializer.Deserialize<MotionGraph>(JsonSerializer.Serialize(graph, NativeContentStore.Json), NativeContentStore.Json)!;
            foreach (var clip in copy.Clips) clip.Tracks.Sort((first, second) => string.CompareOrdinal(first.Bone, second.Bone));
            return JsonNode.Parse(JsonSerializer.Serialize(copy, NativeContentStore.Json))!;
        }
        bool Same(MotionGraph first, MotionGraph second) => JsonNode.DeepEquals(Normal(first), Normal(second));
        var graph = new MotionGraph
        {
            FormatVersion = 1,
            Id = "audit.toml",
            Skeleton = "Nana",
            Entry = "idle",
            Clips =
            [
                new() { Id = "idle", Duration = 20, Loop = true,
                    Root = [new() { Frame = 0, X = 1.5, Y = -2, Angle = 15 }],
                    Tracks =
                    [
                        new() { Bone = "HEAD", Keys = [new() { Frame = 0, X = 2, Y = -3, Angle = 30, ScaleX = 1.25, ScaleY = .75, Picture = 2 }, new() { Frame = 10 }] },
                        new() { Bone = "BODY_TOP", Keys = [new() { Frame = 5, X = .5 }] }
                    ],
                    Phases = [new() { Name = "active", Start = 0, End = 20 }],
                    Events = [new() { Id = "hit", Kind = MotionEventKind.Damage, Start = 1, End = 3, Contact = true, Target = MotionTarget.Enemy, Bone = "HEAD", X = 4, Y = 5, Width = 16, Height = 12, Amount = 2, Value = "a\"b#c\nd" }] },
                new() { Id = "walk", Duration = 30 }
            ],
            States =
            [
                new() { Id = "idle", Clip = "idle", Movement = new() { VelocityX = 1, Gravity = .6, MaxFall = 24, InputSpeed = 4, JumpSpeed = 10, DisplaceX = .5, DisplaceY = -.5 },
                    Transitions = [new() { Target = "walk", Priority = 2, Conditions = [new() { Kind = MotionConditionKind.Timer, Comparison = MotionComparison.GreaterOrEqual, Key = 0, Value = 3 }, new() { Kind = MotionConditionKind.Parameter, Comparison = MotionComparison.Greater, Parameter = "jump boost", Value = 1 }] }] },
                new() { Id = "walk", Clip = "walk" }
            ],
            Parameters = new(StringComparer.Ordinal) { { "jump boost", 2.5 }, { "power", -3 } },
            IkChains = [new() { Root = "BODY_TOP", Mid = "HAND1_ABOVE", End = "HAND1_BELOW" }],
            EntitySlots = new(StringComparer.Ordinal) { { "HEAD", "process:3" } }
        };
        string text = MotionToml.Render(graph);
        var parsed = MotionToml.Parse(text);
        Check(Same(graph, parsed), "model to TOML to model keeps every field");
        Check(Same(parsed, MotionToml.Parse(MotionToml.Render(parsed))), "TOML re-render round-trips");
        Check(text.Contains("[[frame]]") && text.Contains("[[clip]]") && text.Contains("[[state.transition.condition]]") && text.Contains("[[ikChain]]") && text.Contains("[parameters]") && text.Contains("[entitySlots]"), "whole graph renders frame blocks and normal tables");
        Check(parsed.Parameters["jump boost"] == 2.5, "quoted parameter keys survive");
        Check(parsed.Clips[0].Events[0].Value == "a\"b#c\nd", "strings keep escapes and hash characters");
        Check(Same(graph, MotionToml.Parse("# 注释\n" + text)), "leading comments are ignored");
        var sparse = new MotionGraph { Id = "sparse", Clips = [new() { Id = "idle", Duration = 10, Tracks = [new() { Bone = "HEAD", Keys = [new() { Frame = 0, X = 2 }] }] }], States = [new() { Id = "idle", Clip = "idle" }] };
        string sparseText = MotionToml.Render(sparse);
        Check(sparseText.Contains("HEAD.x = 2") && !sparseText.Contains("HEAD.y") && !sparseText.Contains("HEAD.angle") && !sparseText.Contains("HEAD.scaleX") && !sparseText.Contains("HEAD.picture"), "frame blocks are sparse and skip defaults");
        Check(Same(sparse, MotionToml.Parse(sparseText)), "sparse frame blocks round-trip");
        var marker = new MotionGraph { Id = "marker", Clips = [new() { Id = "idle", Duration = 10, Tracks = [new() { Bone = "HEAD", Keys = [new() { Frame = 0 }] }] }], States = [new() { Id = "idle", Clip = "idle" }] };
        Check(MotionToml.Render(marker).Contains("HEAD.x = 0") && Same(marker, MotionToml.Parse(MotionToml.Render(marker))), "an all-default key keeps a presence marker");
        var minimal = new MotionGraph { Id = "minimal", Clips = [new()], States = [new()] };
        Check(Same(minimal, MotionToml.Parse(MotionToml.Render(minimal))), "minimal graph round-trips");

        var posed = new MotionGraph { Id = "frame", Clips = [new() { Id = "idle", Duration = 20, Tracks = [new() { Bone = "HEAD", Keys = [new() { Frame = 0, X = 2 }, new() { Frame = 10, X = 4 }] }] }], States = [new() { Id = "idle", Clip = "idle" }] };
        var posedClip = posed.Clips[0];
        string frameText = MotionToml.RenderFrame(posedClip, 5);
        Check(frameText.StartsWith("[[frame]]") && frameText.Contains("index = 5") && frameText.Contains("HEAD.x = 3"), "current frame renders the sampled sparse pose");
        Check(!MotionToml.FrameChanged(posedClip, MotionToml.ParseFrame(frameText, "idle", 5)), "rendered current frame is unchanged");
        var changed = MotionToml.ParseFrame("[[frame]]\nindex = 5\nHEAD.x = 9\n", "idle", 5);
        Check(MotionToml.FrameChanged(posedClip, changed), "edited current frame reports a change");
        MotionToml.ApplyFrame(posed, "idle", changed);
        Check(posedClip.Tracks[0].Keys.Single(key => key.Frame == 5).X == 9 && posedClip.Sample(5)["HEAD"].X == 9, "current frame commit writes the key and re-renders the pose");
        ThrowsFrame("[[frame]]\nindex = 6\nHEAD.x = 1\n", "idle", 5, "index 必须");
        ThrowsFrame("[[frame]]\nindex = 5\nHEAD.nope = 1\n", "idle", 5, "未知骨骼字段");
        ThrowsFrame("[[frame]]\nindex = 5\nNO.x = 1\n", "idle", 5, "未知骨骼");
        ThrowsFrame("[[frame]]\nindex = 5\nclip = \"walk\"\nHEAD.x = 1\n", "idle", 5, "clip 必须");
        ThrowsFrame("[[frame]]\nindex = 5\n[[frame]]\nindex = 6\n", "idle", 5, "只接受一个");

        string template = "id = \"t\"\nskeleton = \"Nana\"\nentry = \"idle\"\n[[clip]]\nid = \"idle\"\nduration = 10\n[[state]]\nid = \"idle\"\nclip = \"idle\"\n";
        Throws(template + "unknown = 1\n", "未知字段");
        Throws(template + "\n[frame]\nclip = \"idle\"\nindex = 0\n", "数组表");
        Throws("id = \"t\"\n[[frame]]\nclip = \"idle\"\nindex = 0\nHEAD.x = 1\n", "片段不存在");
        Throws(template + "\n[[frame]]\nclip = \"idle\"\nindex = 0\nNO.x = 1\n", "未知骨骼");
        Throws(template + "\n[[frame]]\nclip = \"idle\"\nindex = 0\nHEAD.nope = 1\n", "未知骨骼字段");
        Throws(template + "\n[[frame]]\nclip = \"idle\"\nindex = -1\nHEAD.x = 1\n", "负数");
        Throws(template + "\n[[frame]]\nclip = \"idle\"\nindex = 0\nHEAD.x = 1\n\n[[frame]]\nclip = \"idle\"\nindex = 0\nHEAD.y = 1\n", "帧块重复");
        Throws(template + "\n[[frame]]\nclip = \"idle\"\nindex = 0\nHEAD.picture = 1.5\n", "整数");
        Throws(template + "\n[[frame]]\nclip = \"idle\"\nindex = 20\nHEAD.x = 1\n", "Invalid or unordered motion key");
        Throws("id = \"t\"\n[[clip]]\nid = \"idle\"\nduration = 10\n[[event]]\nclip = \"idle\"\nid = \"e\"\nkind = \"Nope\"\nstart = 0\n[[state]]\nid = \"idle\"\nclip = \"idle\"\n", "未知枚举值");
        Throws("id = \"t\"\nskeleton = \"Nope\"\n[[clip]]\nid = \"idle\"\n[[state]]\nid = \"idle\"\nclip = \"idle\"\n", "Unsupported skeleton");
        Throws("[[clip]\nid = \"idle\"\n", "数组表头");
        Throws("id = \"a\nid = \"b\"\n", "字符串");
        Throws("id = \"a\"\nid = \"b\"\n", "重复的键");
        Throws("id = { a = 1 }\n", "内联表");
        Throws("id = 'x'\n", "单引号");
        Throws("id =\n", "缺少值");
        string dirty = "HE\u200BAD\u200C\u200D\uFEFF\u00AD";
        Check(MotionToml.Clean(dirty) == "HEAD", "zero-width and soft-hyphen characters are stripped before parsing");
        Check(!MotionToml.Clean(dirty).Any(character => character is '\u200B' or '\u200C' or '\u200D' or '\uFEFF' or '\u00AD'), "cleaned text keeps none of the invisible codepoints");
        var invisibleFrame = MotionToml.ParseFrame(MotionToml.Clean("[[frame]]\nindex = 5\nHE\u200DAD.x = 9\n"), "idle", 5);
        Check(invisibleFrame.Bones.TryGetValue("HEAD", out var invisiblePose) && invisiblePose["x"] == 9, "a pasted zero-width joiner no longer breaks the bone name");
        return checks;
    }
}
