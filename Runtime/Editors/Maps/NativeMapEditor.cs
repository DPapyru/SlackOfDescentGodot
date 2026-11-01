using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public enum NativeMapTool { Select, Tile, Erase, Collision, Entity, Pan, Zoom, Eyedrop }

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
    private Label pointerStatus = null!;
    private Button undoButton = null!, redoButton = null!, applyButton = null!, rawButton = null!, deleteButton = null!, spawnCenterButton = null!;
    private CheckBox tilesToggle = null!, entitiesToggle = null!, solidToggle = null!;
    private Vector2? pointerRoom;
    public NativeMapEditor(GmWorld world, ContentRegistry registry, NativeContentStore store)
    { this.world = world; this.registry = registry; this.store = store; Service = new(world, registry); Name = "NativeMapEditor"; }
    public override void _Ready()
    {
        Title = "地图编辑器"; Size = new(1180, 780); MinSize = new(900, 600); Transient = true; Exclusive = false;
        Theme = NativeEditorTheme.Create();
        BuildWorkbench();
        fileDialog = new FileDialog { Name = "MapFileDialog", Title = NativeLocalization.Text("选择地图文件"), Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Filters = ["*.map.json ; " + NativeLocalization.Text("地图文件")], CurrentDir = store.Maps, Size = new(800, 560) }; AddChild(fileDialog);
        fileDialog.FileSelected += FileSelected;
        unsaved = new ConfirmationDialog { Name = "MapUnsaved", Title = "尚未保存", DialogText = "地图有未保存的修改。要先保存吗？", OkButtonText = "保存", CancelButtonText = "取消" }; AddChild(unsaved);
        unsaved.AddButton("放弃修改", false, "discard"); unsaved.CustomAction += _ => { var action = afterDiscard; afterDiscard = null; unsaved.Hide(); action?.Invoke(); };
        unsaved.Confirmed += () => { closeAfterSave = true; SaveRequested(false); };
        overwrite = new ConfirmationDialog { Name = "MapOverwrite", Title = "覆盖地图", DialogText = "目标文件已存在。确认覆盖？", OkButtonText = "覆盖", CancelButtonText = "取消" }; AddChild(overwrite); overwrite.Confirmed += () => { if (pendingSave is string path) SavePath(path, true); };
        error = new AcceptDialog { Name = "MapMessage", Title = "地图编辑器", OkButtonText = "确定" }; AddChild(error);
        CloseRequested += () => Guard(FinishClose);
        registry.Changed += RegistryChanged;
        Import(Service.TestRoomId);
        LoadWorkbenchSettings();
        uiStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    }
    public override void _ExitTree() { SaveWorkbenchSettings(); registry.Changed -= RegistryChanged; foreach (var texture in paletteTextures.Values) texture.Dispose(); }
    private static string RoomLabel(string name) => name;
    private static void Button(Node parent, string label, string name, Action action) { var button = new Button { Text = label, Name = name }; parent.AddChild(button); button.Pressed += action; }
    private static SpinBox Field(Node parent, string label, string name, double min, double max, double value, double step = 1)
    {
        var row = new HBoxContainer(); parent.AddChild(row); row.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var input = new SpinBox { Name = name, MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new(100, 0) }; row.AddChild(input); return input;
    }
    private void RegistryChanged() { if (IsInsideTree()) { PopulateEntities(entityFilter?.Text ?? ""); if (Document is not null) { Service.Validate(Document); UpdateStatus(); } } }
    public void Import(int roomId)
    {
        var result = Service.ImportOriginal(roomId); SetDocument(result.Document, null); status.Text += string.Format(NativeLocalization.Text("　导入 {0:F1} 毫秒"), result.Milliseconds);
    }
    public void LoadPath(string path)
    {
        try { var result = Service.Load(path); SetDocument(result.Document, IsInstalledMap(path) ? null : Path.GetFullPath(path)); status.Text += string.Format(NativeLocalization.Text("　读取 {0:F1} 毫秒"), result.Milliseconds); }
        catch (Exception exception) { Message(exception.Message); }
    }
    private void SetDocument(NativeMapDocument document, string? path)
    {
        Document = document; FilePath = path; Dirty = false; undo.Clear(); redo.Clear(); selected = null; MapCanvas.Document = document; MapCanvas.HiddenDepths.Clear(); Select(null, false); UpdateStatus(); FitWhenReady();
    }
    private int fitAttempts;
    public void FitWhenReady()
    {
        fitAttempts = 0;
        CallDeferred(nameof(FitCanvas));
    }
    private void FitCanvas()
    {
        if (MapCanvas is null) return;
        if (MapCanvas.Size.X < 16 || MapCanvas.Size.Y < 16)
        {
            if (++fitAttempts < 8) CallDeferred(nameof(FitCanvas));
            return;
        }
        MapCanvas.Fit();
    }
    private void Remember() { if (Document is null) return; undo.Push(Document.Clone()); if (undo.Count > 32) { var newest = undo.Take(32).Reverse().ToArray(); undo.Clear(); foreach (var item in newest) undo.Push(item); } redo.Clear(); }
    private void Restore(NativeMapDocument document) { Document = document; MapCanvas.Document = document; selected = null; Select(null, false); Dirty = true; UpdateStatus(); MapCanvas.QueueRedraw(); }
    public void Undo() { if (undo.TryPop(out var document)) { redo.Push(Document.Clone()); Restore(document); } }
    public void Redo() { if (redo.TryPop(out var document)) { undo.Push(Document.Clone()); Restore(document); } }
    private void Changed() { Dirty = true; UpdateStatus(); MapCanvas.QueueRedraw(); if (selected is not null) Select(selected, selectedTile); }
    private void UpdateStatus()
    {
        if (Document is null || status is null) return;
        var warnings = Service.LastWarnings; status.Text = string.Format(NativeLocalization.Text("{0}{1}　{2}×{3}　实体 {4}　图块 {5}　{6}"), Document.Name, Dirty ? " *" : "", Int(Document.Room, "width"), Int(Document.Room, "height"), Document.Instances.Count, Document.Tiles.Count, FilePath ?? NativeLocalization.Text("尚未保存"));
        if (warnings.Count > 0) status.Text += string.Format(NativeLocalization.Text("　缺少依赖 {0} 项（数据已保留）"), warnings.Count);
        Title = NativeLocalization.Text("地图编辑器") + " — " + Document.Name + (Dirty ? " *" : ""); MapCanvas.GridSize = (float)grid.Value;
        UpdateCommandState();
        UpdatePointerStatus();
        QueueWorkbenchSync();
    }
    public const string SpawnObject = "objSetRespawn";
    private JsonObject? FindSpawn() => world.Constants.TryGetValue(SpawnObject, out var id)
        ? Document.Instances.OfType<JsonObject>().FirstOrDefault(i => Int(i, "object", -1) == id.Int)
        : null;
    public void CenterOnSpawn()
    {
        if (Document is null) return;
        if (FindSpawn() is not { } spawn)
        {
            Message(NativeLocalization.Text("本房间没有出生点标记（objSetRespawn）。"));
            return;
        }
        MapCanvas.Center(new((float)Number(spawn, "x"), (float)Number(spawn, "y")));
    }
    private void UpdateCommandState()
    {
        if (undoButton is null) return;
        undoButton.Disabled = undo.Count == 0;
        redoButton.Disabled = redo.Count == 0;
        bool chosen = selected is not null;
        applyButton.Disabled = !chosen; rawButton.Disabled = !chosen; deleteButton.Disabled = !chosen;
        if (spawnCenterButton is not null) spawnCenterButton.Disabled = Document is null || FindSpawn() is null;
    }
    private void UpdatePointerStatus()
    {
        if (pointerStatus is null) return;
        float zoom = MapCanvas is null ? 1 : MapCanvas.Zoom;
        string cell = pointerRoom is { } room ? string.Format(NativeLocalization.Text("（图块 {0}, {1}）"), Math.Floor(room.X / Math.Max(1, grid.Value)), Math.Floor(room.Y / Math.Max(1, grid.Value))) : "";
        string text = pointerRoom is { } point ? string.Format(NativeLocalization.Text("缩放 {0:0}%　鼠标 {1:0}, {2:0}{3}"), zoom * 100, point.X, point.Y, cell) : string.Format(NativeLocalization.Text("缩放 {0:0}%"), zoom * 100);
        if (pointerStatus.Text != text) pointerStatus.Text = text;
        if (zoomInput is not null && !zoomInput.HasFocus()) zoomInput.Text = (zoom * 100).ToString("0") + "%";
    }
    private void Select(JsonObject? item, bool tile)
    {
        selected = item; selectedTile = tile; MapCanvas.Selection = item; MapCanvas.SelectionIsTile = tile; updating = true;
        selectedTitle.Text = item is null ? NativeLocalization.Text("未选择区块或实体") : NativeLocalization.Text(tile ? "图块" : "实体") + " #" + Int(item, "id");
        propX.Value = item is null ? 0 : Number(item, "x"); propY.Value = item is null ? 0 : Number(item, "y"); propScaleX.Value = item is null ? 1 : Number(item, "xscale", 1); propScaleY.Value = item is null ? 1 : Number(item, "yscale", 1); propAngle.Value = item is null ? 0 : Number(item, "angle");
        properties.Text = item?["properties"]?.ToJsonString(NativeContentStore.Json) ?? "{}"; updating = false; RefreshPropertyForm(); UpdateCommandState(); MapCanvas.QueueRedraw();
    }
    private void ApplyBasicProperties()
    {
        if (updating || selected is null) return; Remember(); selected["x"] = propX.Value; selected["y"] = propY.Value; selected["xscale"] = propScaleX.Value; selected["yscale"] = propScaleY.Value; if (!selectedTile) selected["angle"] = propAngle.Value; Changed();
    }
    private void ApplyCustomProperties()
    {
        if (selected is null) return;
        try { var values = JsonNode.Parse(properties.Text) as JsonObject ?? throw new InvalidDataException("属性必须是 JSON 对象。"); if (values.Any(p => !IsIdentifier(p.Key) || p.Value is not JsonValue)) throw new InvalidDataException("属性名必须是有效变量名；值可以为数字、文字或布尔值。"); Remember(); selected["properties"] = values; Changed(); }
        catch (Exception exception) { Message(NativeLocalization.Text("无法应用属性：") + NativeLocalization.Text(exception.Message)); }
    }
    public void DeleteSelection() { if (selected is null) return; Remember(); (selectedTile ? Document.Tiles : Document.Instances).Remove(selected); Select(null, false); Changed(); }
    public bool DuplicateSelection()
    {
        if (selected is null || Document is null) return false;
        var copy = selected.DeepClone().AsObject(); bool tile = selectedTile;
        Remember();
        copy["x"] = Number(selected, "x") + grid.Value; copy["y"] = Number(selected, "y");
        if (tile) { copy["id"] = NextId(Document.Tiles); Document.Tiles.Add(copy); }
        else { copy["id"] = NextId(Document.Instances); Document.Instances.Add(copy); }
        Select(copy, tile); Changed();
        return true;
    }
    private static int NextId(System.Text.Json.Nodes.JsonArray items)
    {
        int max = 0;
        foreach (var item in items.OfType<JsonObject>()) max = Math.Max(max, Int(item, "id"));
        return max + 1;
    }
    private void PaintAt(Vector2 point, bool erase)
    {
        if (point.X < 0 || point.Y < 0 || point.X >= Int(Document.Room, "width") || point.Y >= Int(Document.Room, "height")) return;
        float unit = (float)grid.Value; bool snap = snapToggle.ButtonPressed && !Input.IsKeyPressed(Key.Ctrl); double x = snap ? Math.Floor(point.X / unit) * unit : Math.Floor(point.X), y = snap ? Math.Floor(point.Y / unit) * unit : Math.Floor(point.Y);
        if (erase || MapCanvas.Tool == NativeMapTool.Erase)
        {
            var hit = MapCanvas.Hit(point); if (hit.Item is not null) { (hit.Tile ? Document.Tiles : Document.Instances).Remove(hit.Item); if (selected == hit.Item) Select(null, false); Changed(); } return;
        }
        if (MapCanvas.Tool == NativeMapTool.Tile)
        {
            if (backgrounds.ItemCount == 0) return;
            foreach (var item in Document.Tiles.OfType<JsonObject>().Where(t => Number(t, "x") == x && Number(t, "y") == y && Int(t, "depth") == (int)depth.Value).ToArray()) Document.Tiles.Remove(item);
            Service.AddTile(Document, backgrounds.GetSelectedId(), x, y, (int)sourceX.Value, (int)sourceY.Value, (int)tileWidth.Value, (int)tileHeight.Value, (int)depth.Value); Changed(); return;
        }
        if (MapCanvas.Tool is NativeMapTool.Entity or NativeMapTool.Collision)
        {
            string choice = entities.GetSelectedItems().Length > 0 ? entities.GetItemMetadata(entities.GetSelectedItems()[0]).AsString() : "object:" + world.Constants["objBlock"].Int;
            bool native = choice.StartsWith("native:", StringComparison.Ordinal); int obj = native ? -1 : int.Parse(choice[7..]);
            if (MapCanvas.Tool == NativeMapTool.Collision && (native || !world.Objects.TryGetValue(obj, out var definition) || !definition.GetProperty("solid").GetBoolean())) { obj = world.Constants["objBlock"].Int; native = false; }
            if (MapCanvas.Tool == NativeMapTool.Collision && Document.Instances.OfType<JsonObject>().Any(t => Int(t, "object") == obj && Number(t, "x") == x && Number(t, "y") == y)) return;
            var item = Service.AddEntity(Document, obj, x, y, native ? choice[7..] : null); ApplyEntityDefaults(item); Select(item, false); Changed();
        }
    }
    private void OpenDialog() { fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile; fileDialog.Title = NativeLocalization.Text("打开地图"); fileDialog.CurrentDir = FilePath is null ? store.Maps : Path.GetDirectoryName(FilePath)!; fileDialog.PopupCentered(); }
    private void SaveRequested(bool saveAs)
    {
        if (!saveAs && FilePath is not null) { SavePath(FilePath, true); return; }
        fileDialog.FileMode = FileDialog.FileModeEnum.SaveFile; fileDialog.Title = NativeLocalization.Text("保存地图"); fileDialog.CurrentDir = FilePath is null ? store.Maps : Path.GetDirectoryName(FilePath)!; fileDialog.CurrentFile = FilePath is null ? Document.Name + ".map.json" : Path.GetFileName(FilePath); fileDialog.PopupCentered();
    }
    private void FileSelected(string path) { if (fileDialog.FileMode == FileDialog.FileModeEnum.OpenFile) LoadPath(path); else SavePath(path, false); }
    public void SavePath(string path, bool overwriteConfirmed = false)
    {
        if (!path.EndsWith(".map.json", StringComparison.OrdinalIgnoreCase)) path += ".map.json";
        if (File.Exists(path) && !overwriteConfirmed) { pendingSave = path; overwrite.PopupCentered(); return; }
        try { if (IsInstalledMap(path)) throw new InvalidDataException(L("请将已安装地图另存到个人目录或编辑项目。", "Save installed maps to your own folder or an editing project.")); Service.Save(Document, path); FilePath = Path.GetFullPath(path); Dirty = false; UpdateStatus(); if (closeAfterSave) { closeAfterSave = false; var action = afterDiscard; afterDiscard = null; action?.Invoke(); } }
        catch (Exception exception) { closeAfterSave = false; Message(NativeLocalization.Text("保存失败，原文件未被替换：") + NativeLocalization.Text(exception.Message)); }
    }
    private void Guard(Action action) { if (!Dirty) { action(); return; } afterDiscard = action; closeAfterSave = false; unsaved.PopupCentered(); }
    private void FinishClose() { Hide(); Closed?.Invoke(); QueueFree(); }
    private void Message(string text) { error.DialogText = NativeLocalization.Text(text); error.PopupCentered(new(600, 240)); }
    private async void Play()
    {
        if (PlayRequested is null) { Message("当前宿主没有连接试玩功能。"); return; }
        try { Hide(); await PlayRequested(Document); }
        catch (Exception exception) { Show(); Message(NativeLocalization.Text("试玩启动失败：") + NativeLocalization.Text(exception.Message)); }
    }
    public void ResumeAfterPlaytest() { Show(); GrabFocus(); MapCanvas.QueueRedraw(); UpdateStatus(); }
    private void OpenRoomSettings()
    {
        var dialog = new ConfirmationDialog { Name = "MapRoomSettings", Title = "房间、出生点与相机", OkButtonText = "应用", CancelButtonText = "取消", Size = new(680, 660) }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        var name = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = Document.Name, PlaceholderText = NativeLocalization.Text("地图名称") }; box.AddChild(name);
        var width = Field(box, "地图宽度", "RoomWidth", 1, 1000000, Int(Document.Room, "width")); var height = Field(box, "地图高度", "RoomHeight", 1, 1000000, Int(Document.Room, "height"));
        box.AddChild(new Label { Text = "相机、背景、房间初始化与切换数据\n出生点通过画布中的出生实体位置设置；保留全部原版字段。" });
        var metadata = Document.Room.DeepClone().AsObject(); metadata.Remove("instances"); metadata.Remove("tiles");
        var text = new TextEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = metadata.ToJsonString(NativeContentStore.Json), CustomMinimumSize = new(640, 430), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; box.AddChild(text);
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
        var text = new TextEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = selected.ToJsonString(NativeContentStore.Json), CustomMinimumSize = new(620, 440) }; dialog.AddChild(text);
        dialog.Confirmed += () => { try { var replacement = JsonNode.Parse(text.Text)!.AsObject(); replacement["id"] = Int(original, "id"); var candidate = Document.Clone(); var candidateCollection = tile ? candidate.Tiles : candidate.Instances; int index = (tile ? Document.Tiles : Document.Instances).IndexOf(original); candidateCollection[index] = replacement.DeepClone(); Service.Validate(candidate); Remember(); var collection = tile ? Document.Tiles : Document.Instances; collection[index] = replacement; Select(replacement, tile); Changed(); dialog.QueueFree(); } catch (Exception exception) { Message(exception.Message); } };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true, Echo: false } key || TypingInField()) return;
        string? action = shortcuts.FirstOrDefault(pair => pair.Value == ShortcutText(key)).Key;
        if (action is not null) { RunMapAction(action); SetInputAsHandled(); return; }
        if (ToolShortcut(key.Keycode) is int tool && !key.CtrlPressed && !key.AltPressed) { SelectTool(tool); SetInputAsHandled(); }
    }
    private bool TypingInField() => GetViewport()?.GuiGetFocusOwner() switch { LineEdit or TextEdit => true, SpinBox box => box.GetLineEdit().HasFocus(), _ => false };
    private static int? ToolShortcut(Key code) => code switch
    {
        Key.Key1 or Key.Kp1 => 0, Key.Key2 or Key.Kp2 => 1, Key.Key3 or Key.Kp3 => 2,
        Key.Key4 or Key.Kp4 => 3, Key.Key5 or Key.Kp5 => 4, Key.Key6 or Key.Kp6 => 5,
        _ => null
    };
    public void SelectTool(int tool)
    {
        if (toolChoice is null || MapCanvas is null || tool < 0 || tool >= toolChoice.ItemCount) return;
        toolChoice.Select(tool);
        MapCanvas.Tool = (NativeMapTool)tool;
        SyncToolOptions();
        UpdateStatus();
        MapCanvas.GrabFocus();
    }
}
