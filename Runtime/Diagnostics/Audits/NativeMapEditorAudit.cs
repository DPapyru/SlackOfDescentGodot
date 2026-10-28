using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeMapEditor
{
    public async Task RunUiAudit(string directory)
    {
        Directory.CreateDirectory(directory); int checks = 0;
        void Expect(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Map UI: " + message); }
        async Task Tick() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        T Find<T>(string name) where T : Node => (T)FindChild(name, true, false);
        void Mouse(Vector2 position, bool down) => PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = down, ButtonMask = down ? MouseButtonMask.Left : 0 }, true);
        async Task Click(Control control)
        {
            control.GrabFocus(); PushInput(new InputEventKey { Keycode = Godot.Key.Space, Pressed = true }, true); await Tick(); PushInput(new InputEventKey { Keycode = Godot.Key.Space, Pressed = false }, true); await Tick();
        }
        async Task CanvasClick(Vector2 room)
        {
            Vector2 point = MapCanvas.GlobalPosition + MapCanvas.ToCanvas(room); Mouse(point, true); await Tick(); Mouse(point, false); await Tick();
        }
        async Task ChooseTool(int tool)
        {
            toolChoice.Select(tool); toolChoice.EmitSignal(OptionButton.SignalName.ItemSelected, tool); await Tick();
        }
        async Task TapKey(Key key, bool ctrl = false)
        {
            PushInput(new InputEventKey { Keycode = key, Pressed = true, CtrlPressed = ctrl }, true); await Tick(); PushInput(new InputEventKey { Keycode = key, Pressed = false, CtrlPressed = ctrl }, true); await Tick();
        }
        await Tick(); Expect(Document.Instances.Count == 1227 && Document.Tiles.Count == 1647, "all original content loaded in rendered editor");
        MapCanvas.Zoom = .7f; MapCanvas.Pan = new(0, 0); MapCanvas.QueueRedraw(); await Tick();
        await ChooseTool(1); int tilesBefore = Document.Tiles.Count;
        await CanvasClick(new(400, 96)); Expect(Document.Tiles.Count == tilesBefore + 1, "tile drawing from routed mouse event");
        await ChooseTool(3); int entitiesBefore = Document.Instances.Count;
        await CanvasClick(new(448, 96)); Expect(Document.Instances.Count == entitiesBefore + 1, "collision drawing from routed mouse event");
        var collision = Document.Instances.OfType<JsonObject>().Last(); Expect(world.Objects[Int(collision, "object")].GetProperty("solid").GetBoolean(), "collision tool places solid object");
        await ChooseTool(4); await CanvasClick(new(512, 96)); Expect(Document.Instances.Count == entitiesBefore + 2, "entity placement");
        var placed = Document.Instances.OfType<JsonObject>().Last(); int placedId = Int(placed, "id");
        await ChooseTool(0); await CanvasClick(new(512, 96)); Expect(selected == placed, "entity selection");
        Vector2 start = MapCanvas.GlobalPosition + MapCanvas.ToCanvas(new(512, 96)), end = MapCanvas.GlobalPosition + MapCanvas.ToCanvas(new(576, 128));
        Mouse(start, true); await Tick(); PushInput(new InputEventMouseMotion { Position = end, GlobalPosition = end, Relative = end - start, ButtonMask = MouseButtonMask.Left }, true); await Tick(); Mouse(end, false); await Tick();
        Expect(Number(placed, "x") == 576 && Number(placed, "y") == 128, "entity drag snaps to grid");
        propX.GetLineEdit().GrabFocus(); await TapKey(Key.A, true); foreach (char c in "608") PushInput(new InputEventKey { Unicode = c, Pressed = true }, true); await TapKey(Key.Enter);
        Expect(Number(placed, "x") == 608, "inspector coordinate edit");
        properties.Text = "{\"editorAudit\": 73, \"label\": \"中文属性\"}"; await Click(Find<Button>("ApplyProperties")); Expect(Int(placed["properties"]!.AsObject(), "editorAudit") == 73, "property apply button");
        await Click(Find<Button>("Undo")); Expect(placed["properties"] is not null, "undo button handled");
        await Click(Find<Button>("Redo")); placed = Document.Instances.OfType<JsonObject>().Single(i => Int(i, "id") == placedId); Expect(Int(placed["properties"]!.AsObject(), "editorAudit") == 73, "redo restores custom properties");
        string path = Path.Combine(directory, "中文 保存目录", "渲染 地图.map.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Click(Find<Button>("SaveMapAs")); await Tick(); Expect(fileDialog.UseNativeDialog && fileDialog.FileMode == FileDialog.FileModeEnum.SaveFile, "system save dialog requested");
        string? external = System.Environment.GetEnvironmentVariable("SLACK_MAP_NATIVE_DIALOG");
        if (external == "1")
        {
            DisplayServer.ClipboardSet(path); GD.Print("MAP_NATIVE_SAVE_READY ", path); await WaitFor(() => FilePath == path, 45); Expect(File.Exists(path), "OS native save file selection");
        }
        else { fileDialog.Hide(); fileDialog.EmitSignal(FileDialog.SignalName.FileSelected, path); await Tick(); }
        Expect(!Dirty && File.Exists(path), "save dialog selection writes Unicode path atomically");
        await ChooseTool(2); await CanvasClick(new(608, 128)); Expect(Document.Instances.Count == entitiesBefore + 1 && Dirty, "erase tool removes placed entity");
        await Click(Find<Button>("OpenMap")); await Tick(); Expect(unsaved.Visible, "unsaved edits prompt before open"); unsaved.EmitSignal(AcceptDialog.SignalName.CustomAction, "discard"); await Tick();
        Expect(fileDialog.FileMode == FileDialog.FileModeEnum.OpenFile && fileDialog.UseNativeDialog, "system open dialog requested");
        if (external == "1") { DisplayServer.ClipboardSet(path); GD.Print("MAP_NATIVE_OPEN_READY ", path); await WaitFor(() => !Dirty && Document.Instances.Count == entitiesBefore + 2, 45); }
        else { fileDialog.Hide(); fileDialog.EmitSignal(FileDialog.SignalName.FileSelected, path); await Tick(); }
        Expect(Document.Instances.Count == entitiesBefore + 2 && Document.Tiles.Count == tilesBefore + 1 && !Dirty, "saved content reloads through dialog");
        placed = Document.Instances.OfType<JsonObject>().Single(i => Int(i, "id") == placedId); Expect(Number(placed, "x") == 608 && Int(placed["properties"]!.AsObject(), "editorAudit") == 73, "loaded entity position and custom props");
        SavePath(path, false); Expect(overwrite.Visible, "existing target requires overwrite confirmation"); overwrite.EmitSignal(ConfirmationDialog.SignalName.Confirmed); overwrite.Hide(); await Tick();
        await Click(Find<Button>("PlayMap")); await WaitFor(() => Visible, 30); Expect(world.Instances.Any(i => i.Id == placedId && !i.Destroyed), "playtest reconstructs placed entity");
        var actor = world.Instances.Single(i => i.Id == placedId); Expect(actor.Scope.ReadOr("editorAudit").Number == 73, "playtest applies saved property");
        Expect(Visible && Document.Instances.Count == entitiesBefore + 2, "return from playtest retains editor document");
        File.WriteAllText(Path.Combine(directory, "ui.json"), JsonSerializer.Serialize(new { checks, nativeDialog = external == "1", entities = Document.Instances.Count, tiles = Document.Tiles.Count, path, passed = true }, NativeContentStore.Json));
        GD.Print("MAP_UI_OK checks=", checks, " nativeDialog=", external == "1");
    }
    private async Task WaitFor(Func<bool> predicate, double timeout)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!predicate()) { if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds > timeout) throw new TimeoutException("地图 UI 审计等待超时。"); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }
}
