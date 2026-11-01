using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeMapEditor
{
    private readonly Dictionary<string, Control> panels = new();
    private readonly HashSet<string> expandingPanels = new();
    private readonly Dictionary<string, Action> commands = new();
    private readonly Dictionary<string, string> commandLabels = new();
    private readonly Dictionary<string, string> shortcuts = new();
    private readonly Dictionary<string, string> defaultShortcuts = new();
    private readonly Dictionary<int, Button> toolButtons = new();
    private readonly Dictionary<NativeMapTool, HBoxContainer> toolOptions = new();
    private ItemList layerList = null!, historyList = null!;
    private Label documentTab = null!, historyCount = null!;
    private LineEdit zoomInput = null!;
    private CheckBox snapToggle = null!;
    private TabContainer assetTabs = null!;
    private HSplitContainer assetSplit = null!, dockSplit = null!;
    private NativeMapMinimap minimap = null!;
    private Button currentAtlas = null!, previousAtlas = null!;
    private int previousBackground = -1, displayedBackground = -1;
    private bool workbenchSyncPending;
    private string SettingsPath => Path.Combine(store.Root, "Editor", "map-editor.json");
    private sealed class WorkbenchSettings
    {
        public Dictionary<string, bool> Panels { get; set; } = new();
        public Dictionary<string, string> Shortcuts { get; set; } = new();
        public int AssetWidth { get; set; } = 228;
        public int CanvasWidth { get; set; } = 650;
    }
    private void BuildWorkbench()
    {
        var windowBackground = new ColorRect { Color = new Color("323232"), MouseFilter = Control.MouseFilterEnum.Ignore }; AddChild(windowBackground); windowBackground.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var root = new VBoxContainer { Name = "EditorLayout" }; AddChild(root); root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); root.AddThemeConstantOverride("separation", 1);
        RegisterCommands();
        var menuBar = new HBoxContainer { Name = "MapMenus", CustomMinimumSize = new(0, 28) }; root.AddChild(menuBar);
        AddMenu(menuBar, "文件", ["file.new", "file.open", "file.save", "file.saveAs", "", "file.import", "file.library", "file.close"]);
        AddMenu(menuBar, "编辑", ["edit.undo", "edit.redo", "", "edit.duplicate", "edit.delete", "edit.cancel", "", "help.editKeys"]);
        AddMenu(menuBar, "视图", ["view.zoomIn", "view.zoomOut", "view.actual", "view.fit", "view.spawn", "", "view.tiles", "view.entities", "view.solid", "view.grid"]);
        AddMenu(menuBar, "地图", ["map.settings", "map.events", "map.play"]);
        AddMenu(menuBar, "窗口", ["panel.assets", "panel.layers", "panel.props", "panel.history", "layout.reset"]);
        AddMenu(menuBar, "帮助", ["help.shortcuts", "help.editKeys", "help.about"]);
        menuBar.AddChild(new Label { Text = "SlackOfDescent · 地图编辑器", HorizontalAlignment = HorizontalAlignment.Right, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Modulate = new Color("808080") });
        var tabs = new HBoxContainer { Name = "MapDocumentTabs", CustomMinimumSize = new(0, 28) }; root.AddChild(tabs);
        documentTab = new Label { Name = "DocumentTab", AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; tabs.AddChild(documentTab);
        Button(tabs, "×", "CloseMapDocument", () => RunMapAction("file.close")); Button(tabs, "+", "NewMapDocument", () => RunMapAction("file.new"));
        var options = new HBoxContainer { Name = "MapToolOptions", CustomMinimumSize = new(0, 34) }; root.AddChild(options);
        HBoxContainer Group(NativeMapTool tool) { var row = new HBoxContainer(); options.AddChild(row); toolOptions[tool] = row; return row; }
        var selectOptions = Group(NativeMapTool.Select); Button(selectOptions, "复制", "DuplicateSelection", () => DuplicateSelection()); Button(selectOptions, "删除", "DeleteSelectionOption", DeleteSelection); Button(selectOptions, "取消选择", "ClearSelection", () => Select(null, false));
        var tileOptions = Group(NativeMapTool.Tile);
        sourceX = CompactField(tileOptions, "源 X", "TileSourceX", 0, 16384, 0); sourceY = CompactField(tileOptions, "Y", "TileSourceY", 0, 16384, 0);
        tileWidth = CompactField(tileOptions, "宽", "TileWidth", 1, 4096, 32); tileHeight = CompactField(tileOptions, "高", "TileHeight", 1, 4096, 32); depth = CompactField(tileOptions, "深度", "TileDepth", -1000000, 1000000, 10000);
        Group(NativeMapTool.Erase).AddChild(new Label { Text = "拖动连续擦除 · 右键临时擦除" });
        Group(NativeMapTool.Collision).AddChild(new Label { Text = "使用选中碰撞实体 · 按网格连续绘制" });
        Group(NativeMapTool.Entity).AddChild(new Label { Text = "从素材面板选择实体，点击放置" });
        Group(NativeMapTool.Pan).AddChild(new Label { Text = "拖动移动视野 · 空格 / 中键临时移动" });
        Group(NativeMapTool.Zoom).AddChild(new Label { Text = "点击放大 · Alt / 右键缩小" });
        Group(NativeMapTool.Eyedrop).AddChild(new Label { Text = "取当前图集格 · Alt 点击已有图块取样" });
        options.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        snapToggle = new CheckBox { Text = "吸附", Name = "GridSnap", ButtonPressed = true }; options.AddChild(snapToggle);
        grid = CompactField(options, "网格", "GridSize", 1, 128, 32);
        undoButton = new Button { Text = "↶", Name = "Undo", TooltipText = "撤销" }; options.AddChild(undoButton); undoButton.Pressed += Undo;
        redoButton = new Button { Text = "↷", Name = "Redo", TooltipText = "重做" }; options.AddChild(redoButton); redoButton.Pressed += Redo;
        var body = new HBoxContainer { Name = "MapWorkbench", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; root.AddChild(body); body.AddThemeConstantOverride("separation", 1);
        var rail = new VBoxContainer { Name = "MapToolRail", CustomMinimumSize = new(42, 0) }; body.AddChild(rail);
        var symbols = new[] { "↖", "▦", "▱", "▣", "♟", "✥", "⌕", "◩" };
        var toolNames = new[] { "选择与移动", "图块画笔", "橡皮", "碰撞区块", "放置实体", "移动视野", "缩放", "吸管" };
        toolChoice = new OptionButton { Name = "MapTool", Visible = false }; rail.AddChild(toolChoice);
        for (int i = 0; i < toolNames.Length; i++)
        {
            int tool = i; toolChoice.AddItem(toolNames[i]); var button = new Button { Text = symbols[i], Name = "Tool" + (NativeMapTool)i, ToggleMode = true, CustomMinimumSize = new(32, 28), TooltipText = toolNames[i] }; rail.AddChild(button); toolButtons[i] = button; button.Pressed += () => SelectTool(tool);
        }
        currentAtlas = new Button { Name = "CurrentAtlas", CustomMinimumSize = new(28, 28), ExpandIcon = true, TooltipText = "当前图集" }; rail.AddChild(currentAtlas); currentAtlas.Pressed += () => SelectTool((int)NativeMapTool.Tile);
        previousAtlas = new Button { Name = "PreviousAtlas", CustomMinimumSize = new(28, 28), ExpandIcon = true, TooltipText = "上一个图集" }; rail.AddChild(previousAtlas);
        previousAtlas.Pressed += () => SelectBackground(previousBackground);
        toolChoice.ItemSelected += index => { MapCanvas.Tool = (NativeMapTool)index; SyncToolOptions(); UpdateStatus(); };
        assetSplit = new HSplitContainer { Name = "MapAssetSplit", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SplitOffsets = [228] }; body.AddChild(assetSplit);
        var assets = new VBoxContainer { Name = "MapAssets", CustomMinimumSize = new(190, 0) }; assetSplit.AddChild(assets); panels["assets"] = assets;
        var assetHead = new HBoxContainer(); assets.AddChild(assetHead); assetHead.AddChild(new Label { Text = "素材", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }); Button(assetHead, "−", "CollapseAssets", () => TogglePanel("assets"));
        assetTabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; assets.AddChild(assetTabs);
        var tilePanel = new VBoxContainer { Name = "图块" }; assetTabs.AddChild(tilePanel);
        backgrounds = new OptionButton { Name = "TileBackground", AutoTranslateMode = AutoTranslateModeEnum.Disabled }; tilePanel.AddChild(backgrounds);
        foreach (var background in world.Manifest.GetProperty("backgrounds").EnumerateArray()) backgrounds.AddItem(background.GetProperty("name").GetString()!, background.GetProperty("id").GetInt32());
        int defaultBackground = Array.FindIndex(world.Manifest.GetProperty("backgrounds").EnumerateArray().ToArray(), b => b.GetProperty("id").GetInt32() == 20); if (defaultBackground >= 0) backgrounds.Select(defaultBackground);
        BuildAtlas(tilePanel);
        tilePanel.AddChild(new Label { Text = "拖动图集选择区域", Modulate = new Color("9d9d9d") });
        var entityPanel = new VBoxContainer { Name = "实体" }; assetTabs.AddChild(entityPanel);
        entityFilter = new LineEdit { Name = "EntityFilter", PlaceholderText = "搜索实体／扩展" }; entityPanel.AddChild(entityFilter);
        entities = new ItemList { Name = "EntityPalette", AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; entityPanel.AddChild(entities);
        entityFilter.TextChanged += PopulateEntities; BuildEntityPreview(entityPanel); PopulateEntities("");
        dockSplit = new HSplitContainer { Name = "MapDockSplit", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SplitOffsets = [650] }; assetSplit.AddChild(dockSplit);
        var stage = new VBoxContainer { Name = "MapStage", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; dockSplit.AddChild(stage);
        var viewBar = new HBoxContainer(); stage.AddChild(viewBar);
        Button(viewBar, "适应", "FitRoom", () => MapCanvas.Fit()); Button(viewBar, "100%", "ActualSize", () => RunMapAction("view.actual"));
        spawnCenterButton = new Button { Text = "出生点", Name = "SpawnCenter" }; viewBar.AddChild(spawnCenterButton); spawnCenterButton.Pressed += CenterOnSpawn;
        tilesToggle = new CheckBox { Text = "图块", Name = "ShowTiles", ButtonPressed = true }; viewBar.AddChild(tilesToggle);
        entitiesToggle = new CheckBox { Text = "实体", Name = "ShowEntities", ButtonPressed = true }; viewBar.AddChild(entitiesToggle);
        solidToggle = new CheckBox { Text = "碰撞", Name = "ShowSolid", ButtonPressed = true }; viewBar.AddChild(solidToggle);
        var canvasHost = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(260, 220) }; stage.AddChild(canvasHost);
        MapCanvas = new NativeMapCanvas(world) { Name = "MapCanvas", ClipContents = true }; canvasHost.AddChild(MapCanvas); MapCanvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        minimap = new NativeMapMinimap { Name = "MapMinimap", Canvas = MapCanvas }; canvasHost.AddChild(minimap); minimap.SetAnchorsPreset(Control.LayoutPreset.BottomRight); minimap.OffsetLeft = -172; minimap.OffsetTop = -112; minimap.OffsetRight = -12; minimap.OffsetBottom = -12;
        MapCanvas.EditStarted = Remember; MapCanvas.Edited = Changed; MapCanvas.Selected = Select; MapCanvas.PaintAt = PaintAt;
        MapCanvas.PointerMoved = point => { pointerRoom = point; UpdatePointerStatus(); }; MapCanvas.ViewChanged = () => { UpdatePointerStatus(); minimap.QueueRedraw(); };
        MapCanvas.SampleRequested = SampleTile; snapToggle.Toggled += value => MapCanvas.GridSnap = value;
        grid.ValueChanged += value => { MapCanvas.GridSize = (float)value; MapCanvas.QueueRedraw(); };
        tilesToggle.Toggled += on => { MapCanvas.ShowTiles = on; if (!on && selectedTile) Select(null, false); MapCanvas.QueueRedraw(); QueueWorkbenchSync(); };
        entitiesToggle.Toggled += on => { MapCanvas.ShowEntities = on; if (!on && !selectedTile) Select(null, false); MapCanvas.QueueRedraw(); QueueWorkbenchSync(); };
        solidToggle.Toggled += on => { MapCanvas.ShowSolidOverlay = on; MapCanvas.QueueRedraw(); };
        stage.AddChild(new Label { Text = "空格 / 中键拖动 · 滚轮滚动 · Ctrl+滚轮缩放 · 小地图跳转", Modulate = new Color("808080"), ClipText = true });
        var dock = new VBoxContainer { Name = "MapDock", CustomMinimumSize = new(240, 0) }; dockSplit.AddChild(dock);
        var layers = DockPanel(dock, "图层", "layers", true); layerList = new ItemList { Name = "MapLayerList", CustomMinimumSize = new(0, 90), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; layers.AddChild(layerList);
        layerList.ItemSelected += ChooseLayer; layerList.ItemActivated += index => ToggleLayer(index);
        Button(layers, "显示 / 隐藏所选层", "ToggleSelectedMapLayer", () => { if (layerList.GetSelectedItems().Length > 0) ToggleLayer(layerList.GetSelectedItems()[0]); });
        var inspector = DockPanel(dock, "属性", "props", true);
        var propScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; inspector.AddChild(propScroll);
        var propBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; propScroll.AddChild(propBody);
        selectedTitle = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Name = "SelectionTitle" }; propBody.AddChild(selectedTitle);
        propX = Field(propBody, "横坐标 X", "PropertyX", -1000000, 1000000, 0); propY = Field(propBody, "纵坐标 Y", "PropertyY", -1000000, 1000000, 0);
        propScaleX = Field(propBody, "横向缩放", "PropertyScaleX", -1000, 1000, 1, .1); propScaleY = Field(propBody, "纵向缩放", "PropertyScaleY", -1000, 1000, 1, .1); propAngle = Field(propBody, "旋转角度", "PropertyAngle", -36000, 36000, 0);
        foreach (var field in new[] { propX, propY, propScaleX, propScaleY, propAngle }) field.ValueChanged += _ => ApplyBasicProperties();
        propertyForm = new VBoxContainer { Name = "EntityPropertyForm" }; propBody.AddChild(propertyForm); propBody.AddChild(new Label { Text = "高级属性 JSON" });
        properties = new TextEdit { Name = "EntityProperties", AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new(0, 100), Text = "{}" }; propBody.AddChild(properties);
        var propActions = new HBoxContainer(); propBody.AddChild(propActions);
        applyButton = new Button { Text = "应用", Name = "ApplyProperties" }; propActions.AddChild(applyButton); applyButton.Pressed += ApplyCustomProperties;
        rawButton = new Button { Text = "完整数据…", Name = "EditRawSelection" }; propActions.AddChild(rawButton); rawButton.Pressed += OpenRawSelection;
        deleteButton = new Button { Text = "删除", Name = "DeleteSelection" }; propActions.AddChild(deleteButton); deleteButton.Pressed += DeleteSelection;
        var history = DockPanel(dock, "历史", "history", false); historyCount = new Label(); history.AddChild(historyCount);
        historyList = new ItemList { Name = "MapHistoryList", CustomMinimumSize = new(0, 110) }; history.AddChild(historyList); historyList.ItemSelected += GoHistory;
        var footer = new HBoxContainer { Name = "StatusBar", CustomMinimumSize = new(0, 24) }; root.AddChild(footer);
        zoomInput = new LineEdit { Name = "MapZoom", Text = "100%", CustomMinimumSize = new(58, 0) }; footer.AddChild(zoomInput);
        zoomInput.TextSubmitted += text => { if (float.TryParse(text.TrimEnd('%'), out float value) && value > 0) MapCanvas.ZoomAt(MapCanvas.Size / 2, value / 100 / MapCanvas.Zoom); UpdatePointerStatus(); };
        Button(footer, "−", "ZoomOut", () => RunMapAction("view.zoomOut")); Button(footer, "+", "ZoomIn", () => RunMapAction("view.zoomIn"));
        status = new Label { Name = "MapStatus", AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true }; footer.AddChild(status);
        pointerStatus = new Label { Name = "MapPointerStatus", AutoTranslateMode = AutoTranslateModeEnum.Disabled }; footer.AddChild(pointerStatus);
        SyncToolOptions();
    }
    private static SpinBox CompactField(Node parent, string label, string name, double min, double max, double value)
    {
        parent.AddChild(new Label { Text = label }); var input = new SpinBox { Name = name, MinValue = min, MaxValue = max, Value = value, CustomMinimumSize = new(58, 0), UpdateOnTextChanged = false }; parent.AddChild(input); return input;
    }
    private VBoxContainer DockPanel(Node parent, string title, string key, bool expand)
    {
        var frame = new VBoxContainer { Name = "MapPanel_" + key, SizeFlagsVertical = expand ? Control.SizeFlags.ExpandFill : Control.SizeFlags.Fill }; parent.AddChild(frame);
        var header = new HBoxContainer(); frame.AddChild(header); header.AddChild(new Label { Text = "▾ " + title, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var content = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; frame.AddChild(content); panels[key] = content; if (expand) expandingPanels.Add(key);
        Button(header, "−", "Collapse" + key, () => TogglePanel(key)); return content;
    }
    private void RegisterCommands()
    {
        void Add(string id, string label, string keys, Action action) { commands[id] = action; commandLabels[id] = label; if (keys.Length > 0) shortcuts[id] = defaultShortcuts[id] = keys; }
        Add("file.new", "新建（导入原版房间）", "", OpenOriginalRooms); Add("file.import", "导入原版房间…", "", OpenOriginalRooms);
        Add("file.open", "打开地图…", "Ctrl+O", () => Guard(OpenDialog)); Add("file.save", "保存", "Ctrl+S", () => SaveRequested(false)); Add("file.saveAs", "另存为…", "Ctrl+Shift+S", () => SaveRequested(true));
        Add("file.library", "地图目录…", "", OpenMapLibrary); Add("file.close", "关闭文档", "", () => Guard(CloseDocument));
        Add("edit.undo", "撤销", "Ctrl+Z", Undo); Add("edit.redo", "重做", "Ctrl+Shift+Z", Redo); Add("edit.duplicate", "复制所选", "Ctrl+D", () => DuplicateSelection()); Add("edit.delete", "删除所选", "Delete", DeleteSelection); Add("edit.cancel", "取消选择", "Escape", () => Select(null, false));
        Add("view.fit", "显示全图", "F", () => MapCanvas.Fit()); Add("view.actual", "实际大小 100%", "W", () => { MapCanvas.ZoomAt(MapCanvas.Size / 2, 1 / MapCanvas.Zoom); });
        Add("view.zoomIn", "放大", "Ctrl+=", () => MapCanvas.ZoomAt(MapCanvas.Size / 2, 1.25f)); Add("view.zoomOut", "缩小", "Ctrl+-", () => MapCanvas.ZoomAt(MapCanvas.Size / 2, .8f)); Add("view.spawn", "居中到出生点", "Ctrl+Shift+H", CenterOnSpawn);
        Add("view.tiles", "显示图块层", "Alt+1", () => tilesToggle.ButtonPressed = !tilesToggle.ButtonPressed); Add("view.entities", "显示实体层", "Alt+2", () => entitiesToggle.ButtonPressed = !entitiesToggle.ButtonPressed); Add("view.solid", "显示碰撞框", "Alt+3", () => solidToggle.ButtonPressed = !solidToggle.ButtonPressed);
        Add("view.grid", "显示网格", "", () => { MapCanvas.ShowGrid = !MapCanvas.ShowGrid; MapCanvas.QueueRedraw(); });
        Add("map.settings", "房间与相机…", "", OpenRoomSettings); Add("map.events", "地图事件…", "", OpenEventEditor); Add("map.play", "试玩 ▶", "", Play);
        foreach (string panel in new[] { "assets", "layers", "props", "history" }) { string key = panel; Add("panel." + key, new Dictionary<string, string> { ["assets"] = "素材面板", ["layers"] = "图层面板", ["props"] = "属性面板", ["history"] = "历史面板" }[key], "", () => TogglePanel(key)); }
        Add("layout.reset", "重置布局", "", ResetLayout); Add("help.shortcuts", "快捷键速查表", "Shift+/", OpenShortcutHelp); Add("help.editKeys", "键盘快捷键设置…", "Ctrl+Alt+K", OpenShortcutEditor); Add("help.about", "关于", "", () => Message("地图编辑器\nSlackOfDescent"));
        var keys = new[] { "V", "B", "E", "C", "T", "H", "Z", "I" };
        for (int i = 0; i < keys.Length; i++) { int tool = i; Add("tool." + i, "工具：" + (NativeMapTool)i, keys[i], () => SelectTool(tool)); }
        Add("tool.cycle", "循环切换工具", "Tab", () => SelectTool(((int)MapCanvas.Tool + 1) % 8));
    }
    private void AddMenu(Node parent, string title, string[] actions)
    {
        var menu = new MenuButton { Text = title, SwitchOnHover = true }; parent.AddChild(menu); var popup = menu.GetPopup();
        for (int i = 0; i < actions.Length; i++) { if (actions[i].Length == 0) popup.AddSeparator(); else if (IsCheckedCommand(actions[i])) popup.AddCheckItem(commandLabels[actions[i]], i); else popup.AddItem(commandLabels[actions[i]], i); }
        popup.AboutToPopup += () =>
        {
            for (int i = 0; i < actions.Length; i++)
            {
                string action = actions[i]; if (action.Length == 0) continue;
                int index = popup.GetItemIndex(i); popup.SetItemText(index, commandLabels[action] + (shortcuts.TryGetValue(action, out string? key) && key.Length > 0 ? "    " + key : ""));
                popup.SetItemDisabled(index, Document is null && NeedsDocument(action) || action == "edit.undo" && undo.Count == 0 || action == "edit.redo" && redo.Count == 0 || action is "edit.delete" or "edit.duplicate" && selected is null);
                if (IsCheckedCommand(action)) popup.SetItemChecked(index, CommandChecked(action));
            }
        };
        popup.IdPressed += id => RunMapAction(actions[(int)id]);
    }
    private static bool IsCheckedCommand(string action) => action.StartsWith("panel.") || action is "view.tiles" or "view.entities" or "view.solid" or "view.grid";
    private bool CommandChecked(string action) => action switch
    {
        "view.tiles" => MapCanvas.ShowTiles, "view.entities" => MapCanvas.ShowEntities, "view.solid" => MapCanvas.ShowSolidOverlay, "view.grid" => MapCanvas.ShowGrid,
        _ => action.StartsWith("panel.") && panels.TryGetValue(action[6..], out var panel) && panel.Visible
    };
    private static bool NeedsDocument(string action) => !(action is "file.new" or "file.open" or "file.import" or "file.library" || action.StartsWith("help.") || action.StartsWith("panel.") || action.StartsWith("layout."));
    private void RunMapAction(string action) { if (Document is null && NeedsDocument(action)) return; if (commands.TryGetValue(action, out var run)) run(); }
    private void OpenOriginalRooms()
    {
        var dialog = new ConfirmationDialog { Name = "ImportOriginalRoom", Title = "导入原版房间", Size = new(400, 160) }; AddChild(dialog);
        rooms = new OptionButton { Name = "OriginalRooms", CustomMinimumSize = new(360, 32), AutoTranslateMode = AutoTranslateModeEnum.Disabled }; dialog.AddChild(rooms);
        foreach (var room in world.Rooms.OrderBy(p => p.Key)) { rooms.AddItem(RoomLabel(room.Value.GetProperty("name").GetString()!), room.Key); if (room.Key == Service.TestRoomId) rooms.Select(rooms.ItemCount - 1); }
        dialog.Confirmed += () => { int room = rooms.GetSelectedId(); Guard(() => Import(room)); dialog.QueueFree(); }; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
    private void CloseDocument()
    {
        Document = null!; FilePath = null; Dirty = false; undo.Clear(); redo.Clear(); MapCanvas.Document = null; Select(null, false); documentTab.Text = "未打开地图"; status.Text = "从文件菜单新建或打开地图"; MapCanvas.QueueRedraw(); minimap.QueueRedraw(); QueueWorkbenchSync();
    }
    private void SyncToolOptions()
    {
        if (MapCanvas is null) return;
        foreach (var pair in toolOptions) pair.Value.Visible = pair.Key == MapCanvas.Tool;
        foreach (var pair in toolButtons) pair.Value.SetPressedNoSignal(pair.Key == (int)MapCanvas.Tool);
        foreach (var pair in toolButtons) pair.Value.TooltipText = commandLabels["tool." + pair.Key] + (shortcuts.TryGetValue("tool." + pair.Key, out string? key) && key.Length > 0 ? " (" + key + ")" : "");
        if (assetTabs is not null && MapCanvas.Tool is NativeMapTool.Entity or NativeMapTool.Collision) assetTabs.CurrentTab = 1;
        else if (assetTabs is not null && MapCanvas.Tool is NativeMapTool.Tile or NativeMapTool.Eyedrop) assetTabs.CurrentTab = 0;
    }
    private void QueueWorkbenchSync()
    {
        if (workbenchSyncPending || !IsInsideTree()) return; workbenchSyncPending = true; Callable.From(SyncWorkbench).CallDeferred();
    }
    private void SyncWorkbench()
    {
        workbenchSyncPending = false; if (layerList is null) return;
        documentTab.Text = Document is null ? "未打开地图" : (Dirty ? "● " : "") + Document.Name + ".map.json";
        layerList.Clear();
        if (Document is not null)
        {
            foreach (var group in Document.Tiles.OfType<JsonObject>().GroupBy(t => Int(t, "depth")).OrderByDescending(g => g.Key))
            { int index = layerList.AddItem((MapCanvas.HiddenDepths.Contains(group.Key) ? "◌ " : "◉ ") + "深度 " + group.Key + "    " + group.Count()); layerList.SetItemMetadata(index, group.Key); if (group.Key == (int)depth.Value) layerList.Select(index); }
            int entityIndex = layerList.AddItem((MapCanvas.ShowEntities ? "◉ " : "◌ ") + "实体层    " + Document.Instances.Count); layerList.SetItemMetadata(entityIndex, "entities");
        }
        if (Document is null) { undoButton.Disabled = redoButton.Disabled = true; }
        historyCount.Text = "撤销 " + undo.Count + " / 重做 " + redo.Count;
        historyList.Clear(); for (int i = 0; i < undo.Count; i++) historyList.AddItem((i + 1) + "  编辑步骤"); historyList.AddItem((undo.Count + 1) + "  当前状态");
        for (int i = 0; i < redo.Count; i++) historyList.AddItem((undo.Count + i + 2) + "  已撤销"); historyList.Select(undo.Count); minimap.QueueRedraw();
    }
    private void ChooseLayer(long index)
    {
        var data = layerList.GetItemMetadata((int)index); if (data.VariantType == Variant.Type.Int) depth.Value = data.AsInt32();
    }
    private void ToggleLayer(long index)
    {
        var data = layerList.GetItemMetadata((int)index);
        if (data.VariantType == Variant.Type.Int) { int value = data.AsInt32(); if (!MapCanvas.HiddenDepths.Add(value)) MapCanvas.HiddenDepths.Remove(value); }
        else entitiesToggle.ButtonPressed = !entitiesToggle.ButtonPressed;
        if (selectedTile && selected is not null && MapCanvas.HiddenDepths.Contains(Int(selected, "depth"))) Select(null, false);
        MapCanvas.QueueRedraw(); QueueWorkbenchSync();
    }
    private void GoHistory(long index)
    {
        int target = (int)index; while (undo.Count > target) Undo(); while (undo.Count < target && redo.Count > 0) Redo(); QueueWorkbenchSync();
    }
    private void SampleTile(Vector2 point, bool fromTile)
    {
        if (fromTile && MapCanvas.Hit(point) is { Item: { } tile, Tile: true })
        {
            SelectBackground(Int(tile, "background"));
            sourceX.Value = Number(tile, "sourceX"); sourceY.Value = Number(tile, "sourceY"); tileWidth.Value = Number(tile, "width", 32); tileHeight.Value = Number(tile, "height", 32); depth.Value = Number(tile, "depth", 10000);
        }
        else { sourceX.Value = Math.Max(0, Math.Floor(point.X / 32) * 32); sourceY.Value = Math.Max(0, Math.Floor(point.Y / 32) * 32); }
        RefreshAtlas(); SelectTool((int)NativeMapTool.Tile);
    }
    private void SelectBackground(int id)
    {
        if (!Enumerable.Range(0, backgrounds.ItemCount).Any(i => backgrounds.GetItemId(i) == id) && FindChild("TileSearch", true, false) is LineEdit search) search.Text = "";
        for (int i = 0; i < backgrounds.ItemCount; i++) if (backgrounds.GetItemId(i) == id) { backgrounds.Select(i); RefreshAtlas(); return; }
    }
    private void SetPanelVisible(string key, bool visible)
    {
        if (!panels.TryGetValue(key, out var panel)) return;
        panel.Visible = visible;
        if (key != "assets" && panel.GetParent() is Control frame) frame.SizeFlagsVertical = visible && expandingPanels.Contains(key) ? Control.SizeFlags.ExpandFill : Control.SizeFlags.Fill;
    }
    private void TogglePanel(string key) { if (panels.TryGetValue(key, out var panel)) SetPanelVisible(key, !panel.Visible); SaveWorkbenchSettings(); }
    private void ResetLayout() { foreach (string panel in panels.Keys) SetPanelVisible(panel, true); assetSplit.SplitOffsets = [228]; dockSplit.SplitOffsets = [650]; SaveWorkbenchSettings(); }
    private static string ShortcutText(InputEventKey key)
    {
        string value = OS.GetKeycodeString(key.Keycode == Key.None ? key.PhysicalKeycode : key.Keycode);
        return (key.CtrlPressed ? "Ctrl+" : "") + (key.AltPressed ? "Alt+" : "") + (key.ShiftPressed ? "Shift+" : "") + value;
    }
    private void OpenShortcutHelp()
    {
        var dialog = new AcceptDialog { Name = "MapShortcutHelp", Title = "快捷键速查表", Size = new(620, 580) }; AddChild(dialog);
        var list = new ItemList { CustomMinimumSize = new(580, 490) }; dialog.AddChild(list);
        foreach (var pair in shortcuts.Where(p => p.Value.Length > 0)) list.AddItem(commandLabels[pair.Key] + "    " + pair.Value);
        list.AddItem("空格 / 中键：临时平移"); list.AddItem("Ctrl+滚轮：缩放；Shift+滚轮：水平滚动"); list.AddItem("Ctrl：临时取消吸附；右键：临时擦除");
        dialog.Confirmed += dialog.QueueFree; dialog.CloseRequested += dialog.QueueFree; dialog.PopupCentered();
    }
    private void OpenShortcutEditor()
    {
        var dialog = new ConfirmationDialog { Name = "MapShortcutEditor", Title = "键盘快捷键", Size = new(620, 580), OkButtonText = "保存" }; AddChild(dialog);
        var scroll = new ScrollContainer { CustomMinimumSize = new(580, 480) }; dialog.AddChild(scroll); var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(rows);
        var draft = new Dictionary<string, string>(shortcuts); var inputs = new Dictionary<string, LineEdit>();
        foreach (var command in commandLabels)
        {
            var row = new HBoxContainer(); rows.AddChild(row); row.AddChild(new Label { Text = command.Value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            string action = command.Key; var input = new LineEdit { Text = draft.GetValueOrDefault(action, ""), CustomMinimumSize = new(210, 0), PlaceholderText = "按组合键；Backspace 清空" }; row.AddChild(input); inputs[action] = input;
            input.GuiInput += ev => { if (ev is not InputEventKey { Pressed: true, Echo: false } key || key.Keycode is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta) return; input.Text = key.Keycode == Key.Backspace ? "" : ShortcutText(key); draft[action] = input.Text; input.AcceptEvent(); };
        }
        Button(rows, "恢复默认快捷键", "ResetMapShortcuts", () => { draft.Clear(); foreach (var pair in defaultShortcuts) draft[pair.Key] = pair.Value; foreach (var pair in inputs) pair.Value.Text = draft.GetValueOrDefault(pair.Key, ""); });
        dialog.Confirmed += () =>
        {
            var duplicate = draft.Where(p => p.Value.Length > 0).GroupBy(p => p.Value).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null) { Message("快捷键冲突：" + duplicate.Key); dialog.PopupCentered(); return; }
            shortcuts.Clear(); foreach (var pair in draft) shortcuts[pair.Key] = pair.Value; SyncToolOptions(); SaveWorkbenchSettings(); dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
    private void LoadWorkbenchSettings()
    {
        if (!File.Exists(SettingsPath)) return;
        try
        {
            var settings = JsonSerializer.Deserialize<WorkbenchSettings>(File.ReadAllText(SettingsPath)); if (settings is null) return;
            foreach (var pair in settings.Panels) SetPanelVisible(pair.Key, pair.Value);
            foreach (var pair in settings.Shortcuts) if (commands.ContainsKey(pair.Key)) shortcuts[pair.Key] = pair.Value;
            assetSplit.SplitOffsets = [Math.Clamp(settings.AssetWidth, 190, 450)]; dockSplit.SplitOffsets = [Math.Clamp(settings.CanvasWidth, 260, 1600)]; SyncToolOptions();
        }
        catch (Exception error) when (error is IOException or JsonException) { GD.PushWarning(error.Message); }
    }
    private void SaveWorkbenchSettings()
    {
        if (assetSplit is null) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new WorkbenchSettings { Panels = panels.ToDictionary(p => p.Key, p => p.Value.Visible), Shortcuts = shortcuts, AssetWidth = assetSplit.SplitOffsets[0], CanvasWidth = dockSplit.SplitOffsets[0] })); }
        catch (IOException error) { GD.PushWarning(error.Message); }
    }
}
