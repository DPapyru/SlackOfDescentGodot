using Godot;

namespace SlackOfDescent;

public partial class NativeCharacterPixelCanvas
{
    public void AuditStroke(Vector2I[] positions)
    {
        if (positions.Length == 0) return;
        Vector2 Local(Vector2I point) => new((point.X + 0.5f) * Zoom, (point.Y + 0.5f) * Zoom);
        _GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = Local(positions[0]) });
        foreach (var position in positions.Skip(1)) _GuiInput(new InputEventMouseMotion { Position = Local(position), ButtonMask = MouseButtonMask.Left });
        _GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = Local(positions[^1]) });
    }
}
