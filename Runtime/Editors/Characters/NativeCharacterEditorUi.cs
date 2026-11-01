using Godot;
using System.IO.Compression;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeCharacterEditor
{
    private ItemList layerList = null!, historyList = null!, timelineList = null!;
    private LineEdit assetSearch = null!;
    private TextureRect assetPreview = null!;
    private Label assetInfo = null!, frameStatus = null!;
    private VBoxContainer leftDock = null!, rightDock = null!, timelineDock = null!;
    private HSplitContainer mainSplit = null!;
    private Button bodyButton = null!, gridButton = null!, playButton = null!, modeButton = null!;
    private readonly Dictionary<string, ImageTexture> editorTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> editorActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> editorKeys = new(StringComparer.Ordinal);
    private string? selectedAsset;
    private double timelineElapsed, draftElapsed;
    private bool draftPending, timelinePlaying;

    private void BuildEditorLayout()
    {
        var root = new VBoxContainer(); AddChild(root); root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        BuildActions();
        var menuBar = Row(root); menuBar.Name = "MenuBar";
        AddMenu(menuBar, "文件", FileMenu); AddMenu(menuBar, "编辑", EditMenu); AddMenu(menuBar, "视图", ViewMenu);
        AddMenu(menuBar, "角色", CharacterMenu); AddMenu(menuBar, "窗口", WindowMenu); AddMenu(menuBar, "帮助", HelpMenu);
        var brand = new Label { Text = L("角色编辑器", "Character editor"), HorizontalAlignment = HorizontalAlignment.Right, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        menuBar.AddChild(brand);

        var tabs = Row(root); tabs.Name = "DocumentTabs";
        var docName = new Label { Name = "DocumentName", Text = Document.Definition.Name, CustomMinimumSize = new(170, 0) }; tabs.AddChild(docName);
        var close = Button(tabs, "×", RequestClose); close.TooltipText = L("关闭文档", "Close document");
        Button(tabs, "+", () => ConfirmUnsaved(NewDocument)).TooltipText = L("新建角色", "New character");

        var options = Row(root); options.Name = "OptionBar";
        modeButton = new Button { Text = L("完整角色", "Full character"), ToggleMode = true, ButtonPressed = true, Name = "ModeToggle" }; options.AddChild(modeButton);
        modeButton.Toggled += value => { overallMode.ButtonPressed = value; canvas.Mode = value ? NativeCharacterCanvasMode.Overall : NativeCharacterCanvasMode.Part; Refresh(); };
        bodyButton = new Button { Text = L("身体参照", "Body reference"), ToggleMode = true, ButtonPressed = true }; options.AddChild(bodyButton);
        bodyButton.Toggled += value => { canvas.ShowBody = value; canvas.QueueRedraw(); };
        gridButton = new Button { Text = L("网格", "Grid"), ToggleMode = true, ButtonPressed = true }; options.AddChild(gridButton);
        gridButton.Toggled += value => { canvas.ShowGrid = value; canvas.QueueRedraw(); };
        options.AddChild(new Label { Text = L("参照透明度", "Reference opacity") });
        referenceOpacity = Spin(options, 0, 100, value => { if (canvas is not null) { canvas.ReferenceOpacity = (float)value / 100; canvas.QueueRedraw(); } }); referenceOpacity.Value = 35;
        Button(options, "−", () => canvas.Zoom--).TooltipText = L("缩小", "Zoom out");
        zoomSpin = Spin(options, 1, 24, value => { if (canvas is not null) canvas.Zoom = (int)value; }); zoomSpin.Value = 8;
        Button(options, "+", () => canvas.Zoom++).TooltipText = L("放大", "Zoom in");
        Button(options, L("适应", "Fit"), FitEditorView);
        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; options.AddChild(spacer);
        frameStatus = new Label { Name = "FrameStatus" }; options.AddChild(frameStatus);

        var editorRegion = new HSplitContainer { Name = "EditorRegion", SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [48] }; root.AddChild(editorRegion);
        var rail = new VBoxContainer { Name = "ToolRail", CustomMinimumSize = new(42, 0) }; editorRegion.AddChild(rail);
        var centerGroup = new HSplitContainer { Name = "EditorSplit", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [250] }; editorRegion.AddChild(centerGroup);
        BuildToolRail(rail);
        leftDock = new VBoxContainer { Name = "AssetsDock", CustomMinimumSize = new(210, 0) }; centerGroup.AddChild(leftDock);
        BuildAssetsDock();
        var sideSplit = new HSplitContainer { Name = "CanvasInspectorSplit", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [810] }; centerGroup.AddChild(sideSplit);
        mainSplit = sideSplit;
        var centerDock = new VBoxContainer { Name = "CanvasDock", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(390, 0) }; mainSplit.AddChild(centerDock);
        BuildCanvasDock(centerDock);
        rightDock = new VBoxContainer { Name = "InspectorDock", CustomMinimumSize = new(250, 0) }; sideSplit.AddChild(rightDock);
        BuildInspectorDock();
        timelineDock = new VBoxContainer { Name = "TimelineDock", CustomMinimumSize = new(0, 116) }; root.AddChild(timelineDock);
        BuildTimelineDock();
        status = new Label { Name = "Status", CustomMinimumSize = new(0, 22) }; root.AddChild(status);
        parts = layerList;
        frames = new OptionButton();
    }

    private void BuildToolRail(VBoxContainer rail)
    {
        var group = new ButtonGroup(); var buttons = new Dictionary<NativeCharacterTool, Button>();
        foreach (var (icon, tool, key) in new[] { ("↖", NativeCharacterTool.Select, "V"), ("✎", NativeCharacterTool.Brush, "B"), ("▱", NativeCharacterTool.Eraser, "E"), ("⌖", NativeCharacterTool.Eyedrop, "I"), ("✋", NativeCharacterTool.Pan, "H"), ("＋", NativeCharacterTool.Zoom, "Z") })
        {
            var button = new Button { Text = icon, ToggleMode = true, ButtonGroup = group, ButtonPressed = tool == NativeCharacterTool.Brush, TooltipText = tool + " (" + key + ")", CustomMinimumSize = new(34, 32) };
            rail.AddChild(button); button.Pressed += () => { canvas.Tool = tool; foreach (var item in buttons) item.Value.SetPressedNoSignal(item.Key == tool); }; buttons[tool] = button;
        }
        SetTool = tool => { if (canvas is null) return; canvas.Tool = tool; foreach (var item in buttons) item.Value.SetPressedNoSignal(item.Key == tool); };
        rail.AddChild(new HSeparator());
        var bodyToggle = new Button { Text = "B", ToggleMode = true, ButtonPressed = true, TooltipText = L("显示身体参照", "Show base body"), CustomMinimumSize = new(34, 30) }; rail.AddChild(bodyToggle);
        bodyToggle.Toggled += value => bodyButton.ButtonPressed = value;
        var gridToggle = new Button { Text = "#", ToggleMode = true, ButtonPressed = true, TooltipText = L("显示网格", "Show pixel grid"), CustomMinimumSize = new(34, 30) }; rail.AddChild(gridToggle);
        gridToggle.Toggled += value => gridButton.ButtonPressed = value;
    }

    private void BuildAssetsDock()
    {
        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; leftDock.AddChild(tabs);
        var assetPage = new VBoxContainer { Name = "素材" }; tabs.AddChild(assetPage);
        assetSearch = new LineEdit { Name = "AssetSearch", PlaceholderText = L("搜索素材", "Search assets") }; assetPage.AddChild(assetSearch);
        assetSearch.TextChanged += PopulateAssets;
        originalAssets = new ItemList { Name = "OriginalAssets", AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; assetPage.AddChild(originalAssets);
        originalAssets.ItemSelected += index => SelectAsset((int)index);
        assetPreview = new TextureRect { Name = "AssetPreview", CustomMinimumSize = new(0, 96), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = CanvasItem.TextureFilterEnum.Nearest }; assetPage.AddChild(assetPreview);
        assetInfo = new Label { Name = "AssetInfo", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new(0, 28) }; assetPage.AddChild(assetInfo);
        var assetButtons = Row(assetPage); Button(assetButtons, L("替换当前部位", "Replace part"), ImportOriginal); Button(assetButtons, L("导入 PNG…", "Import PNG…"), () => ImportPng(false));
        Button(assetPage, L("追加动画帧…", "Append frames…"), () => ImportPng(true));
        tabs.SetTabTitle(0, L("素材", "Assets"));
        var basePage = new VBoxContainer { Name = "基底" }; tabs.AddChild(basePage); tabs.SetTabTitle(1, L("基底", "Base"));
        basePage.AddChild(new Label { Text = L("基础角色", "Base character") });
        preset = new OptionButton(); foreach (string name in Presets) preset.AddItem(name); basePage.AddChild(preset);
        preset.ItemSelected += index => Change(() => { Document.Definition.BasePreset = preset.GetItemText((int)index); Document.Definition.Appearance = "Character-" + Document.Definition.BasePreset; });
        Button(basePage, L("载入原版角色", "Load original character"), LoadOriginalCharacter);
        Button(basePage, L("生成基底部件", "Generate base parts"), GenerateBase);
        Button(basePage, L("选择 PNG / 动画帧…", "Import PNG / frames…"), () => ImportPng(false));
        var size = Row(basePage); size.AddChild(new Label { Text = L("新部位尺寸", "New part size") });
        newWidth = Spin(size, 1, 256, _ => { }); newHeight = Spin(size, 1, 256, _ => { }); newWidth.Value = newHeight.Value = 32;
        Button(basePage, L("＋部位", "+ Part"), () => AddPart(L("部位", "Part"), 0));
        Button(basePage, L("＋装饰", "+ Decoration"), () => AddPart(L("装饰", "Decoration"), 20));
        var hint = new Label { Text = L("支持拖入 PNG 文件。原版部件按游戏锚点和骨骼组合。", "Drop PNG files to import. Original parts use game anchors and bones."), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        basePage.AddChild(hint);
        LoadAssets(); PopulateAssets("");
    }

    private void BuildCanvasDock(VBoxContainer center)
    {
        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; center.AddChild(tabs);
        var pixel = new VBoxContainer { Name = "像素画布" }; tabs.AddChild(pixel); tabs.SetTabTitle(0, L("完整角色", "Complete character"));
        var toolbar = new HFlowContainer(); pixel.AddChild(toolbar);
        color = new ColorPickerButton { Name = "PaintColor", Color = new Color(0.3f, 0.7f, 1, 1), EditAlpha = true, CustomMinimumSize = new(42, 30) }; toolbar.AddChild(color); color.ColorChanged += value => canvas.PaintColor = value;
        toolbar.AddChild(new Label { Text = L("画笔", "Brush") }); brushSize = Spin(toolbar, 1, 16, value => canvas.BrushSize = (int)value); brushSize.Value = 1;
        Button(toolbar, L("全选", "Select all"), () => canvas.SelectAll()); Button(toolbar, L("填充", "Fill"), () => FillSelection(color.Color));
        Button(toolbar, L("清除", "Clear"), ClearSelection); Button(toolbar, "↶", Undo); Button(toolbar, "↷", Redo);
        Button(toolbar, "⧉", Copy); Button(toolbar, "▣", Paste);
        var canvasScroll = new ScrollContainer { Name = "CanvasScroll", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(300, 260), HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        pixel.AddChild(canvasScroll);
        canvas = new NativeCharacterPixelCanvas { Name = "CharacterCanvas", PaintColor = color.Color, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        canvas.ConfigureBody(world); canvasScroll.AddChild(canvas); canvas.Edited += OnPixelsEdited;
        canvas.ZoomChanged += () => { if (!fitting) autoFit = false; if (zoomSpin is not null && Math.Abs(zoomSpin.Value - canvas.Zoom) > 0.5) zoomSpin.SetValueNoSignal(canvas.Zoom); };
        canvasScroll.Resized += () => { if (autoFit && !fitting) FitEditorView(); };
        canvas.ColorPicked += value => { color.Color = value; canvas.PaintColor = value; };
        bodyButton.Toggled += value => { canvas.ShowBody = value; canvas.QueueRedraw(); FitEditorView(); };
        gridButton.Toggled += value => { canvas.ShowGrid = value; canvas.QueueRedraw(); };
        referenceOpacity.ValueChanged += value => { canvas.ReferenceOpacity = (float)value / 100; canvas.QueueRedraw(); };
        var controls = Row(pixel);
        showReference = new CheckButton { Text = L("显示基底参照", "Show other parts"), ButtonPressed = true }; controls.AddChild(showReference);
        showReference.Toggled += value => { canvas.ShowReference = value; canvas.QueueRedraw(); };
        overallMode = new CheckButton { Text = L("完整角色", "Full character"), ButtonPressed = true }; controls.AddChild(overallMode);
        overallMode.Toggled += value => { modeButton.SetPressedNoSignal(value); canvas.Mode = value ? NativeCharacterCanvasMode.Overall : NativeCharacterCanvasMode.Part; FitEditorView(); Refresh(); };
        animate = new CheckButton { Text = L("播放动画", "Animate"), ButtonPressed = true }; controls.AddChild(animate);
        animate.Toggled += value => { timelinePlaying = value; playButton?.SetPressedNoSignal(value); timelineElapsed = 0; canvas.Playing = value; composite.Playing = value; };
        var actions = new VBoxContainer { Name = "RuntimePreview", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; tabs.AddChild(actions); tabs.SetTabTitle(1, L("真实动作预览", "Action preview"));
        workspace = tabs;
        composite = new NativeCharacterComposite { Name = "CharacterComposite", CustomMinimumSize = new(240, 140), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; composite.ConfigureBody(world); actions.AddChild(composite);
        PreviewSurface.SizeFlagsVertical = Control.SizeFlags.ExpandFill; PreviewSurface.CustomMinimumSize = new(260, 180); actions.AddChild(PreviewSurface);
        runtimeState = new Label { Name = "RuntimeState", AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = L("真实角色预览", "Live character preview") }; actions.AddChild(runtimeState);
        var runtime = Row(actions);
        runtime.AddChild(new Label { Text = L("跳跃动作", "Jump action") }); jump = ActionChoice(runtime, CharacterActionSlot.Jump);
        runtime.AddChild(new Label { Text = L("冲刺动作", "Dash action") }); dash = ActionChoice(runtime, CharacterActionSlot.Dash);
        var buttons = Row(actions); Button(buttons, L("应用到预览", "Apply to preview"), ApplyRuntimePreview); Button(buttons, L("重置", "Reset"), () => PreviewResetRequested?.Invoke());
        runtimePlay = new CheckButton { Text = L("播放动作", "Play") }; buttons.AddChild(runtimePlay);
        var movement = Row(actions); left = new CheckButton { Text = L("左", "Left") }; movement.AddChild(left); right = new CheckButton { Text = L("右", "Right") }; movement.AddChild(right);
        Button(movement, L("跳跃", "Jump"), () => { jumpQueued = true; StepRuntime(); }); Button(movement, L("冲刺", "Dash"), () => { dashQueued = true; StepRuntime(); }); Button(movement, L("单步", "Step"), StepRuntime);
    }

    private void BuildInspectorDock()
    {
        var layerPage = new VBoxContainer { Name = "图层", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; rightDock.AddChild(layerPage);
        layerPage.AddChild(new Label { Text = L("图层", "Layers") });
        layerList = new ItemList { Name = "LayerList", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; layerPage.AddChild(layerList);
        layerList.ItemSelected += index => SelectPart((int)index);
        var layerActions = Row(layerPage); Button(layerActions, L("＋部位", "+ Part"), () => AddPart("部位", 0)); Button(layerActions, L("＋装饰", "+ Decoration"), () => AddPart("装饰", 20)); Button(layerActions, L("删除", "Delete"), RemoveSelectedPart);
        var propertyPage = new ScrollContainer { Name = "属性", CustomMinimumSize = new(0, 300), SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; rightDock.AddChild(propertyPage);
        var propertyBody = new VBoxContainer(); propertyPage.AddChild(propertyBody);
        characterName = new LineEdit { Name = "CharacterName", PlaceholderText = L("角色名称", "Character name") }; propertyBody.AddChild(characterName);
        characterName.TextChanged += value => Change(() => Document.Definition.Name = value);
        savedCharacters = new OptionButton { Name = "SavedCharacters" }; propertyBody.AddChild(savedCharacters);
        Button(propertyBody, L("载入已保存角色", "Load saved character"), () => ConfirmUnsaved(LoadSelected));
        Button(propertyBody, L("保存角色", "Save character"), SaveRequested); Button(propertyBody, L("保存到 Mod 项目…", "Save to Mod project…"), SaveToProject);
        partName = new LineEdit { Name = "PartName", PlaceholderText = L("部位名称", "Part name") }; propertyBody.AddChild(partName);
        partName.TextChanged += value => { if (CurrentPart is { } part) Change(() => part.Name = value); };
        propertyBody.AddChild(new Label { Text = L("绑定骨骼", "Attach point") }); targetChoice = new OptionButton(); propertyBody.AddChild(targetChoice);
        foreach (string name in NativeCharacterTargets.Names) targetChoice.AddItem(name);
        targetChoice.ItemSelected += index => { if (CurrentPart is { } part) Change(() => part.Target = NativeCharacterTargets.Names[(int)index]); };
        replaceOriginal = new CheckBox { Text = L("替换原版部位", "Replace original part") }; propertyBody.AddChild(replaceOriginal);
        replaceOriginal.Toggled += value => { if (CurrentPart is { } part) Change(() => part.ReplaceOriginal = value); };
        partVisible = new CheckBox { Text = L("显示部位", "Visible") }; propertyBody.AddChild(partVisible);
        partVisible.Toggled += value => { if (CurrentPart is { } part) Change(() => part.Visible = value); };
        anchorX = PropertySpin(propertyBody, "锚点 X", value => { if (CurrentPart is { } part) Change(() => part.AnchorX = (int)value); });
        anchorY = PropertySpin(propertyBody, "锚点 Y", value => { if (CurrentPart is { } part) Change(() => part.AnchorY = (int)value); });
        offsetX = PropertySpin(propertyBody, "偏移 X", value => { if (CurrentPart is { } part) Change(() => part.OffsetX = (int)value); });
        offsetY = PropertySpin(propertyBody, "偏移 Y", value => { if (CurrentPart is { } part) Change(() => part.OffsetY = (int)value); });
        layer = PropertySpin(propertyBody, "图层", value => { if (CurrentPart is { } part) Change(() => part.Layer = (int)value); });
        propertyBody.AddChild(new Label { Text = L("图像尺寸", "Image size") });
        var dimensions = Row(propertyBody); newWidth = Spin(dimensions, 1, 256, _ => { }); newHeight = Spin(dimensions, 1, 256, _ => { }); newWidth.Value = newHeight.Value = 32;
        var historyPage = new VBoxContainer { Name = "历史", CustomMinimumSize = new(0, 120) }; rightDock.AddChild(historyPage);
        historyList = new ItemList { Name = "HistoryList", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; historyPage.AddChild(historyList);
        historyList.ItemSelected += index => JumpHistory((int)index);
        Button(historyPage, L("撤销", "Undo"), Undo); Button(historyPage, L("重做", "Redo"), Redo);
    }

    private void BuildTimelineDock()
    {
        var header = Row(timelineDock); var collapse = Button(header, "▾", () => timelineDock.Visible = !timelineDock.Visible); collapse.TooltipText = L("折叠时间轴", "Collapse timeline");
        header.AddChild(new Label { Text = L("时间轴", "Timeline") });
        Button(header, "＋", () => AddFrame(false)); Button(header, L("复制帧", "Duplicate frame"), () => AddFrame(true)); Button(header, L("删除帧", "Delete frame"), DeleteFrame);
        header.AddChild(new Label { Text = L("拍数", "Ticks") }); duration = Spin(header, 1, 600, value => { if (CurrentPart is { } part) Change(() => part.Frames[selectedFrame].Duration = (int)value); });
        header.AddChild(new Label { Text = "FPS" }); fps = Spin(header, 1, 60, value => Change(() => Document.Definition.AnimationFps = value));
        playButton = new Button { Text = "▶", ToggleMode = true, TooltipText = L("播放/暂停", "Play/pause") }; header.AddChild(playButton);
        playButton.Toggled += value => { timelinePlaying = value; animate.SetPressedNoSignal(value); canvas.Playing = value; composite.Playing = value; if (value) timelineElapsed = 0; };
        timelineList = new ItemList { Name = "FrameTimeline", CustomMinimumSize = new(0, 74), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MaxColumns = 16, IconMode = ItemList.IconModeEnum.Top, FixedColumnWidth = 68, IconScale = 0.75f };
        timelineDock.AddChild(timelineList); timelineList.ItemSelected += index => { SetTimelinePlaying(false); selectedFrame = (int)index; Refresh(); };
    }

    private void BuildActions()
    {
        AddAction("new", L("新建角色", "New character"), "Ctrl+N", () => ConfirmUnsaved(NewDocument));
        AddAction("save", L("保存", "Save"), "Ctrl+S", SaveRequested);
        AddAction("saveAs", L("导出角色包…", "Export character package…"), "Ctrl+Shift+S", ExportPackage);
        AddAction("open", L("载入角色包…", "Import character package…"), "Ctrl+O", ImportPackage);
        AddAction("undo", L("撤销", "Undo"), "Ctrl+Z", Undo); AddAction("redo", L("重做", "Redo"), "Ctrl+Shift+Z", Redo);
        AddAction("copy", L("复制选区", "Copy selection"), "Ctrl+C", Copy); AddAction("paste", L("粘贴", "Paste"), "Ctrl+V", Paste);
        AddAction("selectAll", L("全选", "Select all"), "Ctrl+A", () => canvas.SelectAll()); AddAction("clear", L("清空选区", "Clear selection"), "Delete", ClearSelection);
        AddAction("brush", L("画笔", "Brush"), "B", () => SetTool?.Invoke(NativeCharacterTool.Brush));
        AddAction("eraser", L("橡皮", "Eraser"), "E", () => SetTool?.Invoke(NativeCharacterTool.Eraser));
        AddAction("select", L("选区", "Select"), "S", () => SetTool?.Invoke(NativeCharacterTool.Select));
        AddAction("fit", L("适应窗口", "Fit view"), "F", FitEditorView);
        AddAction("zoomIn", L("放大", "Zoom in"), "Ctrl+Plus", () => canvas.Zoom++);
        AddAction("zoomOut", L("缩小", "Zoom out"), "Ctrl+Minus", () => canvas.Zoom--);
        AddAction("eyedrop", "吸管", "I", () => SetTool?.Invoke(NativeCharacterTool.Eyedrop));
        AddAction("pan", "平移", "H", () => SetTool?.Invoke(NativeCharacterTool.Pan));
        AddAction("zoom", "缩放", "Z", () => SetTool?.Invoke(NativeCharacterTool.Zoom));
        AddAction("draft", "保存草稿", "Ctrl+Alt+S", () => SaveDraft(true));
        AddAction("play", L("播放/暂停", "Play/pause"), "Space", ToggleTimeline);
        AddAction("nextFrame", L("下一帧", "Next frame"), "Right", () => StepFrame(1)); AddAction("prevFrame", L("上一帧", "Previous frame"), "Left", () => StepFrame(-1));
        AddAction("shortcuts", L("快捷键速查", "Shortcuts"), "?", ShowShortcuts);
    }

    private void AddAction(string id, string label, string key, Action action) { editorActions[id] = action; editorKeys[id] = key; }
    private void AddMenu(HBoxContainer parent, string label, Action<PopupMenu> build)
    {
        var button = new MenuButton { Text = NativeLocalization.Text(label) }; parent.AddChild(button); build(button.GetPopup()); button.GetPopup().IdPressed += value => RunEditorAction(button.GetPopup().GetItemMetadata((int)value).AsString());
    }
    private void MenuItem(PopupMenu menu, string label, string id)
    {
        menu.AddItem(NativeLocalization.Text(label), menu.ItemCount); menu.SetItemMetadata(menu.ItemCount - 1, id);

    }
    private void FileMenu(PopupMenu menu)
    {
        MenuItem(menu, "新建", "new"); MenuItem(menu, "载入角色包…", "open"); MenuItem(menu, "导出角色包…", "saveAs"); menu.AddSeparator();
        MenuItem(menu, "保存草稿", "draft"); MenuItem(menu, "保存", "save"); MenuItem(menu, "保存到 Mod 项目…", "saveProject"); menu.AddSeparator();
        MenuItem(menu, "载入原版角色…", "loadOriginal"); MenuItem(menu, "生成基底部件", "generateBase"); menu.AddSeparator();
        MenuItem(menu, "关闭", "close");
    }
    private void EditMenu(PopupMenu menu) { foreach (string id in new[] { "undo", "redo", "copy", "paste", "selectAll", "clear", "shortcuts" }) MenuItem(menu, editorActions.ContainsKey(id) ? ActionLabel(id) : id, id); }
    private void ViewMenu(PopupMenu menu)
    {
        MenuItem(menu, "适应窗口", "fit"); MenuItem(menu, "放大", "zoomIn"); MenuItem(menu, "缩小", "zoomOut"); menu.AddSeparator();
        MenuItem(menu, "显示网格", "grid"); MenuItem(menu, "身体参照", "body");
    }
    private void CharacterMenu(PopupMenu menu) { MenuItem(menu, "载入原版角色…", "loadOriginal"); MenuItem(menu, "生成基底角色", "generateBase"); MenuItem(menu, "载入已保存角色", "loadSaved"); }
    private void WindowMenu(PopupMenu menu)
    {
        MenuItem(menu, "素材面板", "assetsPanel"); MenuItem(menu, "属性面板", "inspectorPanel"); MenuItem(menu, "时间轴", "timelinePanel");
        MenuItem(menu, "重置布局", "resetLayout");
    }
    private void HelpMenu(PopupMenu menu) { MenuItem(menu, "快捷键速查表", "shortcuts"); MenuItem(menu, "关于角色编辑器", "about"); }
    private string ActionLabel(string id) => id switch { "undo" => "撤销", "redo" => "重做", "copy" => "复制", "paste" => "粘贴", "selectAll" => "全选", "clear" => "清空选区", "shortcuts" => "快捷键", _ => id };
}
