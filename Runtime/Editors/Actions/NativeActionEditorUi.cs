using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private TabBar tabs = null!;
    private TabContainer inspector = null!;
    private Label targetLabel = null!, playState = null!;
    private Label status = null!;
    private OptionButton original = null!, speedChoice = null!;
    private ItemList stateList = null!, boneList = null!, ikList = null!;
    private bool syncingStateList;
    private CheckButton playing = null!;
    private TextureRect surface = null!;
    private SubViewport viewport = null!;
    private GmCanvas canvas = null!;
    private NativeMotionOverlay? overlay;
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
            case "saveTo": SaveToDialog(); break;
            case "close": CloseProject(activeProject); break;
            case "quit": RequestClose(); break;
            case "undo": Guard(() => { document.Undo(); Refresh(); ApplyDocumentPose(); }); break;
            case "redo": Guard(() => { document.Redo(); Refresh(); ApplyDocumentPose(); }); break;
            case "showBones": if (overlay is not null) overlay.ShowBones = !overlay.ShowBones; break;
            case "showNames": if (overlay is not null) overlay.ShowNames = !overlay.ShowNames; break;
            case "reloadPreview": InitializePreview(true); break;
            case "fitView": FitView(); break;
            case "referencePose": GenerateReferencePose(); break;
            case "applyGame": ApplyToGame(); break;
            case "help": ShowHelp(); break;
            case "keyDefault": SetKeyMode(KeyMode.Default); break;
            case "keyVim": SetKeyMode(KeyMode.Vim); break;
        }
    }
    private void BuildEditor()
    {
        LoadKeyMode(); GetViewport().GuiFocusChanged += TrackFocus;
        Name = "NativeActionEditor"; Title = "动作编辑器"; Size = new(1360, 900); MinSize = new(1120, 720); Unresizable = false; Transient = true;
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
        BuildCharacterBar(root);
        var body = new VSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; root.AddChild(body);
        BuildWorkspace(body);
        BuildTimeline(body);
        status = new Label { Name = "动作状态", AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(0, 22) }; root.AddChild(status);
        Refresh(); InitializePreview();
    }
    private void BuildCharacterBar(VBoxContainer root)
    {
        var bar = Row(root); bar.Name = "CharacterBar";
        tabs = new TabBar { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TabCloseDisplayPolicy = TabBar.CloseButtonDisplayPolicy.ShowAlways }; bar.AddChild(tabs);
        tabs.TabChanged += index => { if (refreshingTabs) return; SwitchProject((int)index); };
        tabs.TabClosePressed += index => CloseProject((int)index);
        Button(bar, "+", NewProjectDialog);
        targetLabel = new Label { Name = "Target", CustomMinimumSize = new(200, 0) }; bar.AddChild(targetLabel);
        Button(bar, "切换目标…", ChangeTargetDialog);
    }
    private void BuildMenus(VBoxContainer root)
    {
        var bar = Row(root); bar.Name = "MenuBar";
        AddMenu(bar, "文件", menu => { MenuItem(menu, "新建项目…", "new"); MenuItem(menu, "打开…", "open"); MenuItem(menu, "保存", "save"); MenuItem(menu, "另存为…", "saveAs"); MenuItem(menu, "保存到…", "saveTo"); menu.AddSeparator(); MenuItem(menu, "关闭项目", "close"); MenuItem(menu, "退出", "quit"); });
        AddMenu(bar, "编辑", menu => { MenuItem(menu, "撤销", "undo"); MenuItem(menu, "重做", "redo"); });
        AddMenu(bar, "视图", menu => { MenuItem(menu, "显示骨架", "showBones", overlay?.ShowBones ?? true); MenuItem(menu, "显示骨骼名", "showNames", overlay?.ShowNames ?? false); menu.AddSeparator(); MenuItem(menu, "重载预览", "reloadPreview"); MenuItem(menu, "生成参考姿态", "referencePose"); menu.AddSeparator(); MenuItem(menu, "应用到游戏角色", "applyGame"); menu.AddSeparator(); MenuItem(menu, "键位：默认", "keyDefault", keyMode == KeyMode.Default); MenuItem(menu, "键位：vim", "keyVim", keyMode == KeyMode.Vim); });
        AddMenu(bar, "帮助", menu => MenuItem(menu, "状态机说明…", "help"));
        var brand = new Label { Text = "骨架动作与状态图", HorizontalAlignment = HorizontalAlignment.Right, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; bar.AddChild(brand);
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
        BuildTrackPage(inspector); BuildStatePage(inspector); BuildStateMachinePage(inspector); BuildEventsPage(inspector);
    }
    private void BuildLeftDock(Node parent)
    {
        var dock = new VBoxContainer { CustomMinimumSize = new(250, 0) }; parent.AddChild(dock);
        var pages = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; dock.AddChild(pages);
        var states = Page(pages, "状态");
        stateList = List(states, 160); stateList.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        stateList.ItemSelected += index => { if (!syncingStateList) { stateChoice.Select((int)index); RefreshStateInspector(); } };
        var bones = Page(pages, "骨架节点");
        boneList = List(bones, 160); boneList.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        boneList.ItemSelected += index => { if (!syncingBone) SelectBone(boneList.GetItemText((int)index)); };
        Button(bones, "刷新骨架", PopulateSkeleton);
        var ik = Page(pages, "IK 链");
        ikList = List(ik, 140); ikList.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var ikButtons = Row(ik); Button(ikButtons, "绑定所选末端", BindSelectedIk); Button(ikButtons, "解除所选 IK", UnbindSelectedIk);
        Button(Row(ik), "自定义链…", ChooseIkChain);
        var source = Page(pages, "原版动作");
        original = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; source.AddChild(original);
        foreach (string key in world.Code.Keys.Where(k => k.StartsWith("external/Chara/Motion/", StringComparison.Ordinal)).Order()) original.AddItem(key["external/Chara/Motion/".Length..^4]);
        Button(source, "导入到当前项目", ImportOriginal);
    }
    private void ChooseIkChain()
    {
        Guard(() =>
        {
            var active = PreviewActor ?? throw new InvalidOperationException("请先加载预览骨架。");
            var nodes = active.Services.Motions.Skeleton(active);
            var dialog = new ConfirmationDialog { Title = "自定义 IK 链", OkButtonText = "绑定", CancelButtonText = "取消", Size = new(340, 220) }; AddChild(dialog);
            var body = Column(dialog);
            var root = ChainChoice(body, "Root", nodes); var mid = ChainChoice(body, "Mid", nodes); var end = ChainChoice(body, "End", nodes);
            var current = IkChains.FirstOrDefault(chain => chain.Root == highlightedBone || chain.Mid == highlightedBone || chain.End == highlightedBone) ?? IkChains.FirstOrDefault();
            Pick(root, current?.Root); Pick(mid, current?.Mid); Pick(end, current?.End);
            dialog.Confirmed += () => { BindIkChain(root.GetItemText(root.Selected), mid.GetItemText(mid.Selected), end.GetItemText(end.Selected)); dialog.QueueFree(); };
            dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
        });
    }
    private static OptionButton ChainChoice(Node parent, string label, IReadOnlyList<NativeBone> nodes)
    {
        var row = Row(parent); row.AddChild(new Label { Text = label });
        var choice = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; row.AddChild(choice);
        foreach (var node in nodes) choice.AddItem(node.Name);
        return choice;
    }
    private static void Pick(OptionButton choice, string? name)
    {
        for (int index = 0; index < choice.ItemCount; index++) if (choice.GetItemText(index) == name) { choice.Select(index); return; }
    }
    private void BindIkChain(string rootName, string midName, string endName)
    {
        Guard(() =>
        {
            var active = PreviewActor ?? throw new InvalidOperationException("请先加载预览骨架。");
            if (rootName == midName || midName == endName || rootName == endName) throw new InvalidDataException("Root/Mid/End 必须是三个不同骨骼。");
            var nodes = active.Services.Motions.Skeleton(active);
            foreach (string name in new[] { rootName, midName, endName }) if (!nodes.Any(node => node.Name == name)) throw new InvalidDataException("骨骼不在预览骨架中：" + name);
            var root = nodes.Single(node => node.Name == rootName);
            var mid = nodes.Single(node => node.Name == midName);
            var end = nodes.Single(node => node.Name == endName);
            if (root.Instance != mid.Parent || mid.Instance != end.Parent) throw new InvalidDataException("所选骨骼不是同一条 Root → Mid → End 父链。");
            Change(g => { MotionIkPresets.Materialize(g); g.IkChains!.RemoveAll(chain => chain.End == endName); g.IkChains.Add(new() { Root = rootName, Mid = midName, End = endName }); });
            status.Text = "已绑定 IK：" + rootName + " → " + midName + " → " + endName;
        });
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
        text.AppendLine();
        text.AppendLine("五、键位表");
        text.AppendLine("默认：F 取景；Esc 取消画布拖动；Ctrl+Z 撤销；Ctrl+Shift+Z 重做；Ctrl+S 保存；Ctrl+Space 呼出补全清单。");
        text.AppendLine("vim：Esc 回普通模式；i 插入模式（插入模式只保留 Ctrl 组合与 F）；h / l 上一帧 / 下一帧；j / k 上一个 / 下一个骨骼；dd 删除当前帧关键帧；u 撤销。");
        text.AppendLine("画布：左键拖动骨骼（IK 末端默认 IK，Alt 临时 FK）；右键 / Esc 取消；中键拖动平移；滚轮缩放；点空白取消选中；松手写当前帧关键帧，一次拖动一条撤销。");
        text.AppendLine("所有键位只在文本控件没有焦点时生效；键位模式在「视图」菜单切换并持久化。");
        text.AppendLine();
        text.AppendLine("六、把动作挂进游戏（Mod 五步）");
        text.AppendLine("1. 动作编辑器「文件 → 保存到… → Mod 项目」导出 motions/<动作图 id>.motion.json，并写 mod.json 的 content.motions。");
        text.AppendLine("2. 「保存到… → 角色文件」把 CharacterDefinition.MotionGraph 指向动作图；BasePreset 必须等于动作图骨架。");
        text.AppendLine("3. mod.json 必须是 formatVersion 2，v2 的 content.motions / content.characters 才会被读取。");
        text.AppendLine("4. Mod 窗口项目页「加入角色…」把角色文件写进 content.characters。");
        text.AppendLine("5. 进游戏选人后经 BindFullCharacter → Motions.Configure 生效；代码入口是 ActorContext.PlayMotion(id) 或 Motions.Play(actor, id)，不是原版 MotionNum。");
        text.AppendLine();
        text.AppendLine("七、TOML 帧块");
        text.AppendLine("[[frame]]");
        text.AppendLine("index = 0");
        text.AppendLine("HEAD.x = 4");
        text.AppendLine("HEAD.y = -16");
        text.AppendLine("HEAD.angle = 30");
        text.AppendLine("字段：x / y / angle / scaleX / scaleY / picture；稀疏不补 0；骨骼.字段 必须是合法骨骼与字段。");
        text.AppendLine();
        text.AppendLine("八、状态机与常见坑");
        text.AppendLine("状态机（States / Transitions / Parameters）是 Mod 运行时概念，界面不出现，只保留在动作图数据里。");
        text.AppendLine("没有轨道的骨骼直接改 X / Y 会把未填字段写成 0：先选骨骼或「生成参考姿态」，再改数值。");
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
