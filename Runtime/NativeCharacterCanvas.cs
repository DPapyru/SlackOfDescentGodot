using Godot;

namespace SlackOfDescent;

public enum NativeCharacterTool { Brush, Eraser, Select }

public partial class NativeCharacterPixelCanvas : Control
{
    private NativeCharacterDocument? document;
    private string? partId;
    private int frame;
    private int zoom = 10;
    private bool drawing, selecting;
    private Vector2I start, previous;
    private ImageTexture? texture;
    private bool dirty = true;
    public NativeCharacterTool Tool { get; set; }
    public Color PaintColor { get; set; } = Colors.White;
    public NativePixelRect? Selection { get; set; }
    public Vector2I Cursor { get; set; }
    public event Action? Edited;
    public int Zoom { get => zoom; set { zoom = Math.Clamp(value, 2, 24); UpdateSize(); QueueRedraw(); } }
    private NativeCharacterPart? Part => document?.Definition.Parts.FirstOrDefault(p => p.Id == partId);
    public void Bind(NativeCharacterDocument value, string? id, int index)
    {
        FinishStroke(); if (document != value || partId != id || frame != index) Selection = null;
        document = value; partId = id; frame = index; Invalidate(); UpdateSize();
    }
    private void UpdateSize() { var part = Part; CustomMinimumSize = part is null ? new(320, 320) : new(part.Width * Zoom, part.Height * Zoom); }
    public void Invalidate() { dirty = true; QueueRedraw(); }
    public override void _Ready() { MouseFilter = MouseFilterEnum.Stop; FocusMode = FocusModeEnum.All; TextureFilter = TextureFilterEnum.Nearest; }
    public override void _ExitTree() { FinishStroke(); texture?.Dispose(); }
    public override void _Draw()
    {
        if (Part is not { } part || document is null) { DrawString(ThemeDB.FallbackFont, new(10, 28), NativeLocalization.Text("请添加一个部件"), fontSize: 16); return; }
        for (int y = 0; y < part.Height; y++) for (int x = 0; x < part.Width; x++) DrawRect(new(x * Zoom, y * Zoom, Zoom, Zoom), ((x / 4 + y / 4) & 1) == 0 ? new Color(0.27f, 0.28f, 0.31f) : new Color(0.36f, 0.37f, 0.4f));
        if (dirty)
        {
            using var image = Image.CreateFromData(part.Width, part.Height, false, Image.Format.Rgba8, document.Pixels(part, frame));
            texture?.Dispose(); texture = ImageTexture.CreateFromImage(image); dirty = false;
        }
        DrawTextureRect(texture!, new(0, 0, part.Width * Zoom, part.Height * Zoom), false);
        if (Zoom >= 6)
        {
            var grid = new Color(0, 0, 0, 0.15f);
            for (int x = 0; x <= part.Width; x++) DrawLine(new(x * Zoom, 0), new(x * Zoom, part.Height * Zoom), grid);
            for (int y = 0; y <= part.Height; y++) DrawLine(new(0, y * Zoom), new(part.Width * Zoom, y * Zoom), grid);
        }
        var anchor = new Vector2(part.AnchorX * Zoom, part.AnchorY * Zoom);
        DrawLine(anchor - new Vector2(5, 0), anchor + new Vector2(5, 0), Colors.Cyan, 1); DrawLine(anchor - new Vector2(0, 5), anchor + new Vector2(0, 5), Colors.Cyan, 1);
        if (Selection is { } selected) { DrawRect(new(selected.X * Zoom, selected.Y * Zoom, selected.Width * Zoom, selected.Height * Zoom), new Color(1, 1, 1, 0.12f)); DrawRect(new(selected.X * Zoom + 0.5f, selected.Y * Zoom + 0.5f, selected.Width * Zoom - 1, selected.Height * Zoom - 1), Colors.Yellow, false, 1); }
    }
    public override void _GuiInput(InputEvent input)
    {
        if (Part is not { } part || document is null) return;
        if (input is InputEventMouseButton button)
        {
            if (button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && button.Pressed) { Zoom += button.ButtonIndex == MouseButton.WheelUp ? 1 : -1; AcceptEvent(); return; }
            if (button.ButtonIndex != MouseButton.Left) return;
            Cursor = Pixel(button.Position, part);
            if (button.Pressed)
            {
                GrabFocus(); previous = start = Cursor;
                if (Tool == NativeCharacterTool.Select) { selecting = true; SetSelection(Cursor); }
                else { drawing = true; document.BeginEdit(); PaintAt(Cursor); }
            }
            else FinishStroke();
            AcceptEvent();
        }
        else if (input is InputEventMouseMotion motion)
        {
            Cursor = Pixel(motion.Position, part);
            if (drawing) { PaintLine(previous, Cursor); previous = Cursor; }
            if (selecting) SetSelection(Cursor);
            QueueRedraw();
        }
    }
    private Vector2I Pixel(Vector2 position, NativeCharacterPart part) => new(Math.Clamp((int)Math.Floor(position.X / Zoom), 0, part.Width - 1), Math.Clamp((int)Math.Floor(position.Y / Zoom), 0, part.Height - 1));
    private void SetSelection(Vector2I end) { Selection = new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(start.X - end.X) + 1, Math.Abs(start.Y - end.Y) + 1); QueueRedraw(); }
    private void PaintLine(Vector2I from, Vector2I to)
    {
        int dx = Math.Abs(to.X - from.X), dy = -Math.Abs(to.Y - from.Y), sx = from.X < to.X ? 1 : -1, sy = from.Y < to.Y ? 1 : -1, error = dx + dy, x = from.X, y = from.Y;
        while (true) { PaintAt(new(x, y)); if (x == to.X && y == to.Y) break; int twice = error * 2; if (twice >= dy) { error += dy; x += sx; } if (twice <= dx) { error += dx; y += sy; } }
    }
    private void PaintAt(Vector2I position)
    {
        if (document is null || partId is null) return;
        if (Selection is { } rect && (position.X < rect.X || position.X >= rect.X + rect.Width || position.Y < rect.Y || position.Y >= rect.Y + rect.Height)) return;
        var color = Tool == NativeCharacterTool.Eraser ? new Color(0, 0, 0, 0) : PaintColor;
        document.Paint(partId, frame, position.X, position.Y, (byte)Math.Clamp((int)Math.Round(color.R * 255), 0, 255), (byte)Math.Clamp((int)Math.Round(color.G * 255), 0, 255), (byte)Math.Clamp((int)Math.Round(color.B * 255), 0, 255), (byte)Math.Clamp((int)Math.Round(color.A * 255), 0, 255));
        Invalidate();
    }
    public void FinishStroke()
    {
        if (drawing) { drawing = false; document?.EndEdit(); Edited?.Invoke(); }
        selecting = false;
    }
    public void AuditStroke(Vector2I[] positions)
    {
        if (positions.Length == 0) return;
        Vector2 Local(Vector2I point) => new((point.X + 0.5f) * Zoom, (point.Y + 0.5f) * Zoom);
        _GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = Local(positions[0]) });
        foreach (var position in positions.Skip(1)) _GuiInput(new InputEventMouseMotion { Position = Local(position), ButtonMask = MouseButtonMask.Left });
        _GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = Local(positions[^1]) });
    }
}

public partial class NativeCharacterComposite : Control
{
    public NativeCharacterDocument? Document { get; set; }
    private readonly Dictionary<string, ImageTexture> textures = new(StringComparer.Ordinal);
    private double elapsed;
    public override void _Ready() { TextureFilter = TextureFilterEnum.Nearest; MouseFilter = MouseFilterEnum.Ignore; }
    public void Advance(double delta) { elapsed += delta; QueueRedraw(); }
    public void Invalidate() { foreach (var texture in textures.Values) texture.Dispose(); textures.Clear(); QueueRedraw(); }
    public override void _ExitTree() => Invalidate();
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), new Color(0.13f, 0.15f, 0.19f));
        var origin = new Vector2(Size.X / 2, Size.Y * 0.7f); DrawLine(new(0, origin.Y), new(Size.X, origin.Y), new Color(0.3f, 0.35f, 0.4f));
        if (Document is null) return;
        const float scale = 3;
        foreach (var part in Document.Definition.Parts.Where(p => p.Visible).OrderBy(p => p.Layer))
        {
            int total = part.Frames.Sum(f => f.Duration), tick = (int)(elapsed * Document.Definition.AnimationFps) % Math.Max(1, total), selected = 0;
            while (selected < part.Frames.Count - 1 && tick >= part.Frames[selected].Duration) tick -= part.Frames[selected++].Duration;
            var frame = part.Frames[selected];
            if (!textures.TryGetValue(frame.Id, out var texture))
            { using var image = Image.CreateFromData(part.Width, part.Height, false, Image.Format.Rgba8, Document.Pixels(part, selected)); textures.Add(frame.Id, texture = ImageTexture.CreateFromImage(image)); }
            DrawTextureRect(texture, new(origin + new Vector2(part.OffsetX - part.AnchorX, part.OffsetY - part.AnchorY) * scale, new Vector2(part.Width, part.Height) * scale), false);
        }
    }
}
