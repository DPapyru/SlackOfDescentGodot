using System.Globalization;
using System.Text;

namespace SlackOfDescent;

public sealed class TomlTable
{
    public readonly Dictionary<string, object> Values = new(StringComparer.Ordinal);
}

public sealed class MotionFrameEdit(double index, Dictionary<string, Dictionary<string, double>> bones)
{
    public double Index { get; } = index;
    public Dictionary<string, Dictionary<string, double>> Bones { get; } = bones;
}

public static class MotionToml
{
    private static readonly string[] GraphKeys = ["formatVersion", "id", "skeleton", "entry", "clip", "frame", "phase", "event", "state", "parameters", "ikChain", "entitySlots"];
    internal static readonly string[] PoseFields = ["x", "y", "angle", "scaleX", "scaleY", "picture"];

    public static string Render(MotionGraph graph)
    {
        var text = new StringBuilder();
        text.Append("formatVersion = ").Append(Number(graph.FormatVersion)).Append('\n');
        text.Append("id = ").Append(Quote(graph.Id)).Append('\n');
        text.Append("skeleton = ").Append(Quote(graph.Skeleton)).Append('\n');
        text.Append("entry = ").Append(Quote(graph.Entry)).Append('\n');
        foreach (var clip in graph.Clips)
        {
            text.Append("\n[[clip]]\nid = ").Append(Quote(clip.Id)).Append('\n');
            text.Append("duration = ").Append(Number(clip.Duration)).Append('\n');
            text.Append("loop = ").Append(clip.Loop ? "true" : "false").Append('\n');
        }
        foreach (var clip in graph.Clips) WriteFrames(text, clip);
        foreach (var clip in graph.Clips) WritePhases(text, clip);
        foreach (var clip in graph.Clips) WriteEvents(text, clip);
        foreach (var state in graph.States) WriteState(text, state);
        if (graph.Parameters.Count > 0)
        {
            text.Append("\n[parameters]\n");
            foreach (var pair in graph.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal)) text.Append(KeyPart(pair.Key)).Append(" = ").Append(Number(pair.Value)).Append('\n');
        }
        foreach (var chain in graph.IkChains ?? [])
        {
            text.Append("\n[[ikChain]]\nroot = ").Append(Quote(chain.Root)).Append('\n');
            text.Append("mid = ").Append(Quote(chain.Mid)).Append('\n');
            text.Append("end = ").Append(Quote(chain.End)).Append('\n');
            text.Append("enabled = ").Append(chain.Enabled ? "true" : "false").Append('\n');
        }
        if (graph.EntitySlots.Count > 0)
        {
            text.Append("\n[entitySlots]\n");
            foreach (var pair in graph.EntitySlots.OrderBy(item => item.Key, StringComparer.Ordinal)) text.Append(KeyPart(pair.Key)).Append(" = ").Append(Quote(pair.Value)).Append('\n');
        }
        return text.ToString();
    }

    public static MotionGraph Parse(string text)
    {
        var root = ParseDocument(text, "动作图");
        Only(root, GraphKeys);
        var graph = new MotionGraph
        {
            FormatVersion = Integer(Number(root, "formatVersion", 1), "formatVersion"),
            Id = Text(root, "id", "motion"),
            Skeleton = Text(root, "skeleton", "Nana"),
            Entry = Text(root, "entry", "idle")
        };
        foreach (var item in Tables(root, "clip"))
        {
            Only(item, "id", "duration", "loop");
            graph.Clips.Add(new MotionClip { Id = Text(item, "id"), Duration = Number(item, "duration", 60), Loop = Bool(item, "loop", true) });
        }
        ParseFrames(root, graph);
        foreach (var item in Tables(root, "phase"))
        {
            Only(item, "clip", "name", "start", "end");
            ClipOf(graph, Text(item, "clip")).Phases.Add(new MotionPhase { Name = Text(item, "name", "active"), Start = Number(item, "start", 0), End = Number(item, "end", 0) });
        }
        foreach (var item in Tables(root, "event"))
        {
            Only(item, "clip", "id", "kind", "start", "end", "contact", "oncePerContact", "bone", "target", "x", "y", "width", "height", "amount", "value");
            ClipOf(graph, Text(item, "clip")).Events.Add(new MotionEvent
            {
                Id = Text(item, "id"), Kind = Enumerated<MotionEventKind>(item, "kind"), Start = Number(item, "start", 0), End = MaybeNumber(item, "end"),
                Contact = Bool(item, "contact", false), OncePerContact = Bool(item, "oncePerContact", true), Bone = Text(item, "bone", ""), Target = Enumerated<MotionTarget>(item, "target"),
                X = Number(item, "x", 0), Y = Number(item, "y", 0), Width = Number(item, "width", 32), Height = Number(item, "height", 32), Amount = Number(item, "amount", 1), Value = Text(item, "value", "")
            });
        }
        foreach (var item in Tables(root, "state"))
        {
            Only(item, "id", "clip", "movement", "transition");
            var state = new MotionState { Id = Text(item, "id"), Clip = Text(item, "clip") };
            if (TableValue(item, "movement") is { } movement)
            {
                Only(movement, "velocityX", "velocityY", "gravity", "maxFall", "leftKey", "rightKey", "inputSpeed", "jumpSpeed", "jumpKey", "displaceX", "displaceY");
                state.Movement = new MotionMovement
                {
                    VelocityX = MaybeNumber(movement, "velocityX"), VelocityY = MaybeNumber(movement, "velocityY"), Gravity = Number(movement, "gravity", 0), MaxFall = Number(movement, "maxFall", 24),
                    LeftKey = Integer(Number(movement, "leftKey", 37), "leftKey"), RightKey = Integer(Number(movement, "rightKey", 39), "rightKey"), InputSpeed = MaybeNumber(movement, "inputSpeed"),
                    JumpSpeed = MaybeNumber(movement, "jumpSpeed"), JumpKey = Integer(Number(movement, "jumpKey", 32), "jumpKey"), DisplaceX = Number(movement, "displaceX", 0), DisplaceY = Number(movement, "displaceY", 0)
                };
            }
            if (item.Values.TryGetValue("transition", out var transitions))
            {
                if (transitions is not List<object> list) throw new InvalidDataException("字段需要数组表：transition。");
                foreach (var element in list)
                {
                    if (element is not TomlTable table) throw new InvalidDataException("字段需要数组表：transition。");
                    Only(table, "target", "priority", "condition");
                    var transition = new MotionTransition { Target = Text(table, "target"), Priority = Integer(Number(table, "priority", 0), "priority") };
                    if (table.Values.TryGetValue("condition", out var conditions))
                    {
                        if (conditions is not List<object> conditionList) throw new InvalidDataException("字段需要数组表：condition。");
                        foreach (var conditionElement in conditionList)
                        {
                            if (conditionElement is not TomlTable condition) throw new InvalidDataException("字段需要数组表：condition。");
                            Only(condition, "kind", "comparison", "key", "value", "parameter");
                            transition.Conditions.Add(new MotionCondition
                            {
                                Kind = Enumerated<MotionConditionKind>(condition, "kind"), Comparison = Enumerated<MotionComparison>(condition, "comparison"),
                                Key = Integer(Number(condition, "key", 0), "key"), Value = Number(condition, "value", 1), Parameter = Text(condition, "parameter", "")
                            });
                        }
                    }
                    state.Transitions.Add(transition);
                }
            }
            graph.States.Add(state);
        }
        if (TableValue(root, "parameters") is { } parameters)
        {
            foreach (var pair in parameters.Values)
            {
                if (pair.Value is not double value) throw new InvalidDataException("参数需要数值：" + pair.Key + "。");
                graph.Parameters[pair.Key] = value;
            }
        }
        foreach (var item in Tables(root, "ikChain"))
        {
            Only(item, "root", "mid", "end", "enabled");
            (graph.IkChains ??= []).Add(new MotionIkChain { Root = Text(item, "root"), Mid = Text(item, "mid"), End = Text(item, "end"), Enabled = Bool(item, "enabled", true) });
        }
        if (TableValue(root, "entitySlots") is { } slots)
            foreach (var pair in slots.Values)
            {
                if (pair.Value is not string target) throw new InvalidDataException("实体槽需要字符串：" + pair.Key + "。");
                graph.EntitySlots[pair.Key] = target;
            }
        graph.Validate();
        return graph;
    }

    public static string RenderFrame(MotionClip clip, double frame)
    {
        var text = new StringBuilder();
        text.Append("[[frame]]\nindex = ").Append(Number(frame)).Append('\n');
        if (clip.Root.Count > 0) WritePose(text, "root", clip.RootPose(frame), false);
        foreach (var pair in clip.Sample(frame).OrderBy(item => item.Key, StringComparer.Ordinal)) WritePose(text, pair.Key, pair.Value, false);
        return text.ToString();
    }

    public static MotionFrameEdit ParseFrame(string text, string clipId) => ParseFrameCore(text, clipId, null);

    public static MotionFrameEdit ParseFrame(string text, string clipId, double frame) => ParseFrameCore(text, clipId, frame);

    private static MotionFrameEdit ParseFrameCore(string text, string clipId, double? frame)
    {
        var root = ParseDocument(text, "当前帧");
        if (root.Values.Count != 1 || !root.Values.TryGetValue("frame", out var frames) || frames is not List<object> list)
            throw new InvalidDataException("节点属性修改只接受一个 [[frame]] 块。");
        if (list.Count != 1) throw new InvalidDataException("节点属性修改只接受一个 [[frame]] 块。");
        if (list[0] is not TomlTable table) throw new InvalidDataException("节点属性修改只接受一个 [[frame]] 块。");
        double index = Number(table, "index");
        if (frame is { } expected && Math.Abs(index - expected) > 1e-9) throw new InvalidDataException("index 必须等于当前帧 " + Number(expected) + "。");
        if (table.Values.TryGetValue("clip", out var clipValue) && (clipValue is not string id || id != clipId)) throw new InvalidDataException("clip 必须等于当前片段：" + clipId + "。");
        var bones = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var pair in table.Values)
        {
            if (pair.Key is "index" or "clip") continue;
            if (pair.Value is not TomlTable bone) throw new InvalidDataException("帧键必须是 骨骼.字段：" + pair.Key + "。");
            if (pair.Key != "root" && !MotionSkeleton.IsNodeName(pair.Key)) throw new InvalidDataException("未知骨骼：" + pair.Key + "。");
            var fields = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var field in bone.Values)
            {
                if (!PoseFields.Contains(field.Key)) throw new InvalidDataException("未知骨骼字段：" + pair.Key + "." + field.Key + "。");
                if (field.Value is not double value || !double.IsFinite(value)) throw new InvalidDataException("骨骼字段需要有限数值：" + pair.Key + "." + field.Key + "。");
                if (field.Key == "picture") Integer(value, pair.Key + ".picture");
                fields[field.Key] = value;
            }
            if (fields.Count == 0) throw new InvalidDataException("骨骼需要至少一个字段：" + pair.Key + "。");
            bones[pair.Key] = fields;
        }
        return new MotionFrameEdit(index, bones);
    }

    public static bool FrameChanged(MotionClip clip, MotionFrameEdit edit)
    {
        var sample = clip.Sample(edit.Index);
        var root = clip.RootPose(edit.Index);
        foreach (var pair in edit.Bones)
        {
            var pose = pair.Key == "root" ? root : sample.GetValueOrDefault(pair.Key);
            foreach (var field in pair.Value) if (Field(pose, field.Key) != field.Value) return true;
        }
        return false;
    }

    public static void ApplyFrame(MotionGraph graph, string clipId, MotionFrameEdit edit)
    {
        var clip = ClipOf(graph, clipId);
        if (edit.Index < 0 || edit.Index > clip.Duration) throw new InvalidDataException("帧号超出片段长度：" + Number(edit.Index) + " / " + Number(clip.Duration) + "。");
        if (!FrameChanged(clip, edit)) return;
        var sample = clip.Sample(edit.Index);
        var root = clip.RootPose(edit.Index);
        foreach (var pair in edit.Bones)
        {
            var pose = pair.Key == "root" ? root : sample.GetValueOrDefault(pair.Key);
            var key = new MotionKey { Frame = edit.Index, X = pose.X, Y = pose.Y, Angle = pose.Angle, ScaleX = pose.ScaleX, ScaleY = pose.ScaleY, Picture = pose.Picture };
            bool changed = false;
            foreach (var field in pair.Value)
            {
                if (Field(key.Pose, field.Key) == field.Value) continue;
                changed = true;
                SetField(key, field.Key, field.Value);
            }
            if (changed) MotionPoseEditor.SetKey(clip, pair.Key == "root" ? null : pair.Key, edit.Index, key.Pose);
        }
    }

    private static void WriteFrames(StringBuilder text, MotionClip clip)
    {
        var frames = new SortedDictionary<double, List<(string Bone, MotionKey Key)>>();
        foreach (var key in clip.Root) Add(key, "root");
        foreach (var track in clip.Tracks) foreach (var key in track.Keys) Add(key, track.Bone);
        foreach (var pair in frames)
        {
            text.Append("\n[[frame]]\nclip = ").Append(Quote(clip.Id)).Append('\n');
            text.Append("index = ").Append(Number(pair.Key)).Append('\n');
            foreach (var (bone, key) in pair.Value.OrderBy(item => item.Bone == "root" ? "" : item.Bone, StringComparer.Ordinal)) WritePose(text, bone, key.Pose, true);
        }
        void Add(MotionKey key, string bone)
        {
            if (!frames.TryGetValue(key.Frame, out var list)) frames[key.Frame] = list = new();
            list.Add((bone, key));
        }
    }

    private static void WritePose(StringBuilder text, string bone, MotionPose pose, bool marker)
    {
        string prefix = KeyPart(bone) + ".";
        int written = 0;
        void Field(string name, double value, double fallback)
        {
            if (value == fallback) return;
            text.Append(prefix).Append(name).Append(" = ").Append(Number(value)).Append('\n');
            written++;
        }
        Field("x", pose.X, 0); Field("y", pose.Y, 0); Field("angle", pose.Angle, 0); Field("scaleX", pose.ScaleX, 1); Field("scaleY", pose.ScaleY, 1); Field("picture", pose.Picture, 0);
        if (marker && written == 0) text.Append(prefix).Append("x = 0\n");
    }

    private static void WritePhases(StringBuilder text, MotionClip clip)
    {
        foreach (var phase in clip.Phases)
            text.Append("\n[[phase]]\nclip = ").Append(Quote(clip.Id)).Append('\n').Append("name = ").Append(Quote(phase.Name)).Append('\n')
                .Append("start = ").Append(Number(phase.Start)).Append('\n').Append("end = ").Append(Number(phase.End)).Append('\n');
    }

    private static void WriteEvents(StringBuilder text, MotionClip clip)
    {
        foreach (var item in clip.Events)
        {
            text.Append("\n[[event]]\nclip = ").Append(Quote(clip.Id)).Append('\n');
            text.Append("id = ").Append(Quote(item.Id)).Append('\n');
            text.Append("kind = ").Append(Quote(item.Kind.ToString())).Append('\n');
            text.Append("start = ").Append(Number(item.Start)).Append('\n');
            if (item.End is { } end) text.Append("end = ").Append(Number(end)).Append('\n');
            text.Append("contact = ").Append(item.Contact ? "true" : "false").Append('\n');
            text.Append("oncePerContact = ").Append(item.OncePerContact ? "true" : "false").Append('\n');
            text.Append("bone = ").Append(Quote(item.Bone)).Append('\n');
            text.Append("target = ").Append(Quote(item.Target.ToString())).Append('\n');
            text.Append("x = ").Append(Number(item.X)).Append('\n');
            text.Append("y = ").Append(Number(item.Y)).Append('\n');
            text.Append("width = ").Append(Number(item.Width)).Append('\n');
            text.Append("height = ").Append(Number(item.Height)).Append('\n');
            text.Append("amount = ").Append(Number(item.Amount)).Append('\n');
            text.Append("value = ").Append(Quote(item.Value)).Append('\n');
        }
    }

    private static void WriteState(StringBuilder text, MotionState state)
    {
        text.Append("\n[[state]]\nid = ").Append(Quote(state.Id)).Append('\n');
        text.Append("clip = ").Append(Quote(state.Clip)).Append('\n');
        var movement = state.Movement;
        if (movement is not null)
        {
            text.Append("\n[state.movement]\n");
            if (movement.VelocityX is { } velocityX) text.Append("velocityX = ").Append(Number(velocityX)).Append('\n');
            if (movement.VelocityY is { } velocityY) text.Append("velocityY = ").Append(Number(velocityY)).Append('\n');
            text.Append("gravity = ").Append(Number(movement.Gravity)).Append('\n');
            text.Append("maxFall = ").Append(Number(movement.MaxFall)).Append('\n');
            text.Append("leftKey = ").Append(Number(movement.LeftKey)).Append('\n');
            text.Append("rightKey = ").Append(Number(movement.RightKey)).Append('\n');
            if (movement.InputSpeed is { } inputSpeed) text.Append("inputSpeed = ").Append(Number(inputSpeed)).Append('\n');
            if (movement.JumpSpeed is { } jumpSpeed) text.Append("jumpSpeed = ").Append(Number(jumpSpeed)).Append('\n');
            text.Append("jumpKey = ").Append(Number(movement.JumpKey)).Append('\n');
            text.Append("displaceX = ").Append(Number(movement.DisplaceX)).Append('\n');
            text.Append("displaceY = ").Append(Number(movement.DisplaceY)).Append('\n');
        }
        foreach (var transition in state.Transitions)
        {
            text.Append("\n[[state.transition]]\ntarget = ").Append(Quote(transition.Target)).Append('\n');
            text.Append("priority = ").Append(Number(transition.Priority)).Append('\n');
            foreach (var condition in transition.Conditions)
            {
                text.Append("\n[[state.transition.condition]]\nkind = ").Append(Quote(condition.Kind.ToString())).Append('\n');
                text.Append("comparison = ").Append(Quote(condition.Comparison.ToString())).Append('\n');
                text.Append("key = ").Append(Number(condition.Key)).Append('\n');
                text.Append("value = ").Append(Number(condition.Value)).Append('\n');
                text.Append("parameter = ").Append(Quote(condition.Parameter)).Append('\n');
            }
        }
    }

    private static void ParseFrames(TomlTable root, MotionGraph graph)
    {
        var seen = new HashSet<(string, double)>();
        foreach (var item in Tables(root, "frame"))
        {
            string clipId = Text(item, "clip");
            double index = Number(item, "index");
            var clip = ClipOf(graph, clipId);
            if (index < 0) throw new InvalidDataException("帧号不能为负数：" + Number(index) + "。");
            if (!seen.Add((clipId, index))) throw new InvalidDataException("帧块重复：" + clipId + " @" + Number(index) + "。");
            var keys = new Dictionary<string, MotionKey>(StringComparer.Ordinal);
            foreach (var pair in item.Values)
            {
                if (pair.Key is "clip" or "index") continue;
                if (pair.Value is not TomlTable bone) throw new InvalidDataException("帧键必须是 骨骼.字段：" + pair.Key + "。");
                if (pair.Key != "root" && !MotionSkeleton.IsNodeName(pair.Key)) throw new InvalidDataException("未知骨骼：" + pair.Key + "。");
                var key = new MotionKey { Frame = index };
                int written = 0;
                foreach (var field in bone.Values)
                {
                    if (!PoseFields.Contains(field.Key)) throw new InvalidDataException("未知骨骼字段：" + pair.Key + "." + field.Key + "。");
                    if (field.Value is not double value || !double.IsFinite(value)) throw new InvalidDataException("骨骼字段需要有限数值：" + pair.Key + "." + field.Key + "。");
                    SetField(key, field.Key, value);
                    written++;
                }
                if (written == 0) throw new InvalidDataException("骨骼需要至少一个字段：" + pair.Key + "。");
                keys[pair.Key] = key;
            }
            foreach (var pair in keys) MotionPoseEditor.SetKey(clip, pair.Key == "root" ? null : pair.Key, pair.Value.Frame, pair.Value.Pose);
        }
    }

    private static MotionClip ClipOf(MotionGraph graph, string id) => graph.Clips.FirstOrDefault(item => item.Id == id) ?? throw new InvalidDataException("片段不存在：" + id + "。");

    private static double Field(MotionPose pose, string name) => name switch
    {
        "x" => pose.X,
        "y" => pose.Y,
        "angle" => pose.Angle,
        "scaleX" => pose.ScaleX,
        "scaleY" => pose.ScaleY,
        _ => pose.Picture
    };

    private static void SetField(MotionKey key, string name, double value)
    {
        switch (name)
        {
            case "x": key.X = value; break;
            case "y": key.Y = value; break;
            case "angle": key.Angle = value; break;
            case "scaleX": key.ScaleX = value; break;
            case "scaleY": key.ScaleY = value; break;
            default: key.Picture = Integer(value, "picture"); break;
        }
    }

    private static TomlTable ParseDocument(string text, string label)
    {
        var root = new TomlTable();
        var current = root;
        var defined = new HashSet<TomlTable>(ReferenceEqualityComparer.Instance);
        text = (text ?? "").TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        int lineNumber = 0;
        foreach (string source in text.Split('\n'))
        {
            lineNumber++;
            string line = StripComment(source).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                if (!line.EndsWith("]]", StringComparison.Ordinal)) throw new InvalidDataException("数组表头缺少 ]]：第 " + lineNumber + " 行。");
                current = AppendTable(root, ParsePath(line[2..^2], lineNumber), lineNumber);
            }
            else if (line[0] == '[')
            {
                if (!line.EndsWith(']')) throw new InvalidDataException("表头缺少 ]：第 " + lineNumber + " 行。");
                current = OpenTable(root, ParsePath(line[1..^1], lineNumber), defined, lineNumber);
            }
            else
            {
                int split = FindAssignment(line);
                if (split < 0) throw new InvalidDataException("无法识别的 TOML 行：第 " + lineNumber + " 行。");
                string[] path = ParsePath(line[..split].Trim(), lineNumber);
                object value = ParseValue(line[(split + 1)..].Trim(), lineNumber);
                var table = current;
                for (int index = 0; index < path.Length - 1; index++) table = Descend(table, path[index], lineNumber);
                if (table.Values.ContainsKey(path[^1])) throw new InvalidDataException("重复的键：" + path[^1] + "（第 " + lineNumber + " 行）。");
                table.Values[path[^1]] = value;
            }
        }
        if (root.Values.Count == 0) throw new InvalidDataException(label + "内容为空。");
        return root;
    }

    private static TomlTable Descend(TomlTable table, string key, int line)
    {
        if (!table.Values.TryGetValue(key, out var value))
        {
            var created = new TomlTable();
            table.Values[key] = created;
            return created;
        }
        if (value is TomlTable existing) return existing;
        if (value is List<object> list && list.Count > 0 && list[^1] is TomlTable last) return last;
        throw new InvalidDataException("键与表冲突：第 " + line + " 行。");
    }

    private static TomlTable OpenTable(TomlTable root, string[] path, HashSet<TomlTable> defined, int line)
    {
        var parent = Walk(root, path, line);
        string key = path[^1];
        if (!parent.Values.TryGetValue(key, out var value))
        {
            var created = new TomlTable();
            parent.Values[key] = created;
            defined.Add(created);
            return created;
        }
        if (value is not TomlTable table) throw new InvalidDataException("表与已有值冲突：第 " + line + " 行。");
        if (!defined.Add(table)) throw new InvalidDataException("表重复定义：第 " + line + " 行。");
        return table;
    }

    private static TomlTable AppendTable(TomlTable root, string[] path, int line)
    {
        var parent = Walk(root, path, line);
        string key = path[^1];
        if (!parent.Values.TryGetValue(key, out var value))
        {
            var created = new List<object>();
            parent.Values[key] = created;
            value = created;
        }
        if (value is not List<object> list) throw new InvalidDataException("数组表与已有值冲突：第 " + line + " 行。");
        var table = new TomlTable();
        list.Add(table);
        return table;
    }

    private static TomlTable Walk(TomlTable root, string[] path, int line)
    {
        var parent = root;
        for (int index = 0; index < path.Length - 1; index++)
        {
            if (parent.Values.TryGetValue(path[index], out var value))
            {
                if (value is TomlTable table) { parent = table; continue; }
                if (value is List<object> list && list.Count > 0 && list[^1] is TomlTable last) { parent = last; continue; }
                throw new InvalidDataException("表路径与已有值冲突：第 " + line + " 行。");
            }
            var created = new TomlTable();
            parent.Values[path[index]] = created;
            parent = created;
        }
        return parent;
    }

    private static string[] ParsePath(string text, int line)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, escape = false;
        foreach (char character in text)
        {
            if (quoted)
            {
                current.Append(character);
                if (escape) escape = false;
                else if (character == '\\') escape = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') { quoted = true; current.Append(character); }
            else if (character == '.') { segments.Add(ParseKey(current.ToString().Trim(), line)); current.Clear(); }
            else current.Append(character);
        }
        if (quoted) throw new InvalidDataException("键名中的字符串未闭合：第 " + line + " 行。");
        segments.Add(ParseKey(current.ToString().Trim(), line));
        return segments.ToArray();
    }

    private static string ParseKey(string text, int line)
    {
        if (text.Length == 0) throw new InvalidDataException("空键名：第 " + line + " 行。");
        if (text[0] == '"')
        {
            var (value, end) = ReadString(text, line);
            if (end != text.Length) throw new InvalidDataException("键名后有多余内容：第 " + line + " 行。");
            return value;
        }
        if (text[0] == '\'') throw new InvalidDataException("不支持单引号键名：第 " + line + " 行。");
        foreach (char character in text)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) throw new InvalidDataException("键名需要加引号：第 " + line + " 行。");
        return text;
    }

    private static object ParseValue(string text, int line)
    {
        if (text.Length == 0) throw new InvalidDataException("缺少值：第 " + line + " 行。");
        if (text.StartsWith("\"\"\"", StringComparison.Ordinal)) throw new InvalidDataException("不支持多行字符串：第 " + line + " 行。");
        if (text[0] == '"')
        {
            var (value, end) = ReadString(text, line);
            if (end != text.Length) throw new InvalidDataException("字符串后有多余内容：第 " + line + " 行。");
            return value;
        }
        if (text[0] == '\'') throw new InvalidDataException("不支持单引号字符串：第 " + line + " 行。");
        if (text[0] == '{') throw new InvalidDataException("不支持内联表：第 " + line + " 行。");
        if (text[0] == '[') throw new InvalidDataException("不支持数组：第 " + line + " 行。");
        return ParseScalar(text, line);
    }

    private static object ParseScalar(string text, int line)
    {
        if (text is "true") return true;
        if (text is "false") return false;
        if (text.IndexOfAny(['\'', '"', '{', '}', '[', ']']) >= 0) throw new InvalidDataException("不支持的值语法：第 " + line + " 行。");
        if (!IsNumber(text)) throw new InvalidDataException("不支持的值语法：第 " + line + " 行。");
        if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw new InvalidDataException("数值无效：第 " + line + " 行。");
            return number;
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) return (double)integer;
        throw new InvalidDataException("整数超出范围：第 " + line + " 行。");
    }

    private static bool IsNumber(string text)
    {
        int index = text[0] is '+' or '-' ? 1 : 0, start = index;
        while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
        if (index == start || (index - start > 1 && text[start] == '0')) return false;
        if (index < text.Length && text[index] == '.')
        {
            start = ++index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
            if (index == start) return false;
        }
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            if (index < text.Length && text[index] is '+' or '-') index++;
            start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
            if (index == start) return false;
        }
        return index == text.Length;
    }

    private static string StripComment(string line)
    {
        bool quoted = false, escape = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (quoted)
            {
                if (escape) escape = false;
                else if (character == '\\') escape = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') quoted = true;
            else if (character == '#') return line[..index];
        }
        return line;
    }

    private static int FindAssignment(string line)
    {
        bool quoted = false, escape = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (quoted)
            {
                if (escape) escape = false;
                else if (character == '\\') escape = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') quoted = true;
            else if (character == '=') return index;
        }
        return -1;
    }

    private static (string Value, int End) ReadString(string text, int line)
    {
        var value = new StringBuilder();
        int index = 1;
        while (index < text.Length)
        {
            char character = text[index++];
            if (character == '"') return (value.ToString(), index);
            if (character != '\\')
            {
                if (char.IsControl(character)) throw new InvalidDataException("字符串不能包含控制字符：第 " + line + " 行。");
                value.Append(character);
                continue;
            }
            if (index >= text.Length) break;
            char escape = text[index++];
            switch (escape)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case '/': value.Append('/'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'u':
                    if (index + 4 > text.Length || !text.AsSpan(index, 4).ToArray().All(char.IsAsciiHexDigit) || !ushort.TryParse(text.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                        throw new InvalidDataException("无效的 \\u 转义：第 " + line + " 行。");
                    value.Append((char)code);
                    index += 4;
                    break;
                default: throw new InvalidDataException("不支持的转义：\\" + escape + "（第 " + line + " 行）。");
            }
        }
        throw new InvalidDataException("字符串未闭合：第 " + line + " 行。");
    }

    private static void Only(TomlTable table, params string[] keys)
    {
        foreach (string key in table.Values.Keys)
            if (!keys.Contains(key, StringComparer.Ordinal)) throw new InvalidDataException("未知字段：" + key + "。");
    }

    private static IEnumerable<TomlTable> Tables(TomlTable table, string key)
    {
        if (!table.Values.TryGetValue(key, out var value)) yield break;
        if (value is not List<object> list) throw new InvalidDataException("字段需要数组表：" + key + "。");
        foreach (var item in list)
        {
            if (item is not TomlTable element) throw new InvalidDataException("字段需要数组表：" + key + "。");
            yield return element;
        }
    }

    private static TomlTable? TableValue(TomlTable table, string key)
    {
        if (!table.Values.TryGetValue(key, out var value)) return null;
        if (value is TomlTable nested) return nested;
        throw new InvalidDataException("字段需要表：" + key + "。");
    }

    private static string Text(TomlTable table, string key, string? fallback = null)
    {
        if (table.Values.TryGetValue(key, out var value))
        {
            if (value is string text) return text;
            throw new InvalidDataException("字段需要字符串：" + key + "。");
        }
        if (fallback is not null) return fallback;
        throw new InvalidDataException("缺少字段：" + key + "。");
    }

    private static double Number(TomlTable table, string key, double? fallback = null)
    {
        if (table.Values.TryGetValue(key, out var value))
        {
            if (value is double number) return number;
            throw new InvalidDataException("字段需要数值：" + key + "。");
        }
        if (fallback is { } fallbackValue) return fallbackValue;
        throw new InvalidDataException("缺少字段：" + key + "。");
    }

    private static double? MaybeNumber(TomlTable table, string key) => table.Values.TryGetValue(key, out var value)
        ? value is double number ? number : throw new InvalidDataException("字段需要数值：" + key + "。")
        : null;

    private static bool Bool(TomlTable table, string key, bool fallback)
    {
        if (!table.Values.TryGetValue(key, out var value)) return fallback;
        if (value is bool flag) return flag;
        throw new InvalidDataException("字段需要 true 或 false：" + key + "。");
    }

    private static T Enumerated<T>(TomlTable table, string key) where T : struct, Enum
    {
        string text = Text(table, key);
        if (!Enum.TryParse<T>(text, true, out var value) || !Enum.IsDefined(value)) throw new InvalidDataException("未知枚举值：" + key + " = " + text + "。");
        return value;
    }

    private static int Integer(double value, string name)
    {
        if (!double.IsFinite(value) || Math.Abs(value - Math.Round(value)) > 1e-9 || value < int.MinValue || value > int.MaxValue)
            throw new InvalidDataException("字段需要整数：" + name + "。");
        return (int)Math.Round(value);
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string KeyPart(string key)
    {
        if (key.Length > 0 && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) return key;
        return Quote(key);
    }

    internal static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (char.IsControl(character)) text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(character);
                    break;
            }
        }
        return text.Append('"').ToString();
    }
}