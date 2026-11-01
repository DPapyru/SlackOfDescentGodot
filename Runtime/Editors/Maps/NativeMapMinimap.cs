using Godot;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeMapMinimap : Control
{
    public NativeMapCanvas? Canvas { get; set; }
    private bool dragging;
    private float scale;
    private Vector2 offset;
    public override void _Ready() { MouseFilter = MouseFilterEnum.Stop; CustomMinimumSize = new(100, 60); }
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), new Color("202020"));
        if (Canvas?.Document is not { } document) return;
        Vector2 room = new(Int(document.Room, "width"), Int(document.Room, "height"));
        scale = Math.Min((Size.X - 8) / room.X, (Size.Y - 8) / room.Y); offset = (Size - room * scale) / 2;
        DrawRect(new(offset, room * scale), new Color("484848"));
        if (Canvas.ShowTiles) foreach (var tile in document.Tiles.OfType<JsonObject>().Where(t => !Canvas.HiddenDepths.Contains(Int(t, "depth"))))
        { var bounds = Canvas.ItemBounds(tile, true); DrawRect(new(offset + bounds.Position * scale, bounds.Size * scale), new Color("747b85")); }
        if (Canvas.ShowEntities) foreach (var entity in document.Instances.OfType<JsonObject>()) DrawRect(new(offset + new Vector2((float)Number(entity, "x"), (float)Number(entity, "y")) * scale, new(2, 2)), new Color("c49f53"));
        var visible = new Rect2(Canvas.Pan, Canvas.Size / Canvas.Zoom).Intersection(new Rect2(Vector2.Zero, room));
        DrawRect(new(offset + visible.Position * scale, visible.Size * scale), new Color("73b2ff"), false, 1);
        DrawRect(new(Vector2.One, Size - Vector2.One * 2), new Color("111111"), false, 1);
    }
    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } button) { dragging = button.Pressed; if (dragging) Navigate(button.Position); AcceptEvent(); }
        else if (input is InputEventMouseMotion motion && dragging) { Navigate(motion.Position); AcceptEvent(); }
    }
    public void Navigate(Vector2 local)
    {
        if (Canvas?.Document is null || scale <= 0) return; Canvas.Center((local - offset) / scale); QueueRedraw();
    }
}
