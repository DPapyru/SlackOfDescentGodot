using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeMapEditor
{
    private void PanAxisOk(string label, float pan, float room, float view)
    {
        bool legal = room <= view ? Math.Abs(pan - (room - view) / 2) <= .01f : pan >= -.01f && pan <= room - view + .01f;
        if (!legal) throw new InvalidOperationException("Map pan: " + label + " let the viewport leave the room, pan " + pan + " room " + room + " view " + view);
    }
    private void PanOk(string label) => PanOk(label, Int(Document.Room, "width"), Int(Document.Room, "height"));
    private void PanOk(string label, float roomW, float roomH)
    {
        PanAxisOk(label + " on X", MapCanvas.Pan.X, roomW, MapCanvas.Size.X / MapCanvas.Zoom);
        PanAxisOk(label + " on Y", MapCanvas.Pan.Y, roomH, MapCanvas.Size.Y / MapCanvas.Zoom);
    }
    public int RunPanBoundaryAudit()
    {
        int checks = 0;
        void Expect(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Map pan: " + message); }
        void Check(string label) { checks++; PanOk(label); }
        Expect(MapCanvas.Size.X >= 16 && MapCanvas.Size.Y >= 16, "the canvas is laid out before the pan audit runs, got " + MapCanvas.Size);
        float zoom = MapCanvas.Zoom, panX = MapCanvas.Pan.X, panY = MapCanvas.Pan.Y;
        MapCanvas.Zoom = Math.Max(.05f, MapCanvas.Size.X / 6000); MapCanvas.ClampPan();
        foreach (var origin in new[] { new Vector2(-5000, -5000), new Vector2(0, 0), new Vector2(4000, 320), new Vector2(7976, 616), new Vector2(96, 448) }) { MapCanvas.Center(origin); Check("centring on " + origin); }
        MapCanvas.Pan = new(-99999, -99999); MapCanvas.ClampPan(); Check("panning past the top-left corner");
        Expect(Math.Abs(MapCanvas.Pan.X) < .01f, "top-left horizontal clamp stops at zero");
        MapCanvas.Pan = new(99999, 99999); MapCanvas.ClampPan(); Check("panning past the bottom-right corner");
        Expect(Math.Abs(MapCanvas.Pan.X - (8000 - MapCanvas.Size.X / MapCanvas.Zoom)) < .01f, "panning past the bottom-right corner stops at the last full screen of the room, got " + MapCanvas.Pan);
        foreach (var corner in new[] { new Vector2(0, 0), new Vector2(1, 1) }) { MapCanvas.ZoomAt(corner * MapCanvas.Size, 1.25f); Check("zooming at " + corner); }
        MapCanvas.Zoom = Math.Max(.05f, MapCanvas.Size.X / 6000); MapCanvas.Center(new(4000, 320));
        Vector2 before = MapCanvas.Pan; float panZoom = MapCanvas.Zoom;
        MapCanvas._GuiInput(new InputEventMouseButton { Position = MapCanvas.Size / 2, GlobalPosition = MapCanvas.Size / 2, ButtonIndex = MouseButton.Middle, Pressed = true });
        MapCanvas._GuiInput(new InputEventMouseMotion { Position = MapCanvas.Size / 2, GlobalPosition = MapCanvas.Size / 2, Relative = new(-1e6f, -1e6f), ButtonMask = MouseButtonMask.Middle });
        Check("dragging far past the corner");
        MapCanvas._GuiInput(new InputEventMouseButton { Position = MapCanvas.Size / 2, GlobalPosition = MapCanvas.Size / 2, ButtonIndex = MouseButton.Middle, Pressed = false });
        Expect(Math.Abs(MapCanvas.Pan.X - (8000 - MapCanvas.Size.X / MapCanvas.Zoom)) < .01f, "dragging past bottom-right clamps at the final screen");
        Expect(Math.Abs(MapCanvas.Zoom - panZoom) < .0001f, "dragging does not change the zoom, got " + MapCanvas.Zoom);
        MapCanvas._GuiInput(new InputEventMouseButton { Position = MapCanvas.Size / 2, GlobalPosition = MapCanvas.Size / 2, ButtonIndex = MouseButton.Middle, Pressed = true });
        MapCanvas._GuiInput(new InputEventMouseMotion { Position = MapCanvas.Size / 2, GlobalPosition = MapCanvas.Size / 2, Relative = new(200, 0), ButtonMask = MouseButtonMask.Middle });
        Expect(MapCanvas.Pan.X < 8000 - MapCanvas.Size.X / MapCanvas.Zoom, "dragging away from the corner moves the viewport again");
        MapCanvas._GuiInput(new InputEventMouseButton { Position = MapCanvas.Size / 2, GlobalPosition = MapCanvas.Size / 2, ButtonIndex = MouseButton.Middle, Pressed = false });
        if (FindChild("ActualSize", true, false) is not Button actual) throw new InvalidOperationException("Map pan: the actual-size button is found by its own name");
        checks++;
        Expect(actual.Text == "100%", "the actual-size button carries its label, got " + actual.Text);
        actual.EmitSignal(BaseButton.SignalName.Pressed); Check("the actual-size button");
        Expect(Math.Abs(MapCanvas.Zoom - 1) < .0001f, "the actual-size button sets 100% zoom, got " + MapCanvas.Zoom);
        JsonObject room = Document.Room.DeepClone().AsObject();
        try
        {
            room["width"] = 320; room["height"] = 200; Document.Room = room;
            MapCanvas.Zoom = 1; MapCanvas.Center(new(160, 100)); PanOk("centring inside a room smaller than the canvas", 320, 200);
            Expect(Math.Abs(MapCanvas.Pan.X - (320 - MapCanvas.Size.X) / 2) < .01f && Math.Abs(MapCanvas.Pan.Y - (200 - MapCanvas.Size.Y) / 2) < .01f, "a room smaller than the canvas is centred in it, got pan " + MapCanvas.Pan + " canvas " + MapCanvas.Size);
            MapCanvas.Zoom = Math.Max(.05f, MapCanvas.Size.X / 6000); MapCanvas.Center(new(160, 100)); PanOk("centring a small room while zoomed out", 320, 200);
            Expect(Math.Abs(MapCanvas.Pan.X - (320 - MapCanvas.Size.X / MapCanvas.Zoom) / 2) < .01f && Math.Abs(MapCanvas.Pan.Y - (200 - MapCanvas.Size.Y / MapCanvas.Zoom) / 2) < .01f, "a small room stays centred after zooming out, got " + MapCanvas.Pan);
            MapCanvas.Zoom = 1; MapCanvas.Pan = new(-99999, -99999); MapCanvas.ClampPan(); PanOk("clamping past the corner of a small room", 320, 200);
            Expect(Math.Abs(MapCanvas.Pan.X - (320 - MapCanvas.Size.X) / 2) < .01f, "a small room is pinned to its centre instead of the origin, got " + MapCanvas.Pan);
        }
        finally
        {
            Document.Room = room.DeepClone().AsObject(); Document.Room["width"] = 8000; Document.Room["height"] = 640;
            MapCanvas.Zoom = zoom; MapCanvas.Pan = new(panX, panY); MapCanvas.ClampPan(); MapCanvas.QueueRedraw();
        }
        return checks;
    }
}
