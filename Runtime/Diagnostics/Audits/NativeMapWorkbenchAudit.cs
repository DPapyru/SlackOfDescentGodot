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
        bool previous = panels["layers"].Visible; TogglePanel("layers"); Check(panels["layers"].Visible != previous && ((Control)panels["layers"].GetParent()).SizeFlagsVertical == Control.SizeFlags.Fill, "collapsed panel releases its expansion space");
        Check(File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains("layers", StringComparison.Ordinal), "panel layout persists to the workbench settings file"); TogglePanel("layers");
        var shortcut = new InputEventKey { Keycode = Key.K, CtrlPressed = true, AltPressed = true }; Check(ShortcutText(shortcut) == "Ctrl+Alt+K", "shortcut modifiers normalize consistently");
        MapCanvas.Zoom = 1; MapCanvas.Center(new(4000, 320)); await Tick(); minimap.Navigate(minimap.Size / 2); Check(Math.Abs(MapCanvas.Pan.X + MapCanvas.Size.X / 2 - 4000) < 2, "minimap click navigates to the room center");
        MapCanvas.Center(new(4000, 320)); minimap._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = minimap.Size / 2 });
        var minimapPan = MapCanvas.Pan;
        minimap._GuiInput(new InputEventMouseMotion { Position = minimap.Size / 2 + new Vector2(12, 0), ButtonMask = MouseButtonMask.Left });
        minimap._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = minimap.Size / 2 + new Vector2(12, 0) });
        Check(MapCanvas.Pan.X > minimapPan.X + .01f, "dragging inside the minimap pans the canvas");
        MapCanvas.Zoom = 1; MapCanvas.Center(new(1000, 320)); var pan = MapCanvas.Pan;
        MapCanvas._GuiInput(new InputEventKey { Keycode = Key.Space, Pressed = true });
        MapCanvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Position = new(200, 100), Pressed = true });
        MapCanvas._GuiInput(new InputEventMouseMotion { Position = new(180, 100), Relative = new(-20, 0), ButtonMask = MouseButtonMask.Left });
        MapCanvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Position = new(180, 100), Pressed = false });
        MapCanvas._GuiInput(new InputEventKey { Keycode = Key.Space, Pressed = false });
        Check(MapCanvas.Pan.X > pan.X, "space dragging temporarily pans with paint tool active");
        Check(commandLabels["tool.0"] == "工具：选择与移动" && commandLabels["tool.6"] == "工具：缩放", "tool menu labels use display names");
        var menuBar = GetNode<HBoxContainer>("EditorLayout/MapMenus");
        var editPopup = ((MenuButton)menuBar.GetChild(1)).GetPopup(); editPopup.EmitSignal(PopupMenu.SignalName.AboutToPopup);
        Check(!editPopup.IsItemDisabled(editPopup.GetItemIndex(0)), "undo menu item is enabled with history");
        Select(null, false); editPopup.EmitSignal(PopupMenu.SignalName.AboutToPopup);
        Check(editPopup.IsItemDisabled(editPopup.GetItemIndex(3)), "duplicate menu item is greyed out without a selection");
        var viewPopup = ((MenuButton)menuBar.GetChild(2)).GetPopup(); viewPopup.EmitSignal(PopupMenu.SignalName.AboutToPopup);
        Check(viewPopup.IsItemChecked(viewPopup.GetItemIndex(6)) == tilesToggle.ButtonPressed, "view menu check mirrors the tile layer");
        var scratch = Document.Clone(); scratch.Instances.Clear(); scratch.Tiles.Clear(); scratch.Room["width"] = 640; scratch.Room["height"] = 480; SetDocument(scratch, null);
        SelectTool((int)NativeMapTool.Tile); sourceX.Value = sourceY.Value = 0; tileWidth.Value = tileHeight.Value = 32;
        PaintAt(new(64, 64), false); Changed(); PaintAt(new(65, 65), false); Changed();
        Check(Document.Tiles.Count == 1, "same cell and depth replaces the tile instead of stacking");
        SelectTool((int)NativeMapTool.Collision); PaintAt(new(128, 64), false); PaintAt(new(129, 65), false); Changed();
        Check(Document.Instances.Count == 1 && world.Objects[Int(Document.Instances[0]!.AsObject(), "object")].GetProperty("solid").GetBoolean(), "collision tool deduplicates the same cell with a solid object");
        SelectTool((int)NativeMapTool.Erase); PaintAt(new(65, 65), false); Changed();
        Check(Document.Tiles.Count == 0, "erase tool removes the painted tile");
        SelectTool((int)NativeMapTool.Entity); PaintAt(new(192, 64), false); Changed();
        Check(Document.Instances.Count == 2 && selected is not null, "entity tool places and selects an entity");
        var dragged = selected!; double dragStartX = Number(dragged, "x"); Vector2 dragFrom = new((float)dragStartX, (float)Number(dragged, "y"));
        SelectTool((int)NativeMapTool.Select); Select(dragged, false);
        Vector2 pressAt = MapCanvas.ToCanvas(dragFrom), dragTo = MapCanvas.ToCanvas(dragFrom + new Vector2(13, 0));
        MapCanvas._GuiInput(new InputEventMouseButton { Position = pressAt, ButtonIndex = MouseButton.Left, Pressed = true });
        MapCanvas._GuiInput(new InputEventMouseMotion { Position = dragTo, Relative = dragTo - pressAt, CtrlPressed = true, ButtonMask = MouseButtonMask.Left });
        MapCanvas._GuiInput(new InputEventMouseButton { Position = dragTo, ButtonIndex = MouseButton.Left, Pressed = false });
        Check(Math.Abs(Number(dragged, "x") - dragStartX - 13) < .01, "ctrl suspends snapping while dragging the selection");
        Select(null, false);
        Check(!propAngle.Editable && !properties.Editable, "inspector disables angle and JSON without a selection");
        Check(selectionStatus.Text == L("未选择", "None"), "status bar reports no selection");
        SelectTool((int)NativeMapTool.Tile); PaintAt(new(96, 96), false); Changed();
        Select(Document.Tiles[0]!.AsObject(), true);
        Check(!propAngle.Editable && properties.Editable, "angle is disabled for tiles while JSON stays editable");
        Select(Document.Instances[0]!.AsObject(), false);
        Check(propAngle.Editable && properties.Editable, "entity selection enables angle and JSON");
        Check(selectionStatus.Text == L("实体", "Entity") + " #" + Int(Document.Instances[0]!.AsObject(), "id"), "status bar names the selected entity");
        Select(Document.Tiles[0]!.AsObject(), true);
        Check(selectionStatus.Text == L("图块", "Tile") + " #" + Int(Document.Tiles[0]!.AsObject(), "id") && !propAngle.Editable, "status bar names the selected tile");
        MapCanvas.Zoom = 1.5f;
        Check(zoomInput.Text == "150%", "status bar mirrors the current zoom");
        zoomInput.EmitSignal(LineEdit.SignalName.TextSubmitted, "200");
        Check(Math.Abs(MapCanvas.Zoom - 2) < .001 && zoomInput.Text == "200%", "typing a percentage in the status bar zooms around the canvas center");
        Check(assetTabs.GetTabTitle(0).StartsWith("图集", StringComparison.Ordinal) && assetTabs.GetTabTitle(0).Contains(Document.Tiles.Count.ToString(), StringComparison.Ordinal) && assetTabs.GetTabTitle(1).Contains(Document.Instances.Count.ToString(), StringComparison.Ordinal), "asset tabs carry the live tile and entity counts");
        Check(status.Text.Contains(Document.Name, StringComparison.Ordinal) && status.Text.Contains("×", StringComparison.Ordinal) && status.Text.Contains(" *", StringComparison.Ordinal) && status.Text.Contains(Document.Instances.Count.ToString(), StringComparison.Ordinal), "status bar shows document, size, counts and the unsaved mark");
        MapCanvas._GuiInput(new InputEventMouseMotion { Position = MapCanvas.ToCanvas(new(512, 96)) });
        Check(pointerStatus.Text.Contains("16, 3", StringComparison.Ordinal), "pointer status shows room and cell coordinates");
        var propActionsRow = (HBoxContainer)applyButton.GetParent(); var propsScroll = panels["props"].GetChild<ScrollContainer>(0);
        Check(propActionsRow.GetParent() == panels["props"] && propActionsRow.GetGlobalRect().Position.Y >= propsScroll.GetGlobalRect().End.Y - 1, "property action buttons stay pinned under the scrolling form");
        atlas.AtlasScale = 2; atlas.Refresh();
        Check(Math.Abs(atlas.CustomMinimumSize.X - atlas.Atlas!.GetWidth() * 2) < 1, "atlas zoom scales the drawn atlas");
        atlas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = new(70, 134) });
        Check(sourceX.Value == 32 && sourceY.Value == 64, "zoomed atlas keeps the 32 pixel cell mapping");
        atlas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = new(70, 134) });
        atlas.Fit();
        Check(atlas.AtlasScale <= 1 && atlas.CustomMinimumSize.X <= Math.Max(200, atlas.GetParent<Control>().Size.X) + 1, "atlas fit keeps the atlas inside the asset panel");
        CloseDocument(); SelectTool(1); tilesToggle.ButtonPressed = false; backgrounds.EmitSignal(OptionButton.SignalName.ItemSelected, backgrounds.Selected); RunMapAction("map.events");
        Check(Document is null && MapCanvas.Document is null, "closed document tolerates tool and palette signals"); tilesToggle.ButtonPressed = true;
        SetDocument(original, null); MapCanvas.Zoom = originalZoom; MapCanvas.Pan = originalPan; MapCanvas.QueueRedraw(); SelectTool(0); await Tick();
        GD.Print("MAP_WORKBENCH_OK checks=", checks); return checks;
    }
}
