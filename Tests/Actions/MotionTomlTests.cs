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
        string Serialized(MotionGraph graph) => JsonSerializer.Serialize(graph, NativeContentStore.Json);
        bool Same(MotionGraph first, MotionGraph second) => JsonNode.DeepEquals(JsonNode.Parse(Serialized(first)), JsonNode.Parse(Serialized(second)));
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
                    Tracks = [new() { Bone = "HEAD", Keys = [new() { Frame = 0, X = 2, Y = -3, Angle = 30, ScaleX = 1.25, ScaleY = .75, Picture = 2 }, new() { Frame = 10 }] }],
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
        string rendered = MotionToml.Render(graph);
        var parsed = MotionToml.Parse(rendered);
        Check(Same(graph, parsed), "model to TOML to model keeps every field");
        Check(Same(parsed, MotionToml.Parse(MotionToml.Render(parsed))), "TOML re-render round-trips");
        Check(rendered.Contains("[[Clips.Tracks]]", StringComparison.Ordinal) && rendered.Contains("\"jump boost\"", StringComparison.Ordinal) && rendered.Contains("#", StringComparison.Ordinal), "rendered TOML uses array tables, quoted keys and comments");
        var minimal = new MotionGraph { Id = "minimal", Clips = [new()], States = [new()] };
        Check(Same(minimal, MotionToml.Parse(MotionToml.Render(minimal))), "minimal graph round-trips");
        Check(Same(minimal, MotionToml.Parse("# 注释\n" + MotionToml.Render(minimal))), "leading comments are ignored");
        Check(MotionToml.Parse(MotionToml.Render(graph)).Parameters["jump boost"] == 2.5, "quoted parameter keys survive");
        Check(MotionToml.Parse(MotionToml.Render(graph)).Clips[0].Events[0].Value == "a\"b#c\nd", "strings keep escapes and hash characters");
        Throws("id = \"unterminated\n", "字符串");
        Throws("[parameters\n", "表头");
        Throws("id = 1\nid = 2\n", "重复的键");
        Throws("id = { a = 1 }\n", "内联表");
        Throws("id = 1979-05-27\n", "不支持的值");
        Throws("id = 'x'\n", "单引号");
        Throws("id =\n", "缺少值");
        Throws("[[clips]\n", "数组表头");
        Throws("id = 1\nclips = []\nstates = []\n", "结构与动作图字段不匹配");
        Throws("id = \"audit\"\nclips = []\nstates = []\nunknown = 1\n", "结构与动作图字段不匹配");
        Throws("id = 007\n", "不支持的值");
        Throws("id = .5\n", "不支持的值");
        Throws("id = 1.\n", "不支持的值");
        Throws("id = 01.5\n", "不支持的值");
        Throws("id = 9223372036854775808\n", "超出范围");
        Throws("id = \"a\\u 041b\"\n", "转义");
        Throws("[a]\n[a]\n", "表重复定义");
        Throws("'id' = 1\n", "单引号键名");
        Throws("id = \"\"\"x\"\"\"\n", "多行字符串");
        return checks;
    }
}
