using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeMapCanvas : Control
{
    private readonly GmWorld world;
    private readonly Dictionary<string, Texture2D?> textures = new();
    private readonly Dictionary<int, JsonElement> backgrounds;
    public NativeMapDocument? Document { get; set; }
    public JsonObject? Selection { get; set; }
    public bool SelectionIsTile { get; set; }
    public NativeMapTool Tool { get; set; }
    private Vector2 pan;
    private float zoom = 1;
    public Vector2 Pan { get => pan; set { if (pan == value) return; pan = value; PointerMoved?.Invoke(pointer); ViewChanged?.Invoke(); } }
    public float Zoom { get => zoom; set { float next = Math.Clamp(value, .03f, 8); if (Mathf.IsEqualApprox(zoom, next)) return; zoom = next; PointerMoved?.Invoke(pointer); ViewChanged?.Invoke(); } }
    public float GridSize { get; set; } = 32;
    public bool ShowTiles { get; set; } = true;
    public bool ShowEntities { get; set; } = true;
    public bool ShowSolidOverlay { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool GridSnap { get; set; } = true;
    public HashSet<int> HiddenDepths { get; } = new();
    public Action<Vector2, bool>? SampleRequested { get; set; }
    public Action? ViewChanged { get; set; }
    public int DrawnTiles { get; private set; }
    public int DrawnEntities { get; private set; }
    public int DrawnSolidRects { get; private set; }
    public Action? EditStarted { get; set; }
    public Action? Edited { get; set; }
    public Action<JsonObject?, bool>? Selected { get; set; }
    public Action<Vector2, bool>? PaintAt { get; set; }
    public Action<Vector2?>? PointerMoved { get; set; }
    private Vector2? pointer;
    private bool dragging, panning, editing, erase, spacePanning;
    private Vector2I lastPaintCell = new(int.MinValue, int.MinValue);
    private Vector2 lastPoint, dragStart, selectionStart;
    public NativeMapCanvas(GmWorld world)
    {
        this.world = world; backgrounds = world.Manifest.GetProperty("backgrounds").EnumerateArray().ToDictionary(b => b.GetProperty("id").GetInt32());
        MouseFilter = MouseFilterEnum.Stop; FocusMode = FocusModeEnum.All; TextureFilter = TextureFilterEnum.Nearest;
        Resized += () => { ClampPan(); QueueRedraw(); ViewChanged?.Invoke(); };
    }
    public Vector2 ToRoom(Vector2 local) => local / Zoom + Pan;
    public Vector2 ToCanvas(Vector2 room) => (room - Pan) * Zoom;
    public void ClampPan()
    {
        if (Document is null) return;
        Vector2 room = new(Int(Document.Room, "width"), Int(Document.Room, "height")), view = Size / Zoom;
        Pan = new(ClampAxis(Pan.X, room.X, view.X), ClampAxis(Pan.Y, room.Y, view.Y));
    }
    private static float ClampAxis(float offset, float room, float view) => room <= view ? (room - view) / 2 : Math.Clamp(offset, 0, room - view);
    public void Center(Vector2 room) { Pan = room - Size / Zoom / 2; ClampPan(); QueueRedraw(); }
    public void Fit()
    {
        if (Document is null) return; Zoom = Math.Clamp(Math.Min(Size.X / Int(Document.Room, "width"), Size.Y / Int(Document.Room, "height")), .03f, 4); Pan = Vector2.Zero; ClampPan(); QueueRedraw();
    }
    public void ZoomAt(Vector2 local, float factor) { Vector2 room = ToRoom(local); Zoom = Math.Clamp(Zoom * factor, .03f, 8); Pan = room - local / Zoom; ClampPan(); QueueRedraw(); }
    private Texture2D? Texture(string path)
    {
        if (textures.TryGetValue(path, out var cached)) return cached;
        string fullPath = Path.Combine(world.Root, path); if (!File.Exists(fullPath)) return textures[path] = null;
        using var image = Image.LoadFromFile(fullPath); return textures[path] = image is null || image.IsEmpty() ? null : ImageTexture.CreateFromImage(image);
    }
    private static Color Tint(double value) { long colour = (long)value; return new((colour & 255) / 255f, ((colour >> 8) & 255) / 255f, ((colour >> 16) & 255) / 255f); }
    private JsonElement? Sprite(JsonObject item)
    {
        if (!world.Objects.TryGetValue(Int(item, "object", -1), out var obj) || !world.Sprites.TryGetValue(obj.GetProperty("sprite").GetInt32(), out var sprite) || sprite.GetProperty("frames").GetArrayLength() == 0) return null;
        return sprite;
    }
    public Rect2 ItemBounds(JsonObject item, bool tile)
    {
        Vector2 position = new((float)Number(item, "x"), (float)Number(item, "y"));
        if (tile) { Vector2 extent = new((float)(Number(item, "width", 32) * Number(item, "xscale", 1)), (float)(Number(item, "height", 32) * Number(item, "yscale", 1))); return new(position.Min(position + extent), extent.Abs()); }
        if (Sprite(item) is JsonElement sprite)
        {
            var frame = sprite.GetProperty("frames")[0]; Vector2 origin = new(sprite.GetProperty("originX").GetInt32(), sprite.GetProperty("originY").GetInt32());
            Vector2 size = new(frame.GetProperty("width").GetInt32(), frame.GetProperty("height").GetInt32());
            var transform = new Transform2D(-Mathf.DegToRad((float)Number(item, "angle")), new Vector2((float)Number(item, "xscale", 1), (float)Number(item, "yscale", 1)), 0, position);
            return transform * new Rect2(-origin, size);
        }
        return new(position - new Vector2(8, 8), new(16, 16));
    }
    private bool Solid(JsonObject item) => world.Objects.TryGetValue(Int(item, "object", -1), out var asset) && asset.GetProperty("solid").GetBoolean();
    public (JsonObject? Item, bool Tile) Hit(Vector2 roomPoint)
    {
        if (Document is null) return (null, false);
        foreach (var item in ShowEntities ? Document.Instances.OfType<JsonObject>().Reverse() : Enumerable.Empty<JsonObject>()) if (ItemBounds(item, false).Grow(2 / Zoom).HasPoint(roomPoint)) return (item, false);
        foreach (var item in ShowTiles ? Document.Tiles.OfType<JsonObject>().Where(t => !HiddenDepths.Contains(Int(t, "depth"))).OrderBy(t => Int(t, "depth")) : Enumerable.Empty<JsonObject>()) if (ItemBounds(item, true).HasPoint(roomPoint)) return (item, true);
        return (null, false);
    }
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), new Color("1e1e1e")); DrawnTiles = 0; DrawnEntities = 0; DrawnSolidRects = 0; if (Document is null) return;
        Rect2 visible = new(Pan, Size / Zoom), roomBounds = new(0, 0, Int(Document.Room, "width"), Int(Document.Room, "height"));
        DrawSetTransform(-Pan * Zoom, 0, Vector2.One * Zoom);
        DrawRect(roomBounds, Tint(Number(Document.Room, "colour", 8421504)));
        foreach (var background in Document.Room["backgrounds"]!.AsArray().OfType<JsonObject>())
        {
            if (background["visible"]?.GetValue<bool>() != true || !backgrounds.TryGetValue(Int(background, "background", -1), out var asset) || Texture(asset.GetProperty("path").GetString()!) is not Texture2D texture) continue;
            Vector2 position = new((float)Number(background, "x"), (float)Number(background, "y")); Vector2 size = texture.GetSize();
            if (background["stretch"]?.GetValue<bool>() == true) size = roomBounds.Size;
            bool tileX = background["tileX"]?.GetValue<bool>() == true, tileY = background["tileY"]?.GetValue<bool>() == true;
            int x1 = tileX ? (int)Math.Floor((visible.Position.X - position.X) / size.X) : 0, x2 = tileX ? (int)Math.Ceiling((visible.End.X - position.X) / size.X) : 1;
            int y1 = tileY ? (int)Math.Floor((visible.Position.Y - position.Y) / size.Y) : 0, y2 = tileY ? (int)Math.Ceiling((visible.End.Y - position.Y) / size.Y) : 1;
            for (int y = y1; y < y2; y++) for (int x = x1; x < x2; x++) DrawTextureRect(texture, new(position + new Vector2(x * size.X, y * size.Y), size), false);
        }
        foreach (var item in ShowTiles ? Document.Tiles.OfType<JsonObject>().Where(t => !HiddenDepths.Contains(Int(t, "depth"))).OrderByDescending(t => Int(t, "depth")) : Enumerable.Empty<JsonObject>())
        {
            Rect2 bounds = ItemBounds(item, true); if (!bounds.Intersects(visible)) continue; DrawnTiles++;
            if (backgrounds.TryGetValue(Int(item, "background", -1), out var background) && Texture(background.GetProperty("path").GetString()!) is Texture2D texture)
            {
                Vector2 scale = new((float)Number(item, "xscale", 1), (float)Number(item, "yscale", 1)); Vector2 position = new((float)Number(item, "x"), (float)Number(item, "y"));
                DrawSetTransform((position - Pan) * Zoom, 0, scale * Zoom); DrawTextureRectRegion(texture, new(0, 0, (float)Number(item, "width", 32), (float)Number(item, "height", 32)), new((float)Number(item, "sourceX"), (float)Number(item, "sourceY"), (float)Number(item, "width", 32), (float)Number(item, "height", 32)), Tint(Number(item, "blend", 16777215)));
                DrawSetTransform(-Pan * Zoom, 0, Vector2.One * Zoom);
            }
            else DrawRect(bounds, new Color("d14fc5"));
        }
        foreach (var item in ShowEntities ? Document.Instances.OfType<JsonObject>() : Enumerable.Empty<JsonObject>())
        {
            Rect2 bounds = ItemBounds(item, false); if (!bounds.Intersects(visible)) continue; DrawnEntities++;
            if (Sprite(item) is JsonElement sprite && Texture(sprite.GetProperty("frames")[0].GetProperty("path").GetString()!) is Texture2D texture)
            {
                Vector2 position = new((float)Number(item, "x"), (float)Number(item, "y")), origin = new(sprite.GetProperty("originX").GetInt32(), sprite.GetProperty("originY").GetInt32()); Vector2 scale = new((float)Number(item, "xscale", 1), (float)Number(item, "yscale", 1));
                DrawSetTransform((position - Pan) * Zoom, -Mathf.DegToRad((float)Number(item, "angle")), scale * Zoom); DrawTexture(texture, -origin, Tint(Number(item, "blend", 16777215))); DrawSetTransform(-Pan * Zoom, 0, Vector2.One * Zoom);
            }
            else { DrawRect(bounds, new Color(item["entityType"] is null ? "efb956" : "ce78f2"), false, 1 / Zoom); DrawLine(bounds.Position, bounds.End, Colors.White, 1 / Zoom); DrawLine(new(bounds.End.X, bounds.Position.Y), new(bounds.Position.X, bounds.End.Y), Colors.White, 1 / Zoom); }
            if (ShowSolidOverlay && Solid(item)) { DrawnSolidRects++; DrawRect(bounds, new Color(0.25f, 1, .45f, .6f), false, 1 / Zoom); }
        }
        if (ShowGrid && GridSize * Zoom >= 8)
        {
            Color line = new(1, 1, 1, .1f); float unit = Math.Max(1, GridSize);
            for (float x = MathF.Floor(visible.Position.X / unit) * unit; x < visible.End.X; x += unit) DrawLine(new(x, visible.Position.Y), new(x, visible.End.Y), line, 1 / Zoom);
            for (float y = MathF.Floor(visible.Position.Y / unit) * unit; y < visible.End.Y; y += unit) DrawLine(new(visible.Position.X, y), new(visible.End.X, y), line, 1 / Zoom);
        }
        DrawRect(roomBounds, Colors.White, false, 2 / Zoom);
        if (Selection is not null) DrawRect(ItemBounds(Selection, SelectionIsTile).Grow(2 / Zoom), new Color("ffe26b"), false, 2 / Zoom);
        DrawSetTransform(Vector2.Zero);
    }
    public override void _GuiInput(InputEvent input)
    {
        if (Document is null) return;
        if (input is InputEventKey { Keycode: Key.Space } space)
        {
            spacePanning = space.Pressed; if (!space.Pressed) panning = false; AcceptEvent(); return;
        }
        if (input is InputEventMouseButton button)
        {
            if (button.Pressed && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                if (button.CtrlPressed) ZoomAt(button.Position, button.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f);
                else { float distance = (button.ButtonIndex == MouseButton.WheelUp ? -36 : 36) / Zoom; Pan += button.ShiftPressed ? new Vector2(distance, 0) : new Vector2(0, distance); ClampPan(); QueueRedraw(); }
                AcceptEvent(); return;
            }
            if (button.ButtonIndex == MouseButton.Middle || (button.ButtonIndex == MouseButton.Left && (Tool == NativeMapTool.Pan || spacePanning)))
            { panning = button.Pressed; lastPoint = button.Position; GrabFocus(); AcceptEvent(); return; }
            if (Tool == NativeMapTool.Zoom && button.ButtonIndex is MouseButton.Left or MouseButton.Right)
            { if (button.Pressed) ZoomAt(button.Position, button.ButtonIndex == MouseButton.Right || button.AltPressed ? .8f : 1.25f); AcceptEvent(); return; }
            if (Tool == NativeMapTool.Eyedrop && button.ButtonIndex == MouseButton.Left)
            { if (button.Pressed) SampleRequested?.Invoke(ToRoom(button.Position), button.AltPressed); AcceptEvent(); return; }
            if (button.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
            if (!button.Pressed) { dragging = false; editing = false; AcceptEvent(); return; }
            GrabFocus(); erase = button.ButtonIndex == MouseButton.Right; dragging = true; lastPoint = ToRoom(button.Position); dragStart = lastPoint; editing = false;
            if (Tool == NativeMapTool.Select && !erase)
            {
                var hit = Hit(lastPoint); Selection = hit.Item; SelectionIsTile = hit.Tile; Selected?.Invoke(hit.Item, hit.Tile); if (Selection is not null) selectionStart = new((float)Number(Selection, "x"), (float)Number(Selection, "y"));
            }
            else
            {
                EditStarted?.Invoke(); editing = true; PaintAt?.Invoke(lastPoint, erase);
                float unit = GridSnap && !button.CtrlPressed ? GridSize : 1; lastPaintCell = new((int)Math.Floor(lastPoint.X / unit), (int)Math.Floor(lastPoint.Y / unit));
            }
            AcceptEvent(); QueueRedraw(); return;
        }
        if (input is InputEventMouseMotion motion)
        {
            pointer = ToRoom(motion.Position); PointerMoved?.Invoke(pointer);
            if (panning) { Pan -= motion.Relative / Zoom; ClampPan(); QueueRedraw(); AcceptEvent(); return; }
            if (!dragging) return; Vector2 point = ToRoom(motion.Position);
            if (Tool == NativeMapTool.Select && !erase && Selection is not null)
            {
                Vector2 target = selectionStart + point - dragStart; if (GridSnap && !motion.CtrlPressed) target = (target / GridSize).Round() * GridSize;
                if (target == new Vector2((float)Number(Selection, "x"), (float)Number(Selection, "y"))) return;
                if (!editing) { EditStarted?.Invoke(); editing = true; } Selection["x"] = (double)target.X; Selection["y"] = (double)target.Y; Edited?.Invoke(); QueueRedraw();
            }
            else if (Tool is not (NativeMapTool.Entity or NativeMapTool.Select) || erase)
            {
                float unit = GridSnap && !motion.CtrlPressed ? GridSize : 1, distance = point.DistanceTo(lastPoint), step = Math.Max(1, unit / 3); int count = Math.Clamp((int)Math.Ceiling(distance / step), 1, 2048);
                for (int i = 1; i <= count; i++)
                {
                    Vector2 candidate = lastPoint.Lerp(point, (float)i / count); var cell = new Vector2I((int)Math.Floor(candidate.X / unit), (int)Math.Floor(candidate.Y / unit));
                    if (cell != lastPaintCell) { PaintAt?.Invoke(candidate, erase); lastPaintCell = cell; }
                }
            }
            lastPoint = point; AcceptEvent();
        }
    }
    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit) { pointer = null; PointerMoved?.Invoke(null); }
        else if (what == NotificationFocusExit) { panning = spacePanning = dragging = false; editing = false; }
    }
    public override void _ExitTree() { foreach (var texture in textures.Values) texture?.Dispose(); textures.Clear(); }
}
