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
    public Vector2 Pan { get; set; } = Vector2.Zero;
    public float Zoom { get; set; } = 1;
    public float GridSize { get; set; } = 32;
    public Action? EditStarted { get; set; }
    public Action? Edited { get; set; }
    public Action<JsonObject?, bool>? Selected { get; set; }
    public Action<Vector2, bool>? PaintAt { get; set; }
    private bool dragging, panning, editing, erase;
    private Vector2 lastPoint, dragStart, selectionStart;
    public NativeMapCanvas(GmWorld world)
    {
        this.world = world; backgrounds = world.Manifest.GetProperty("backgrounds").EnumerateArray().ToDictionary(b => b.GetProperty("id").GetInt32());
        MouseFilter = MouseFilterEnum.Stop; FocusMode = FocusModeEnum.All; TextureFilter = TextureFilterEnum.Nearest;
    }
    public Vector2 ToRoom(Vector2 local) => local / Zoom + Pan;
    public Vector2 ToCanvas(Vector2 room) => (room - Pan) * Zoom;
    public void Fit()
    {
        if (Document is null) return; Zoom = Math.Clamp(Math.Min(Size.X / Int(Document.Room, "width"), Size.Y / Int(Document.Room, "height")), .03f, 4); Pan = Vector2.Zero; QueueRedraw();
    }
    public void ZoomAt(Vector2 local, float factor) { Vector2 room = ToRoom(local); Zoom = Math.Clamp(Zoom * factor, .03f, 8); Pan = room - local / Zoom; QueueRedraw(); }
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
        foreach (var item in Document.Instances.OfType<JsonObject>().Reverse()) if (ItemBounds(item, false).Grow(2 / Zoom).HasPoint(roomPoint)) return (item, false);
        foreach (var item in Document.Tiles.OfType<JsonObject>().Reverse()) if (ItemBounds(item, true).HasPoint(roomPoint)) return (item, true);
        return (null, false);
    }
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), new Color("17212c")); if (Document is null) return;
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
        foreach (var item in Document.Tiles.OfType<JsonObject>().OrderByDescending(t => Int(t, "depth")))
        {
            Rect2 bounds = ItemBounds(item, true); if (!bounds.Intersects(visible)) continue;
            if (backgrounds.TryGetValue(Int(item, "background", -1), out var background) && Texture(background.GetProperty("path").GetString()!) is Texture2D texture)
            {
                Vector2 scale = new((float)Number(item, "xscale", 1), (float)Number(item, "yscale", 1)); Vector2 position = new((float)Number(item, "x"), (float)Number(item, "y"));
                DrawSetTransform((position - Pan) * Zoom, 0, scale * Zoom); DrawTextureRectRegion(texture, new(0, 0, (float)Number(item, "width", 32), (float)Number(item, "height", 32)), new((float)Number(item, "sourceX"), (float)Number(item, "sourceY"), (float)Number(item, "width", 32), (float)Number(item, "height", 32)), Tint(Number(item, "blend", 16777215)));
                DrawSetTransform(-Pan * Zoom, 0, Vector2.One * Zoom);
            }
            else DrawRect(bounds, new Color("d14fc5"));
        }
        foreach (var item in Document.Instances.OfType<JsonObject>())
        {
            Rect2 bounds = ItemBounds(item, false); if (!bounds.Intersects(visible)) continue;
            if (Sprite(item) is JsonElement sprite && Texture(sprite.GetProperty("frames")[0].GetProperty("path").GetString()!) is Texture2D texture)
            {
                Vector2 position = new((float)Number(item, "x"), (float)Number(item, "y")), origin = new(sprite.GetProperty("originX").GetInt32(), sprite.GetProperty("originY").GetInt32()); Vector2 scale = new((float)Number(item, "xscale", 1), (float)Number(item, "yscale", 1));
                DrawSetTransform((position - Pan) * Zoom, -Mathf.DegToRad((float)Number(item, "angle")), scale * Zoom); DrawTexture(texture, -origin, Tint(Number(item, "blend", 16777215))); DrawSetTransform(-Pan * Zoom, 0, Vector2.One * Zoom);
            }
            else { DrawRect(bounds, new Color(item["entityType"] is null ? "efb956" : "ce78f2"), false, 1 / Zoom); DrawLine(bounds.Position, bounds.End, Colors.White, 1 / Zoom); DrawLine(new(bounds.End.X, bounds.Position.Y), new(bounds.Position.X, bounds.End.Y), Colors.White, 1 / Zoom); }
            if (Solid(item)) DrawRect(bounds, new Color(0.25f, 1, .45f, .6f), false, 1 / Zoom);
        }
        if (GridSize * Zoom >= 8)
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
        if (input is InputEventMouseButton button)
        {
            if (button.Pressed && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown) { ZoomAt(button.Position, button.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f); AcceptEvent(); return; }
            if (button.ButtonIndex == MouseButton.Middle || (button.ButtonIndex == MouseButton.Left && Tool == NativeMapTool.Pan)) { panning = button.Pressed; lastPoint = button.Position; AcceptEvent(); return; }
            if (button.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
            if (!button.Pressed) { dragging = false; editing = false; AcceptEvent(); return; }
            GrabFocus(); erase = button.ButtonIndex == MouseButton.Right; dragging = true; lastPoint = ToRoom(button.Position); dragStart = lastPoint; editing = false;
            if (Tool == NativeMapTool.Select && !erase)
            {
                var hit = Hit(lastPoint); Selection = hit.Item; SelectionIsTile = hit.Tile; Selected?.Invoke(hit.Item, hit.Tile); if (Selection is not null) selectionStart = new((float)Number(Selection, "x"), (float)Number(Selection, "y"));
            }
            else { EditStarted?.Invoke(); editing = true; PaintAt?.Invoke(lastPoint, erase); }
            AcceptEvent(); QueueRedraw(); return;
        }
        if (input is InputEventMouseMotion motion)
        {
            if (panning) { Pan -= motion.Relative / Zoom; QueueRedraw(); AcceptEvent(); return; }
            if (!dragging) return; Vector2 point = ToRoom(motion.Position);
            if (Tool == NativeMapTool.Select && !erase && Selection is not null)
            {
                Vector2 target = selectionStart + point - dragStart; if (!motion.CtrlPressed) target = (target / GridSize).Round() * GridSize;
                if (target == new Vector2((float)Number(Selection, "x"), (float)Number(Selection, "y"))) return;
                if (!editing) { EditStarted?.Invoke(); editing = true; } Selection["x"] = (double)target.X; Selection["y"] = (double)target.Y; Edited?.Invoke(); QueueRedraw();
            }
            else if (Tool != NativeMapTool.Entity || erase)
            {
                float distance = point.DistanceTo(lastPoint), step = Math.Max(1, GridSize / 3); int count = Math.Clamp((int)Math.Ceiling(distance / step), 1, 2048);
                for (int i = 1; i <= count; i++) { Vector2 candidate = lastPoint.Lerp(point, (float)i / count); Vector2 cell = (candidate / GridSize).Floor(); Vector2 previousCell = (lastPoint / GridSize).Floor(); if (cell != previousCell) PaintAt?.Invoke(candidate, erase); }
            }
            lastPoint = point; AcceptEvent();
        }
    }
    public override void _ExitTree() { foreach (var texture in textures.Values) texture?.Dispose(); textures.Clear(); }
}
