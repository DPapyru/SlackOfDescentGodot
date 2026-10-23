using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public enum NativeMapTool { Select, Tile, Erase, Collision, Entity, Pan }

/// <summary>The in-game map editor; its document remains alive while the host runs a playtest.</summary>
public partial class NativeMapEditor : Window
{
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private readonly NativeContentStore store;
    public NativeMapService Service { get; }
    public NativeMapDocument Document { get; private set; } = null!;
    public event Func<NativeMapDocument, Task>? PlayRequested;
    public event Action? Closed;
    public string? FilePath { get; private set; }
    public bool Dirty { get; private set; }
    public NativeMapCanvas MapCanvas { get; private set; } = null!;
    private Label status = null!;
    private OptionButton rooms = null!, backgrounds = null!, toolChoice = null!;
    private ItemList entities = null!;
    private SpinBox grid = null!, sourceX = null!, sourceY = null!, tileWidth = null!, tileHeight = null!, depth = null!;
    private SpinBox propX = null!, propY = null!, propScaleX = null!, propScaleY = null!, propAngle = null!;
    private TextEdit properties = null!;
    private Label selectedTitle = null!;
    private bool updating;
    private JsonObject? selected;
    private bool selectedTile;
    private readonly Stack<NativeMapDocument> undo = new(), redo = new();
    private FileDialog fileDialog = null!;
    private ConfirmationDialog unsaved = null!, overwrite = null!;
    private AcceptDialog error = null!;
    private Action? afterDiscard;
    private string? pendingSave;
    private bool closeAfterSave;
    private long uiStarted;
    public NativeMapEditor(GmWorld world, ContentRegistry registry, NativeContentStore store)
    { this.world = world; this.registry = registry; this.store = store; Service = new(world, registry); Name = "NativeMapEditor"; }
    public override void _Ready()
    {
        Title = "地图编辑器"; Size = new(1180, 780); MinSize = new(900, 600); Transient = true; Exclusive = false;
        var root = new VBoxContainer { Name = "EditorLayout" }; AddChild(root); root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var bar = new HBoxContainer(); root.AddChild(bar);
        rooms = new OptionButton { Name = "OriginalRooms", CustomMinimumSize = new(190, 0) }; bar.AddChild(rooms);
        foreach (var pair in world.Rooms.OrderBy(p => p.Key)) { rooms.AddItem($"{RoomLabel(pair.Value.GetProperty("name").GetString()!)}", pair.Key); if (pair.Key == Service.TestRoomId) rooms.Select(rooms.ItemCount - 1); }
        Button(bar, "导入原版", "ImportOriginal", () => Guard(() => Import(rooms.GetSelectedId())));
        Button(bar, "打开…", "OpenMap", () => Guard(OpenDialog));
        Button(bar, "保存", "SaveMap", () => SaveRequested(false)); Button(bar, "另存为…", "SaveMapAs", () => SaveRequested(true));
        Button(bar, "撤销", "Undo", Undo); Button(bar, "重做", "Redo", Redo);
        Button(bar, "房间与相机", "RoomSettings", OpenRoomSettings);
        Button(bar, "试玩 ▶", "PlayMap", Play);
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; root.AddChild(split); split.SplitOffsets = [235];
        var left = new VBoxContainer { CustomMinimumSize = new(230, 0) }; split.AddChild(left);
        left.AddChild(new Label { Text = "工具（右键擦除／中键移动视野）" });
        toolChoice = new OptionButton { Name = "MapTool" }; left.AddChild(toolChoice);
        foreach (string text in new[] { "选择与移动", "绘制图块", "擦除", "碰撞区块", "放置实体", "移动视野" }) toolChoice.AddItem(text);
        toolChoice.ItemSelected += index => { MapCanvas.Tool = (NativeMapTool)index; UpdateStatus(); };
        grid = Field(left, "网格大小", "GridSize", 1, 128, 32); grid.ValueChanged += value => { if (MapCanvas is not null) { MapCanvas.GridSize = (float)value; MapCanvas.QueueRedraw(); } };
        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; left.AddChild(tabs);
        var tilePanel = new VBoxContainer { Name = "图块" }; tabs.AddChild(tilePanel);
        backgrounds = new OptionButton { Name = "TileBackground" }; tilePanel.AddChild(backgrounds);
        foreach (var background in world.Manifest.GetProperty("backgrounds").EnumerateArray()) backgrounds.AddItem(background.GetProperty("name").GetString()!, background.GetProperty("id").GetInt32());
        int defaultBackground = Array.FindIndex(world.Manifest.GetProperty("backgrounds").EnumerateArray().ToArray(), b => b.GetProperty("id").GetInt32() == 20); if (defaultBackground >= 0) backgrounds.Select(defaultBackground);
        sourceX = Field(tilePanel, "图集横坐标", "TileSourceX", 0, 16384, 0); sourceY = Field(tilePanel, "图集纵坐标", "TileSourceY", 0, 16384, 0);
        tileWidth = Field(tilePanel, "图块宽度", "TileWidth", 1, 4096, 32); tileHeight = Field(tilePanel, "图块高度", "TileHeight", 1, 4096, 32);
        depth = Field(tilePanel, "图层深度", "TileDepth", -1000000, 1000000, 10000);
        tilePanel.AddChild(new Label { Text = "同一网格、同一图层再次绘制会替换图块。\n碰撞工具使用左侧实体列表中的区块。", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var entityPanel = new VBoxContainer { Name = "实体" }; tabs.AddChild(entityPanel);
        var filter = new LineEdit { Name = "EntityFilter", PlaceholderText = "搜索实体／扩展" }; entityPanel.AddChild(filter);
        entities = new ItemList { Name = "EntityPalette", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; entityPanel.AddChild(entities);
        filter.TextChanged += PopulateEntities; PopulateEntities("");
        var right = new HSplitContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; split.AddChild(right); right.SplitOffsets = [650];
        var canvasFrame = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; right.AddChild(canvasFrame);
        var canvasBar = new HBoxContainer(); canvasFrame.AddChild(canvasBar);
        Button(canvasBar, "缩小", "ZoomOut", () => MapCanvas.ZoomAt(MapCanvas.Size / 2, .8f)); Button(canvasBar, "放大", "ZoomIn", () => MapCanvas.ZoomAt(MapCanvas.Size / 2, 1.25f));
        Button(canvasBar, "显示全图", "FitRoom", () => MapCanvas.Fit()); Button(canvasBar, "回到出生区", "SpawnView", () => { MapCanvas.Pan = new(0, 0); MapCanvas.Zoom = 1; MapCanvas.QueueRedraw(); });
        MapCanvas = new NativeMapCanvas(world) { Name = "MapCanvas", SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(300, 260), ClipContents = true };
        canvasFrame.AddChild(MapCanvas);
        MapCanvas.EditStarted = Remember; MapCanvas.Edited = Changed; MapCanvas.Selected = Select; MapCanvas.PaintAt = PaintAt;
        var inspector = new VBoxContainer { CustomMinimumSize = new(230, 0) }; right.AddChild(inspector);
        selectedTitle = new Label { Text = "未选择区块或实体", Name = "SelectionTitle" }; inspector.AddChild(selectedTitle);
        propX = Field(inspector, "横坐标", "PropertyX", -1000000, 1000000, 0); propY = Field(inspector, "纵坐标", "PropertyY", -1000000, 1000000, 0);
        propScaleX = Field(inspector, "横向缩放", "PropertyScaleX", -1000, 1000, 1, .1); propScaleY = Field(inspector, "纵向缩放", "PropertyScaleY", -1000, 1000, 1, .1); propAngle = Field(inspector, "旋转角度", "PropertyAngle", -36000, 36000, 0);
        foreach (var field in new[] { propX, propY, propScaleX, propScaleY, propAngle }) field.ValueChanged += _ => ApplyBasicProperties();
        inspector.AddChild(new Label { Text = "实体自定义属性（JSON）" });
        properties = new TextEdit { Name = "EntityProperties", SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(220, 140), Text = "{}" }; inspector.AddChild(properties);
        Button(inspector, "应用属性", "ApplyProperties", ApplyCustomProperties); Button(inspector, "编辑完整数据…", "EditRawSelection", OpenRawSelection); Button(inspector, "删除所选", "DeleteSelection", DeleteSelection);
        status = new Label { Name = "MapStatus" }; root.AddChild(status);
        fileDialog = new FileDialog { Name = "MapFileDialog", Title = "选择地图文件", Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Filters = ["*.map.json ; 地图文件"], CurrentDir = store.Maps, Size = new(800, 560) }; AddChild(fileDialog);
        fileDialog.FileSelected += FileSelected;
        unsaved = new ConfirmationDialog { Name = "MapUnsaved", Title = "尚未保存", DialogText = "地图有未保存的修改。要先保存吗？", OkButtonText = "保存", CancelButtonText = "取消" }; AddChild(unsaved);
        unsaved.AddButton("放弃修改", false, "discard"); unsaved.CustomAction += _ => { var action = afterDiscard; afterDiscard = null; unsaved.Hide(); action?.Invoke(); };
        unsaved.Confirmed += () => { closeAfterSave = true; SaveRequested(false); };
        overwrite = new ConfirmationDialog { Name = "MapOverwrite", Title = "覆盖地图", DialogText = "目标文件已存在。确认覆盖？", OkButtonText = "覆盖", CancelButtonText = "取消" }; AddChild(overwrite); overwrite.Confirmed += () => { if (pendingSave is string path) SavePath(path, true); };
        error = new AcceptDialog { Name = "MapMessage", Title = "地图编辑器", OkButtonText = "确定" }; AddChild(error);
        CloseRequested += () => Guard(FinishClose);
        registry.Changed += RegistryChanged;
        Import(Service.TestRoomId);
        uiStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    }
    public override void _ExitTree() { registry.Changed -= RegistryChanged; }
    private static string RoomLabel(string name) => name switch { "EnemyTestRoom" => "测试地图（原版 TestRoom）", "Playground" => "自由练习场", "Title" => "主界面", _ => name };
    private static void Button(Node parent, string label, string name, Action action) { var button = new Button { Text = label, Name = name }; parent.AddChild(button); button.Pressed += action; }
    private static SpinBox Field(Node parent, string label, string name, double min, double max, double value, double step = 1)
    {
        var row = new HBoxContainer(); parent.AddChild(row); row.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var input = new SpinBox { Name = name, MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new(100, 0) }; row.AddChild(input); return input;
    }
    private void PopulateEntities(string filter)
    {
        entities.Clear();
        foreach (var pair in world.Objects.OrderBy(p => p.Value.GetProperty("name").GetString()))
        {
            string name = pair.Value.GetProperty("name").GetString()!; if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            int index = entities.AddItem((pair.Value.GetProperty("solid").GetBoolean() ? "▣ " : "● ") + name); entities.SetItemMetadata(index, "object:" + pair.Key);
        }
        foreach (string id in registry.Entities.Keys.Order()) if (id.Contains(filter, StringComparison.OrdinalIgnoreCase)) { int index = entities.AddItem("扩展：" + id); entities.SetItemMetadata(index, "native:" + id); }
        if (entities.ItemCount > 0) entities.Select(0);
    }
    private void RegistryChanged() { if (IsInsideTree()) { PopulateEntities(""); if (Document is not null) UpdateStatus(); } }
    public void Import(int roomId)
    {
        var result = Service.ImportOriginal(roomId); SetDocument(result.Document, null); status.Text += $"　导入 {result.Milliseconds:F1} 毫秒";
    }
    public void LoadPath(string path)
    {
        try { var result = Service.Load(path); SetDocument(result.Document, Path.GetFullPath(path)); status.Text += $"　读取 {result.Milliseconds:F1} 毫秒"; }
        catch (Exception exception) { Message(exception.Message); }
    }
    private void SetDocument(NativeMapDocument document, string? path)
    {
        Document = document; FilePath = path; Dirty = false; undo.Clear(); redo.Clear(); selected = null; MapCanvas.Document = document; Select(null, false); MapCanvas.Pan = Vector2.Zero; MapCanvas.Zoom = 1; UpdateStatus(); MapCanvas.QueueRedraw();
    }
    private void Remember() { if (Document is null) return; undo.Push(Document.Clone()); if (undo.Count > 32) { var newest = undo.Take(32).Reverse().ToArray(); undo.Clear(); foreach (var item in newest) undo.Push(item); } redo.Clear(); }
    private void Restore(NativeMapDocument document) { Document = document; MapCanvas.Document = document; selected = null; Select(null, false); Dirty = true; UpdateStatus(); MapCanvas.QueueRedraw(); }
    public void Undo() { if (undo.TryPop(out var document)) { redo.Push(Document.Clone()); Restore(document); } }
    public void Redo() { if (redo.TryPop(out var document)) { undo.Push(Document.Clone()); Restore(document); } }
    private void Changed() { Dirty = true; UpdateStatus(); MapCanvas.QueueRedraw(); if (selected is not null) Select(selected, selectedTile); }
    private void UpdateStatus()
    {
        if (Document is null || status is null) return;
        var warnings = Service.Validate(Document); status.Text = $"{Document.Name}{(Dirty ? " *" : "")}　{Int(Document.Room, "width")}×{Int(Document.Room, "height")}　实体 {Document.Instances.Count}　图块 {Document.Tiles.Count}　{(FilePath ?? "尚未保存")}";
        if (warnings.Count > 0) status.Text += $"　缺少依赖 {warnings.Count} 项（数据已保留）";
        Title = "地图编辑器 — " + Document.Name + (Dirty ? " *" : ""); MapCanvas.GridSize = (float)grid.Value;
    }
    private void Select(JsonObject? item, bool tile)
    {
        selected = item; selectedTile = tile; MapCanvas.Selection = item; MapCanvas.SelectionIsTile = tile; updating = true;
        selectedTitle.Text = item is null ? "未选择区块或实体" : (tile ? "图块 #" : "实体 #") + Int(item, "id");
        propX.Value = item is null ? 0 : Number(item, "x"); propY.Value = item is null ? 0 : Number(item, "y"); propScaleX.Value = item is null ? 1 : Number(item, "xscale", 1); propScaleY.Value = item is null ? 1 : Number(item, "yscale", 1); propAngle.Value = item is null ? 0 : Number(item, "angle");
        properties.Text = item?["properties"]?.ToJsonString(NativeContentStore.Json) ?? "{}"; updating = false; MapCanvas.QueueRedraw();
    }
    private void ApplyBasicProperties()
    {
        if (updating || selected is null) return; Remember(); selected["x"] = propX.Value; selected["y"] = propY.Value; selected["xscale"] = propScaleX.Value; selected["yscale"] = propScaleY.Value; if (!selectedTile) selected["angle"] = propAngle.Value; Changed();
    }
    private void ApplyCustomProperties()
    {
        if (selected is null) return;
        try { var values = JsonNode.Parse(properties.Text) as JsonObject ?? throw new InvalidDataException("属性必须是 JSON 对象。"); if (values.Any(p => !IsIdentifier(p.Key) || p.Value is not JsonValue)) throw new InvalidDataException("属性名必须是有效变量名；值可以为数字、文字或布尔值。"); Remember(); selected["properties"] = values; Changed(); }
        catch (Exception exception) { Message("无法应用属性：" + exception.Message); }
    }
    public void DeleteSelection() { if (selected is null) return; Remember(); (selectedTile ? Document.Tiles : Document.Instances).Remove(selected); Select(null, false); Changed(); }
    private void PaintAt(Vector2 point, bool erase)
    {
        float unit = (float)grid.Value; double x = Math.Floor(point.X / unit) * unit, y = Math.Floor(point.Y / unit) * unit;
        if (erase || MapCanvas.Tool == NativeMapTool.Erase)
        {
            var hit = MapCanvas.Hit(point); if (hit.Item is not null) { (hit.Tile ? Document.Tiles : Document.Instances).Remove(hit.Item); if (selected == hit.Item) Select(null, false); Changed(); } return;
        }
        if (MapCanvas.Tool == NativeMapTool.Tile)
        {
            foreach (var item in Document.Tiles.OfType<JsonObject>().Where(t => Number(t, "x") == x && Number(t, "y") == y && Int(t, "depth") == (int)depth.Value).ToArray()) Document.Tiles.Remove(item);
            Service.AddTile(Document, backgrounds.GetSelectedId(), x, y, (int)sourceX.Value, (int)sourceY.Value, (int)tileWidth.Value, (int)tileHeight.Value, (int)depth.Value); Changed(); return;
        }
        if (MapCanvas.Tool is NativeMapTool.Entity or NativeMapTool.Collision)
        {
            string choice = entities.GetSelectedItems().Length > 0 ? entities.GetItemMetadata(entities.GetSelectedItems()[0]).AsString() : "object:" + world.Constants["objBlock"].Int;
            bool native = choice.StartsWith("native:", StringComparison.Ordinal); int obj = native ? -1 : int.Parse(choice[7..]);
            if (MapCanvas.Tool == NativeMapTool.Collision && (native || !world.Objects.TryGetValue(obj, out var definition) || !definition.GetProperty("solid").GetBoolean())) { obj = world.Constants["objBlock"].Int; native = false; }
            if (MapCanvas.Tool == NativeMapTool.Collision && Document.Instances.OfType<JsonObject>().Any(t => Int(t, "object") == obj && Number(t, "x") == x && Number(t, "y") == y)) return;
            var item = Service.AddEntity(Document, obj, x, y, native ? choice[7..] : null); Select(item, false); Changed();
        }
    }
    private void OpenDialog() { fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile; fileDialog.Title = "打开地图"; fileDialog.CurrentDir = FilePath is null ? store.Maps : Path.GetDirectoryName(FilePath)!; fileDialog.PopupCentered(); }
    private void SaveRequested(bool saveAs)
    {
        if (!saveAs && FilePath is not null) { SavePath(FilePath, true); return; }
        fileDialog.FileMode = FileDialog.FileModeEnum.SaveFile; fileDialog.Title = "保存地图"; fileDialog.CurrentDir = FilePath is null ? store.Maps : Path.GetDirectoryName(FilePath)!; fileDialog.CurrentFile = FilePath is null ? Document.Name + ".map.json" : Path.GetFileName(FilePath); fileDialog.PopupCentered();
    }
    private void FileSelected(string path) { if (fileDialog.FileMode == FileDialog.FileModeEnum.OpenFile) LoadPath(path); else SavePath(path, false); }
    public void SavePath(string path, bool overwriteConfirmed = false)
    {
        if (!path.EndsWith(".map.json", StringComparison.OrdinalIgnoreCase)) path += ".map.json";
        if (File.Exists(path) && !overwriteConfirmed) { pendingSave = path; overwrite.PopupCentered(); return; }
        try { Service.Save(Document, path); FilePath = Path.GetFullPath(path); Dirty = false; UpdateStatus(); if (closeAfterSave) { closeAfterSave = false; var action = afterDiscard; afterDiscard = null; action?.Invoke(); } }
        catch (Exception exception) { closeAfterSave = false; Message("保存失败，原文件未被替换：" + exception.Message); }
    }
    private void Guard(Action action) { if (!Dirty) { action(); return; } afterDiscard = action; closeAfterSave = false; unsaved.PopupCentered(); }
    private void FinishClose() { Hide(); Closed?.Invoke(); QueueFree(); }
    private void Message(string text) { error.DialogText = text; error.PopupCentered(new(600, 240)); }
    private async void Play()
    {
        if (PlayRequested is null) { Message("当前宿主没有连接试玩功能。"); return; }
        try { Hide(); await PlayRequested(Document); }
        catch (Exception exception) { Show(); Message("试玩启动失败：" + exception.Message); }
    }
    public void ResumeAfterPlaytest() { Show(); GrabFocus(); MapCanvas.QueueRedraw(); UpdateStatus(); }
    private void OpenRoomSettings()
    {
        var dialog = new ConfirmationDialog { Name = "MapRoomSettings", Title = "房间、出生点与相机", OkButtonText = "应用", CancelButtonText = "取消", Size = new(680, 660) }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        var name = new LineEdit { Text = Document.Name, PlaceholderText = "地图名称" }; box.AddChild(name);
        var width = Field(box, "地图宽度", "RoomWidth", 1, 1000000, Int(Document.Room, "width")); var height = Field(box, "地图高度", "RoomHeight", 1, 1000000, Int(Document.Room, "height"));
        box.AddChild(new Label { Text = "相机、背景、房间初始化与切换数据\n出生点通过画布中的出生实体位置设置；保留全部原版字段。" });
        var metadata = Document.Room.DeepClone().AsObject(); metadata.Remove("instances"); metadata.Remove("tiles");
        var text = new TextEdit { Text = metadata.ToJsonString(NativeContentStore.Json), CustomMinimumSize = new(640, 430), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; box.AddChild(text);
        dialog.Confirmed += () =>
        {
            try
            {
                var replacement = JsonNode.Parse(text.Text)?.AsObject() ?? throw new InvalidDataException("房间数据必须是对象。"); replacement["width"] = (int)width.Value; replacement["height"] = (int)height.Value; replacement["instances"] = Document.Instances.DeepClone(); replacement["tiles"] = Document.Tiles.DeepClone();
                var candidate = Document.Clone(); candidate.Room = replacement; candidate.Name = name.Text; Service.Validate(candidate); Remember(); Document.Room = replacement; Document.Name = name.Text; selected = null; Select(null, false); Changed(); dialog.QueueFree();
            }
            catch (Exception exception) { Message(exception.Message); }
        };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
    private void OpenRawSelection()
    {
        if (selected is null) return; var original = selected; bool tile = selectedTile;
        var dialog = new ConfirmationDialog { Name = "MapRawSelection", Title = "完整属性（保留未知字段）", OkButtonText = "应用", CancelButtonText = "取消", Size = new(650, 520) }; AddChild(dialog);
        var text = new TextEdit { Text = selected.ToJsonString(NativeContentStore.Json), CustomMinimumSize = new(620, 440) }; dialog.AddChild(text);
        dialog.Confirmed += () => { try { var replacement = JsonNode.Parse(text.Text)!.AsObject(); replacement["id"] = Int(original, "id"); var candidate = Document.Clone(); var candidateCollection = tile ? candidate.Tiles : candidate.Instances; int index = (tile ? Document.Tiles : Document.Instances).IndexOf(original); candidateCollection[index] = replacement.DeepClone(); Service.Validate(candidate); Remember(); var collection = tile ? Document.Tiles : Document.Instances; collection[index] = replacement; Select(replacement, tile); Changed(); dialog.QueueFree(); } catch (Exception exception) { Message(exception.Message); } };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.CtrlPressed && key.Keycode == Key.S) SaveRequested(key.ShiftPressed);
        else if (key.CtrlPressed && key.Keycode == Key.Z) { if (key.ShiftPressed) Redo(); else Undo(); }
        else if (key.CtrlPressed && key.Keycode == Key.Y) Redo();
        else if (key.Keycode == Key.Delete && !properties.HasFocus()) DeleteSelection();
        else return;
        SetInputAsHandled();
    }
}
