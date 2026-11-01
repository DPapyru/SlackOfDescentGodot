using Godot;

namespace SlackOfDescent;

public enum NativeCharacterTool { Brush, Eraser, Select, Eyedrop, Pan, Zoom }

public enum NativeCharacterCanvasMode { Part, Overall }

public partial class NativeCharacterPixelCanvas : Control
{
    private NativeCharacterDocument? document;
    private string? partId;
    private int frame;
    private int zoom = 10;
    private bool drawing, selecting, hovering, panning;
    private Vector2I start, previous;
    private List<NativeCharacterPart>? ordered;
    private readonly Dictionary<string, ImageTexture> references = new(StringComparer.Ordinal);
    private NativeCharacterBody? body;
    private bool showBody = true;
    public bool ShowBody { get => showBody; set { if (showBody == value) return; showBody = value; Selection = null; UpdateSize(); QueueRedraw(); } }
    private bool showGrid = true;
    private bool playing;
    private double elapsed;
    public bool ShowGrid { get => showGrid; set { showGrid = value; QueueRedraw(); } }
    public bool Playing { get => playing; set { playing = value; QueueRedraw(); } }
    public double Elapsed { get => elapsed; set { elapsed = value; QueueRedraw(); } }
    public int SelectedFrame { get => frame; set { int next = Math.Max(0, value); if (frame == next) return; FinishStroke(); frame = next; Selection = null; QueueRedraw(); } }
    public bool ShowReference { get; set; }
    public float ReferenceOpacity { get; set; } = 0.35f;
    public NativeCharacterTool Tool { get; set; }
    public NativeCharacterCanvasMode Mode { get => mode; set { if (mode == value) return; mode = value; Selection = null; UpdateSize(); QueueRedraw(); } }
    private NativeCharacterCanvasMode mode;
    public Color PaintColor { get; set; } = Colors.White;
    public NativePixelRect? Selection { get; set; }
    public Vector2I Cursor { get; set; }
    public event Action? Edited;
    public event Action? ZoomChanged;
    public event Action<Color>? ColorPicked;
    public int BrushSize { get; set; } = 1;
    public int Zoom { get => zoom; set { int clamped = Math.Clamp(value, 1, 24); if (zoom == clamped) return; zoom = clamped; UpdateSize(); QueueRedraw(); ZoomChanged?.Invoke(); } }
    private NativeCharacterPart? Part => document?.Definition.Parts.FirstOrDefault(p => p.Id == partId);
    public NativePixelRect ContentBounds => document is null ? new(0, 0, 0, 0) : ShowBody && body is not null ? body.Bounds(document.Definition) : NativeCharacterLayout.Bounds(document.Definition);
    private NativePixelRect Bounds => ContentBounds;
    private int FrameIndex(NativeCharacterPart part) => NativeCharacterAnimation.FrameIndex(part, frame, Playing, Elapsed, document?.Definition.AnimationFps ?? 8);
    public void ConfigureBody(GmWorld world) { body?.Dispose(); body = new(world); UpdateSize(); QueueRedraw(); }
    public void Advance(double delta) { Elapsed += delta; QueueRedraw(); }
    public void FitZoom(float width, float height)
    {
        var bounds = Mode == NativeCharacterCanvasMode.Overall ? Bounds : Part is { } part ? new NativePixelRect(0, 0, part.Width, part.Height) : new(0, 0, 0, 0);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        Zoom = (int)Math.Clamp(Math.Floor(Math.Min(width / bounds.Width, height / bounds.Height)), 1, 12);
    }
    public Vector2I Origin => Mode == NativeCharacterCanvasMode.Overall ? new(Bounds.X, Bounds.Y) : Vector2I.Zero;
    public Vector2I ToCharacter(Vector2I canvasPoint) => canvasPoint + Origin;
    public void Bind(NativeCharacterDocument value, string? id, int index)
    {
        FinishStroke(); if (document != value || partId != id || frame != index) Selection = null;
        document = value; partId = id; frame = index; InvalidateOrder(); Invalidate(); UpdateSize();
    }
    private void UpdateSize()
    {
        if (Mode == NativeCharacterCanvasMode.Overall)
        {
            var bounds = Bounds;
            CustomMinimumSize = bounds.Width > 0 && bounds.Height > 0 ? new(bounds.Width * Zoom, bounds.Height * Zoom) : new(320, 320);
            return;
        }
        var part = Part; CustomMinimumSize = part is null ? new(320, 320) : new(part.Width * Zoom, part.Height * Zoom);
    }
    public void Invalidate() { ClearReferences(); UpdateSize(); QueueRedraw(); }
    public void InvalidateOrder() { ordered = null; }
    private void ClearReferences() { foreach (var value in references.Values) value.Dispose(); references.Clear(); }
    public override void _Ready() { MouseFilter = MouseFilterEnum.Stop; FocusMode = FocusModeEnum.All; TextureFilter = TextureFilterEnum.Nearest; ClipContents = true; MouseExited += () => { hovering = false; QueueRedraw(); }; MouseEntered += () => { hovering = true; QueueRedraw(); }; }
    public override void _ExitTree() { FinishStroke(); body?.Dispose(); ClearReferences(); }
    public override void _Draw()
    {
        if (Mode == NativeCharacterCanvasMode.Overall) { DrawOverall(); return; }
        if (Part is not { } part || document is null) { DrawString(ThemeDB.FallbackFont, new(10, 28), NativeLocalization.Text("请添加一个部件"), fontSize: 16); return; }
        for (int y = 0; y < part.Height; y++) for (int x = 0; x < part.Width; x++) DrawRect(new(x * Zoom, y * Zoom, Zoom, Zoom), ((x / 4 + y / 4) & 1) == 0 ? new Color(0.27f, 0.28f, 0.31f) : new Color(0.36f, 0.37f, 0.4f));
        if (ShowReference) DrawReferences(part);
        if (ReferenceTexture(part) is { } texture) DrawTextureRect(texture, new(0, 0, part.Width * Zoom, part.Height * Zoom), false);
        if (ShowGrid && Zoom >= 6)
        {
            var grid = new Color(0, 0, 0, 0.15f);
            for (int x = 0; x <= part.Width; x++) DrawLine(new(x * Zoom, 0), new(x * Zoom, part.Height * Zoom), grid);
            for (int y = 0; y <= part.Height; y++) DrawLine(new(0, y * Zoom), new(part.Width * Zoom, y * Zoom), grid);
        }
        var anchor = new Vector2(part.AnchorX * Zoom, part.AnchorY * Zoom);
        DrawLine(anchor - new Vector2(5, 0), anchor + new Vector2(5, 0), Colors.Cyan, 1); DrawLine(anchor - new Vector2(0, 5), anchor + new Vector2(0, 5), Colors.Cyan, 1);
        if (Selection is { } selected) { DrawRect(new(selected.X * Zoom, selected.Y * Zoom, selected.Width * Zoom, selected.Height * Zoom), new Color(1, 1, 1, 0.12f)); DrawRect(new(selected.X * Zoom + 0.5f, selected.Y * Zoom + 0.5f, selected.Width * Zoom - 1, selected.Height * Zoom - 1), Colors.Yellow, false, 1); }
        if (hovering && Tool is NativeCharacterTool.Brush or NativeCharacterTool.Eraser) DrawBrush(part);
    }
    private void DrawBrush(NativeCharacterPart part)
    {
        int half = BrushSize / 2;
        int x = Math.Clamp(Cursor.X - half, 0, part.Width), y = Math.Clamp(Cursor.Y - half, 0, part.Height);
        DrawRect(new(x * Zoom + 0.5f, y * Zoom + 0.5f, BrushSize * Zoom - 1, BrushSize * Zoom - 1), Colors.White, false, 1);
    }
    private List<NativeCharacterPart> Ordered
    {
        get
        {
            if (ordered is null) ordered = document is null ? new() : NativeCharacterLayout.Ordered(document.Definition);
            return ordered;
        }
    }
    private string? Resolve(Vector2I point)
    {
        if (document is null) return null;
        if (Mode == NativeCharacterCanvasMode.Part) return partId;
        var character = ToCharacter(point);
        return NativeCharacterLayout.Hit(Ordered, p => document.Pixels(p, FrameIndex(p)), character.X, character.Y);
    }
    private Vector2I ClampToBounds(Vector2I point)
    {
        var bounds = Bounds;
        return Mode == NativeCharacterCanvasMode.Overall
            ? new(Math.Clamp(point.X, 0, Math.Max(0, bounds.Width - 1)), Math.Clamp(point.Y, 0, Math.Max(0, bounds.Height - 1)))
            : point;
    }
    private ScrollContainer? Scroll
    {
        get { for (Node? node = GetParent(); node is not null; node = node.GetParent()) if (node is ScrollContainer scroll) return scroll; return null; }
    }
    public void ZoomAt(Vector2 position, int value)
    {
        var scroll = Scroll;
        int before = Zoom;
        var pixel = position / before;
        var screen = position - new Vector2(scroll?.ScrollHorizontal ?? 0, scroll?.ScrollVertical ?? 0);
        Zoom = value;
        if (scroll is not null && before != Zoom)
        {
            int zoomAtRequest = Zoom;
            Callable.From(() =>
            {
                if (!IsInstanceValid(scroll) || Zoom != zoomAtRequest) return;
                scroll.ScrollHorizontal = (int)Math.Round(pixel.X * Zoom - screen.X);
                scroll.ScrollVertical = (int)Math.Round(pixel.Y * Zoom - screen.Y);
            }).CallDeferred();
        }
    }
    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton button)
        {
            if (panning && !button.Pressed && button.ButtonIndex is MouseButton.Left or MouseButton.Middle) { panning = false; AcceptEvent(); return; }
            if (button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && button.Pressed)
            {
                ZoomAt(button.Position, Zoom + (button.ButtonIndex == MouseButton.WheelUp ? 1 : -1)); AcceptEvent(); return;
            }
            if (button.ButtonIndex == MouseButton.Middle || button.ButtonIndex == MouseButton.Left && (Tool == NativeCharacterTool.Pan || Input.IsPhysicalKeyPressed(Key.Space)))
            {
                if (button.Pressed) { FinishStroke(); GrabFocus(); }
                panning = button.Pressed; AcceptEvent(); return;
            }
            if (Tool == NativeCharacterTool.Zoom && button.Pressed && button.ButtonIndex is MouseButton.Left or MouseButton.Right)
            {
                ZoomAt(button.Position, Zoom + (button.ButtonIndex == MouseButton.Right || button.AltPressed ? -1 : 1)); AcceptEvent(); return;
            }
        }
        if (input is InputEventMouseMotion pan && panning)
        {
            if (Scroll is { } scroll) { scroll.ScrollHorizontal -= (int)Math.Round(pan.Relative.X); scroll.ScrollVertical -= (int)Math.Round(pan.Relative.Y); }
            AcceptEvent(); return;
        }
        if (document is null || Mode == NativeCharacterCanvasMode.Part && Part is null) return;
        if (Mode == NativeCharacterCanvasMode.Overall && (Bounds.Width <= 0 || Bounds.Height <= 0)) return;
        var position = input switch { InputEventMouseButton fired => fired.Position, InputEventMouseMotion motion => motion.Position, _ => Vector2.Zero };
        var point = Mode == NativeCharacterCanvasMode.Overall
            ? ClampToBounds(new((int)Math.Floor(position.X / Zoom), (int)Math.Floor(position.Y / Zoom)))
            : Pixel(position, Part!);
        Cursor = point;
        if (input is InputEventMouseButton click)
        {
            if (click.ButtonIndex != MouseButton.Left) return;
            if (click.Pressed)
            {
                GrabFocus(); previous = start = point;
                if (click.AltPressed || Tool == NativeCharacterTool.Eyedrop) PickAt(point);
                else if (Tool == NativeCharacterTool.Select) { selecting = true; SetSelection(point); }
                else if (!Playing && (Tool is NativeCharacterTool.Brush or NativeCharacterTool.Eraser) && Resolve(point) is not null)
                { drawing = true; document.BeginEdit(); PaintAt(point); }
            }
            else FinishStroke();
            AcceptEvent();
        }
        else if (input is InputEventMouseMotion)
        {
            if (drawing) { PaintLine(previous, point); previous = point; }
            if (selecting) SetSelection(point);
            QueueRedraw();
        }
    }
    private void DrawOverall()
    {
        var bounds = Bounds;
        if (document is null || bounds.Width <= 0 || bounds.Height <= 0) { DrawString(ThemeDB.FallbackFont, new(10, 28), NativeLocalization.Text("请添加一个部件"), fontSize: 16); return; }
        for (int y = 0; y < bounds.Height; y++) for (int x = 0; x < bounds.Width; x++) DrawRect(new(x * Zoom, y * Zoom, Zoom, Zoom), ((x / 4 + y / 4) & 1) == 0 ? new Color(0.27f, 0.28f, 0.31f) : new Color(0.36f, 0.37f, 0.4f));
        var layers = new List<(int Layer, NativeCharacterPart? Part, NativeCharacterBody.Entry? Body)>();
        if (ShowBody && body is not null) layers.AddRange(body.Visible(document.Definition).Select(entry => (entry.Layer, (NativeCharacterPart?)null, (NativeCharacterBody.Entry?)entry)));
        layers.AddRange(NativeCharacterLayout.Ordered(document.Definition).Select(part => (part.Layer, (NativeCharacterPart?)part, (NativeCharacterBody.Entry?)null)));
        foreach (var layer in layers.OrderBy(item => item.Layer))
        {
            if (layer.Part is { } part)
            {
                if (ReferenceTexture(part) is not { } texture) continue;
                var corner = NativeCharacterLayout.Corner(part);
                DrawTextureRect(texture, new(new Vector2((corner.X - bounds.X) * Zoom, (corner.Y - bounds.Y) * Zoom), new Vector2(part.Width, part.Height) * Zoom), false);
            }
            else if (layer.Body is { } entry && body!.Texture(entry, frame, Playing, Elapsed, document.Definition.AnimationFps) is { } texture)
            {
                var rect = entry.Bounds;
                DrawTextureRect(texture, new(new Vector2((rect.X - bounds.X) * Zoom, (rect.Y - bounds.Y) * Zoom), new Vector2(rect.Width, rect.Height) * Zoom), false);
            }
        }
        if (ShowGrid && Zoom >= 6)
        {
            var grid = new Color(0, 0, 0, 0.15f);
            for (int x = 0; x <= bounds.Width; x++) DrawLine(new(x * Zoom, 0), new(x * Zoom, bounds.Height * Zoom), grid);
            for (int y = 0; y <= bounds.Height; y++) DrawLine(new(0, y * Zoom), new(bounds.Width * Zoom, y * Zoom), grid);
        }
        if (Selection is { } selected) { DrawRect(new(selected.X * Zoom, selected.Y * Zoom, selected.Width * Zoom, selected.Height * Zoom), new Color(1, 1, 1, 0.12f)); DrawRect(new(selected.X * Zoom + 0.5f, selected.Y * Zoom + 0.5f, selected.Width * Zoom - 1, selected.Height * Zoom - 1), Colors.Yellow, false, 1); }
    }
    private void DrawReferences(NativeCharacterPart current)
    {
        if (document is null) return;
        var origin = NativeCharacterLayout.Corner(current);
        var tint = new Color(1, 1, 1, ReferenceOpacity);
        if (ShowBody && body is not null)
            foreach (var entry in body.Visible(document.Definition))
                if (body.Texture(entry, frame, Playing, Elapsed, document.Definition.AnimationFps) is { } texture)
                {
                    var rect = entry.Bounds;
                    DrawTextureRect(texture, new(new Vector2((rect.X - origin.X) * Zoom, (rect.Y - origin.Y) * Zoom), new Vector2(rect.Width, rect.Height) * Zoom), false, tint);
                }
        foreach (var part in NativeCharacterLayout.Ordered(document.Definition))
        {
            if (part.Id == current.Id || ReferenceTexture(part) is not { } texture) continue;
            var corner = NativeCharacterLayout.Corner(part);
            DrawTextureRect(texture, new(new Vector2((corner.X - origin.X) * Zoom, (corner.Y - origin.Y) * Zoom), new Vector2(part.Width, part.Height) * Zoom), false, tint);
        }
    }
    private ImageTexture? ReferenceTexture(NativeCharacterPart part)
    {
        if (document is null || part.Frames.Count == 0) return null;
        int index = FrameIndex(part);
        string id = part.Frames[index].Id;
        if (references.TryGetValue(id, out var texture)) return texture;
        using var image = Image.CreateFromData(part.Width, part.Height, false, Image.Format.Rgba8, document.Pixels(part, index));
        texture = ImageTexture.CreateFromImage(image); references.Add(id, texture); return texture;
    }
    private Vector2I Pixel(Vector2 position, NativeCharacterPart part) => new(Math.Clamp((int)Math.Floor(position.X / Zoom), 0, part.Width - 1), Math.Clamp((int)Math.Floor(position.Y / Zoom), 0, part.Height - 1));
    private void SetSelection(Vector2I end) { Selection = new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(start.X - end.X) + 1, Math.Abs(start.Y - end.Y) + 1); QueueRedraw(); }
    private void PaintLine(Vector2I from, Vector2I to)
    {
        int dx = Math.Abs(to.X - from.X), dy = -Math.Abs(to.Y - from.Y), sx = from.X < to.X ? 1 : -1, sy = from.Y < to.Y ? 1 : -1, error = dx + dy, x = from.X, y = from.Y;
        while (true) { PaintAt(new(x, y)); if (x == to.X && y == to.Y) break; int twice = error * 2; if (twice >= dy) { error += dy; x += sx; } if (twice <= dx) { error += dx; y += sy; } }
    }
    private void PickAt(Vector2I position)
    {
        if (SampleAt(position) is not { } picked || picked.A <= 0) return;
        PaintColor = picked; ColorPicked?.Invoke(picked);
    }
    public void PickColorAtCursor() => PickAt(Cursor);
    public void SelectAll()
    {
        var bounds = Mode == NativeCharacterCanvasMode.Overall ? Bounds : Part is { } part ? new NativePixelRect(0, 0, part.Width, part.Height) : new(0, 0, 0, 0);
        Selection = bounds.Width > 0 && bounds.Height > 0 ? new NativePixelRect(0, 0, bounds.Width, bounds.Height) : null;
        QueueRedraw();
    }
    private (string Part, int LocalX, int LocalY)? Locate(Vector2I position)
    {
        if (document is null || Resolve(position) is not { } target) return null;
        var part = document.Definition.Parts.First(p => p.Id == target);
        var corner = NativeCharacterLayout.Corner(part);
        var point = ToCharacter(position);
        return (target, Mode == NativeCharacterCanvasMode.Overall ? point.X - corner.X : position.X, Mode == NativeCharacterCanvasMode.Overall ? point.Y - corner.Y : position.Y);
    }
    public Color? SampleAt(Vector2I position)
    {
        if (Locate(position) is not { } hit || document is null) return null;
        var part = document.Definition.Parts.First(p => p.Id == hit.Part);
        var rgba = document.Pixels(part, FrameIndex(part));
        int offset = (hit.LocalY * part.Width + hit.LocalX) * 4;
        if (hit.LocalX < 0 || hit.LocalY < 0 || hit.LocalX >= part.Width || hit.LocalY >= part.Height || offset + 3 >= rgba.Length) return null;
        return new Color(rgba[offset] / 255f, rgba[offset + 1] / 255f, rgba[offset + 2] / 255f, rgba[offset + 3] / 255f);
    }
    private void PaintAt(Vector2I position)
    {
        if (document is null || Locate(position) is not { } hit) return;
        var color = Tool == NativeCharacterTool.Eraser ? new Color(0, 0, 0, 0) : PaintColor;
        byte r = (byte)Math.Clamp((int)Math.Round(color.R * 255), 0, 255), g = (byte)Math.Clamp((int)Math.Round(color.G * 255), 0, 255), b = (byte)Math.Clamp((int)Math.Round(color.B * 255), 0, 255), a = (byte)Math.Clamp((int)Math.Round(color.A * 255), 0, 255);
        var part = document.Definition.Parts.First(p => p.Id == hit.Part);
        int index = FrameIndex(part), half = BrushSize / 2;
        for (int dy = -half; dy < BrushSize - half; dy++)
        {
            int localY = hit.LocalY + dy;
            if (localY < 0 || localY >= part.Height) continue;
            for (int dx = -half; dx < BrushSize - half; dx++)
            {
                int localX = hit.LocalX + dx;
                if (localX < 0 || localX >= part.Width) continue;
                if (Selection is { } rect && !Inside(rect, position.X + dx, position.Y + dy)) continue;
                document.Paint(hit.Part, index, localX, localY, r, g, b, a);
            }
        }
        Invalidate();
    }
    private static bool Inside(NativePixelRect rect, int x, int y) => x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;
    public void FinishStroke()
    {
        if (drawing) { drawing = false; document?.EndEdit(); Edited?.Invoke(); }
        selecting = false;
    }
}

public partial class NativeCharacterComposite : Control
{
    public NativeCharacterDocument? Document { get; set; }
    private readonly Dictionary<string, ImageTexture> textures = new(StringComparer.Ordinal);
    private double elapsed;
    private NativeCharacterBody? body;
    public bool ShowBody { get; set; } = true;
    public bool Playing { get; set; }
    public double Elapsed { get => elapsed; set { elapsed = value; QueueRedraw(); } }
    public int SelectedFrame { get; set; }
    public NativePixelRect ContentBounds => Document is null ? new(0, 0, 0, 0) : ShowBody && body is not null ? body.Bounds(Document.Definition) : NativeCharacterLayout.Bounds(Document.Definition);
    public void ConfigureBody(GmWorld world) { body?.Dispose(); body = new(world); QueueRedraw(); }
    public float PaintedScale { get; private set; }
    public Vector2 PaintedOrigin { get; private set; }
    public int PaintedParts { get; private set; }
    public override void _Ready() { TextureFilter = TextureFilterEnum.Nearest; MouseFilter = MouseFilterEnum.Ignore; ClipContents = true; }
    public void Advance(double delta) { Elapsed += delta; }
    public void Invalidate() { foreach (var texture in textures.Values) texture.Dispose(); textures.Clear(); QueueRedraw(); }
    public override void _ExitTree() { body?.Dispose(); Invalidate(); }
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), new Color("1e1e1e"));
        if (Document is null) return;
        var bounds = ContentBounds;
        float scale = 3;
        if (bounds.Width > 0 && bounds.Height > 0) scale = Math.Max(1, MathF.Floor(MathF.Min(Size.X / bounds.Width, Size.Y / bounds.Height)));
        var origin = new Vector2(MathF.Floor((Size.X - bounds.Width * scale) / 2), MathF.Floor((Size.Y - bounds.Height * scale) / 2));
        PaintedScale = scale; PaintedOrigin = origin; PaintedParts = 0;
        DrawLine(new(0, origin.Y + bounds.Height * scale), new(Size.X, origin.Y + bounds.Height * scale), new Color(0.3f, 0.35f, 0.4f));
        var layers = new List<(int Layer, NativeCharacterPart? Part, NativeCharacterBody.Entry? Body)>();
        if (ShowBody && body is not null) layers.AddRange(body.Visible(Document.Definition).Select(entry => (entry.Layer, (NativeCharacterPart?)null, (NativeCharacterBody.Entry?)entry)));
        layers.AddRange(NativeCharacterLayout.Ordered(Document.Definition).Select(part => (part.Layer, (NativeCharacterPart?)part, (NativeCharacterBody.Entry?)null)));
        foreach (var layer in layers.OrderBy(item => item.Layer))
        {
            if (layer.Body is { } entry)
            {
                if (body!.Texture(entry, SelectedFrame, Playing, Elapsed, Document.Definition.AnimationFps) is { } bodyTexture)
                {
                    var rect = entry.Bounds;
                    DrawTextureRect(bodyTexture, new(origin + new Vector2(rect.X - bounds.X, rect.Y - bounds.Y) * scale, new Vector2(rect.Width, rect.Height) * scale), false);
                }
                continue;
            }
            var part = layer.Part!;
            int selected = NativeCharacterAnimation.FrameIndex(part, SelectedFrame, Playing, Elapsed, Document.Definition.AnimationFps);
            var frame = part.Frames[selected];
            if (!textures.TryGetValue(frame.Id, out var texture))
            { using var image = Image.CreateFromData(part.Width, part.Height, false, Image.Format.Rgba8, Document.Pixels(part, selected)); textures.Add(frame.Id, texture = ImageTexture.CreateFromImage(image)); }
            var corner = NativeCharacterLayout.Corner(part);
            DrawTextureRect(texture, new(origin + new Vector2(corner.X - bounds.X, corner.Y - bounds.Y) * scale, new Vector2(part.Width, part.Height) * scale), false);
            PaintedParts++;
        }
    }
}
