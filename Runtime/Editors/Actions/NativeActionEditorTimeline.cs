using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private OptionButton clipChoice = null!, bone = null!;
    private LineEdit clipId = null!;
    private SpinBox frame = null!, duration = null!, x = null!, y = null!, angle = null!, sx = null!, sy = null!, picture = null!;
    private CheckButton loop = null!, rootTrack = null!;
    private HSlider timelineSlider = null!;
    private ItemList keyList = null!;
    private bool syncingTimeline, readingKey;
    private static HSlider Slider(Node parent, double max, Action<double> changed) { var slider = new HSlider { MinValue = 0, MaxValue = max, Step = .25, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; parent.AddChild(slider); slider.ValueChanged += value => changed(value); return slider; }
    private List<MotionKey> Keys()
    {
        string name = SelectedBone ?? "root";
        if (rootTrack.ButtonPressed || name == "root") return Clip.Root;
        var track = Clip.Tracks.SingleOrDefault(t => t.Bone == name);
        if (track is null) { track = new() { Bone = name }; Clip.Tracks.Add(track); } return track.Keys;
    }
    private void ReadKey()
    {
        if (refreshing || x is null) return;
        string? selected = SelectedBone;
        if (!rootTrack.ButtonPressed && selected is null) { x.Value = 0; y.Value = 0; angle.Value = 0; sx.Value = 1; sy.Value = 1; picture.Value = 0; if (keyList is not null) keyList.Clear(); return; }
        readingKey = true;
        try
        {
            var track = Clip.Tracks.SingleOrDefault(t => t.Bone == selected);
            var key = rootTrack.ButtonPressed || selected == "root" ? new MotionTrack { Keys = Clip.Root }.Sample(frame.Value) : track is { Keys.Count: > 0 } ? track.Sample(frame.Value) : MotionRigTemplates.Pose(selected ?? "");
            x.Value = key.X; y.Value = key.Y; angle.Value = key.Angle; sx.Value = key.ScaleX; sy.Value = key.ScaleY; picture.Value = key.Picture;
            if (timelineSlider is not null) { timelineSlider.SetValueNoSignal(frame.Value); keyList.Clear(); var keys = rootTrack.ButtonPressed || selected == "root" ? Clip.Root : Clip.Tracks.SingleOrDefault(t => t.Bone == selected)?.Keys ?? new(); foreach (var k in keys) { keyList.AddItem($"{k.Frame:F2}  ({k.X:F1}, {k.Y:F1})  {k.Angle:F1}°  picture={k.Picture}"); keyList.SetItemMetadata(keyList.ItemCount - 1, k.Frame); } }
        }
        finally { readingKey = false; }
    }
    private void ChangeKey(Action<MotionGraph> action, string coalesce) { if (refreshing) return; Guard(() => { document.Edit(action, coalesce); Refresh(); ApplyDocumentPose(); if (playing.ButtonPressed) RestartPlayback(); status.Text = "已编辑。"; }); }
    private void BindKey(SpinBox number, string field) => number.ValueChanged += _ => { if (!readingKey && !syncingTimeline && !refreshing && (rootTrack.ButtonPressed || SelectedBone is not null)) WriteKey(field); };
    private void WriteKey(string field)
    {
        if (refreshing || (!rootTrack.ButtonPressed && SelectedBone is null)) return;
        string name = rootTrack.ButtonPressed ? "root" : SelectedBone!;
        double at = frame.Value;
        ChangeKey(_ =>
        {
            var keys = Keys();
            var key = BaseKey(name, keys, at);
            switch (field)
            {
                case "x": key.X = x.Value; break;
                case "y": key.Y = y.Value; break;
                case "angle": key.Angle = angle.Value; break;
                case "sx": key.ScaleX = sx.Value; break;
                case "sy": key.ScaleY = sy.Value; break;
                case "picture": key.Picture = (int)picture.Value; break;
            }
            keys.RemoveAll(item => item.Frame == at); keys.Add(key); keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        }, $"key:{Clip.Id}:{name}:{at}:{field}");
    }
    private MotionKey BaseKey(string name, List<MotionKey> keys, double at)
    {
        if (keys.FirstOrDefault(item => item.Frame == at) is { } existing) return new MotionKey { Frame = at, X = existing.X, Y = existing.Y, Angle = existing.Angle, ScaleX = existing.ScaleX, ScaleY = existing.ScaleY, Picture = existing.Picture };
        var pose = name is "root" or "__root__" ? Clip.RootPose(at) : Clip.Tracks.FirstOrDefault(item => item.Bone == name) is { Keys.Count: > 0 } track ? track.Sample(at) : MotionRigTemplates.Pose(name);
        return new MotionKey { Frame = at, X = pose.X, Y = pose.Y, Angle = pose.Angle, ScaleX = pose.ScaleX, ScaleY = pose.ScaleY, Picture = pose.Picture };
    }
    private void RefreshTimeline() { syncingTimeline = true; try { timelineSlider.MaxValue = Clip.Duration; duration.Value = Clip.Duration; loop.ButtonPressed = Clip.Loop; ReadKey(); } finally { syncingTimeline = false; } }
    private void BuildTrackPage(TabContainer tabs)
    {
        var page = Page(tabs, "轨道");
        var track = Row(page); track.AddChild(new Label { Text = "骨骼" }); bone = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; track.AddChild(bone); bone.ItemSelected += _ => { if (!syncingBone) SelectBone(bone.GetItemText((int)bone.Selected)); };
        rootTrack = new CheckButton { Text = "姿态根偏移" }; track.AddChild(rootTrack); rootTrack.Toggled += _ => ReadKey();
        var translate = Row(page); x = Number(translate, "X"); y = Number(translate, "Y"); angle = Number(translate, "角度", -36000, 36000);
        var scale = Row(page); sx = Number(scale, "缩放X", -100, 100, 1); sy = Number(scale, "缩放Y", -100, 100, 1); picture = Number(scale, "贴图帧", 0, 10000); picture.Step = 1;
        BindKey(x, "x"); BindKey(y, "y"); BindKey(angle, "angle"); BindKey(sx, "sx"); BindKey(sy, "sy"); BindKey(picture, "picture");
        var keys = Row(page); Button(keys, "删除关键帧", () => Change(_ => Keys().RemoveAll(k => k.Frame == frame.Value)));
        var ik = Row(page); Button(ik, "绑定所选末端", BindSelectedIk); Button(ik, "解除所选 IK", UnbindSelectedIk);
        page.AddChild(new Label { Text = "位移与缩放线性插值；旋转取最短角度；贴图帧阶梯切换。根偏移只改变姿态，不移动实体。", AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }
    private void BuildTimeline(Node root)
    {
        var dock = new VBoxContainer { Name = "TimelineDock", CustomMinimumSize = new(0, 150) }; root.AddChild(dock);
        var header = Row(dock);
        clipChoice = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; header.AddChild(clipChoice); clipChoice.ItemSelected += _ => RefreshTimeline();
        clipId = new LineEdit { PlaceholderText = "新片段标识", CustomMinimumSize = new(120, 0) }; header.AddChild(clipId);
        Button(header, "新增片段", () => Change(g => g.Clips.Add(new() { Id = clipId.Text })));
        duration = Number(header, "长度（60Hz）", 1, 100000, 60); duration.ValueChanged += value => { if (!syncingTimeline && !refreshing) ChangeKey(_ => Clip.Duration = value, "duration:" + Clip.Id); };
        loop = new CheckButton { Text = "循环", ButtonPressed = true }; header.AddChild(loop); loop.Toggled += on => { if (!syncingTimeline && !refreshing) ChangeKey(_ => Clip.Loop = on, "loop:" + Clip.Id); };
        playing = new CheckButton { Text = "▶" }; header.AddChild(playing); playing.Toggled += on => { playing.Text = on ? "⏸" : "▶"; if (on) RestartPlayback(); else UpdatePlaybackOverlay(); };
        Button(header, "单帧", Step);
        speedChoice = new OptionButton(); foreach (string speed in new[] { "0.5x", "1x", "2x" }) speedChoice.AddItem(speed); speedChoice.Select(1); header.AddChild(speedChoice);
        speedChoice.ItemSelected += index => playbackSpeed = index switch { 0 => .5, 2 => 2, _ => 1 };
        frame = Number(header, "动作帧", 0, 100000); frame.ValueChanged += _ => { ReadKey(); if (!refreshing) ApplyDocumentPose(); };
        playState = new Label { Name = "PlayState", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; header.AddChild(playState);
        timelineSlider = Slider(dock, 60, value => frame.Value = value);
        keyList = new ItemList { CustomMinimumSize = new(0, 90) }; dock.AddChild(keyList); keyList.ItemSelected += index => frame.Value = keyList.GetItemMetadata((int)index).AsDouble();
    }
}
