using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private OptionButton clipChoice = null!;
    private LineEdit clipId = null!;
    private SpinBox frame = null!, duration = null!, x = null!, y = null!, angle = null!, sx = null!, sy = null!, picture = null!;
    private CheckButton loop = null!, rootTrack = null!;
    private HSlider timelineSlider = null!;
    private ItemList keyList = null!;
    private TextEdit eventsText = null!, parametersText = null!;
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
        var key = rootTrack.ButtonPressed || selected == "root" ? new MotionTrack { Keys = Clip.Root }.Sample(frame.Value) : Clip.Tracks.SingleOrDefault(t => t.Bone == selected)?.Sample(frame.Value) ?? new(0, 0, 0, 1, 1);
        x.Value = key.X; y.Value = key.Y; angle.Value = key.Angle; sx.Value = key.ScaleX; sy.Value = key.ScaleY; picture.Value = key.Picture;
        if (timelineSlider is not null) { timelineSlider.SetValueNoSignal(frame.Value); keyList.Clear(); var keys = rootTrack.ButtonPressed || selected == "root" ? Clip.Root : Clip.Tracks.SingleOrDefault(t => t.Bone == selected)?.Keys ?? new(); foreach (var k in keys) { keyList.AddItem($"{k.Frame:F2}  ({k.X:F1}, {k.Y:F1})  {k.Angle:F1}°  picture={k.Picture}"); keyList.SetItemMetadata(keyList.ItemCount - 1, k.Frame); } }
    }
    private void WriteKey() { if (!rootTrack.ButtonPressed && SelectedBone is null) { status.Text = "请先选择骨骼节点。"; return; } Change(_ => { var keys = Keys(); keys.RemoveAll(k => k.Frame == frame.Value); keys.Add(new() { Frame = frame.Value, X = x.Value, Y = y.Value, Angle = angle.Value, ScaleX = sx.Value, ScaleY = sy.Value, Picture = (int)picture.Value }); keys.Sort((a, b) => a.Frame.CompareTo(b.Frame)); }); }
    private void RefreshTimeline() { timelineSlider.MaxValue = Clip.Duration; duration.Value = Clip.Duration; loop.ButtonPressed = Clip.Loop; eventsText.Text = JsonSerializer.Serialize(Clip.Events, NativeContentStore.Json); ReadKey(); }
    private void BuildTimeline(VBoxContainer root)
    {
        var dock = new VBoxContainer { Name = "TimelineDock", CustomMinimumSize = new(0, 150) }; root.AddChild(dock);
        var header = Row(dock);
        clipChoice = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; header.AddChild(clipChoice); clipChoice.ItemSelected += _ => RefreshTimeline();
        clipId = new LineEdit { PlaceholderText = "新片段标识", CustomMinimumSize = new(120, 0) }; header.AddChild(clipId);
        Button(header, "新增片段", () => Change(g => g.Clips.Add(new() { Id = clipId.Text })));
        duration = Number(header, "长度（60Hz）", 1, 100000, 60); loop = new CheckButton { Text = "循环", ButtonPressed = true }; header.AddChild(loop);
        Button(header, "更新片段", () => Change(_ => { Clip.Duration = duration.Value; Clip.Loop = loop.ButtonPressed; }));
        frame = Number(header, "动作帧", 0, 100000); frame.ValueChanged += _ => { ReadKey(); if (!refreshing) ApplyDocumentPose(); };
        Button(header, "读取运行帧", () => { if (PreviewActor is { } actor && actor.Services.Motions.State(actor) is { } state) frame.Value = state.Frame; });
        Button(header, "预览当前帧", () => Guard(() => { if (PreviewActor is not { } actor) return; playing.ButtonPressed = false; actor.Services.Motions.Seek(actor, frame.Value); preview!.Graphics.Build(); canvas.QueueRedraw(); }));
        timelineSlider = Slider(dock, 60, value => frame.Value = value);
        keyList = new ItemList { CustomMinimumSize = new(0, 90) }; dock.AddChild(keyList); keyList.ItemSelected += index => frame.Value = keyList.GetItemMetadata((int)index).AsDouble();
    }
}
