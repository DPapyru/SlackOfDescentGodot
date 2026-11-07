using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private TabBar tabs = null!;
    private TabContainer inspector = null!;
    private GraphEdit graphView = null!;
    private Label targetLabel = null!, playState = null!;
    private OptionButton bone = null!, original = null!, stateChoice = null!, stateClip = null!, transitionTarget = null!, conditionKind = null!, conditionComparison = null!, speedChoice = null!;
    private ItemList boneList = null!, ikList = null!, transitionList = null!, conditionList = null!;
    private LineEdit stateId = null!, stateRename = null!, conditionParameter = null!;
    private SpinBox transitionPriority = null!, conditionKey = null!, conditionValue = null!, velocityX = null!, velocityY = null!, gravity = null!, maxFall = null!, inputSpeed = null!, jumpSpeed = null!, displaceX = null!, displaceY = null!;
    private CheckButton velocityXOn = null!, velocityYOn = null!, inputSpeedOn = null!, jumpSpeedOn = null!, playing = null!;
    private static HBoxContainer Row(Node parent) { var row = new HBoxContainer(); parent.AddChild(row); return row; }
    private static Button Button(Node parent, string text, Action action) { var button = new Button { Text = text }; parent.AddChild(button); button.Pressed += action; return button; }
    private static VBoxContainer Column(Node parent) { var column = new VBoxContainer(); parent.AddChild(column); return column; }
    private static VBoxContainer Page(TabContainer tabs, string name) { var page = new VBoxContainer { Name = name }; tabs.AddChild(page); return page; }
    private static ItemList List(Node parent, int height) { var list = new ItemList { CustomMinimumSize = new(0, height) }; parent.AddChild(list); return list; }
    private static int SelectedIndex(ItemList list) { var selected = list.GetSelectedItems(); return selected.Length > 0 ? selected[0] : -1; }
    private static SpinBox Number(Node parent, string label, double min = -4096, double max = 4096, double value = 0)
    {
        parent.AddChild(new Label { Text = label }); var number = new SpinBox { MinValue = min, MaxValue = max, Step = .01, Value = value, CustomMinimumSize = new(90, 0) }; parent.AddChild(number); return number;
    }
    private void AddMenu(HBoxContainer parent, string label, Action<PopupMenu> build)
    {
        var button = new MenuButton { Text = label }; parent.AddChild(button); var popup = button.GetPopup();
        popup.AboutToPopup += () => { popup.Clear(); build(popup); };
        popup.IdPressed += value => RunAction(popup.GetItemMetadata((int)value).AsString());
    }
    private static void MenuItem(PopupMenu menu, string label, string id, bool? check = null)
    {
        int index = menu.ItemCount;
        if (check is bool state) { menu.AddCheckItem(label, index); menu.SetItemChecked(index, state); } else menu.AddItem(label, index);
        menu.SetItemMetadata(index, id);
    }
    private void RunAction(string id)
    {
        switch (id)
        {
            case "new": NewProjectDialog(); break;
            case "open": ChooseFile(false); break;
            case "save": Save(); break;
            case "saveAs": ChooseFile(true); break;
            case "saveProject": ExportToProjectDialog(); break;
            case "bindCharacter": BindSavedCharacter(); break;
            case "close": CloseProject(activeProject); break;
            case "quit": RequestClose(); break;
            case "undo": Guard(() => { document.Undo(); Refresh(); ApplyDocumentPose(); }); break;
            case "redo": Guard(() => { document.Redo(); Refresh(); ApplyDocumentPose(); }); break;
            case "showBones": if (overlay is not null) overlay.ShowBones = !overlay.ShowBones; break;
            case "showNames": if (overlay is not null) overlay.ShowNames = !overlay.ShowNames; break;
            case "reloadPreview": InitializePreview(); break;
            case "fitView": FitView(); break;
            case "referencePose": GenerateReferencePose(); break;
            case "applyGame": ApplyToGame(); break;
            case "help": ShowHelp(); break;
        }
    }
    private void BuildEditor()
    {
        Name = "NativeActionEditor"; Title = L("动作编辑器", "Action editor"); Size = new(1360, 900); MinSize = new(1120, 720); Unresizable = false; Transient = true;
        Theme = NativeEditorTheme.Create();
        CloseRequested += RequestClose;
        confirmation = new ConfirmationDialog { Title = "请确认", OkButtonText = "确定", CancelButtonText = "取消" }; AddChild(confirmation);
        confirmation.Confirmed += () => { var action = pendingConfirmation; pendingConfirmation = null; action?.Invoke(); };
        confirmation.Canceled += () => pendingConfirmation = null;
        message = new AcceptDialog { Title = "动作编辑器", OkButtonText = "确定" }; AddChild(message);
        var margin = new MarginContainer(); AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 10);
        var root = new VBoxContainer(); margin.AddChild(root);
        BuildMenus(root);
        BuildWorkspaceSwitch(root);
        BuildProjectBar(root);
        actionWorkspace = new VBoxContainer { Name = "动作参数工作区", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; root.AddChild(actionWorkspace);
        motionWorkspace = new VBoxContainer { Name = "动作图工作区", SizeFlagsVertical = Control.SizeFlags.ExpandFill, Visible = false }; root.AddChild(motionWorkspace);
        BuildActionWorkspace(actionWorkspace);
        BuildToolbar(motionWorkspace);
        BuildWorkspace(motionWorkspace);
        BuildTimeline(motionWorkspace);
        status = new Label { Name = "动作状态", AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(0, 22) }; root.AddChild(status);
        projectBar.Visible = false;
        RefreshCatalog(); RefreshApplyButton(); Refresh();
    }
    private void BuildWorkspaceSwitch(VBoxContainer root)
    {
        var bar = Row(root); bar.Name = "WorkspaceBar";
        workspaceTabs = new TabBar { Name = "工作区", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; bar.AddChild(workspaceTabs);
        workspaceTabs.AddTab(L("动作参数", "Parameters")); workspaceTabs.AddTab(L("动作图", "Motion graph"));
        workspaceTabs.TabChanged += index => ShowWorkspace((int)index);
    }
    private void ShowWorkspace(int index)
    {
        bool motion = index == 1;
        actionWorkspace.Visible = !motion; motionWorkspace.Visible = motion; projectBar.Visible = motion;
        if (motion && preview is null && !busy) InitializePreview();
    }
    internal void ShowMotionWorkspace()
    {
        workspaceTabs.CurrentTab = 1; ShowWorkspace(1);
    }
    private void BuildMenus(VBoxContainer root)
    {
        var bar = Row(root); bar.Name = "MenuBar";
        AddMenu(bar, "文件", menu => { MenuItem(menu, "新建项目…", "new"); MenuItem(menu, "打开…", "open"); MenuItem(menu, "保存", "save"); MenuItem(menu, "另存为…", "saveAs"); MenuItem(menu, "保存到 Mod 项目…", "saveProject"); menu.AddSeparator(); MenuItem(menu, "将动作图保存到角色", "bindCharacter"); menu.AddSeparator(); MenuItem(menu, "关闭项目", "close"); MenuItem(menu, "退出", "quit"); });
        AddMenu(bar, "编辑", menu => { MenuItem(menu, "撤销", "undo"); MenuItem(menu, "重做", "redo"); });
        AddMenu(bar, "视图", menu => { MenuItem(menu, "显示骨架", "showBones", overlay?.ShowBones ?? true); MenuItem(menu, "显示骨骼名", "showNames", overlay?.ShowNames ?? false); menu.AddSeparator(); MenuItem(menu, "适配画布", "fitView"); MenuItem(menu, "生成参考姿态", "referencePose"); menu.AddSeparator(); MenuItem(menu, "重载预览", "reloadPreview"); MenuItem(menu, "应用到游戏角色", "applyGame"); });
        AddMenu(bar, "帮助", menu => MenuItem(menu, "状态机说明…", "help"));
        var brand = new Label { Text = "骨架动作与状态图", HorizontalAlignment = HorizontalAlignment.Right, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; bar.AddChild(brand);
    }
    private void BuildProjectBar(VBoxContainer root)
    {
        projectBar = Row(root); projectBar.Name = "ProjectBar";
        tabs = new TabBar { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TabCloseDisplayPolicy = TabBar.CloseButtonDisplayPolicy.ShowAlways }; projectBar.AddChild(tabs);
        tabs.TabChanged += index => { if (refreshingTabs) return; SwitchProject((int)index); };
        tabs.TabClosePressed += index => CloseProject((int)index);
        Button(projectBar, "+", NewProjectDialog);
        targetLabel = new Label { Name = "Target", CustomMinimumSize = new(200, 0) }; projectBar.AddChild(targetLabel);
        Button(projectBar, "切换目标…", ChangeTargetDialog);
    }
    private void BuildToolbar(Node parent)
    {
        var bar = Row(parent); bar.Name = "Toolbar";
        Button(bar, "重载预览", InitializePreview); Button(bar, "取景", FitView); playing = new CheckButton { Text = "播放" }; bar.AddChild(playing); playing.Toggled += on => { if (on) RestartPlayback(); else UpdatePlaybackOverlay(); }; Button(bar, "单帧", Step);
        speedChoice = new OptionButton(); foreach (string speed in new[] { "0.5x", "1x", "2x" }) speedChoice.AddItem(speed); speedChoice.Select(1); bar.AddChild(speedChoice);
        speedChoice.ItemSelected += index => playbackSpeed = index switch { 0 => .5, 2 => 2, _ => 1 };
        Button(bar, "应用到游戏角色", ApplyToGame);
        playState = new Label { Name = "PlayState", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; bar.AddChild(playState);
    }
    private void BuildWorkspace(Node parent)
    {
        var content = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [250] }; parent.AddChild(content);
        BuildLeftDock(content);
        var center = Column(content); center.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        surface = new TextureRect { CustomMinimumSize = new(400, 300), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; center.AddChild(surface);
        overlay = new NativeMotionOverlay(); surface.AddChild(overlay); overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); surface.GuiInput += SurfaceInput; surface.Resized += SyncOverlay;
        viewport = new SubViewport { Size = new(640, 480), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false }; AddChild(viewport);
        canvas = new GmCanvas(); viewport.AddChild(canvas); surface.Texture = viewport.GetTexture();
        inspector = new TabContainer { CustomMinimumSize = new(340, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; content.AddChild(inspector);
        BuildTrackPage(); BuildStatePage(); BuildEventPage(); BuildParameterPage();
    }
    private void BuildLeftDock(Node parent)
    {
        var dock = new VBoxContainer { CustomMinimumSize = new(250, 0) }; parent.AddChild(dock);
        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; dock.AddChild(tabs);
        var bones = Page(tabs, "骨架节点");
        boneList = List(bones, 160); boneList.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        boneList.ItemSelected += index => { if (!syncingBone) SelectBone(boneList.GetItemText((int)index)); };
        Button(bones, "刷新骨架", PopulateSkeleton);
        var ik = Page(tabs, "IK 链");
        ikList = List(ik, 140); ikList.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var ikButtons = Row(ik); Button(ikButtons, "绑定所选末端", BindSelectedIk); Button(ikButtons, "解除所选 IK", UnbindSelectedIk);
        var source = Page(tabs, "原版动作");
        original = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; source.AddChild(original);
        foreach (string key in world.Code.Keys.Where(k => k.StartsWith("external/Chara/Motion/", StringComparison.Ordinal)).Order()) original.AddItem(key["external/Chara/Motion/".Length..^4]);
        Button(source, "导入到当前项目", ImportOriginal);
    }
    private void BuildTrackPage()
    {
        var page = Page(inspector, "轨道");
        var track = Row(page); track.AddChild(new Label { Text = "骨骼" }); bone = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; track.AddChild(bone); bone.ItemSelected += _ => { if (!syncingBone) SelectBone(bone.GetItemText((int)bone.Selected)); };
        rootTrack = new CheckButton { Text = "姿态根偏移" }; track.AddChild(rootTrack); rootTrack.Toggled += _ => ReadKey();
        var translate = Row(page); x = Number(translate, "X"); y = Number(translate, "Y"); angle = Number(translate, "角度", -36000, 36000);
        var scale = Row(page); sx = Number(scale, "缩放X", -100, 100, 1); sy = Number(scale, "缩放Y", -100, 100, 1); picture = Number(scale, "贴图帧", 0, 10000); picture.Step = 1;
        var keys = Row(page); Button(keys, "写入关键帧", WriteKey); Button(keys, "删除关键帧", () => Change(_ => Keys().RemoveAll(k => k.Frame == frame.Value)));
        var ik = Row(page); Button(ik, "绑定所选末端", BindSelectedIk); Button(ik, "解除所选 IK", UnbindSelectedIk);
        page.AddChild(new Label { Text = "位移与缩放线性插值；旋转取最短角度；贴图帧阶梯切换。根偏移只改变姿态，不移动实体。", AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }
    private void BuildStatePage()
    {
        var page = Page(inspector, "状态");
        var states = Row(page); stateChoice = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; states.AddChild(stateChoice); stateChoice.ItemSelected += _ => { if (!refreshing) RefreshStateInspector(); };
        stateId = new LineEdit { PlaceholderText = "新状态标识", CustomMinimumSize = new(110, 0) }; states.AddChild(stateId); Button(states, "新增", AddState);
        var rename = Row(page); stateRename = new LineEdit { PlaceholderText = "重命名", CustomMinimumSize = new(110, 0) }; rename.AddChild(stateRename);
        Button(rename, "重命名", RenameSelectedState); Button(rename, "删除", RemoveSelectedState);
        var clipRow = Row(page); clipRow.AddChild(new Label { Text = "片段" }); stateClip = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; clipRow.AddChild(stateClip); stateClip.ItemSelected += index => ChangeState(state => state.Clip = stateClip.GetItemText((int)index));
        page.AddChild(new HSeparator()); page.AddChild(new Label { Text = "运动" });
        var first = Row(page); velocityXOn = new CheckButton { Text = "速度X" }; velocityX = OptionalSpin(first, velocityXOn, (state, value) => state.Movement.VelocityX = value);
        velocityYOn = new CheckButton { Text = "速度Y" }; velocityY = OptionalSpin(first, velocityYOn, (state, value) => state.Movement.VelocityY = value);
        var second = Row(page); gravity = MovementSpin(second, "重力", (state, value) => state.Movement.Gravity = value); maxFall = MovementSpin(second, "最大下落", (state, value) => state.Movement.MaxFall = value);
        var third = Row(page); inputSpeedOn = new CheckButton { Text = "输入速度" }; inputSpeed = OptionalSpin(third, inputSpeedOn, (state, value) => state.Movement.InputSpeed = value);
        jumpSpeedOn = new CheckButton { Text = "跳跃速度" }; jumpSpeed = OptionalSpin(third, jumpSpeedOn, (state, value) => state.Movement.JumpSpeed = value);
        var fourth = Row(page); displaceX = MovementSpin(fourth, "位移X", (state, value) => state.Movement.DisplaceX = value); displaceY = MovementSpin(fourth, "位移Y", (state, value) => state.Movement.DisplaceY = value);
        page.AddChild(new HSeparator()); page.AddChild(new Label { Text = "转换（优先级从高到低，每 Tick 最多一次）" });
        transitionList = List(page, 90); transitionList.ItemSelected += _ => RefreshConditions();
        var add = Row(page); transitionTarget = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; add.AddChild(transitionTarget);
        transitionPriority = new SpinBox { MinValue = -100, MaxValue = 100, Step = 1, CustomMinimumSize = new(70, 0) }; add.AddChild(transitionPriority);
        Button(add, "添加", AddTransition); Button(add, "删除", RemoveTransition);
        page.AddChild(new Label { Text = "条件" });
        conditionList = List(page, 80); conditionList.ItemSelected += _ => ReadCondition();
        var condition = Row(page);
        conditionKind = new OptionButton(); foreach (string name in Enum.GetNames<MotionConditionKind>()) conditionKind.AddItem(name); condition.AddChild(conditionKind);
        conditionComparison = new OptionButton(); foreach (string name in Enum.GetNames<MotionComparison>()) conditionComparison.AddItem(name); condition.AddChild(conditionComparison);
        conditionKey = new SpinBox { MinValue = 0, MaxValue = 400, Step = 1, CustomMinimumSize = new(60, 0) }; condition.AddChild(conditionKey);
        conditionValue = new SpinBox { MinValue = -4096, MaxValue = 4096, Step = .01, CustomMinimumSize = new(80, 0) }; condition.AddChild(conditionValue);
        conditionParameter = new LineEdit { PlaceholderText = "参数名", CustomMinimumSize = new(90, 0) }; condition.AddChild(conditionParameter);
        var conditionButtons = Row(page); Button(conditionButtons, "添加条件", AddCondition); Button(conditionButtons, "删除条件", RemoveCondition);
        graphView = new GraphEdit { CustomMinimumSize = new(0, 180), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; page.AddChild(graphView);
        graphView.ConnectionRequest += (from, fromPort, to, toPort) => Change(g => g.States.Single(s => NodeName(s.Id) == from.ToString()).Transitions.Add(new() { Target = g.States.Single(s => NodeName(s.Id) == to.ToString()).Id }));
        graphView.DisconnectionRequest += (from, fromPort, to, toPort) => Change(g => g.States.Single(s => NodeName(s.Id) == from.ToString()).Transitions.RemoveAll(t => NodeName(t.Target) == to.ToString()));
    }
    private void BuildEventPage()
    {
        var page = Page(inspector, "事件");
        page.AddChild(new Label { Text = "指定帧或接触区间；Damage / Interact / Spawn / Destroy / SdkEvent / MapEvent", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        eventsText = new TextEdit { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; page.AddChild(eventsText);
        var buttons = Row(page); Button(buttons, "读取事件", () => eventsText.Text = JsonSerializer.Serialize(Clip.Events, NativeContentStore.Json)); Button(buttons, "新增接触事件", () => Change(_ => Clip.Events.Add(new() { Contact = true, Start = 0, End = Clip.Duration, Kind = MotionEventKind.SdkEvent, Value = "motion.contact" }))); Button(buttons, "写入事件", () => Change(_ => Clip.Events = JsonSerializer.Deserialize<List<MotionEvent>>(eventsText.Text, NativeContentStore.Json) ?? new()));
    }
    private void BuildParameterPage()
    {
        var page = Page(inspector, "图参数");
        parametersText = new TextEdit { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; page.AddChild(parametersText);
        Button(page, "写入参数", () => Change(g => g.Parameters = JsonSerializer.Deserialize<Dictionary<string, double>>(parametersText.Text, NativeContentStore.Json) ?? new()));
    }
    private void BuildActionWorkspace(Node parent)
    {
        var file = Row(parent);
        Button(file, L("保存配置", "Save config"), SaveRequested);
        Button(file, L("绑定到角色", "Bind to character"), BindConfigToCharacter);
        Button(file, L("导入 .action.json", "Import .action.json"), ImportRequested);
        Button(file, L("导出 .action.json", "Export .action.json"), ExportRequested);
        Button(file, L("检查 Mod 编译", "Check mod compile"), () => CheckCompile());
        Button(file, L("关闭", "Close"), RequestClose);
        var columns = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; parent.AddChild(columns);
        var left = new VBoxContainer { CustomMinimumSize = new(280, 0) }; columns.AddChild(left);
        left.AddChild(new Label { Text = L("动作", "Actions") });
        catalog = new ItemList { Name = "动作列表", AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new(270, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; left.AddChild(catalog);
        catalog.ItemSelected += index => Select((int)index);
        var middle = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; columns.AddChild(middle);
        middle.AddChild(new Label { Text = L("参数", "Parameters") });
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(320, 0) }; middle.AddChild(scroll);
        parameterForm = new VBoxContainer { Name = "动作参数", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(parameterForm);
        var right = new VBoxContainer { CustomMinimumSize = new(360, 0) }; columns.AddChild(right);
        var targetRow = Row(right); targetRow.AddChild(new Label { Text = L("目标", "Target") });
        targetChoice = new OptionButton { Name = "动作目标", CustomMinimumSize = new(180, 32) };
        targetChoice.AddItem(L("玩家 objPlayerDoll", "Player objPlayerDoll"));
        targetChoice.AddItem(L("怪物 objEnemyDoll", "Monster objEnemyDoll"));
        targetChoice.ItemSelected += _ => ReframeTarget();
        targetRow.AddChild(targetChoice);
        actionSurface = new TextureRect { Name = "动作预览", CustomMinimumSize = new(340, 260), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, TextureFilter = CanvasItem.TextureFilterEnum.Nearest }; right.AddChild(actionSurface);
        previewState = new Label { Name = "动作预览状态", AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = L("等待应用动作", "Waiting to apply an action") }; right.AddChild(previewState);
        var buttons = Row(right); play = new CheckButton { Name = "动作播放", Text = L("播放", "Play") }; buttons.AddChild(play);
        Button(buttons, L("单步", "Step"), StepPreview);
        var operations = Row(parent);
        applyButton = Button(operations, L("应用到预览", "Apply to preview"), Apply);
        undoButton = Button(operations, L("撤销", "Undo"), Undo);
        redoButton = Button(operations, L("重做", "Redo"), Redo);
    }
    private void BuildForm(NativeActionDefinition value)
    {
        foreach (Node child in parameterForm.GetChildren()) { parameterForm.RemoveChild(child); child.QueueFree(); }
        parameters.Clear();
        foreach (var parameter in value.Parameters)
        {
            var row = Row(parameterForm);
            row.AddChild(new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = parameter.Name, TooltipText = parameter.Description, CustomMinimumSize = new(120, 0) });
            var parsed = NativeActionValues.Parse(parameter, parameter.Default);
            Control control;
            switch (parameter.Type)
            {
                case NativeActionParameterType.Number:
                    var spin = new SpinBox { MinValue = parameter.Min ?? -1000000, MaxValue = parameter.Max ?? 1000000, Step = .1, Value = parsed.Number, UpdateOnTextChanged = false, CustomMinimumSize = new(150, 30) };
                    control = spin; break;
                case NativeActionParameterType.Bool:
                    control = new CheckButton { ButtonPressed = parsed.Truth }; break;
                case NativeActionParameterType.Enum:
                    var option = new OptionButton { CustomMinimumSize = new(150, 30) };
                    foreach (string item in parameter.Options) option.AddItem(item);
                    int selected = parameter.Options.IndexOf(parsed.ToString()); if (selected >= 0) option.Select(selected);
                    control = option; break;
                default:
                    control = new LineEdit { Text = parsed.ToString(), CustomMinimumSize = new(150, 30) }; break;
            }
            control.Name = "动作参数_" + parameter.Name; control.AutoTranslateMode = AutoTranslateModeEnum.Disabled; control.TooltipText = parameter.Description;
            Wire(control);
            row.AddChild(control); parameters[parameter.Name] = control;
        }
        if (value.Parameters.Count == 0) parameterForm.AddChild(new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = L("该动作没有参数", "This action has no parameters") });
        FillFromConfig(value);
    }
    private void Wire(Control control)
    {
        switch (control)
        {
            case SpinBox spin: spin.ValueChanged += _ => MarkEdited(); break;
            case CheckButton check: check.Toggled += _ => MarkEdited(); break;
            case OptionButton option: option.ItemSelected += _ => MarkEdited(); break;
            case LineEdit line: line.TextChanged += _ => MarkEdited(); break;
        }
    }
    private SpinBox MovementSpin(HBoxContainer row, string label, Action<MotionState, double> write)
    {
        row.AddChild(new Label { Text = label }); var spin = new SpinBox { MinValue = -4096, MaxValue = 4096, Step = .01, CustomMinimumSize = new(86, 0) }; row.AddChild(spin);
        spin.ValueChanged += value => { if (!refreshing) ChangeState(state => write(state, value)); };
        return spin;
    }
    private SpinBox OptionalSpin(HBoxContainer row, CheckButton toggle, Action<MotionState, double?> write)
    {
        row.AddChild(toggle); var spin = new SpinBox { MinValue = -4096, MaxValue = 4096, Step = .01, CustomMinimumSize = new(86, 0) }; row.AddChild(spin);
        spin.ValueChanged += value => { if (!refreshing) ChangeState(state => write(state, value)); };
        toggle.Toggled += on => { if (!refreshing) ChangeState(state => write(state, on ? spin.Value : null)); };
        return spin;
    }
    private void ChangeState(Action<MotionState> change)
    {
        if (refreshing || stateChoice.Selected < 0) return;
        int index = (int)stateChoice.Selected;
        Change(graph => change(graph.States[index]));
    }
    private void ChangeTransition(Action<MotionTransition> change)
    {
        if (refreshing || stateChoice.Selected < 0 || SelectedIndex(transitionList) < 0) return;
        int stateIndex = (int)stateChoice.Selected, transitionIndex = SelectedIndex(transitionList);
        Change(graph => { var state = graph.States[stateIndex]; if (transitionIndex < state.Transitions.Count) change(state.Transitions[transitionIndex]); });
    }
    private void AddState() => Change(graph => graph.States.Add(new() { Id = stateId.Text, Clip = Clip.Id }));
    private void RenameSelectedState() => Change(graph => MotionGraphEdits.RenameState(graph, SelectedStateId, stateRename.Text));
    private void RemoveSelectedState() => Change(graph => MotionGraphEdits.RemoveState(graph, SelectedStateId));
    private void AddTransition() => ChangeState(state => { if (transitionTarget.Selected >= 0) state.Transitions.Add(new() { Target = transitionTarget.GetItemText((int)transitionTarget.Selected), Priority = (int)transitionPriority.Value }); });
    private void RemoveTransition() => ChangeState(state => { int index = SelectedIndex(transitionList); if (index >= 0) state.Transitions.RemoveAt(index); });
    private void AddCondition() => ChangeTransition(transition => transition.Conditions.Add(new() { Kind = (MotionConditionKind)conditionKind.Selected, Comparison = (MotionComparison)conditionComparison.Selected, Key = (int)conditionKey.Value, Value = conditionValue.Value, Parameter = conditionParameter.Text }));
    private void RemoveCondition() => ChangeTransition(transition => { int index = SelectedIndex(conditionList); if (index >= 0) transition.Conditions.RemoveAt(index); });
    private void RefreshStateInspector()
    {
        bool previous = refreshing; refreshing = true;
        try { RefreshStateInspectorCore(); } finally { refreshing = previous; }
    }
    private void RefreshStateInspectorCore()
    {
        var graph = document.Graph;
        int stateIndex = Math.Clamp(stateChoice.Selected < 0 ? 0 : (int)stateChoice.Selected, 0, graph.States.Count - 1);
        stateChoice.Clear(); foreach (var state in graph.States) stateChoice.AddItem(state.Id); stateChoice.Select(stateIndex);
        var current = graph.States[stateIndex];
        stateRename.Text = current.Id;
        stateClip.Clear(); foreach (var clip in graph.Clips) stateClip.AddItem(clip.Id);
        int clipIndex = Math.Max(0, graph.Clips.FindIndex(clip => clip.Id == current.Clip)); stateClip.Select(clipIndex);
        var movement = current.Movement;
        velocityXOn.ButtonPressed = movement.VelocityX is not null; velocityX.Value = movement.VelocityX ?? 0;
        velocityYOn.ButtonPressed = movement.VelocityY is not null; velocityY.Value = movement.VelocityY ?? 0;
        gravity.Value = movement.Gravity; maxFall.Value = movement.MaxFall;
        inputSpeedOn.ButtonPressed = movement.InputSpeed is not null; inputSpeed.Value = movement.InputSpeed ?? 0;
        jumpSpeedOn.ButtonPressed = movement.JumpSpeed is not null; jumpSpeed.Value = movement.JumpSpeed ?? 0;
        displaceX.Value = movement.DisplaceX; displaceY.Value = movement.DisplaceY;
        transitionTarget.Clear(); foreach (var state in graph.States) transitionTarget.AddItem(state.Id);
        transitionList.Clear(); foreach (var transition in current.Transitions) transitionList.AddItem("→ " + transition.Target + "  [priority " + transition.Priority + "]");
        RefreshConditions();
    }
    private void RefreshConditions()
    {
        conditionList.Clear();
        if (SelectedTransition is not { } transition) return;
        foreach (var condition in transition.Conditions) conditionList.AddItem(condition.Kind + " " + condition.Comparison + " " + condition.Key + " " + condition.Value + (condition.Parameter.Length > 0 ? "  " + condition.Parameter : ""));
    }
    private void ReadCondition()
    {
        int index = SelectedIndex(conditionList);
        if (SelectedTransition is not { } transition || index < 0 || index >= transition.Conditions.Count) return;
        var condition = transition.Conditions[index];
        conditionKind.Select((int)condition.Kind); conditionComparison.Select((int)condition.Comparison);
        conditionKey.Value = condition.Key; conditionValue.Value = condition.Value; conditionParameter.Text = condition.Parameter;
    }
    internal string HelpText()
    {
        string preset = HelpPreset();
        var text = new System.Text.StringBuilder();
        text.AppendLine("一、状态机是什么");
        text.AppendLine("状态：角色当前正在播哪一段动作。片段：一段带关键帧的姿态轨道。转换：满足条件时从当前状态切到目标状态。条件：按键/落地/速度/血量/受击/计时/参数等判断。运动：状态自带的速度、重力与位移。事件：片段内指定帧或接触区间触发的伤害、交互、生成、销毁、SDK 与地图事件。");
        text.AppendLine();
        text.AppendLine("二、原版对照");
        text.AppendLine("状态 ↔ objPlayerDoll 的 Step 里硬编码的 MotionNum 分支。");
        text.AppendLine("片段 ↔ motion_list.txt 里 PlayerMotionLoad 注册的 Motion。");
        text.AppendLine("转换 ↔ 原版 MotionChange(motion, 关键帧, 时长) 及其触发条件。");
        text.AppendLine("条件 ↔ 原版 Step 里的 if/switch 判断。");
        text.AppendLine("运动 ↔ 原版对 speed/gravity/vspeed 的赋值加上 place_free 碰撞。");
        text.AppendLine("事件 ↔ 原版 data_mot 的事件帧与碰撞判定。");
        text.AppendLine("受击/死亡/束缚/泥潭 ↔ 图控制器里 Forced() 的优先分支，会挂起图播放。");
        text.AppendLine();
        text.AppendLine("三、新动作从哪来与边界");
        text.AppendLine("来源：编辑器内新建动作图；Mod 的 content.motions + content.characters；SDK 的 Motions.Play / Motions.Parameter 与地图/图事件。");
        text.AppendLine("边界：不扩展原版 MotionNum，原版 Step 不会自动播放新动作；不支持自定义骨架拓扑；一个角色同时只有一个图控制器。");
        text.AppendLine();
        text.AppendLine("四、原版动作名清单（" + preset + "）");
        foreach (var item in Registered(preset)) text.AppendLine(item.Name + " → " + item.Resource);
        return text.ToString();
    }
    private IReadOnlyList<MotionRegistered> Registered(string preset)
    {
        if (world.Code.TryGetValue("external/Chara/" + preset + "/motion_list.txt", out var ast)) return MotionImport.Registered(ast);
        return world.Code.TryGetValue("external/Chara/Nana/motion_list.txt", out var fallback) ? MotionImport.Registered(fallback) : [];
    }
    private string HelpPreset() => Project.Target.Kind switch
    {
        MotionTargetKind.PlayerPreset => Project.Target.Id,
        MotionTargetKind.PlayerCharacter => CharacterPreset(Project.Target),
        _ => "Nana"
    };
    private void ShowHelp()
    {
        var dialog = new AcceptDialog { Title = "状态机说明", OkButtonText = "关闭", Size = new(820, 620) };
        dialog.AddChild(new TextEdit { Text = HelpText(), Editable = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        AddChild(dialog); dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCenteredRatio(.8f);
    }
}
