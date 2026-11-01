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
        if (OS.GetCmdlineUserArgs().Contains("--host-probe")) { await HostProbeAsync(); return; }
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
    private async Task HostProbeAsync()
    {
        string dir = System.Environment.GetEnvironmentVariable("SLACK_PLAY_OUTPUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        var host = new GameHost { Name = "ProbeHost" }; AddChild(host);
        for (int frame = 0; frame < 900 && !host.ProbeWorldReady; frame++) await Tick();
        Expect(host.ProbeWorldReady, "the host boots its world");
        host.ProbeOpenCharacterEditor();
        for (int frame = 0; frame < 900 && host.ProbeEditor is null; frame++) await Tick();
        var editor = host.ProbeEditor;
        Expect(editor is not null, "GameHostNative.OpenCharacterEditor opens the character editor");
        for (int frame = 0; frame < 1500 && host.ProbePreview?.Player is null; frame++) await Tick();
        Expect(host.ProbePreview?.Player is not null, "the host initialises the preview world without applying the document");
        await host.ProbeFrames(60);
        async Task Probe(string label)
        {
            var surface = editor!.PreviewSurface; var viewport = host.ProbeViewport; var render = host.ProbeCanvas; var runtime = host.ProbePreview; var player = runtime?.Player;
            GD.Print("PLAY_AUDIT ", label, " texture=", surface.Texture is not null, " viewport=", viewport?.Size, " commands=", render?.Commands.Count ?? -1,
                " player=", player?.Id, " at=", player?.Scope.ReadOr("x").Number, ",", player?.Scope.ReadOr("y").Number,
                " visible=", player?.Scope.ReadOr("visible").Truth, " active=", player?.Active,
                " sprite=", player?.Scope.ReadOr("sprite_index").Int, " chara=", player?.Scope.ReadOr("Chara"),
                " parts=", runtime?.Rendering.Audit.Count ?? -1, " frames=", runtime?.World.Frame ?? -1);
            if (runtime is not null) GD.Print("PLAY_AUDIT ", label, " bound=", host.ProbeBound(), " targets=", string.Join("|", runtime.Rendering.Audit.Select(a => a.Target).Distinct()), " bones=", Bones(runtime));
            RenderingServer.ForceDraw(true);
            using var image = editor.GetTexture().GetImage();
            image.SavePng(Path.Combine(dir, label + ".png"));
        }
        string Bones(NativeCharacterPreview runtime)
        {
            if (runtime.Player is not { } player) return "no-player";
            var list = new List<string>();
            foreach (string target in NativeCharacterBones.Order)
                list.Add(target + "=" + (NativeCharacterBones.Resolve(runtime.World, player, target) is { } bone ? (bone.Scope.ReadOr("visible").Truth ? "V" : "h") + (bone.Active ? "A" : "a") : "missing"));
            return string.Join(",", list);
        }
        var gamePlayer = host.ProbeGamePlayer();
        GD.Print("PLAY_AUDIT game-player id=", gamePlayer?.Id, " at=", gamePlayer?.Scope.ReadOr("x").Number, ",", gamePlayer?.Scope.ReadOr("y").Number,
            " visible=", gamePlayer?.Scope.ReadOr("visible").Truth, " active=", gamePlayer?.Active,
            " sprite=", gamePlayer?.Scope.ReadOr("sprite_index").Int, " chara=", gamePlayer?.Scope.ReadOr("Chara"),
            " room=", host.ProbeWorld.CurrentRoom, " frames=", host.ProbeWorld.Frame);
        await Probe("a0-blank-doc");
        var document = editor.Document;
        document.Paint(document.Definition.Parts[0].Id, 0, 0, 0, 220, 40, 60, 255);
        editor.ApplyRuntimePreview();
        await host.ProbeFrames(40);
        await Probe("a1-after-pixel-apply");
        host.ProbeResetPreview();
        await host.ProbeFrames(30);
        await Probe("b-after-reset");
        host.ProbeApplyPreview();
        await host.ProbeFrames(40);
        await Probe("c-after-reapply");
        GetTree().Quit();
    }
    private void audio() { }
    private GmValue? Platform(string name, GmValue[] args, GmContext context) => name switch
    {
        "show_message" or "show_message_ext" => 1,
        "show_question" => false,
        "get_string" => args.Length > 1 ? args[1] : "",
        _ => (GmValue?)null
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
    private static int DocumentInt(System.Text.Json.Nodes.JsonObject room, string key) => room[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<int>(out int parsed) ? parsed : 0;
    private static PopupMenu MenuPopup(NativeMapEditor map, int menu)
    {
        var bar = (HBoxContainer)map.FindChild("MapMenus", true, false);
        var popup = ((MenuButton)bar.GetChild(menu)).GetPopup();
        popup.EmitSignal(PopupMenu.SignalName.AboutToPopup);
        return popup;
    }
    private static bool MenuEnabled(PopupMenu popup, int id) => !popup.IsItemDisabled(popup.GetItemIndex(id));
    private async Task AuditMapEditor()
    {
        var map = new NativeMapEditor(world, registry, store); AddChild(map); map.PopupCentered();
        await WaitFor(() => map.Document is not null, "map editor document");
        await Tick();
        Expect(map.MapCanvas.Zoom < .9f, "opening a map fits the whole room instead of showing the top-left corner, got zoom " + map.MapCanvas.Zoom);
        float fittedWidth = DocumentInt(map.Document.Room, "width") * map.MapCanvas.Zoom, fittedHeight = DocumentInt(map.Document.Room, "height") * map.MapCanvas.Zoom;
        Expect(fittedWidth <= map.MapCanvas.Size.X + 1 && fittedHeight <= map.MapCanvas.Size.Y + 1, "the fitted room fits the canvas, got " + fittedWidth + "x" + fittedHeight + " in " + map.MapCanvas.Size);
        map.MapCanvas.Zoom = .7f; map.MapCanvas.Pan = new(0, 0); map.MapCanvas.QueueRedraw(); await Tick();
        var pointer = (Label)map.FindChild("MapPointerStatus", true, false);
        Expect(pointer.Text.Contains("70%", StringComparison.Ordinal), "the status bar reports the live zoom, got " + pointer.Text);
        Vector2 hover = map.MapCanvas.ToCanvas(new(640, 128));
        map.MapCanvas._GuiInput(new InputEventMouseMotion { Position = hover, GlobalPosition = hover, Relative = Vector2.Zero });
        await Tick();
        Expect(pointer.Text.Contains("640", StringComparison.Ordinal) && pointer.Text.Contains("128", StringComparison.Ordinal), "the status bar reports the room coordinate under the pointer, got " + pointer.Text);
        Expect(pointer.Text.Contains("20, 4", StringComparison.Ordinal), "the status bar also reports the tile cell, got " + pointer.Text);
        var left = (Label)map.FindChild("MapStatus", true, false);
        Expect(left.Text.Contains("实体 " + map.Document.Instances.Count, StringComparison.Ordinal) && left.Text.Contains("图块 " + map.Document.Tiles.Count, StringComparison.Ordinal), "the left status label keeps the document summary, got " + left.Text);
        Expect(!left.Text.Contains("缩放", StringComparison.Ordinal), "the document summary is not rewritten on every pointer move, got " + left.Text);
        var undoButton = (Button)map.FindChild("Undo", true, false);
        var redoButton = (Button)map.FindChild("Redo", true, false);
        var deleteButton = (Button)map.FindChild("DeleteSelection", true, false);
        var historyList = (ItemList)map.FindChild("MapHistoryList", true, false);
        var documentTab = (Label)map.FindChild("DocumentTab", true, false);
        Expect(undoButton.Disabled && redoButton.Disabled, "undo and redo start disabled on a freshly opened map");
        Expect(deleteButton.Disabled, "delete starts disabled without a selection");
        Expect(MenuPopup(map, 1).GetItemIndex(0) >= 0, "the edit menu exposes the undo command");
        Expect(MenuPopup(map, 1).IsItemDisabled(MenuPopup(map, 1).GetItemIndex(0)), "the undo menu command starts greyed out on a freshly opened map");
        Expect(MenuPopup(map, 1).IsItemDisabled(MenuPopup(map, 1).GetItemIndex(3)), "the duplicate menu command starts greyed out without a selection");
        Expect(MenuEnabled(MenuPopup(map, 3), 2), "the playtest menu command stays available without a selection");
        Expect(MenuPopup(map, 0).IsItemDisabled(MenuPopup(map, 0).GetItemIndex(2)) == false, "saving stays available on an opened map");
        Expect(historyList.ItemCount == 1 && historyList.IsSelected(0), "the history panel shows only the current state before any edit, got " + historyList.ItemCount);
        Expect(!documentTab.Text.StartsWith("●", StringComparison.Ordinal), "an untouched document is not marked unsaved in the document tab, got " + documentTab.Text);
        map.SelectTool((int)NativeMapTool.Select);
        Expect(map.MapCanvas.Tool == NativeMapTool.Select, "the tool shortcut starts from the select tool");
        map.PushInput(new InputEventKey { Keycode = Godot.Key.B, Pressed = true }, true); await Tick();
        map.PushInput(new InputEventKey { Keycode = Godot.Key.B, Pressed = false }, true); await Tick();
        Expect(map.MapCanvas.Tool == NativeMapTool.Tile, "pressing the bound tile key switches to the tile tool, got " + map.MapCanvas.Tool);
        Expect(((Button)map.FindChild("ToolTile", true, false)).ButtonPressed, "the tool rail highlights the active tool");
        ((SpinBox)map.FindChild("GridSize", true, false)).GetLineEdit().GrabFocus(); await Tick();
        map.PushInput(new InputEventKey { Keycode = Godot.Key.C, Pressed = true }, true); await Tick();
        map.PushInput(new InputEventKey { Keycode = Godot.Key.C, Pressed = false }, true); await Tick();
        Expect(map.MapCanvas.Tool == NativeMapTool.Tile, "typing inside a numeric field does not steal the tool shortcut, got " + map.MapCanvas.Tool);
        map.MapCanvas.GrabFocus(); await Tick();
        map.PushInput(new InputEventKey { Keycode = Godot.Key.C, Pressed = true }, true); await Tick();
        map.PushInput(new InputEventKey { Keycode = Godot.Key.C, Pressed = false }, true); await Tick();
        Expect(map.MapCanvas.Tool == NativeMapTool.Collision, "after leaving the field the shortcut works again, got " + map.MapCanvas.Tool);
        var tilesToggle = (CheckBox)map.FindChild("ShowTiles", true, false);
        var entitiesToggle = (CheckBox)map.FindChild("ShowEntities", true, false);
        var solidToggle = (CheckBox)map.FindChild("ShowSolid", true, false);
        map.MapCanvas.QueueRedraw(); await Tick();
        int visibleTiles = map.MapCanvas.DrawnTiles, visibleEntities = map.MapCanvas.DrawnEntities, visibleSolid = map.MapCanvas.DrawnSolidRects;
        Expect(visibleTiles > 0 && visibleEntities > 0, "the canvas actually paints tiles and entities, got " + visibleTiles + "/" + visibleEntities);
        tilesToggle.ButtonPressed = false; await Tick();
        Expect(map.MapCanvas.DrawnTiles == 0 && map.MapCanvas.DrawnEntities == visibleEntities, "hiding the tile layer stops tiles from being painted while entities stay, got " + map.MapCanvas.DrawnTiles + "/" + map.MapCanvas.DrawnEntities);
        entitiesToggle.ButtonPressed = false; await Tick();
        Expect(map.MapCanvas.DrawnEntities == 0 && map.MapCanvas.DrawnSolidRects == 0, "hiding entities stops entities and their collision boxes from being painted, got " + map.MapCanvas.DrawnEntities + "/" + map.MapCanvas.DrawnSolidRects);
        tilesToggle.ButtonPressed = true; entitiesToggle.ButtonPressed = true; await Tick();
        Expect(map.MapCanvas.DrawnTiles == visibleTiles && map.MapCanvas.DrawnEntities == visibleEntities, "restoring the layers paints them again, got " + map.MapCanvas.DrawnTiles + "/" + map.MapCanvas.DrawnEntities);
        Expect(visibleSolid > 0, "the room has solid entities to outline, got " + visibleSolid);
        solidToggle.ButtonPressed = false; await Tick();
        Expect(map.MapCanvas.DrawnSolidRects == 0 && map.MapCanvas.DrawnEntities == visibleEntities, "hiding the collision overlay stops the outlines but keeps entities, got " + map.MapCanvas.DrawnSolidRects);
        solidToggle.ButtonPressed = true; await Tick();
        Expect(map.MapCanvas.DrawnSolidRects == visibleSolid, "restoring the collision overlay paints the outlines again, got " + map.MapCanvas.DrawnSolidRects);
        var spawnButton = (Button)map.FindChild("SpawnCenter", true, false);
        var spawn = map.Document.Instances.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(i => DocumentInt(i, "object") == world.Constants["objSetRespawn"].Int);
        Expect(spawn is not null, "the test room carries an objSetRespawn marker");
        map.MapCanvas.Zoom = Math.Max(1f, map.MapCanvas.Size.X / 180f); map.MapCanvas.ClampPan();
        map.CenterOnSpawn(); await Tick();
        float spawnX = spawn!["x"] is System.Text.Json.Nodes.JsonValue spawnValue && spawnValue.TryGetValue<double>(out double parsedSpawn) ? (float)parsedSpawn : 0;
        Expect(Math.Abs(map.MapCanvas.Pan.X + map.MapCanvas.Size.X / map.MapCanvas.Zoom / 2 - spawnX) <= 1, "centring on the spawn marker puts it in the middle of the canvas, pan=" + map.MapCanvas.Pan + " spawnX=" + spawnX);
        Expect(!spawnButton.Disabled, "the spawn button is enabled when the room has a spawn marker");
        map.MapCanvas.Zoom = .7f; map.MapCanvas.Pan = new(0, 0); map.MapCanvas.QueueRedraw(); await Tick();
        map.SelectTool((int)NativeMapTool.Select); await Tick();
        int tiles = map.Document.Tiles.Count, entities = map.Document.Instances.Count;
        Expect(tiles > 0, "map editor loaded original tiles");
        await Shot("editor-map-01-open", map);
        map.SelectTool((int)NativeMapTool.Tile); await Tick();
        Expect(historyList.ItemCount == 1, "selecting a tool without editing keeps the history empty, got " + historyList.ItemCount);
        Vector2 point = map.MapCanvas.GlobalPosition + map.MapCanvas.ToCanvas(new(400, 96));
        map.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        await Tick();
        map.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Tick();
        Expect(map.Document.Tiles.Count == tiles + 1, "drawing a tile with the mouse edits the document, got " + map.Document.Tiles.Count + " from " + tiles);
        Expect(map.Dirty, "tile drawing marks the map dirty");
        Expect(!undoButton.Disabled, "drawing enables the undo button, it is no longer a dead button");
        await Tick();
        Expect(!MenuPopup(map, 1).IsItemDisabled(MenuPopup(map, 1).GetItemIndex(0)), "drawing enables the undo menu command");
        Expect(historyList.ItemCount == 2 && historyList.IsSelected(1), "drawing appends one undo step and selects the current state, got " + historyList.ItemCount);
        Expect(documentTab.Text.StartsWith("●", StringComparison.Ordinal) && documentTab.Text.Contains(map.Document.Name, StringComparison.Ordinal), "the document tab marks the edited map unsaved, got " + documentTab.Text);
        map.Undo(); await Tick();
        Expect(map.Document.Tiles.Count == tiles, "the enabled undo button really reverts the drawing, got " + map.Document.Tiles.Count);
        Expect(!redoButton.Disabled, "undo enables the redo button");
        Expect(MenuPopup(map, 1).IsItemDisabled(MenuPopup(map, 1).GetItemIndex(0)) && !MenuPopup(map, 1).IsItemDisabled(MenuPopup(map, 1).GetItemIndex(1)), "the edit menu mirrors undo and redo availability");
        Expect(historyList.ItemCount == 2 && historyList.IsSelected(0), "undo moves the history selection back to the current state, got " + historyList.ItemCount);
        map.Redo(); await Tick();
        Expect(map.Document.Tiles.Count == tiles + 1, "the enabled redo button really reapplies the drawing, got " + map.Document.Tiles.Count);
        map.SelectTool((int)NativeMapTool.Entity); await Tick();
        Vector2 spot = map.MapCanvas.GlobalPosition + map.MapCanvas.ToCanvas(new(448, 96));
        map.PushInput(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        await Tick();
        map.PushInput(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Tick();
        Expect(map.Document.Instances.Count == entities + 1, "placing an entity with the mouse edits the document, got " + map.Document.Instances.Count + " from " + entities);
        var placedOriginal = map.Document.Instances.OfType<System.Text.Json.Nodes.JsonObject>().Last();
        Expect(map.DuplicateSelection(), "duplicating a placed entity is refused, it should succeed");
        Expect(map.Document.Instances.Count == entities + 2, "duplicating the selection adds one copy, got " + map.Document.Instances.Count);
        var duplicated = map.Document.Instances.OfType<System.Text.Json.Nodes.JsonObject>().Last();
        Expect(DocumentInt(duplicated, "id") != DocumentInt(placedOriginal, "id"), "the duplicate gets a fresh id, both were " + DocumentInt(duplicated, "id"));
        Expect(duplicated["object"]!.GetValue<int>() == placedOriginal["object"]!.GetValue<int>(), "the duplicate keeps the same object type");
        map.Undo(); await Tick();
        Expect(map.Document.Instances.Count == entities + 1, "undo removes the duplicate, got " + map.Document.Instances.Count);
        await Shot("editor-map-02-edit", map);
        string path = Path.Combine(output, "play-audit", "hands-on.map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);
        map.SavePath(path);
        await Tick();
        Expect(!map.Dirty && map.FilePath is not null, "saving through the editor clears the dirty flag");
        await Tick();
        Expect(!documentTab.Text.StartsWith("●", StringComparison.Ordinal), "saving clears the unsaved mark in the document tab, got " + documentTab.Text);
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
        var partMode = editor.FindChild("PartModeToggle", true, false) as Button;
        Expect(partMode is not null, "character editor exposes the part-pixel mode toggle");
        partMode!.ButtonPressed = true;
        await Tick();
        Expect(canvasControl!.Mode == NativeCharacterCanvasMode.Part, "the part-pixel toggle selects the part canvas");
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
        await Shot("character-base-overall", editor);
        editor.SetReferenceForAudit(true, 0.45f); await Tick();
        var pixelPanel = editor.FindChild("像素画布", true, false) as NativeCharacterPixelCanvas;
        Expect(pixelPanel is not null && pixelPanel.ShowReference, "the reference overlay toggles on");
        await Shot("character-base-reference", editor);
        GD.Print("PLAY_AUDIT step=character-editor parts=", document.Definition.Parts.Count, " painted=", drawn, " dirty=", document.Dirty, " path=", saved);
        editor.QueueFree(); await Tick();
    }
}