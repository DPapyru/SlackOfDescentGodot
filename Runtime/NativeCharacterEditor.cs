using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public readonly record struct NativeCharacterPreviewInput(bool Left, bool Right, bool Jump, bool Dash);

public partial class NativeCharacterEditor : Window
{
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private readonly NativeCharacterRepository repository;
    public string PreviewDirectory { get; } = Path.Combine(Path.GetTempPath(), "slack-character-frames-" + Guid.NewGuid().ToString("N"));
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
    private CheckBox partVisible = null!, replaceOriginal = null!;
    private OptionButton targetChoice = null!;
    private ItemList originalAssets = null!;
    private TabContainer workspace = null!;
    private readonly List<(string Name, JsonElement Data)> assets = new();
    private double previewDirtyTime;
    private bool previewDirty;
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
    private static string L(string chinese, string english) => NativeLocalization.IsChinese ? chinese : english;
    public override void _Ready()
    {
        Title = L("角色编辑器", "Character editor"); Name = "NativeCharacterEditor"; Size = new(1180, 800); MinSize = new(960, 700); Unresizable = false; Transient = true;
        CloseRequested += RequestClose;
        var margin = new MarginContainer(); AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 10);
        var root = new VBoxContainer(); margin.AddChild(root);
        var file = Row(root);
        Button(file, L("新建角色", "New character"), () => ConfirmUnsaved(NewDocument));
        savedCharacters = new OptionButton { AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new(160, 32) }; file.AddChild(savedCharacters);
        Button(file, L("载入", "Open"), () => ConfirmUnsaved(LoadSelected)); Button(file, L("保存", "Save"), SaveRequested);
        Button(file, L("保存到 Mod 项目…", "Save to Mod project…"), SaveToProject);
        characterName = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, PlaceholderText = L("角色名称", "Character name"), CustomMinimumSize = new(140, 32), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; file.AddChild(characterName);
        characterName.TextChanged += value => Change(() => Document.Definition.Name = value); Button(file, L("关闭", "Close"), RequestClose);
        var columns = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [260] }; root.AddChild(columns);
        var sidebar = new TabContainer { CustomMinimumSize = new(260, 0) }; columns.AddChild(sidebar);
        VBoxContainer Page(string title)
        {
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; sidebar.AddChild(scroll); sidebar.SetTabTitle(sidebar.GetTabCount() - 1, title);
            var page = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(page); return page;
        }
        var partPage = Page(L("部位", "Parts"));
        partPage.AddChild(new Label { Text = L("未替换的部位保留原样", "Unassigned parts keep their original appearance") });
        parts = new ItemList { AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new(245, 240) }; partPage.AddChild(parts); parts.ItemSelected += index => SelectPart((int)index);
        var add = Row(partPage); Button(add, L("＋部位", "+ Part"), () => AddPart(L("部位", "Part"), 0)); Button(add, L("＋装饰", "+ Decoration"), () => AddPart(L("装饰", "Decoration"), 20));
        var dimensions = Row(partPage); dimensions.AddChild(new Label { Text = L("尺寸", "Size") }); newWidth = Spin(dimensions, 1, 256, _ => { }); newHeight = Spin(dimensions, 1, 256, _ => { }); newWidth.Value = newHeight.Value = 32;
        Button(partPage, L("删除当前部位", "Remove selected part"), () => Confirm("删除当前部件及全部动画帧？", () => { if (CurrentPart is { } part) Document.RemovePart(part.Id); selectedPart = Math.Max(0, selectedPart - 1); Refresh(); }));
        var assetPage = Page(L("素材", "Assets"));
        Button(assetPage, L("导入 PNG／动画帧…", "Import PNG / animation frames…"), () => ImportPng(false));
        Button(assetPage, L("追加 PNG 动画帧…", "Append PNG frames…"), () => ImportPng(true));
        var search = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, PlaceholderText = L("搜索原版素材 / ID", "Search original assets / ID") }; assetPage.AddChild(search);
        originalAssets = new ItemList { AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new(245, 380) }; assetPage.AddChild(originalAssets);
        search.TextChanged += PopulateAssets; Button(assetPage, L("使用所选原版素材", "Use selected original asset"), ImportOriginal); LoadAssets(); PopulateAssets("");
        var properties = Page(L("属性", "Properties"));
        partName = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, PlaceholderText = L("部位名称", "Part name") }; properties.AddChild(partName); partName.TextChanged += value => { if (CurrentPart is { } part) Change(() => part.Name = value); };
        properties.AddChild(new Label { Text = L("绑定部位", "Attach to part") }); targetChoice = new OptionButton(); properties.AddChild(targetChoice);
        foreach (string name in NativeCharacterTargets.Names) targetChoice.AddItem(name);
        targetChoice.ItemSelected += index => { if (CurrentPart is { } part) Change(() => part.Target = NativeCharacterTargets.Names[(int)index]); };
        replaceOriginal = new CheckBox { Text = L("替换此部位的原纹理", "Replace original part textures") }; properties.AddChild(replaceOriginal); replaceOriginal.Toggled += value => { if (CurrentPart is { } part) Change(() => part.ReplaceOriginal = value); };
        partVisible = new CheckBox { Text = L("显示部位", "Visible") }; properties.AddChild(partVisible); partVisible.Toggled += value => { if (CurrentPart is { } part) Change(() => part.Visible = value); };
        anchorX = PropertySpin(properties, L("锚点 X", "Anchor X"), value => { if (CurrentPart is { } p) Change(() => p.AnchorX = (int)value); });
        anchorY = PropertySpin(properties, L("锚点 Y", "Anchor Y"), value => { if (CurrentPart is { } p) Change(() => p.AnchorY = (int)value); });
        offsetX = PropertySpin(properties, L("偏移 X", "Offset X"), value => { if (CurrentPart is { } p) Change(() => p.OffsetX = (int)value); });
        offsetY = PropertySpin(properties, L("偏移 Y", "Offset Y"), value => { if (CurrentPart is { } p) Change(() => p.OffsetY = (int)value); });
        layer = PropertySpin(properties, L("图层", "Layer"), value => { if (CurrentPart is { } p) Change(() => p.Layer = (int)value); });
        workspace = new TabContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; columns.AddChild(workspace);
        var preview = new VBoxContainer(); workspace.AddChild(preview); workspace.SetTabTitle(0, L("完整角色", "Complete character"));
        PreviewSurface.CustomMinimumSize = new(640, 360); PreviewSurface.SizeFlagsVertical = Control.SizeFlags.ExpandFill; preview.AddChild(PreviewSurface);
        runtimeState = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = L("加载独立动作预览…", "Loading action preview…") }; preview.AddChild(runtimeState);
        var settings = Row(preview); settings.AddChild(new Label { Text = L("基础行为", "Base behavior") });
        preset = new OptionButton { AutoTranslateMode = AutoTranslateModeEnum.Disabled }; foreach (var value in Presets) preset.AddItem(value); settings.AddChild(preset);
        preset.ItemSelected += index => Change(() => { Document.Definition.BasePreset = preset.GetItemText((int)index); Document.Definition.Appearance = "Character-" + Document.Definition.BasePreset; });
        settings.AddChild(new Label { Text = L("跳跃", "Jump") }); jump = ActionChoice(settings, CharacterActionSlot.Jump);
        settings.AddChild(new Label { Text = L("冲刺", "Dash") }); dash = ActionChoice(settings, CharacterActionSlot.Dash);
        var runtimeButtons = Row(preview); Button(runtimeButtons, L("应用到预览", "Apply to preview"), ApplyRuntimePreview); Button(runtimeButtons, L("重置", "Reset"), () => PreviewResetRequested?.Invoke());
        runtimePlay = new CheckButton { Text = L("播放动作", "Play actions") }; runtimeButtons.AddChild(runtimePlay); Button(runtimeButtons, L("单步", "Step"), StepRuntime);
        var movement = Row(preview); left = new CheckButton { Text = L("左", "Left") }; movement.AddChild(left); right = new CheckButton { Text = L("右", "Right") }; movement.AddChild(right); Button(movement, L("跳跃", "Jump"), () => { jumpQueued = true; StepRuntime(); }); Button(movement, L("冲刺", "Dash"), () => { dashQueued = true; StepRuntime(); });
        var pixel = new VBoxContainer(); workspace.AddChild(pixel); workspace.SetTabTitle(1, L("部位像素", "Part pixels"));
        var tools = Row(pixel); var group = new ButtonGroup();
        foreach (var (text, tool) in new[] { (L("画笔 B", "Brush B"), NativeCharacterTool.Brush), (L("橡皮 E", "Eraser E"), NativeCharacterTool.Eraser), (L("选区 S", "Select S"), NativeCharacterTool.Select) })
        { var button = new Button { Text = text, ToggleMode = true, ButtonGroup = group, ButtonPressed = tool == NativeCharacterTool.Brush }; tools.AddChild(button); button.Pressed += () => canvas.Tool = tool; }
        color = new ColorPickerButton { Color = new Color(0.3f, 0.7f, 1, 1), CustomMinimumSize = new(50, 32), EditAlpha = true }; tools.AddChild(color); color.ColorChanged += value => canvas.PaintColor = value;
        var zoom = Spin(tools, 2, 24, value => { if (canvas is not null) canvas.Zoom = (int)value; }); zoom.Value = 10;
        Button(tools, L("撤销", "Undo"), Undo); Button(tools, L("重做", "Redo"), Redo); Button(tools, L("复制", "Copy"), Copy); Button(tools, L("粘贴", "Paste"), Paste);
        var canvasScroll = new ScrollContainer { CustomMinimumSize = new(350, 300), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; pixel.AddChild(canvasScroll);
        canvas = new NativeCharacterPixelCanvas { Name = "像素画布", PaintColor = color.Color }; canvasScroll.AddChild(canvas); canvas.Edited += OnPixelsEdited;
        composite = new NativeCharacterComposite { CustomMinimumSize = new(240, 100) }; pixel.AddChild(composite);
        animate = new CheckButton { Text = L("播放部位动画", "Play part animation"), ButtonPressed = true }; pixel.AddChild(animate);
        var timeline = Row(pixel); frames = new OptionButton { CustomMinimumSize = new(90, 32) }; timeline.AddChild(frames); frames.ItemSelected += index => { selectedFrame = (int)index; Refresh(); };
        Button(timeline, L("＋帧", "+ Frame"), () => AddFrame(false)); Button(timeline, L("复制帧", "Duplicate frame"), () => AddFrame(true)); Button(timeline, L("删除帧", "Delete frame"), DeleteFrame);
        timeline.AddChild(new Label { Text = L("拍数", "Ticks") }); duration = Spin(timeline, 1, 600, value => { if (CurrentPart is { } p) Change(() => p.Frames[selectedFrame].Duration = (int)value); });
        timeline.AddChild(new Label { Text = "FPS" }); fps = Spin(timeline, 1, 60, value => Change(() => Document.Definition.AnimationFps = value));
        status = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart }; root.AddChild(status);
        confirmation = new ConfirmationDialog { Title = L("请确认", "Confirm"), OkButtonText = L("确定", "OK"), CancelButtonText = L("取消", "Cancel") }; AddChild(confirmation);
        confirmation.Confirmed += () => { var action = pendingConfirmation; pendingConfirmation = null; action?.Invoke(); }; confirmation.Canceled += () => pendingConfirmation = null;
        message = new AcceptDialog { Title = L("角色编辑器", "Character editor"), OkButtonText = L("确定", "OK") }; AddChild(message);
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
        Document.Edit(action); previewDirty = true; previewDirtyTime = 0; RefreshTitle(); composite.Invalidate(); canvas.QueueRedraw();
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
                partName.Text = p.Name; partVisible.ButtonPressed = p.Visible; replaceOriginal.ButtonPressed = p.ReplaceOriginal; targetChoice.Select(Array.IndexOf(NativeCharacterTargets.Names, p.Target));
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
        Title = (Document.Dirty ? "＊ " : "") + L("角色编辑器", "Character editor") + " — " + Document.Definition.Name;
        if (status is not null) status.Text = string.Format(NativeLocalization.Text("{0}　ID：{1}　部件：{2}　撤销：{3}　依赖：{4}"), NativeLocalization.Text(Document.Dirty ? "尚未保存" : "已保存"), Document.Definition.Id, Document.Definition.Parts.Count, Document.UndoCount, string.Join("、", Document.Definition.Dependencies));
        if(status is not null&&Document.MissingFrames.Count>0)status.Text+="\n"+NativeLocalization.Text("缺少或损坏的帧（保留原始引用）：")+string.Join("、",Document.MissingFrames.Values);
    }
    private void RefreshSaved()
    {
        string? previous = savedCharacters.Selected >= 0 ? savedCharacters.GetItemMetadata(savedCharacters.Selected).AsString() : null;
        savedCharacters.Clear();
        foreach (var definition in registry.Characters.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        { savedCharacters.AddItem(NativeContentLocalization.Display(registry,definition.Id,definition.Name)); savedCharacters.SetItemMetadata(savedCharacters.ItemCount - 1, definition.Id); if (definition.Id == previous || definition.Id == Document.Definition.Id) savedCharacters.Select(savedCharacters.ItemCount - 1); }
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
                copy.Id=Guid.NewGuid().ToString("N");copy.Name+="（副本）";Document=repository.LoadDefinition(copy, registry.CharacterDirectories.GetValueOrDefault(id));
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
    private void OnPixelsEdited() { previewDirty = true; previewDirtyTime = 0; canvas.Invalidate(); composite.Invalidate(); RefreshTitle(); }
    public void SetPreviewState(string text) { if (runtimeState is not null) runtimeState.Text = NativeLocalization.Text(text); }
    public void ApplyRuntimePreview()
    {
        try
        {
            Document.EndEdit(); Document.Validate(); var staged = Document.Duplicate();
            string revision = Guid.NewGuid().ToString("N");
            foreach (var part in staged.Definition.Parts) for (int f = 0; f < part.Frames.Count; f++)
            {
                var frame = part.Frames[f]; frame.Png = Path.Combine("frames", revision, frame.Id + ".png").Replace('\\', '/');
                NativeContentStore.WriteAtomic(repository.FramePath(frame, PreviewDirectory), NativeCharacterPng.Encode(part.Width, part.Height, staged.Pixels(part, f)));
            }
            previewDirty = false; PreviewRequested?.Invoke(staged.Definition);
            SetPreviewState("已将当前部件与动作配置应用到真实角色。");
        }
        catch (Exception e) { ShowMessage(NativeLocalization.Text("预览失败：") + NativeLocalization.Text(e.Message)); }
    }
    private void StepRuntime() { PreviewAdvanceRequested?.Invoke(new(left.ButtonPressed, right.ButtonPressed, jumpQueued, dashQueued)); jumpQueued = dashQueued = false; }
    public override void _Process(double delta)
    {
        if (composite is null) return;
        if (previewDirty && PreviewRequested is not null && (previewDirtyTime += delta) >= 0.3) ApplyRuntimePreview();
        if (animate.ButtonPressed) composite.Advance(delta);
        if (!runtimePlay.ButtonPressed) { previewAccumulator = 0; return; }
        previewAccumulator += Math.Min(delta, 0.1);
        while (previewAccumulator >= FrameContext.FixedDelta) { previewAccumulator -= FrameContext.FixedDelta; StepRuntime(); }
    }
    public override void _ExitTree()
    {
        if (Directory.Exists(PreviewDirectory)) Directory.Delete(PreviewDirectory, true);
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
        Directory.CreateDirectory(outputDirectory); RunAssetUiAudit(outputDirectory); NewDocument();
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
