using Godot;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeMapEditor
{
    private async Task<int> RunWorkbenchAudit()
    {
        int checks = 0; void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Map workbench: " + message); }
        async Task Tick() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        var original = Document.Clone(); float originalZoom = MapCanvas.Zoom; var originalPan = MapCanvas.Pan;
        Check(toolButtons.Count == 8 && toolOptions.Count == 8, "all eight tools have buttons and options");
        for (int i = 0; i < 8; i++) { SelectTool(i); Check((int)MapCanvas.Tool == i && toolOptions[(NativeMapTool)i].Visible && toolButtons[i].ButtonPressed, "tool selection synchronizes the workbench"); }
        int tiles = Document.Tiles.Count;
        SelectTool((int)NativeMapTool.Zoom); float zoom = MapCanvas.Zoom;
        MapCanvas._GuiInput(new InputEventMouseButton { Position = MapCanvas.Size / 2, ButtonIndex = MouseButton.Left, Pressed = true });
        Check(MapCanvas.Zoom > zoom && Document.Tiles.Count == tiles, "zoom tool changes only the view");
        SelectTool((int)NativeMapTool.Tile); sourceX.Value = sourceY.Value = 0; tileWidth.Value = tileHeight.Value = 32;
        Remember(); PaintAt(new(400, 96), false); Changed(); var tile = Document.Tiles.OfType<JsonObject>().Last(); int tileDepth = Int(tile, "depth");
        MapCanvas.HiddenDepths.Add(tileDepth); Check(MapCanvas.Hit(new(401, 97)).Item != tile, "hidden depth cannot be selected"); MapCanvas.HiddenDepths.Remove(tileDepth);
        SelectTool((int)NativeMapTool.Eyedrop); SampleTile(new(401, 97), true); Check(MapCanvas.Tool == NativeMapTool.Tile && (int)depth.Value == tileDepth, "Alt eyedropper samples existing tile settings");
        int count = undo.Count; GoHistory(count - 1); Check(Document.Tiles.Count == tiles, "history navigation restores document"); GoHistory(count); Check(Document.Tiles.Count == tiles + 1, "history navigation redoes document");
        bool previous = panels["layers"].Visible; TogglePanel("layers"); Check(panels["layers"].Visible != previous && ((Control)panels["layers"].GetParent()).SizeFlagsVertical == Control.SizeFlags.Fill, "collapsed panel releases its expansion space"); TogglePanel("layers");
        var shortcut = new InputEventKey { Keycode = Key.K, CtrlPressed = true, AltPressed = true }; Check(ShortcutText(shortcut) == "Ctrl+Alt+K", "shortcut modifiers normalize consistently");
        MapCanvas.Zoom = 1; MapCanvas.Center(new(4000, 320)); await Tick(); minimap.Navigate(minimap.Size / 2); Check(Math.Abs(MapCanvas.Pan.X + MapCanvas.Size.X / 2 - 4000) < 2, "minimap click navigates to the room center");
        MapCanvas.Zoom = 1; MapCanvas.Center(new(1000, 320)); var pan = MapCanvas.Pan;
        MapCanvas._GuiInput(new InputEventKey { Keycode = Key.Space, Pressed = true });
        MapCanvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Position = new(200, 100), Pressed = true });
        MapCanvas._GuiInput(new InputEventMouseMotion { Position = new(180, 100), Relative = new(-20, 0), ButtonMask = MouseButtonMask.Left });
        MapCanvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Position = new(180, 100), Pressed = false });
        MapCanvas._GuiInput(new InputEventKey { Keycode = Key.Space, Pressed = false });
        Check(MapCanvas.Pan.X > pan.X, "space dragging temporarily pans with paint tool active");
        CloseDocument(); SelectTool(1); tilesToggle.ButtonPressed = false; backgrounds.EmitSignal(OptionButton.SignalName.ItemSelected, backgrounds.Selected); RunMapAction("map.events");
        Check(Document is null && MapCanvas.Document is null, "closed document tolerates tool and palette signals"); tilesToggle.ButtonPressed = true;
        SetDocument(original, null); MapCanvas.Zoom = originalZoom; MapCanvas.Pan = originalPan; MapCanvas.QueueRedraw(); SelectTool(0); await Tick();
        GD.Print("MAP_WORKBENCH_OK checks=", checks); return checks;
    }
}
