using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public readonly record struct NativeCharacterPreviewInput(bool Left, bool Right, bool Jump, bool Dash);

public partial class NativeCharacterEditor : Window
{
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private readonly NativeCharacterRepository repository;
    public NativeCharacterDocument Document { get; private set; } = NativeCharacterDocument.Create();
    public event Action? Closed;
    public event Action<CharacterDefinition>? PreviewRequested;
    public event Action<NativeCharacterPreviewInput>? PreviewAdvanceRequested;
    public event Action? PreviewResetRequested;
    public TextureRect PreviewSurface { get; } = new() { Name = "真实角色预览", CustomMinimumSize = new(260, 180), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, TextureFilter = CanvasItem.TextureFilterEnum.Nearest };
    private NativeCharacterPixelCanvas canvas = null!;
    private NativeCharacterComposite composite = null!;
    private ItemList parts = null!;
    private OptionButton frames = null!, savedCharacters = null!, preset = null!, jump = null!, dash = null!;
    private LineEdit characterName = null!, partName = null!;
    private Label status = null!, runtimeState = null!;
    private SpinBox anchorX = null!, anchorY = null!, offsetX = null!, offsetY = null!, layer = null!, duration = null!, fps = null!, newWidth = null!, newHeight = null!;
    private ColorPickerButton color = null!;
    private CheckButton animate = null!, runtimePlay = null!, left = null!, right = null!;
    private CheckBox partVisible = null!;
    private int selectedPart, selectedFrame;
    private bool refreshing, jumpQueued, dashQueued;
    private double previewAccumulator;
    private Action? pendingConfirmation;
    private ConfirmationDialog confirmation = null!;
    private AcceptDialog message = null!;
    private NativePixelClipboard? clipboard;
    private static readonly string[] Presets = ["Nana", "Ryoko", "Yuki"];
    public NativeCharacterEditor(GmWorld world, ContentRegistry registry, NativeContentStore store)
    { this.world = world; this.registry = registry; repository = new(store); }
    public override void _Ready()
    {
        Title = "像素角色编辑器"; Name = "NativeCharacterEditor"; Size = new(1180, 800); MinSize = new(960, 700); Unresizable = false; Transient = true;
        CloseRequested += RequestClose;
        var margin = new MarginContainer(); AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 10);
        var page = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; margin.AddChild(page);
        var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; page.AddChild(root);
        var file = Row(root);
        Button(file, "新建角色", () => ConfirmUnsaved(NewDocument));
        savedCharacters = new OptionButton { AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new(170, 32) }; file.AddChild(savedCharacters);
        Button(file, "载入", () => ConfirmUnsaved(LoadSelected)); Button(file, "保存", SaveRequested);
        characterName = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, PlaceholderText = NativeLocalization.Text("角色名称"), CustomMinimumSize = new(160, 32), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; file.AddChild(characterName);
        characterName.TextChanged += value => Change(() => Document.Definition.Name = value);
        Button(file, "关闭", RequestClose);
        var settings = Row(root); settings.AddChild(new Label { Text = "基础行为" });
        preset = new OptionButton { AutoTranslateMode = AutoTranslateModeEnum.Disabled }; foreach (var value in Presets) preset.AddItem(value); settings.AddChild(preset);
        preset.ItemSelected += index => Change(() => { Document.Definition.BasePreset = preset.GetItemText((int)index); Document.Definition.Appearance = "Character-" + Document.Definition.BasePreset; });
        settings.AddChild(new Label { Text = "跳跃动作" }); jump = ActionChoice(settings, CharacterActionSlot.Jump);
        settings.AddChild(new Label { Text = "冲刺动作" }); dash = ActionChoice(settings, CharacterActionSlot.Dash);
        settings.AddChild(new Label { Text = "动画帧率" }); fps = Spin(settings, 1, 60, value => Change(() => Document.Definition.AnimationFps = value));
        var columns = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [220] }; root.AddChild(columns);
        var sidebar = new VBoxContainer { CustomMinimumSize = new(220, 0) }; columns.AddChild(sidebar);
        sidebar.AddChild(new Label { Text = "独立部件（按图层叠加）" });
        parts = new ItemList { AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new(210, 140), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; sidebar.AddChild(parts);
        parts.ItemSelected += index => SelectPart((int)index);
        var add = Row(sidebar); Button(add, "＋帽子", () => AddPart("帽子", 20)); Button(add, "＋尾巴", () => AddPart("尾巴", -10)); Button(add, "＋部件", () => AddPart("部件", 0));
        var dimensions = Row(sidebar); dimensions.AddChild(new Label { Text = "新部件尺寸" }); newWidth = Spin(dimensions, 1, 256, _ => { }); newHeight = Spin(dimensions, 1, 256, _ => { }); newWidth.Value = newHeight.Value = 32;
        Button(sidebar, "删除当前部件", () => Confirm("删除当前部件及全部动画帧？", () => { if (CurrentPart is { } part) Document.RemovePart(part.Id); selectedPart = Math.Max(0, selectedPart - 1); Refresh(); }));
        partName = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, PlaceholderText = NativeLocalization.Text("部件名称") }; sidebar.AddChild(partName); partName.TextChanged += value => { if (CurrentPart is { } part) Change(() => part.Name = value); };
        partVisible = new CheckBox { Text = "显示部件" }; sidebar.AddChild(partVisible); partVisible.Toggled += value => { if (CurrentPart is { } part) Change(() => part.Visible = value); };
        anchorX = PropertySpin(sidebar, "锚点 X", value => { if (CurrentPart is { } p) Change(() => p.AnchorX = (int)value); });
        anchorY = PropertySpin(sidebar, "锚点 Y", value => { if (CurrentPart is { } p) Change(() => p.AnchorY = (int)value); });
        offsetX = PropertySpin(sidebar, "偏移 X", value => { if (CurrentPart is { } p) Change(() => p.OffsetX = (int)value); });
        offsetY = PropertySpin(sidebar, "偏移 Y", value => { if (CurrentPart is { } p) Change(() => p.OffsetY = (int)value); });
        layer = PropertySpin(sidebar, "图层", value => { if (CurrentPart is { } p) Change(() => p.Layer = (int)value); });
        var center = new VBoxContainer(); columns.AddChild(center);
        var tools = Row(center);
        var group = new ButtonGroup();
        foreach (var (text, tool) in new[] { ("画笔 B", NativeCharacterTool.Brush), ("橡皮 E", NativeCharacterTool.Eraser), ("矩形选区 S", NativeCharacterTool.Select) })
        { var button = new Button { Text = text, ToggleMode = true, ButtonGroup = group, ButtonPressed = tool == NativeCharacterTool.Brush }; tools.AddChild(button); button.Pressed += () => canvas.Tool = tool; }
        color = new ColorPickerButton { Color = new Color(0.3f, 0.7f, 1, 1), CustomMinimumSize = new(62, 32), EditAlpha = true }; tools.AddChild(color); color.ColorChanged += value => canvas.PaintColor = value;
        tools.AddChild(new Label { Text = "缩放" }); var zoom = Spin(tools, 2, 24, value => { if (canvas is not null) canvas.Zoom = (int)value; }); zoom.Value = 10;
        Button(tools, "撤销", Undo); Button(tools, "重做", Redo); Button(tools, "复制", Copy); Button(tools, "粘贴", Paste);
        var content = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [470] }; center.AddChild(content);
        var canvasScroll = new ScrollContainer { CustomMinimumSize = new(350, 350), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; content.AddChild(canvasScroll);
        canvas = new NativeCharacterPixelCanvas { Name = "像素画布", PaintColor = color.Color }; canvasScroll.AddChild(canvas);
        canvas.Edited += OnPixelsEdited;
        var preview = new VBoxContainer { CustomMinimumSize = new(260, 0) }; content.AddChild(preview);
        preview.AddChild(new Label { Text = "部件叠加动画" });
        composite = new NativeCharacterComposite { CustomMinimumSize = new(260, 150) }; preview.AddChild(composite);
        animate = new CheckButton { Text = "播放部件动画", ButtonPressed = true }; preview.AddChild(animate);
        preview.AddChild(new Label { Text = "游戏中的真实角色" }); preview.AddChild(PreviewSurface);
        runtimeState = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = NativeLocalization.Text("点击“应用到预览”绑定真实角色。"), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new(260, 42) }; preview.AddChild(runtimeState);
        var runtimeButtons = Row(preview); Button(runtimeButtons, "应用到预览", ApplyRuntimePreview); Button(runtimeButtons, "重置", () => PreviewResetRequested?.Invoke());
        runtimePlay = new CheckButton { Text = "运行预览（实际碰撞与动作）" }; preview.AddChild(runtimePlay);
        var movement = Row(preview); left = new CheckButton { Text = "左" }; movement.AddChild(left); right = new CheckButton { Text = "右" }; movement.AddChild(right); Button(movement, "跳跃", () => { jumpQueued = true; StepRuntime(); }); Button(movement, "冲刺", () => { dashQueued = true; StepRuntime(); }); Button(preview, "单步", StepRuntime);
        var timeline = Row(center); timeline.AddChild(new Label { Text = "当前动画帧" }); frames = new OptionButton { CustomMinimumSize = new(100, 32) }; timeline.AddChild(frames); frames.ItemSelected += index => { selectedFrame = (int)index; Refresh(); };
        Button(timeline, "＋空白帧", () => AddFrame(false)); Button(timeline, "复制帧", () => AddFrame(true)); Button(timeline, "删除帧", DeleteFrame);
        timeline.AddChild(new Label { Text = "持续拍数" }); duration = Spin(timeline, 1, 600, value => { if (CurrentPart is { } p) Change(() => p.Frames[selectedFrame].Duration = (int)value); });
        status = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart }; root.AddChild(status);
        root.AddChild(new Label { Text = "Ctrl+C 复制选区／整帧　Ctrl+V 原样替换 RGBA　Ctrl+Z 撤销　Ctrl+Shift+Z 重做　Ctrl+S 保存　滚轮缩放" });
        confirmation = new ConfirmationDialog { Title = "请确认", OkButtonText = "确定", CancelButtonText = "取消" }; AddChild(confirmation);
        confirmation.Confirmed += () => { var action = pendingConfirmation; pendingConfirmation = null; action?.Invoke(); };
        confirmation.Canceled += () => pendingConfirmation = null;
        message = new AcceptDialog { Title = "像素角色编辑器", OkButtonText = "确定" }; AddChild(message);
        RefreshSaved(); Refresh();
    }
    private NativeCharacterPart? CurrentPart => Document.Definition.Parts.Count > 0 ? Document.Definition.Parts[Math.Clamp(selectedPart, 0, Document.Definition.Parts.Count - 1)] : null;
    private static HBoxContainer Row(Node parent) { var row = new HBoxContainer(); parent.AddChild(row); return row; }
    private static Button Button(Node parent, string text, Action action) { var button = new Button { Text = text }; parent.AddChild(button); button.Pressed += action; return button; }
    private static SpinBox Spin(Node parent, double min, double max, Action<double> changed) { var spin = new SpinBox { MinValue = min, MaxValue = max, Step = 1, CustomMinimumSize = new(66, 30), UpdateOnTextChanged = false }; parent.AddChild(spin); spin.ValueChanged += value => changed(value); return spin; }
    private static SpinBox PropertySpin(Node parent, string name, Action<double> changed) { var row = Row(parent); row.AddChild(new Label { Text = name, CustomMinimumSize = new(70, 0) }); return Spin(row, -1024, 1024, changed); }
    private OptionButton ActionChoice(Node parent, CharacterActionSlot slot)
    {
        var choice = new OptionButton { AutoTranslateMode = AutoTranslateModeEnum.Disabled }; parent.AddChild(choice); choice.AddItem(NativeLocalization.Text("原作动作（保持基础行为）")); choice.SetItemMetadata(0, "");
        foreach (var pair in registry.Actions.OrderBy(p => p.Key, StringComparer.Ordinal))
            try { if (pair.Value().Slot == slot) { choice.AddItem(pair.Key); choice.SetItemMetadata(choice.ItemCount - 1, pair.Key); } } catch { }
        choice.ItemSelected += index => Change(() => { string id = choice.GetItemMetadata((int)index).AsString(); if (id.Length == 0) Document.Definition.Actions.Remove(slot); else Document.Definition.Actions[slot] = id; });
        return choice;
    }
    private void Change(Action action)
    {
        if (refreshing) return;
        Document.Edit(action); RefreshTitle(); composite.Invalidate(); canvas.QueueRedraw();
        if (CurrentPart is { } part && selectedPart < parts.ItemCount) parts.SetItemText(selectedPart, $"{part.Name}  [{part.Layer}]  {part.Width}×{part.Height}");
    }
    private void Refresh()
    {
        refreshing = true;
        try
        {
            selectedPart = Math.Clamp(selectedPart, 0, Math.Max(0, Document.Definition.Parts.Count - 1));
            parts.Clear(); foreach (var part in Document.Definition.Parts) parts.AddItem($"{part.Name}  [{part.Layer}]  {part.Width}×{part.Height}");
            if (parts.ItemCount > 0) parts.Select(selectedPart);
            characterName.Text = Document.Definition.Name;
            int presetIndex = Array.IndexOf(Presets, Document.Definition.BasePreset);
            if (presetIndex < 0) { preset.AddItem(Document.Definition.BasePreset); presetIndex = preset.ItemCount - 1; } preset.Select(presetIndex);
            SelectAction(jump, CharacterActionSlot.Jump); SelectAction(dash, CharacterActionSlot.Dash); fps.Value = Document.Definition.AnimationFps;
            frames.Clear();
            var p = CurrentPart;
            if (p is not null)
            {
                selectedFrame = Math.Clamp(selectedFrame, 0, p.Frames.Count - 1);
                for (int i = 0; i < p.Frames.Count; i++) frames.AddItem($"{i + 1} / {p.Frames.Count}"); frames.Select(selectedFrame);
                partName.Text = p.Name; partVisible.ButtonPressed = p.Visible;
                anchorX.Value = p.AnchorX; anchorY.Value = p.AnchorY; offsetX.Value = p.OffsetX; offsetY.Value = p.OffsetY; layer.Value = p.Layer; duration.Value = p.Frames[selectedFrame].Duration;
            }
            canvas.Bind(Document, p?.Id, selectedFrame); composite.Document = Document; composite.Invalidate(); RefreshTitle();
        }
        finally { refreshing = false; }
    }
    private void SelectAction(OptionButton choice, CharacterActionSlot slot)
    {
        string id = Document.Definition.Actions.GetValueOrDefault(slot, "");
        for (int i = 0; i < choice.ItemCount; i++) if (choice.GetItemMetadata(i).AsString() == id) { choice.Select(i); return; }
        choice.AddItem(id + NativeLocalization.Text("（缺少依赖，保留配置）")); choice.SetItemMetadata(choice.ItemCount - 1, id); choice.Select(choice.ItemCount - 1);
    }
    private void RefreshTitle()
    {
        Title = (Document.Dirty ? "＊ " : "") + NativeLocalization.Text("像素角色编辑器") + " — " + Document.Definition.Name;
        if (status is not null) status.Text = string.Format(NativeLocalization.Text("{0}　ID：{1}　部件：{2}　撤销：{3}　依赖：{4}"), NativeLocalization.Text(Document.Dirty ? "尚未保存" : "已保存"), Document.Definition.Id, Document.Definition.Parts.Count, Document.UndoCount, string.Join("、", Document.Definition.Dependencies));
        if(status is not null&&Document.MissingFrames.Count>0)status.Text+="\n"+NativeLocalization.Text("缺少或损坏的帧（保留原始引用）：")+string.Join("、",Document.MissingFrames.Values);
    }
    private void RefreshSaved()
    {
        string? previous = savedCharacters.Selected >= 0 ? savedCharacters.GetItemMetadata(savedCharacters.Selected).AsString() : null;
        savedCharacters.Clear();
        foreach (var definition in registry.Characters.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        { savedCharacters.AddItem(definition.Name); savedCharacters.SetItemMetadata(savedCharacters.ItemCount - 1, definition.Id); if (definition.Id == previous || definition.Id == Document.Definition.Id) savedCharacters.Select(savedCharacters.ItemCount - 1); }
    }
    private void SelectPart(int index) { canvas.FinishStroke(); selectedPart = index; selectedFrame = 0; canvas.Selection = null; Refresh(); }
    private void AddPart(string name, int depth) { Document.AddPart(name, (int)newWidth.Value, (int)newHeight.Value, depth); selectedPart = Document.Definition.Parts.Count - 1; selectedFrame = 0; Refresh(); }
    private void AddFrame(bool duplicate) { if (CurrentPart is { } p) { selectedFrame = Document.AddFrame(p.Id, duplicate ? selectedFrame : null); Refresh(); } }
    private void DeleteFrame() { if (CurrentPart is not { } p) return; try { Document.RemoveFrame(p.Id, selectedFrame); Refresh(); } catch (Exception e) { ShowMessage(e.Message); } }
    private void NewDocument() { Document = NativeCharacterDocument.Create(); selectedPart = selectedFrame = 0; Refresh(); }
    private void LoadSelected()
    {
        if (savedCharacters.Selected < 0) { ShowMessage("暂无可载入的角色。"); return; }
        try
        {
            string id=savedCharacters.GetItemMetadata(savedCharacters.Selected).AsString();
            if(registry.Characters.TryGetValue(id,out var registered)&&!File.Exists(Path.Combine(repository.Store.Characters,id+".character.json")))
            {
                var copy=System.Text.Json.JsonSerializer.Deserialize<CharacterDefinition>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(registered,NativeContentStore.Json),NativeContentStore.Json)!;
                copy.Id=Guid.NewGuid().ToString("N");copy.Name+="（副本）";Document=repository.LoadDefinition(copy);
            }
            else Document=repository.Load(id);
            selectedPart=selectedFrame=0;canvas.Selection=null;Refresh();
        }
        catch (Exception e) { ShowMessage(NativeLocalization.Text("载入失败：") + NativeLocalization.Text(e.Message)); }
    }
    private void SaveRequested()
    {
        canvas.FinishStroke();
        if (File.Exists(repository.DefinitionPath(Document.Definition.Id))) Confirm("将覆盖同一 ID 的已保存角色。确定保存当前修改？", () => Save(true));
        else Save(false);
    }
    private void Save(bool overwrite)
    {
        try { repository.Save(Document, registry, overwrite); RefreshSaved(); RefreshTitle(); }
        catch (Exception e) { ShowMessage(NativeLocalization.Text("保存失败：") + NativeLocalization.Text(e.Message)); }
    }
    private void ConfirmUnsaved(Action action) { canvas.FinishStroke(); if (Document.Dirty) Confirm("当前角色有未保存的修改。确定放弃这些修改？", action); else action(); }
    private void Confirm(string text, Action action) { pendingConfirmation = action; confirmation.DialogText = NativeLocalization.Text(text); confirmation.PopupCentered(); }
    private void ShowMessage(string text) { message.DialogText = NativeLocalization.Text(text); message.PopupCentered(); }
    private void RequestClose() => ConfirmUnsaved(() => { runtimePlay.ButtonPressed = false; Hide(); Closed?.Invoke(); QueueFree(); });
    private void Undo() { canvas.FinishStroke(); Document.Undo(); Refresh(); }
    private void Redo() { canvas.FinishStroke(); Document.Redo(); Refresh(); }
    private void Copy()
    {
        if (CurrentPart is not { } part) return;
        try { clipboard = Document.Copy(part.Id, selectedFrame, canvas.Selection); if (DisplayServer.GetName() != "headless") DisplayServer.ClipboardSet(clipboard.Encode()); status.Text = string.Format(NativeLocalization.Text("已复制 {0}×{1}，包含全部 RGBA 通道。"), clipboard.Width, clipboard.Height); }
        catch (Exception e) { ShowMessage(e.Message); }
    }
    private void Paste()
    {
        if (CurrentPart is not { } part) return;
        var data = DisplayServer.GetName() != "headless" ? NativePixelClipboard.Decode(DisplayServer.ClipboardGet()) ?? clipboard : clipboard;
        if (data is null) { ShowMessage("请先复制像素选区或整帧。"); return; }
        var position = canvas.Selection is { } rect ? new Vector2I(rect.X, rect.Y) : canvas.Cursor;
        canvas.Selection = Document.Paste(part.Id, selectedFrame, data, position.X, position.Y); OnPixelsEdited();
    }
    private void OnPixelsEdited() { canvas.Invalidate(); composite.Invalidate(); RefreshTitle(); }
    public void SetPreviewState(string text) { if (runtimeState is not null) runtimeState.Text = NativeLocalization.Text(text); }
    public void ApplyRuntimePreview()
    {
        try
        {
            Document.EndEdit(); Document.Validate(); var staged = Document.Duplicate();
            string revision = Guid.NewGuid().ToString("N");
            foreach (var part in staged.Definition.Parts) for (int f = 0; f < part.Frames.Count; f++)
            {
                var frame = part.Frames[f]; frame.Png = Path.Combine(".preview", revision, frame.Id + ".png").Replace('\\', '/');
                NativeContentStore.WriteAtomic(repository.FramePath(frame), NativeCharacterPng.Encode(part.Width, part.Height, staged.Pixels(part, f)));
            }
            PreviewRequested?.Invoke(staged.Definition);
            SetPreviewState("已将当前部件与动作配置应用到真实角色。");
        }
        catch (Exception e) { ShowMessage(NativeLocalization.Text("预览失败：") + NativeLocalization.Text(e.Message)); }
    }
    private void StepRuntime() { PreviewAdvanceRequested?.Invoke(new(left.ButtonPressed, right.ButtonPressed, jumpQueued, dashQueued)); jumpQueued = dashQueued = false; }
    public override void _Process(double delta)
    {
        if (composite is null) return;
        if (animate.ButtonPressed) composite.Advance(delta);
        if (!runtimePlay.ButtonPressed) { previewAccumulator = 0; return; }
        previewAccumulator += Math.Min(delta, 0.1);
        while (previewAccumulator >= FrameContext.FixedDelta) { previewAccumulator -= FrameContext.FixedDelta; StepRuntime(); }
    }
    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true, Echo: false } key || confirmation.Visible || message.Visible) return;
        bool handled = true;
        if (key.CtrlPressed) switch (key.Keycode) { case Key.C: Copy(); break; case Key.V: Paste(); break; case Key.Z: if (key.ShiftPressed) Redo(); else Undo(); break; case Key.Y: Redo(); break; case Key.S: SaveRequested(); break; default: handled = false; break; }
        else if (GuiGetFocusOwner() is LineEdit) return;
        else switch (key.Keycode) { case Key.B: canvas.Tool = NativeCharacterTool.Brush; break; case Key.E: canvas.Tool = NativeCharacterTool.Eraser; break; case Key.S: canvas.Tool = NativeCharacterTool.Select; break; case Key.Escape: canvas.Selection = null; canvas.QueueRedraw(); break; default: handled = false; break; }
        if (handled) GetViewport().SetInputAsHandled();
    }

    public string RunUiAudit(string outputDirectory)
    {
        if (!IsNodeReady()) throw new InvalidOperationException("编辑器必须先加入场景树。");
        Directory.CreateDirectory(outputDirectory); NewDocument();
        characterName.Text = "UI 像素审核"; characterName.EmitSignal(LineEdit.SignalName.TextChanged, characterName.Text); Document.Definition.Dependencies.Add("missing.example.dependency");
        Document.Definition.Extra = new() { ["futureMetadata"] = JsonSerializer.SerializeToElement(new { keep = true }) };
        SelectPart(1); canvas.PaintColor = new Color(0.8f, 0.2f, 0.4f, 0.5f); canvas.Tool = NativeCharacterTool.Brush;
        canvas.AuditStroke([new(3, 4), new(4, 4), new(5, 4), new(5, 5)]);
        Document.Paint(CurrentPart!.Id, selectedFrame, 4, 5, 19, 71, 211, 0); OnPixelsEdited();
        canvas.Selection = new(3, 4, 4, 3); _UnhandledKeyInput(new InputEventKey { Keycode = Key.C, CtrlPressed = true, Pressed = true });
        var source = clipboard!; File.WriteAllBytes(Path.Combine(outputDirectory, "source.png"), NativeCharacterPng.Encode(source.Width, source.Height, source.Rgba));
        SelectPart(2); canvas.Selection = new(7, 8, 4, 3); _UnhandledKeyInput(new InputEventKey { Keycode = Key.V, CtrlPressed = true, Pressed = true });
        var pasted = Document.Copy(CurrentPart!.Id, 0, canvas.Selection); File.WriteAllBytes(Path.Combine(outputDirectory, "pasted.png"), NativeCharacterPng.Encode(pasted.Width, pasted.Height, pasted.Rgba));
        _UnhandledKeyInput(new InputEventKey { Keycode = Key.Z, CtrlPressed = true, Pressed = true }); var undo = Document.Copy(CurrentPart!.Id, 0, new(7, 8, 4, 3)); File.WriteAllBytes(Path.Combine(outputDirectory, "undo.png"), NativeCharacterPng.Encode(undo.Width, undo.Height, undo.Rgba)); Redo();
        AddFrame(true); canvas.PaintColor = new Color(0.1f, 0.9f, 0.3f, 0.75f); canvas.AuditStroke([new(9, 9), new(10, 9)]);
        anchorX.Value = 11; anchorY.Value = 15; offsetX.Value = 6; offsetY.Value = -3; layer.Value = -7;
        _UnhandledKeyInput(new InputEventKey { Keycode = Key.S, CtrlPressed = true, Pressed = true });
        if (Document.Dirty) throw new InvalidOperationException("首次保存未建立基线。");
        canvas.Selection = null; canvas.AuditStroke([new(12, 12)]);
        bool discarded = false; ConfirmUnsaved(() => discarded = true);
        if (!confirmation.Visible) throw new InvalidOperationException("未保存修改未显示确认。");
        confirmation.EmitSignal(ConfirmationDialog.SignalName.Canceled); confirmation.Hide();
        if (discarded || !Document.Dirty) throw new InvalidOperationException("取消丢弃未保留修改。");
        _UnhandledKeyInput(new InputEventKey { Keycode = Key.S, CtrlPressed = true, Pressed = true });
        if (!confirmation.Visible) throw new InvalidOperationException("覆盖保存未显示确认。");
        confirmation.EmitSignal(ConfirmationDialog.SignalName.Canceled); confirmation.Hide();
        if (!Document.Dirty) throw new InvalidOperationException("取消覆盖保存未保留修改。");
        _UnhandledKeyInput(new InputEventKey { Keycode = Key.S, CtrlPressed = true, Pressed = true });
        confirmation.EmitSignal(ConfirmationDialog.SignalName.Confirmed); confirmation.Hide();
        if (Document.Dirty) throw new InvalidOperationException("确认覆盖后角色仍然脏。");
        string id = Document.Definition.Id; var before = Document.Duplicate();
        LoadSelected(); if (Document.Definition.Name != "UI 像素审核" || Document.Definition.Id != id || Document.Dirty || !Document.Definition.Dependencies.Contains("missing.example.dependency") || Document.Definition.Extra?["futureMetadata"].GetProperty("keep").GetBoolean() != true) throw new InvalidOperationException("UI 载入未完整恢复角色定义。");
        int count = 0;
        foreach (var part in Document.Definition.Parts) for (int f = 0; f < part.Frames.Count; f++)
        {
            var expected = before.Pixels(before.Definition.Parts.Single(p => p.Id == part.Id), f); var actual = Document.Pixels(part, f);
            File.WriteAllBytes(Path.Combine(outputDirectory, $"before-{count}.png"), NativeCharacterPng.Encode(part.Width, part.Height, expected));
            File.WriteAllBytes(Path.Combine(outputDirectory, $"loaded-{count}.png"), NativeCharacterPng.Encode(part.Width, part.Height, actual)); count++;
        }
        NativeContentStore.Save(Path.Combine(outputDirectory, "ui-audit.json"), new { id, frames = count, copyRect = new { x = 3, y = 4, width = 4, height = 3 }, pasteRect = new { x = 7, y = 8, width = 4, height = 3 }, png = repository.DefinitionPath(id) });
        return $"CHARACTER_UI_AUDIT_OK id={id} parts={Document.Definition.Parts.Count} frames={count} rgba=separate-python-audit";
    }
}
