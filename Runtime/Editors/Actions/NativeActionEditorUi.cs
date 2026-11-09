using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private TabBar tabs = null!;
    private Label targetLabel = null!;
    private Label status = null!;
    private TextEdit sourceView = null!, rawView = null!;
    private TabContainer leftPages = null!;
    private TextureRect surface = null!;
    private SubViewport viewport = null!;
    private GmCanvas canvas = null!;
    private NativeMotionOverlay? overlay;
    private bool rootTrackMode;
    private static HBoxContainer Row(Node parent) { var row = new HBoxContainer(); parent.AddChild(row); return row; }
    private static Button Button(Node parent, string text, Action action) { var button = new Button { Text = text }; parent.AddChild(button); button.Pressed += action; return button; }
    private static VBoxContainer Column(Node parent) { var column = new VBoxContainer(); parent.AddChild(column); return column; }
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
            case "importOriginal": ShowOriginalMotions(); break;
            case "close": CloseProject(activeProject); break;
            case "quit": RequestClose(); break;
            case "undo": Guard(() => { document.Undo(); Refresh(); ApplyDocumentPose(); }); break;
            case "redo": Guard(() => { document.Redo(); Refresh(); ApplyDocumentPose(); }); break;
            case "deleteKey": DeleteSelectedKey(); break;
            case "bindIk": BindSelectedIk(); break;
            case "unbindIk": UnbindSelectedIk(); break;
            case "rootTrack": rootTrackMode = !rootTrackMode; status.Text = rootTrackMode ? "根偏移已开启：删除关键帧作用于根轨道。" : "根偏移已关闭。"; break;
            case "showBones": if (overlay is not null) overlay.ShowBones = !overlay.ShowBones; break;
            case "showNames": if (overlay is not null) overlay.ShowNames = !overlay.ShowNames; break;
            case "reloadPreview": InitializePreview(true); break;
            case "fitView": FitView(); break;
            case "referencePose": GenerateReferencePose(); break;
            case "applyGame": ApplyToGame(); break;
            case "help": ShowHelp(); break;
        }
    }
    private void BuildEditor()
    {
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
        BuildStatusBar(root);
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
        AddMenu(bar, "文件", menu => { MenuItem(menu, "新建项目…", "new"); MenuItem(menu, "打开…", "open"); MenuItem(menu, "保存", "save"); MenuItem(menu, "另存为…", "saveAs"); MenuItem(menu, "保存到…", "saveTo"); menu.AddSeparator(); MenuItem(menu, "导入原版动作…", "importOriginal"); menu.AddSeparator(); MenuItem(menu, "关闭项目", "close"); MenuItem(menu, "退出", "quit"); });
        AddMenu(bar, "编辑", menu => { MenuItem(menu, "撤销", "undo"); MenuItem(menu, "重做", "redo"); menu.AddSeparator(); MenuItem(menu, "删除当前帧关键帧", "deleteKey"); MenuItem(menu, "IK 绑定所选末端", "bindIk"); MenuItem(menu, "解除所选 IK", "unbindIk"); menu.AddSeparator(); MenuItem(menu, "姿态根偏移", "rootTrack", rootTrackMode); });
        AddMenu(bar, "视图", menu => { MenuItem(menu, "显示骨架", "showBones", overlay?.ShowBones ?? true); MenuItem(menu, "显示骨骼名", "showNames", overlay?.ShowNames ?? false); menu.AddSeparator(); MenuItem(menu, "重载预览", "reloadPreview"); MenuItem(menu, "取景", "fitView"); MenuItem(menu, "生成参考姿态", "referencePose"); menu.AddSeparator(); MenuItem(menu, "应用到游戏角色", "applyGame"); });
        AddMenu(bar, "帮助", menu => MenuItem(menu, "说明手册…", "help"));
        var brand = new Label { Text = "动作编辑器", HorizontalAlignment = HorizontalAlignment.Right, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; bar.AddChild(brand);
    }
    private void BuildStatusBar(VBoxContainer root)
    {
        var bar = new PanelContainer { Name = "StatusBar", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(0, 24) };
        bar.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("#007acc") });
        status = new Label { Name = "Status", AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Text = "就绪" };
        status.AddThemeColorOverride("font_color", Colors.White);
        bar.AddChild(status); root.AddChild(bar);
    }
    private void BuildWorkspace(Node parent)
    {
        var content = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [350] }; parent.AddChild(content);
        BuildLeftDock(content);
        var center = Column(content); center.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        surface = new TextureRect { CustomMinimumSize = new(400, 300), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; center.AddChild(surface);
        overlay = new NativeMotionOverlay(); surface.AddChild(overlay); overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); surface.GuiInput += SurfaceInput; surface.Resized += SyncOverlay;
        viewport = new SubViewport { Size = new(640, 480), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false }; AddChild(viewport);
        canvas = new GmCanvas(); viewport.AddChild(canvas); surface.Texture = viewport.GetTexture();
        BuildActionPanel(content);
    }
    private void BuildLeftDock(Node parent)
    {
        var dock = new VBoxContainer { CustomMinimumSize = new(350, 0) }; parent.AddChild(dock);
        leftPages = new TabContainer { Name = "LeftPages", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; dock.AddChild(leftPages);
        var source = new VBoxContainer { Name = "节点属性修改" }; leftPages.AddChild(source);
        sourceView = new TextEdit { Name = "SourceView", Editable = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(320, 200) };
        source.AddChild(sourceView);
        var raw = new VBoxContainer { Name = "原始数据" }; leftPages.AddChild(raw);
        rawView = new TextEdit { Name = "RawView", Editable = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        raw.AddChild(rawView);
    }
    private void BuildActionPanel(Node parent)
    {
        var panel = new VBoxContainer { Name = "ActionSettings", CustomMinimumSize = new(280, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; parent.AddChild(panel);
        panel.AddChild(new Label { Text = "动作设置" });
        panel.AddChild(new Label { Text = "当前编辑动作 (Action)" });
        clipChoice = new OptionButton { Name = "ClipChoice", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; panel.AddChild(clipChoice);
        clipChoice.ItemSelected += _ => { if (refreshing) return; Project.ClipIndex = Math.Max(0, clipChoice.Selected); RefreshTimeline(); ApplyDocumentPose(); RefreshCodeViews(); };
        var durationRow = Row(panel); durationRow.AddChild(new Label { Text = "总帧数" });
        duration = new SpinBox { MinValue = 1, MaxValue = 100000, Step = 1, CustomMinimumSize = new(90, 0) }; durationRow.AddChild(duration);
        duration.ValueChanged += value => { if (!syncingTimeline && !refreshing) ChangeKey(_ => Clip.Duration = value, "duration:" + Clip.Id); };
        var loopRow = Row(panel); loop = new CheckButton { Text = "循环", ButtonPressed = true }; loopRow.AddChild(loop);
        loop.Toggled += on => { if (!syncingTimeline && !refreshing) ChangeKey(_ => Clip.Loop = on, "loop:" + Clip.Id); };
    }
    private void RefreshCodeViews()
    {
        Guard(() =>
        {
            var clip = Clip;
            var text = new System.Text.StringBuilder();
            text.Append("// 片段 ").Append(clip.Id).Append("  帧 ").Append(frame.Value.ToString("F1")).Append(" / ").Append(clip.Duration.ToString("F0")).Append('\n');
            text.Append(Line("root", clip.RootPose(frame.Value)));
            foreach (var pair in clip.Sample(frame.Value).OrderBy(item => item.Key, StringComparer.Ordinal)) text.Append(Line(pair.Key, pair.Value));
            static string Line(string name, MotionPose pose) => name + ": { x: " + pose.X.ToString("F2") + ", y: " + pose.Y.ToString("F2") + ", angle: " + pose.Angle.ToString("F2")
                + ", sx: " + pose.ScaleX.ToString("F2") + ", sy: " + pose.ScaleY.ToString("F2") + ", picture: " + pose.Picture + " }\n";
            if (!sourceView.HasFocus()) sourceView.Text = text.ToString();
            if (!rawView.HasFocus()) rawView.Text = JsonSerializer.Serialize(document.Graph, NativeContentStore.Json);
        });
    }
    private const string MotionCodePrefix = "external/Chara/Motion/";
    private void ShowOriginalMotions()
    {
        var popup = new PopupMenu { Name = "ImportOriginal" }; AddChild(popup);
        int index = 0;
        foreach (string key in world.Code.Keys.Where(k => k.StartsWith(MotionCodePrefix, StringComparison.Ordinal)).Order()) popup.AddItem(key[MotionCodePrefix.Length..^4], index++);
        popup.IdPressed += id => ImportOriginal(popup.GetItemText((int)id));
        popup.PopupHide += popup.QueueFree;
        popup.Popup(new Rect2I(new Vector2I((int)GetMousePosition().X, (int)GetMousePosition().Y), Vector2I.One));
    }
    internal string HelpText()
    {
        string preset = HelpPreset();
        var text = new System.Text.StringBuilder();
        text.AppendLine("一、动作图是什么");
        text.AppendLine("片段：一段关键帧姿态。状态：Mod 运行时的节点，引用一个片段并带运动与转换；编辑器界面不出现状态编辑，只能在「原始数据」里只读查看。");
        text.AppendLine("转换：满足条件时从当前状态切到目标状态。条件：按键/落地/速度/血量/受击/计时/参数等判断。运动：状态自带的速度、重力与位移。事件：片段内指定帧或接触区间触发的伤害、交互、生成、销毁、SDK 与地图事件。");
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
        var dialog = new AcceptDialog { Title = "说明手册", OkButtonText = "关闭", Size = new(820, 620) };
        dialog.AddChild(new TextEdit { Text = HelpText(), Editable = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        AddChild(dialog); dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCenteredRatio(.8f);
    }
}
