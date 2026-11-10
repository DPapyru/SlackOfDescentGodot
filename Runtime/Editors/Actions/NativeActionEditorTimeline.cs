using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private const int TimelineFps = 30;
    private const int EngineStepsPerTimelineFrame = 2;
    private OptionButton clipChoice = null!;
    private LineEdit clipId = null!;
    private SpinBox frame = null!, duration = null!;
    private CheckButton loop = null!, playing = null!;
    private Label playState = null!, timeLabel = null!, clipName = null!;
    private PopupMenu timelineMenu = null!;
    private TimelineTracks timelineTracks = null!;
    private string? selectedKeyBone;
    private double selectedKeyFrame = -1;
    private bool syncingTimeline, syncingPlayhead;
    private void ChangeKey(Action<MotionGraph> action, string coalesce) { if (refreshing) return; Guard(() => { document.Edit(action, coalesce); Refresh(); ApplyDocumentPose(); if (playing.ButtonPressed) RestartPlayback(); status.Text = "已编辑。"; }); }
    private void AddClip(string text) => Guard(() =>
    {
        string id = text.Trim();
        if (id.Length == 0) { status.Text = "片段标识不能为空。"; return; }
        document.Edit(graph => graph.Clips.Add(new() { Id = id }));
        Project.ClipIndex = document.Graph.Clips.Count - 1;
        clipId.Clear(); Refresh(); ApplyDocumentPose(); status.Text = "已新增片段：" + id;
    });
    private void AddSelectedKey() => Guard(() =>
    {
        string name = selectedKeyBone ?? (rootTrackMode || highlightedBone is null ? "root" : highlightedBone);
        double at = selectedKeyBone is not null ? selectedKeyFrame : frame.Value;
        if (name != "root" && !MotionSkeleton.IsNodeName(name)) throw new InvalidOperationException("请先选中骨骼。");
        Change(graph =>
        {
            var clip = graph.Clips[(int)Math.Max(0, clipChoice.Selected)];
            List<MotionKey> keys;
            if (name == "root") keys = clip.Root;
            else
            {
                var track = clip.Tracks.FirstOrDefault(item => item.Bone == name);
                if (track is null) { track = new() { Bone = name }; clip.Tracks.Add(track); }
                keys = track.Keys;
            }
            if (keys.Any(key => key.Frame == at)) return;
            var pose = name == "root" ? clip.RootPose(at) : clip.Sample(at).GetValueOrDefault(name);
            keys.Add(new() { Frame = at, X = pose.X, Y = pose.Y, Angle = pose.Angle, ScaleX = pose.ScaleX, ScaleY = pose.ScaleY, Picture = pose.Picture });
            keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        });
        selectedKeyBone = name; selectedKeyFrame = at;
        status.Text = "已添加关键帧：" + name + " 帧 " + at.ToString("F0");
        timelineTracks.QueueRedraw();
    });
    private void DeleteSelectedKey() => Guard(() =>
    {
        string name = selectedKeyBone ?? (rootTrackMode || highlightedBone is null ? "root" : highlightedBone);
        double at = selectedKeyBone is not null ? selectedKeyFrame : frame.Value;
        if (name != "root" && !MotionSkeleton.IsNodeName(name)) throw new InvalidOperationException("请先选中骨骼。");
        var clip = Clip;
        var keys = name == "root" ? clip.Root : clip.Tracks.FirstOrDefault(track => track.Bone == name)?.Keys;
        if (keys is null || !keys.Any(key => key.Frame == at)) { status.Text = "当前帧没有关键帧。"; return; }
        Change(_ => keys.RemoveAll(key => key.Frame == at));
        selectedKeyBone = null; selectedKeyFrame = -1;
        status.Text = "已删除关键帧：" + name + " 帧 " + at.ToString("F0");
    });
    private void SelectKeyframe(string bone, double at)
    {
        frame.Value = Math.Clamp(at, 0, Clip.Duration);
        selectedKeyBone = bone; selectedKeyFrame = at;
        SelectBone(bone);
        status.Text = "已选中关键帧：" + bone + " 帧 " + at.ToString("F0");
        timelineTracks.QueueRedraw();
    }
    private void ClearSelectedKey()
    {
        selectedKeyBone = null; selectedKeyFrame = -1;
    }
    private void SetTimelineFrame(double value)
    {
        if (playing.ButtonPressed) playing.ButtonPressed = false;
        frame.Value = Math.Clamp(Math.Round(value), 0, Clip.Duration);
    }
    private void RefreshTimeline()
    {
        syncingTimeline = true;
        try
        {
            duration.Value = Clip.Duration;
            loop.ButtonPressed = Clip.Loop;
            frame.MaxValue = Math.Max(1, Clip.Duration);
            if (frame.Value > Clip.Duration) frame.Value = Clip.Duration;
            if (clipName is not null) clipName.Text = Clip.Id;
            UpdateTimeLabel();
        }
        finally { syncingTimeline = false; }
        timelineTracks?.QueueRedraw();
    }
    private void UpdateTimeLabel()
    {
        if (timeLabel is null) return;
        timeLabel.Text = frame.Value.ToString("F0") + " / " + Clip.Duration.ToString("F0");
    }
    private void BuildTimeline(Node root)
    {
        var dock = new VBoxContainer { Name = "TimelineDock", CustomMinimumSize = new(0, 280) }; root.AddChild(dock);
        var header = Row(dock); header.Name = "TimelineToolbar";
        clipName = new Label { Name = "ClipName", CustomMinimumSize = new(120, 0) }; header.AddChild(clipName);
        clipId = new LineEdit { Name = "NewClipId", PlaceholderText = "新片段标识", CustomMinimumSize = new(120, 0) }; header.AddChild(clipId);
        clipId.TextSubmitted += AddClip;
        playing = new CheckButton { Name = "PlayToggle", Text = "▶" }; header.AddChild(playing);
        playing.Toggled += on => { playing.Text = on ? "⏸" : "▶"; if (on) RestartPlayback(); else UpdatePlaybackOverlay(); };
        timeLabel = new Label { Name = "TimeLabel", Text = "0 / 0" }; header.AddChild(timeLabel);
        timelineMenu = new PopupMenu { Name = "TimelineMenu" }; dock.AddChild(timelineMenu);
        timelineMenu.AddItem("添加关键帧", 0); timelineMenu.AddItem("删除关键帧", 1);
        timelineMenu.IdPressed += id => { if (id == 0) AddSelectedKey(); else DeleteSelectedKey(); };
        var scroll = new ScrollContainer { Name = "TimelineScroll", SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        dock.AddChild(scroll);
        timelineTracks = new TimelineTracks(this); scroll.AddChild(timelineTracks);
    }
    private sealed partial class TimelineTracks : Control
    {
        internal const int HeaderWidth = 120, RulerHeight = 25, RowHeight = 35;
        private const int LeftPad = 10, RightPad = 10, DiamondRadius = 6, KeyHit = 8;
        private static readonly Color HeaderBg = new("#252526"), RulerBg = new("#2d2d2d"), TrackBg = new("#1e1e1e"), RowLine = new("#333333"), TickLine = new("#2a2a2a"), LabelColor = new("#999999"), HeaderColor = new("#aaaaaa"), SelectedColor = new("#007acc"), KeyColor = new("#e0e0e0"), PlayheadColor = new("#ff4444");
        private readonly NativeActionEditor editor;
        private bool scrubbing;
        internal TimelineTracks(NativeActionEditor editor)
        {
            this.editor = editor;
            Name = "TimelineTracks";
            CustomMinimumSize = new(0, RulerHeight + MotionSkeleton.Bones.Length * RowHeight);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            MouseFilter = MouseFilterEnum.Stop;
            ClipContents = true;
        }
        private MotionClip Clip => editor.Clip;
        private float TrackLeft => HeaderWidth + LeftPad;
        private float TrackWidth => Math.Max(1, Size.X - TrackLeft - RightPad);
        private double Duration => Math.Max(1, Clip.Duration);
        private float XForFrame(double frame) => TrackLeft + (float)(Math.Clamp(frame, 0, Duration) / Duration) * TrackWidth;
        private float RowCenter(int row) => RulerHeight + row * RowHeight + RowHeight / 2f;
        internal int RowCount => MotionSkeleton.Bones.Length;
        internal float PlayheadX => XForFrame(editor.frame.Value);
        internal (string Bone, double Frame)? SelectedKey => editor.selectedKeyBone is { } bone ? (bone, editor.selectedKeyFrame) : null;
        internal bool RowHighlighted(string bone) => editor.highlightedBone == bone;
        internal Vector2 KeyPoint(string bone, double keyFrame) => new(XForFrame(keyFrame), RowCenter(Math.Max(0, Array.IndexOf(MotionSkeleton.Bones, bone))));
        internal IReadOnlyList<double> KeyFrames(string bone) => Clip.Tracks.FirstOrDefault(track => track.Bone == bone)?.Keys.Select(key => key.Frame).ToArray() ?? Array.Empty<double>();
        public override void _Draw()
        {
            var size = Size;
            DrawRect(new Rect2(0, 0, size.X, size.Y), TrackBg);
            DrawRect(new Rect2(0, 0, HeaderWidth, size.Y), HeaderBg);
            DrawRect(new Rect2(0, 0, size.X, RulerHeight), RulerBg);
            var font = GetThemeDefaultFont();
            int step = TickStep();
            for (int value = 0; value <= Duration; value += step)
            {
                float x = XForFrame(value);
                DrawLine(new(x, RulerHeight), new(x, size.Y), TickLine);
                DrawLine(new(x, RulerHeight - 5), new(x, RulerHeight), TickLine);
                DrawString(font, new(x + 3, 16), value.ToString(), HorizontalAlignment.Left, -1, 10, LabelColor);
            }
            for (int row = 0; row < MotionSkeleton.Bones.Length; row++)
            {
                string bone = MotionSkeleton.Bones[row];
                float top = RulerHeight + row * RowHeight;
                DrawLine(new(0, top + RowHeight), new(size.X, top + RowHeight), RowLine);
                bool highlighted = RowHighlighted(bone);
                DrawString(font, new(10, top + RowHeight / 2f + 4), bone, HorizontalAlignment.Left, HeaderWidth - 14, 12, highlighted ? SelectedColor : HeaderColor);
                var track = Clip.Tracks.FirstOrDefault(item => item.Bone == bone);
                if (track is null) continue;
                foreach (var key in track.Keys)
                {
                    bool selected = editor.selectedKeyBone == bone && Math.Abs(editor.selectedKeyFrame - key.Frame) < .001;
                    Diamond(XForFrame(key.Frame), RowCenter(row), selected);
                }
            }
            float playhead = PlayheadX;
            DrawLine(new(playhead, 0), new(playhead, size.Y), PlayheadColor);
            DrawColoredPolygon([new(playhead - 5, 0), new(playhead + 5, 0), new(playhead, 8)], PlayheadColor);
        }
        private int TickStep()
        {
            double perFrame = TrackWidth / Duration;
            foreach (int step in new[] { 1, 2, 5, 10, 15, 30, 60, 120, 300 }) if (step * perFrame >= 60) return step;
            return 300;
        }
        private void Diamond(float x, float y, bool selected)
        {
            Vector2[] points = [new(x, y - DiamondRadius), new(x + DiamondRadius, y), new(x, y + DiamondRadius), new(x - DiamondRadius, y)];
            DrawColoredPolygon(points, selected ? SelectedColor : KeyColor);
            DrawPolyline([points[0], points[1], points[2], points[3], points[0]], selected ? Colors.White : Colors.Black);
        }
        public override void _GuiInput(InputEvent input)
        {
            if (input is InputEventMouseButton button)
            {
                if (button.ButtonIndex == MouseButton.Left)
                {
                    if (button.Pressed) Press(button.Position);
                    else scrubbing = false;
                    AcceptEvent();
                }
                else if (button.ButtonIndex == MouseButton.Right && button.Pressed)
                {
                    Context(button.Position);
                    AcceptEvent();
                }
                return;
            }
            if (input is InputEventMouseMotion motion && scrubbing) editor.SetTimelineFrame(FrameAt(motion.Position.X));
        }
        private void Press(Vector2 position)
        {
            if (position.X < HeaderWidth)
            {
                int row = (int)((position.Y - RulerHeight) / RowHeight);
                if (row >= 0 && row < MotionSkeleton.Bones.Length) editor.SelectBone(MotionSkeleton.Bones[row]);
                return;
            }
            if (position.Y < RulerHeight) { scrubbing = true; editor.SetTimelineFrame(FrameAt(position.X)); return; }
            if (HitKey(position) is { } key) { editor.SelectKeyframe(key.Bone, key.Frame); return; }
            int index = (int)((position.Y - RulerHeight) / RowHeight);
            if (index >= 0 && index < MotionSkeleton.Bones.Length) editor.SelectBone(MotionSkeleton.Bones[index]);
            scrubbing = true; editor.SetTimelineFrame(FrameAt(position.X));
        }
        private void Context(Vector2 position)
        {
            scrubbing = false;
            Press(position);
            scrubbing = false;
            if (HitKey(position) is { } key) editor.SelectKeyframe(key.Bone, key.Frame);
            editor.timelineMenu.Position = (Vector2I)(GetScreenPosition() + position);
            editor.timelineMenu.Popup();
        }
        private (string Bone, double Frame)? HitKey(Vector2 position)
        {
            for (int row = 0; row < MotionSkeleton.Bones.Length; row++)
            {
                string bone = MotionSkeleton.Bones[row];
                var track = Clip.Tracks.FirstOrDefault(item => item.Bone == bone);
                if (track is null) continue;
                float y = RowCenter(row);
                foreach (var key in track.Keys) if (Math.Abs(position.X - XForFrame(key.Frame)) <= KeyHit && Math.Abs(position.Y - y) <= KeyHit) return (bone, key.Frame);
            }
            return null;
        }
        private double FrameAt(float x) => Math.Clamp((x - TrackLeft) / TrackWidth * Duration, 0, Duration);
    }
}
