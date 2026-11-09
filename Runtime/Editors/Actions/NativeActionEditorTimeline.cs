using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private OptionButton clipChoice = null!, speedChoice = null!;
    private LineEdit clipId = null!;
    private SpinBox frame = null!, duration = null!;
    private CheckButton loop = null!, playing = null!;
    private HSlider timelineSlider = null!;
    private Label playState = null!, timeLabel = null!;
    private bool syncingTimeline;
    private static HSlider Slider(Node parent, double max, Action<double> changed) { var slider = new HSlider { MinValue = 0, MaxValue = max, Step = .25, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; parent.AddChild(slider); slider.ValueChanged += value => changed(value); return slider; }
    private void DeleteSelectedKey() => Guard(() =>
    {
        if (!rootTrackMode && highlightedBone is null) throw new InvalidOperationException("请先选中骨骼。");
        string name = rootTrackMode || highlightedBone is null ? "root" : highlightedBone;
        var keys = name == "root" ? Clip.Root : Clip.Tracks.SingleOrDefault(track => track.Bone == name)?.Keys;
        if (keys is null || !keys.Any(key => key.Frame == frame.Value)) { status.Text = "当前帧没有关键帧。"; return; }
        Change(_ => keys.RemoveAll(key => key.Frame == frame.Value));
        status.Text = "已删除当前帧关键帧。";
    });
    private void ChangeKey(Action<MotionGraph> action, string coalesce) { if (refreshing) return; Guard(() => { document.Edit(action, coalesce); Refresh(); ApplyDocumentPose(); if (playing.ButtonPressed) RestartPlayback(); status.Text = "已编辑。"; }); }
    private void RefreshTimeline()
    {
        syncingTimeline = true;
        try { timelineSlider.MaxValue = Clip.Duration; duration.Value = Clip.Duration; loop.ButtonPressed = Clip.Loop; UpdateTimeLabel(); }
        finally { syncingTimeline = false; }
    }
    private void UpdateTimeLabel() { if (timeLabel is not null) timeLabel.Text = (frame.Value / 30).ToString("F1") + "s / " + (Clip.Duration / 30).ToString("F1") + "s"; }
    private void BuildTimeline(Node root)
    {
        var dock = new VBoxContainer { Name = "TimelineDock", CustomMinimumSize = new(0, 150) }; root.AddChild(dock);
        var header = Row(dock);
        timeLabel = new Label { Name = "TimeLabel", Text = "0.0s / 0.0s" }; header.AddChild(timeLabel);
        clipId = new LineEdit { PlaceholderText = "新片段标识", CustomMinimumSize = new(120, 0) }; header.AddChild(clipId);
        Button(header, "新增片段", () => Change(g => g.Clips.Add(new() { Id = clipId.Text })));
        playing = new CheckButton { Text = "▶" }; header.AddChild(playing); playing.Toggled += on => { playing.Text = on ? "⏸" : "▶"; if (on) RestartPlayback(); else UpdatePlaybackOverlay(); };
        Button(header, "单帧", Step);
        speedChoice = new OptionButton(); foreach (string speed in new[] { "0.5x", "1x", "2x" }) speedChoice.AddItem(speed); speedChoice.Select(1); header.AddChild(speedChoice);
        speedChoice.ItemSelected += index => playbackSpeed = index switch { 0 => .5, 2 => 2, _ => 1 };
        frame = Number(header, "动作帧", 0, 100000); frame.ValueChanged += _ => { if (!refreshing) { timelineSlider.SetValueNoSignal(frame.Value); ApplyDocumentPose(); RefreshCodeViews(); UpdateTimeLabel(); } };
        playState = new Label { Name = "PlayState", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; header.AddChild(playState);
        timelineSlider = Slider(dock, 60, value => frame.Value = value);
    }
}
