using Godot;

namespace SlackOfDescent;

public partial class NativeMapAtlas : Control
{
    public Texture2D? Atlas { get; set; }
    public Rect2I Region { get; set; } = new(0, 0, 32, 32);
    public event Action<Rect2I>? RegionSelected;
    private Vector2I start;
    private bool selecting;
    private Vector2I selectionCell;
    private float atlasScale = 1;
    public float AtlasScale
    {
        get => atlasScale;
        set { float next = Math.Clamp(value, .25f, 4); if (Mathf.IsEqualApprox(atlasScale, next)) return; atlasScale = next; Refresh(); }
    }
    public override void _Ready() { TextureFilter = TextureFilterEnum.Nearest; MouseFilter = MouseFilterEnum.Stop; }
    public void Refresh() { CustomMinimumSize = Atlas is null ? new(200, 120) : Atlas.GetSize() * AtlasScale; QueueRedraw(); }
    public void Fit() { if (Atlas is null) return; float available = GetParent() is Control parent && parent.Size.X > 8 ? parent.Size.X : Math.Max(200, Atlas.GetWidth()); AtlasScale = Math.Min(1, available / Math.Max(1, Atlas.GetWidth())); }
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), new Color("1a1a1a"));
        if (Atlas is null) return;
        DrawTextureRect(Atlas, new(Vector2.Zero, Atlas.GetSize() * AtlasScale), false);
        DrawRect(new((Vector2)Region.Position * AtlasScale, (Vector2)Region.Size * AtlasScale), new Color(1, .9f, .2f, .25f));
        DrawRect(new((Vector2)Region.Position * AtlasScale, (Vector2)Region.Size * AtlasScale), Colors.Yellow, false, 2);
    }
    public override void _GuiInput(InputEvent input)
    {
        if (Atlas is null) return;
        Vector2I Cell(Vector2 point) => new(Math.Clamp((int)(point.X / AtlasScale) / Math.Max(1, selecting ? selectionCell.X : Region.Size.X) * Math.Max(1, selecting ? selectionCell.X : Region.Size.X), 0, Math.Max(0, Atlas.GetWidth() - 1)), Math.Clamp((int)(point.Y / AtlasScale) / Math.Max(1, selecting ? selectionCell.Y : Region.Size.Y) * Math.Max(1, selecting ? selectionCell.Y : Region.Size.Y), 0, Math.Max(0, Atlas.GetHeight() - 1)));
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed) { selecting = true; selectionCell = Region.Size; start = Cell(button.Position); Region = new(start, Region.Size); RegionSelected?.Invoke(Region); }
            else selecting = false;
            QueueRedraw(); AcceptEvent();
        }
        else if (input is InputEventMouseMotion motion && selecting)
        {
            var point = Cell(motion.Position); var size = selectionCell;
            Region = new(new(Math.Min(start.X, point.X), Math.Min(start.Y, point.Y)), new(Math.Abs(start.X - point.X) + Math.Max(1, size.X), Math.Abs(start.Y - point.Y) + Math.Max(1, size.Y)));
            RegionSelected?.Invoke(Region); QueueRedraw(); AcceptEvent();
        }
    }
}
