using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    internal async Task InputRouteAuditAsync()
    {
        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("INPUT_PROBE_SKIP reason=headless");
            return;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (busy || preview?.Player is null)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds > 90) throw new TimeoutException("Input probe preview");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        void Expect(bool ok, string message) { if (!ok) throw new InvalidOperationException("Input route: " + message); }
        async Task Tick() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        void Send(InputEvent input) => PushInput(input, true);
        void TapKey(Key keycode, bool pressed, bool ctrl = false) => Send(new InputEventKey { Keycode = keycode, Pressed = pressed, CtrlPressed = ctrl });
        void Type(string text) { foreach (char c in text) Send(new InputEventKey { Unicode = c, Pressed = true }); }
        Vector2 SurfacePoint(double x, double y) { var (surfaceX, surfaceY) = MotionViewport.ToSurface(x, y, surface.Size.X, surface.Size.Y, PreviewView); return new((float)surfaceX, (float)surfaceY); }
        Vector2 BonePoint(string name)
        {
            var active = PreviewActor!;
            var node = active.Services.Motions.Skeleton(active).Single(item => item.Name == name);
            var bounds = active.World.Bounds(active.World.Instances.First(item => item.Id == node.Instance));
            for (double y = bounds.Top + 2; y <= bounds.Bottom - 2; y += 2)
                for (double x = bounds.Left + 2; x <= bounds.Right - 2; x += 2)
                {
                    var point = SurfacePoint(x, y);
                    if (PickBoneAt(point) == name) return point;
                }
            throw new InvalidOperationException("Input route: no hittable surface point for " + name);
        }
        Vector2 WindowPoint(Vector2 surfacePoint) => surface.GlobalPosition + surfacePoint;
        void Mouse(Vector2 surfacePoint, bool pressed) => Send(new InputEventMouseButton { Position = WindowPoint(surfacePoint), GlobalPosition = WindowPoint(surfacePoint), ButtonIndex = MouseButton.Left, Pressed = pressed, ButtonMask = pressed ? MouseButtonMask.Left : 0 });
        PopupCentered();
        await Tick();

        clipId.Text = "";
        clipId.GrabFocus();
        await Tick();
        const string typed = "probe";
        Type(typed);
        await Tick();
        bool delivered = clipId.Text == typed;
        GD.Print("INPUT_ROUTE_PROBE text=", delivered ? "PASS" : "FAIL", " display=", DisplayServer.GetName());
        Expect(delivered, "typing reaches the focused clip id field, got '" + clipId.Text + "'");
        clipId.ReleaseFocus();
        clipId.Text = "";

        Project.ClipIndex = 0;
        document = new(new MotionGraph { Id = "audit.input", Clips = [new() { Id = "idle", Duration = 20, Tracks = [new() { Bone = "HEAD", Keys = [new() { Frame = 0, X = 4, Y = -16 }] }] }], States = [new() { Id = "idle", Clip = "idle" }] });
        Refresh(); frame.Value = 0;
        rootTrack.ButtonPressed = true; ApplyDocumentPose();
        double poseBefore = PreviewActor!.Part("HEAD")!.X;
        long builds = preview!.Graphics.Builds;
        x.GetLineEdit().GrabFocus();
        await Tick();
        TapKey(Key.A, true, true); TapKey(Key.A, false, true);
        Type("40");
        TapKey(Key.Enter, true); TapKey(Key.Enter, false);
        await Tick();
        Expect(Math.Abs(Clip.RootPose(0).X - 40) < .001, "typing 40 writes the root X key, got " + Clip.RootPose(0).X);
        Expect(Math.Abs(PreviewActor!.Part("HEAD")!.X - poseBefore) > 1, "the preview pose follows the typed value: " + poseBefore + " -> " + PreviewActor!.Part("HEAD")!.X);
        Expect(preview.Graphics.Builds > builds, "the typed value rebuilds the preview canvas");
        rootTrack.ButtonPressed = false;
        GD.Print("INPUT_ROUTE_POSE ok=1 x=", Clip.RootPose(0).X.ToString("F1"));

        ClearBoneSelection();
        await Tick();
        var head = BonePoint("HEAD");
        Mouse(head, true);
        await Tick();
        Expect(highlightedBone == "HEAD", "the routed mouse press selects the bone, got " + (highlightedBone ?? "<none>"));
        Expect(boneList.GetSelectedItems().Length == 1 && boneList.GetItemText(boneList.GetSelectedItems()[0]) == "HEAD", "the bone list follows the routed selection");
        Mouse(head, false);
        await Tick();
        Expect(!dragging && highlightedBone == "HEAD", "releasing without movement keeps the selection");
        GD.Print("INPUT_ROUTE_SELECT ok=1 bone=", highlightedBone);

        var dragBefore = Clip.Sample(0).GetValueOrDefault("HEAD");
        int dragUndo = document.UndoDepth;
        Mouse(head, true);
        await Tick();
        Expect(dragging && dragMode == DragMode.Fk, "pressing a selected FK bone starts a drag");
        var target = head + new Vector2(24, -8);
        Send(new InputEventMouseMotion { Position = WindowPoint(target), GlobalPosition = WindowPoint(target), Relative = target - head, ButtonMask = MouseButtonMask.Left });
        await Tick();
        Mouse(target, false);
        await Tick();
        var dragAfter = Clip.Sample(0)["HEAD"];
        Expect(document.UndoDepth == dragUndo + 1, "the routed drag is one undo step, got " + document.UndoDepth + " status=" + status.Text);
        Expect(Math.Abs(dragAfter.X - dragBefore.X) > .001 || Math.Abs(dragAfter.Y - dragBefore.Y) > .001 || Math.Abs(dragAfter.Angle - dragBefore.Angle) > .001, "the routed drag writes the HEAD key, status=" + status.Text);
        GD.Print("INPUT_ROUTE_DRAG ok=1 undo=", document.UndoDepth);

        GD.Print("INPUT_ROUTE_OK text=PASS pose=PASS select=PASS drag=PASS route=push_input display=", DisplayServer.GetName());
    }
}
