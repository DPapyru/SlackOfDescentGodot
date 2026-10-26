using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeEditorPlayAudit : Node
{
    private GmWorld world = null!;
    private NativeContentStore store = null!;
    private ContentRegistry registry = null!;
    private GmGraphics graphics = null!;
    private GmCanvas canvas = null!;
    private int checks;
    private string output = "";
    public override async void _Ready()
    {
        try
        {
            string project = ProjectSettings.GlobalizePath("res://");
            output = System.Environment.GetEnvironmentVariable("SLACK_PLAY_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "slack-play-audit");
            Directory.CreateDirectory(output);
            string user = System.Environment.GetEnvironmentVariable("SLACK_USER_ROOT") ?? Path.Combine(output, "user");
            world = new GmWorld(Path.Combine(project, "GameData"), System.Environment.GetEnvironmentVariable("SLACK_SOURCE") ?? Path.Combine(project, "GameData/assets"), user);
            world.Random.Seed = 0;
            store = new NativeContentStore(user); registry = new ContentRegistry(); store.DiscoverCharacters(registry);
            graphics = new GmGraphics(this, world); canvas = new GmCanvas(); AddChild(canvas); canvas.Paint = graphics.Render;
            world.Platform = Platform; world.Redraw = () => { graphics.Build(); canvas.QueueRedraw(); };
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var args = OS.GetCmdlineUserArgs();
            int only = Array.IndexOf(args, "--only");
            string stage = only >= 0 && only + 1 < args.Length ? args[only + 1] : "all";
            if (stage is "map" or "all") await AuditMapEditor();
            if (stage is "character" or "all") await AuditCharacterEditor();
            GD.Print("PLAY_AUDIT editor_checks=", checks);
            audio();
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("PLAY_AUDIT_FAILED " + error); GetTree().Quit(1); }
    }
    private void audio() { }
    private GmValue? Platform(string name, GmValue[] args, GmContext context) => name switch
    {
        "show_message" or "show_message_ext" => 1,
        "show_question" => false,
        "get_string" => args.Length > 1 ? args[1] : "",
        _ => null
    };
    private void Expect(bool passed, string message)
    {
        checks++;
        if (!passed) throw new InvalidOperationException("Play editor: " + message);
    }
    private async Task Tick()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private async Task Shot(string name, Window? window = null)
    {
        graphics.Build(); canvas.QueueRedraw();
        await Tick();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = (window ?? GetWindow()).GetTexture().GetImage();
        string path = Path.Combine(output, name + ".png");
        image.SavePng(path);
        GD.Print("PLAY_AUDIT shot=", name, " kind=editor path=", path);
    }
    private async Task WaitFor(Func<bool> done, string what)
    {
        for (int frame = 0; frame < 900; frame++) { if (done()) return; await Tick(); }
        throw new InvalidOperationException("Play editor timed out: " + what);
    }
    private async Task AuditMapEditor()
    {
        var map = new NativeMapEditor(world, registry, store); AddChild(map); map.PopupCentered();
        await WaitFor(() => map.Document is not null, "map editor document");
        await Tick();
        map.MapCanvas.Zoom = .7f; map.MapCanvas.Pan = new(0, 0); map.MapCanvas.QueueRedraw(); await Tick();
        int tiles = map.Document.Tiles.Count, entities = map.Document.Instances.Count;
        Expect(tiles > 0, "map editor loaded original tiles");
        await Shot("editor-map-01-open", map);
        map.MapCanvas.Tool = NativeMapTool.Tile; await Tick();
        Vector2 point = map.MapCanvas.GlobalPosition + map.MapCanvas.ToCanvas(new(400, 96));
        map.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        await Tick();
        map.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Tick();
        Expect(map.Document.Tiles.Count == tiles + 1, "drawing a tile with the mouse edits the document, got " + map.Document.Tiles.Count + " from " + tiles);
        Expect(map.Dirty, "tile drawing marks the map dirty");
        map.MapCanvas.Tool = NativeMapTool.Entity; await Tick();
        Vector2 spot = map.MapCanvas.GlobalPosition + map.MapCanvas.ToCanvas(new(448, 96));
        map.PushInput(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        await Tick();
        map.PushInput(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Tick();
        Expect(map.Document.Instances.Count == entities + 1, "placing an entity with the mouse edits the document, got " + map.Document.Instances.Count + " from " + entities);
        await Shot("editor-map-02-edit", map);
        string path = Path.Combine(output, "play-audit", "hands-on.map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        map.Service.Save(map.Document, path);
        await Tick();
        Expect(File.Exists(path), "map editor writes the saved document");
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Expect(saved.RootElement.GetProperty("Room").GetProperty("tiles").GetArrayLength() == tiles + 1, "saved map keeps the drawn tile");
        await Shot("editor-map-03-saved", map);
        GD.Print("PLAY_AUDIT step=map-editor tiles=", map.Document.Tiles.Count, " entities=", map.Document.Instances.Count, " dirty=", map.Dirty, " path=", path);
        map.QueueFree(); await Tick();
    }
    private async Task AuditCharacterEditor()
    {
        var editor = new NativeCharacterEditor(world, registry, store); AddChild(editor); editor.PopupCentered();
        await WaitFor(() => editor.Document is not null, "character editor document");
        await Tick();
        var document = editor.Document;
        Expect(document.Definition.Parts.Count > 0, "character editor starts with parts");
        await Shot("editor-character-01-open", editor);
        var part = document.Definition.Parts[0];
        byte[] before = (byte[])document.Pixels(part, 0).Clone();
        var canvasControl = editor.FindChild("像素画布", true, false) as NativeCharacterPixelCanvas;
        Expect(canvasControl is not null, "character editor exposes its pixel canvas");
        var workspace = editor.FindChild("*", true, false) as TabContainer;
        var tabs = editor.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>().ToList();
        var pixelTab = tabs.FirstOrDefault(tab => tab.GetTabCount() > 1 && tab.GetTabTitle(1).Contains("部位"));
        Expect(pixelTab is not null, "character editor exposes the part-pixel tab");
        pixelTab!.CurrentTab = 1;
        await Tick();
        canvasControl!.Zoom = 10; canvasControl.Tool = NativeCharacterTool.Brush; canvasControl.PaintColor = new Color(1f, 0f, .5f, 1f);
        canvasControl.AuditStroke([new(3, 4), new(9, 4)]);
        await Tick();
        byte[] after = document.Pixels(part, 0);
        int offset = (4 * part.Width + 3) * 4;
        Expect(after[offset] == 255 && after[offset + 1] == 0 && after[offset + 2] == 128 && after[offset + 3] == 255, "brush paints an opaque pixel, got " + string.Join(",", after[offset], after[offset + 1], after[offset + 2], after[offset + 3]));
        int drawn = 0; for (int i = 0; i < after.Length; i += 4) if (after[i + 3] != 0) drawn++;
        Expect(!before.SequenceEqual(after), "brush changed frame pixels");
        Expect(drawn >= 7, "brush stroke covers the dragged pixels, got " + drawn);
        Expect(document.Dirty, "painting marks the character dirty");
        await Shot("editor-character-02-painted", editor);
        new NativeCharacterRepository(store).Save(document, registry, true);
        await Tick();
        string saved = Path.Combine(store.Characters, document.Definition.Id + ".character.json");
        Expect(!document.Dirty, "saving clears the dirty flag");
        Expect(File.Exists(saved), "character file written to " + saved);
        await Shot("editor-character-03-saved", editor);
        editor.GenerateBaseForAudit(); await Tick();
        Expect(document.Definition.Parts.Count >= 10, "base generation fills the document, got " + document.Definition.Parts.Count);
        var canvasMode = editor.FindChild("像素画布", true, false) as NativeCharacterPixelCanvas;
        Expect(canvasMode is not null && canvasMode.Mode == NativeCharacterCanvasMode.Overall, "base generation opens the overall canvas");
        await Shot("character-base-overall");
        editor.SetReferenceForAudit(true, 0.45f); await Tick();
        var pixelPanel = editor.FindChild("像素画布", true, false) as NativeCharacterPixelCanvas;
        Expect(pixelPanel is not null && pixelPanel.ShowReference, "the reference overlay toggles on");
        await Shot("character-base-reference");
        GD.Print("PLAY_AUDIT step=character-editor parts=", document.Definition.Parts.Count, " painted=", drawn, " dirty=", document.Dirty, " path=", saved);
        editor.QueueFree(); await Tick();
    }
}