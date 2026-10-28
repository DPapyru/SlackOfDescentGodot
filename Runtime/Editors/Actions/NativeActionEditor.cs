using Godot;
using System.Globalization;

namespace SlackOfDescent;

public partial class NativeActionEditor : Window
{
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private readonly NativeContentStore store;
    private readonly NativeCharacterRepository repository;
    private readonly string[] packages;
    private readonly Dictionary<string, NativeActionDefinition> definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> parameters = new(StringComparer.Ordinal);
    private ItemList catalog = null!;
    private OptionButton targetChoice = null!;
    private VBoxContainer parameterForm = null!;
    private Label status = null!, previewState = null!;
    private CheckButton play = null!;
    private TextureRect surface = null!;
    private SubViewport? viewport;
    private GmCanvas? canvas;
    private NativeCharacterPreview? preview;
    private NativeActionDefinition? definition;
    private double accumulator;
    private string activeTarget = NativeActionTargets.Player;
    private bool previewBusy, previewReady, closing, loading, dirty;
    private NativeActionConfig? lastApplied;
    private Button applyButton = null!, undoButton = null!, redoButton = null!;
    internal bool IsDirty => dirty;
    internal string DirtyButtonText => applyButton.Text;
    internal string UndoButtonText => undoButton.Text;
    private readonly Stack<NativeActionConfig> undo = new(), redo = new();
    internal static readonly Vector2I PreviewFrame = new(160, 120);
    internal static readonly Vector2I PreviewViewport = new(640, 480);
    private string SelectedTarget => targetChoice.Selected == 1 ? NativeActionTargets.Enemy : NativeActionTargets.Player;
    public string ConfigDirectory => Path.Combine(store.Root, "Actions");
    public event Action? Closed;
    public NativeActionEditor(GmWorld world, ContentRegistry registry, NativeContentStore store, IEnumerable<string>? packages = null)
    {
        this.world = world; this.registry = registry; this.store = store; repository = new(store);
        this.packages = packages?.ToArray() ?? [];
    }
    private static string L(string chinese, string english) => NativeLocalization.IsChinese ? chinese : english;
    private static HBoxContainer Row(Node parent) { var row = new HBoxContainer(); parent.AddChild(row); return row; }
    private static Button Button(Node parent, string text, Action action) { var button = new Button { Text = text }; parent.AddChild(button); button.Pressed += action; return button; }
    public override void _Ready()
    {
        Title = L("动作编辑器", "Action editor"); Name = "NativeActionEditor"; Size = new(1180, 800); MinSize = new(960, 700); Unresizable = false; Transient = true;
        CloseRequested += RequestClose;
        var margin = new MarginContainer(); AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 10);
        var root = new VBoxContainer(); margin.AddChild(root);
        var file = Row(root);
        Button(file, L("骨架与动作图", "Skeleton and motion graph"),()=>{var editor=new NativeMotionEditor(world,registry,store,packages);AddChild(editor);editor.PopupCentered();});
        Button(file, L("保存配置", "Save config"), SaveRequested);
        Button(file,L("绑定到角色", "Bind to character"),BindConfigToCharacter);
        Button(file, L("导入 .action.json", "Import .action.json"), ImportRequested);
        Button(file, L("导出 .action.json", "Export .action.json"), ExportRequested);
        Button(file, L("检查 Mod 编译", "Check mod compile"), () => CheckCompile());
        Button(file, L("关闭", "Close"), RequestClose);
        var columns = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; root.AddChild(columns);
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
        surface = new TextureRect { Name = "动作预览", CustomMinimumSize = new(340, 260), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, TextureFilter = CanvasItem.TextureFilterEnum.Nearest }; right.AddChild(surface);
        previewState = new Label { Name = "动作预览状态", AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = L("等待应用动作", "Waiting to apply an action") }; right.AddChild(previewState);
        var buttons = Row(right); play = new CheckButton { Name = "动作播放", Text = L("播放", "Play") }; buttons.AddChild(play);
        Button(buttons, L("单步", "Step"), StepPreview);
        var operations = Row(root);
        applyButton = Button(operations, L("应用到预览", "Apply to preview"), Apply);
        undoButton = Button(operations, L("撤销", "Undo"), Undo);
        redoButton = Button(operations, L("重做", "Redo"), Redo);
        status = new Label { Name = "动作状态", AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; operations.AddChild(status);
        RefreshCatalog();
        RefreshApplyButton();
    }
    public override void _Process(double delta)
    {
        if (!play.ButtonPressed || preview is null || previewBusy) { accumulator = 0; return; }
        accumulator += Math.Min(delta, 0.1);
        if (accumulator < FrameContext.FixedDelta) return;
        accumulator -= FrameContext.FixedDelta; StepPreview();
    }
    private void StepPreview()
    {
        if (preview is null || previewBusy) return;
        previewBusy = true;
        try { Step(); }
        catch (Exception error) { previewBusy = false; SetStatus(L("预览失败：", "Preview failed: ") + NativeLocalization.Text(error.GetBaseException().Message)); }
    }
    private async void Step()
    {
        try { await StepOnce(); }
        finally { previewBusy = false; }
    }
    private async Task StepOnce()
    {
        var target = preview!;
        if (activeTarget == NativeActionTargets.Player) { await target.AdvanceAsync(new(false, false, false, false)); canvas?.QueueRedraw(); return; }
        var clock = target.Clock; clock.BeginTick();
        target.Bridge.Update(new(FrameContext.FixedDelta, clock.GameTime, clock.Tick, InputSnapshot.Empty));
        canvas?.QueueRedraw();
    }
    private void RefreshCatalog()
    {
        catalog.Clear(); definitions.Clear();
        void Add(IActionSchema schema, string fallback)
        {
            var value = schema.Definition;
            if (value.Id.Length == 0 || definitions.ContainsKey(value.Id)) return;
            NativeActionValues.ValidateDefinition(value);
            definitions[value.Id] = value;
            catalog.AddItem(value.Name.Length == 0 ? fallback : value.Name);
            catalog.SetItemMetadata(catalog.ItemCount - 1, value.Id);
            catalog.SetItemTooltip(catalog.ItemCount - 1, value.Id + "\n" + value.Description);
        }
        foreach (var sample in NativeActionSamples.All()) Add(sample, sample.Definition.Id);
        foreach (var pair in registry.Actions)
        {
            if (definitions.ContainsKey(pair.Key)) continue;
            try { if (pair.Value() is IActionSchema schema) Add(schema, pair.Key); }
            catch (Exception error) { SetStatus(pair.Key + "：" + error.GetBaseException().Message); }
        }
        if (catalog.ItemCount > 0) { catalog.Select(0); Select(0); }
    }
    private void Select(int index)
    {
        if (index < 0 || index >= catalog.ItemCount) return;
        string id = catalog.GetItemMetadata(index).AsString();
        if (!definitions.TryGetValue(id, out var value)) return;
        definition = value; BuildForm(value);
        undo.Clear(); redo.Clear(); dirty = false;
        lastApplied = Describe();
        RefreshApplyButton();
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
    private void MarkEdited()
    {
        if (loading) return;
        dirty = true;
        RefreshApplyButton();
        SetStatus(L("参数已修改，点击「应用到预览」生效。", "Parameters changed; press Apply to preview."));
    }
    private void RefreshApplyButton()
    {
        applyButton.Text = dirty ? L("应用到预览 (未应用)", "Apply to preview (pending)") : L("应用到预览", "Apply to preview");
        if (undoButton is not null) undoButton.Disabled = undo.Count == 0;
        if (redoButton is not null) redoButton.Disabled = redo.Count == 0;
    }
    private void PushUndo()
    {
        if (definition is null || lastApplied is null) return;
        undo.Push(lastApplied);
        redo.Clear();
        RefreshApplyButton();
    }
    internal void Undo()
    {
        if (undo.Count == 0 || definition is null) return;
        redo.Push(Describe());
        Restore(undo.Pop());
    }
    internal void Redo()
    {
        if (redo.Count == 0 || definition is null) return;
        undo.Push(Describe());
        Restore(redo.Pop());
    }
    private void Restore(NativeActionConfig config)
    {
        if (definition is null || config.Action != definition.Id) return;
        loading = true;
        try
        {
            foreach (var pair in config.Parameters)
                if (parameters.TryGetValue(pair.Key, out var control)) Apply(control, pair.Value);
        }
        finally { loading = false; }
        dirty = true;
        RefreshApplyButton();
        SetStatus(L("已恢复参数，点击「应用到预览」生效。", "Parameters restored; press Apply to preview."));
    }
    private Dictionary<string, string> Collect()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters)
            values[pair.Key] = pair.Value switch
            {
                SpinBox spin => spin.Value.ToString("R", CultureInfo.InvariantCulture),
                CheckButton check => check.ButtonPressed ? "true" : "false",
                OptionButton option => option.Selected >= 0 ? option.GetItemText(option.Selected) : "",
                LineEdit line => line.Text,
                _ => ""
            };
        return values;
    }
    internal bool TryValidate(IReadOnlyDictionary<string, string> values, out string error) => TryValidate(definition, values, out error);
    private static bool TryValidate(NativeActionDefinition? value, IReadOnlyDictionary<string, string> values, out string error)
    {
        try
        {
            NativeActionValues.Resolve(value ?? throw new InvalidDataException("未选择动作。"), values); error = ""; return true;
        }
        catch (Exception failure) when (failure is InvalidDataException or ArgumentException)
        { error = failure.Message; return false; }
    }
    private void FillFromConfig(NativeActionDefinition value)
    {
        string path = NativeActionConfig.Path(ConfigDirectory, value.Id);
        if (!File.Exists(path)) return;
        try
        {
            var config = NativeActionConfig.Load(path); config.Validate(value);
            loading = true;
            foreach (var pair in config.Parameters)
                if (parameters.TryGetValue(pair.Key, out var control)) Apply(control, pair.Value);
            loading = false;
            SelectTarget(config.Target);
            SetStatus(L("已载入配置：", "Loaded config: ") + path);
        }
        catch (Exception error) { SetStatus(L("配置载入失败：", "Config load failed: ") + NativeLocalization.Text(error.GetBaseException().Message)); }
    }
    private void BindConfigToCharacter()
    {
        if(definition is null)return;
        var config=Describe();
        if(config.Target!=NativeActionTargets.Player){SetStatus(L("请选择玩家动作。","Select a player action."));return;}
        var dialog=new FileDialog{FileMode=FileDialog.FileModeEnum.OpenFile,Access=FileDialog.AccessEnum.Filesystem,UseNativeDialog=true};dialog.AddFilter("*.character.json","Character");AddChild(dialog);dialog.CurrentDir=store.Characters;
        dialog.FileSelected+=file=>
        {
            try
            {
                var character=NativeContentStore.Load<CharacterDefinition>(file);character.Actions[config.Slot]=config.Action;character.ActionConfigs[config.Slot]=config;
                NativeContentStore.Save(file,character);registry.RegisterCharacter(character);SetStatus(L("已保存角色动作参数。","Character action parameters saved."));
            }
            catch(Exception error){SetStatus(error.GetBaseException().Message);}
            finally{dialog.QueueFree();}
        };
        dialog.Canceled+=()=>dialog.QueueFree();dialog.PopupCenteredRatio(.8f);
    }
    private static void Apply(Control control, string text)
    {
        switch (control)
        {
            case SpinBox spin: if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) spin.Value = number; break;
            case CheckButton check: check.ButtonPressed = text.Equals("true", StringComparison.OrdinalIgnoreCase); break;
            case OptionButton option: for (int i = 0; i < option.ItemCount; i++) if (option.GetItemText(i) == text) { option.Select(i); break; } break;
            case LineEdit line: line.Text = text; break;
        }
    }
    private void SelectTarget(string target) => targetChoice.Select(target == NativeActionTargets.Enemy ? 1 : 0);
    internal void ReframeTarget()
    {
        if (preview is null || !previewReady) return;
        var actor = TargetActor(SelectedTarget);
        if (actor is null) return;
        activeTarget = SelectedTarget;
        ShowVisual(actor);
        FrameOn(actor);
    }
    private void SaveRequested() => SaveConfig();
    internal string SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var config = Describe();
            config.Save(ConfigDirectory);
            SetStatus(L("已保存：", "Saved: ") + NativeActionConfig.Path(ConfigDirectory, config.Action));
            return "";
        }
        catch (Exception error) { SetStatus(L("保存失败：", "Save failed: ") + NativeLocalization.Text(error.GetBaseException().Message)); return error.GetBaseException().Message; }
    }
    internal NativeActionConfig Describe() => new() { Action = definition!.Id, Slot = definition.Slot, Mode = definition.Mode, Target = SelectedTarget, Parameters = Collect() };
    internal Dictionary<string, string> Values => Collect();
    private void ImportRequested() => Dialog(FileDialog.FileModeEnum.OpenFile, path => Import(path));
    private void ExportRequested() => Dialog(FileDialog.FileModeEnum.SaveFile, path => Export(path));
    private void Dialog(FileDialog.FileModeEnum mode, Action<string> choice)
    {
        var dialog = new FileDialog
        {
            FileMode = mode, Access = FileDialog.AccessEnum.Filesystem,
            Filters = ["*.action.json ; " + L("动作配置", "Action config")],
            CurrentFile = definition is null ? "" : definition.Id + ".action.json"
        };
        AddChild(dialog);
        dialog.FileSelected += path => { choice(path); dialog.QueueFree(); };
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered(new(720, 520));
    }
    internal string Import(string path)
    {
        try
        {
            var config = NativeActionConfig.Load(path);
            if (!definitions.TryGetValue(config.Action, out var value)) throw new InvalidDataException("未加载动作：" + config.Action);
            config.Validate(value);
            int index = Enumerable.Range(0, catalog.ItemCount).FirstOrDefault(i => catalog.GetItemMetadata(i).AsString() == value.Id, -1);
            if (index >= 0) { catalog.Select(index); Select(index); }
            FillFromConfig(value);
            return "";
        }
        catch (Exception error) { SetStatus(L("导入失败：", "Import failed: ") + NativeLocalization.Text(error.GetBaseException().Message)); return error.GetBaseException().Message; }
    }
    internal string Export(string path)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            NativeContentStore.Save(path, Describe());
            SetStatus(L("已导出：", "Exported: ") + path); return "";
        }
        catch (Exception error) { SetStatus(L("导出失败：", "Export failed: ") + NativeLocalization.Text(error.GetBaseException().Message)); return error.GetBaseException().Message; }
    }
    internal string CheckCompile(IEnumerable<string>? targets = null)
    {
        try
        {
            var directories = (targets ?? packages).Where(Directory.Exists).ToArray();
            if (directories.Length == 0) { SetStatus(L("没有已装载的 Mod 可编译检查。", "No installed mods to compile.")); return ""; }
            using var isolated = new NativeModManager(new ContentRegistry());
            var report = new List<string>();
            foreach (string directory in directories)
            {
                var result = isolated.Reload(directory);
                if (!result.Success) report.Add(result.Id + "：" + result.Diagnostics);
            }
            string diagnostics = string.Join("\n", report);
            SetStatus(diagnostics.Length == 0 ? L("编译检查通过。", "Compile check passed.") : L("编译检查失败：", "Compile check failed: ") + diagnostics);
            return diagnostics;
        }
        catch (Exception error) { SetStatus(L("编译检查失败：", "Compile check failed: ") + NativeLocalization.Text(error.GetBaseException().Message)); return error.GetBaseException().Message; }
    }
    internal async void Apply()
    {
        PushUndo();
        try { await Bind(SelectedTarget, definition ?? throw new InvalidOperationException("未选择动作。"), Collect()); dirty = false; RefreshApplyButton(); }
        catch (Exception failure) { SetStatus(L("应用失败：", "Apply failed: ") + NativeLocalization.Text(failure.GetBaseException().Message)); }
    }
    internal async Task<GmInstance> Bind(string target, NativeActionDefinition value, IReadOnlyDictionary<string, string> values)
    {
        if (previewBusy) throw new InvalidOperationException("预览正在忙碌。");
        if (!TryValidate(value, values, out string error)) throw new InvalidDataException(error);
        previewBusy = true;
        try
        {
            await EnsurePreview();
            var action = Create(value, NativeActionValues.Resolve(value, values));
            var actor = TargetActor(target) ?? throw new InvalidOperationException("没有可绑定的目标对象");
            preview!.Bridge.Services.Actors.Bind(new(preview.World, actor), value.Slot, action);
            lastApplied = Describe();
            activeTarget = target;
            bool shown = ShowVisual(actor);
            await SettleAsync(actor);
            previewState.Text = string.Format(NativeLocalization.Text("{0} 已绑定 {1}（{2}）；预览角色：{3}"), target, value.Id, value.Slot, VisualLabel(shown));
            SetStatus(L("已应用到预览。", "Applied to preview."));
            canvas?.QueueRedraw();
            return actor;
        }
        finally { previewBusy = false; }
    }
    internal async Task AdvancePreviewAsync()
    {
        if (preview is null) throw new InvalidOperationException("预览尚未就绪。");
        await StepOnce();
    }
    internal async Task SettleAsync(GmInstance actor)
    {
        var target = preview!;
        if (activeTarget == NativeActionTargets.Player)
            for (int n = 0; n < 12 && !target.World.IsLoadingRoom && target.World.PendingRoom is null; n++)
                await target.AdvanceAsync(new(false, false, false, false));
        else
        {
            for (int n = 0; n < 12; n++)
            {
                var clock = target.Clock; clock.BeginTick();
                target.Bridge.Update(new(FrameContext.FixedDelta, clock.GameTime, clock.Tick, InputSnapshot.Empty));
            }
        }
        FrameOn(actor);
    }
    internal void FrameOn(GmInstance actor)
    {
        int x = actor.Scope.ReadOr("x").Int, y = actor.Scope.ReadOr("y").Int;
        if (activeTarget == NativeActionTargets.Player && PlayerDefinition is { } definition)
        {
            var bounds = NativeCharacterLayout.Bounds(definition);
            x += bounds.X + bounds.Width / 2; y += bounds.Y + bounds.Height / 2;
        }
        preview!.FrameOn(x, y, PreviewFrame.X, PreviewFrame.Y, PreviewViewport.X, PreviewViewport.Y);
    }
    private CharacterDefinition? PlayerDefinition => registry.Characters.Count > 0 ? registry.Characters.Values.First() : null;
    internal NativeCharacterPreview Preview => preview ?? throw new InvalidOperationException("预览尚未就绪。");
    internal OptionButton TargetChoiceControl => targetChoice;
    internal NativeActionDefinition DescribeDefinition => definition ?? throw new InvalidOperationException("未选择动作。");
    internal bool SelectAction(string id)
    {
        for (int index = 0; index < catalog.ItemCount; index++)
            if (catalog.GetItemMetadata(index).AsString() == id) { catalog.Select(index); Select(index); return true; }
        return false;
    }
    internal IReadOnlyDictionary<string, Control> ParameterControls => parameters;
    private ICharacterAction Create(NativeActionDefinition value, IReadOnlyDictionary<string, GmValue> values)
    {
        foreach (var pair in registry.ActionSettings) if (pair.Value.Id == value.Id) return registry.CreateAction(pair.Key, NativeActionValues.Format(value, values));
        if (NativeActionSamples.Factory(value.Id) is { } factory) { var sample = factory(); sample.Configure(values); return (ICharacterAction)sample; }
        throw new InvalidDataException("动作没有可实例化的工厂：" + value.Id);
    }
    private async Task EnsurePreview()
    {
        if (preview is not null && previewReady) return;
        if (viewport is null)
        {
            viewport = new SubViewport { Size = new(PreviewViewport.X, PreviewViewport.Y), TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Disable3D = true };
            AddChild(viewport); canvas = new GmCanvas(); viewport.AddChild(canvas);
            surface.Texture = viewport.GetTexture();
        }
        preview ??= new(this, world, registry, repository, packages);
        canvas!.Paint = target => preview?.Graphics.Render(target);
        if (!previewReady)
        {
            previewState.Text = L("正在初始化独立预览…", "Initializing isolated preview…");
            await preview.InitializeAsync(); previewReady = true;
        }
        previewClock();
        canvas.QueueRedraw();
        void previewClock() => preview?.Clock.RoomChanged();
    }
    private GmInstance? TargetActor(string target)
    {
        if (preview is null) return null;
        var world = preview.World;
        if (target == NativeActionTargets.Player) return preview.Player;
        int kind = world.Constants["objEnemyDoll"].Int;
        var existing = world.Instances.FirstOrDefault(i => !i.Destroyed && i.Object == kind);
        if (existing is not null) return existing;
        var instance = world.Create(kind, 96, 0, run: false);
        instance.Scope.Write("PartsNumber", 0); instance.Scope.Write("PartsList", world.Constants["noone"]);
        instance.Scope.Write("chgDepth", 0); instance.Scope.Write("DefaultDepth", 0); instance.Scope.Write("DefaultDepth2", 0);
        instance.Scope.Write("Angle", 0); instance.Scope.Write("INV", 0); instance.Scope.Write("WEAK", 0); instance.Scope.Write("DAMAGE_ACTIVE", 1);
        instance.Scope.Write("mask_index", world.Constants["sprMask"]); instance.Scope.Write("HP", 1000); instance.Scope.Write("MotionNum", 0);
        instance.Scope.Write("DEAD_MOTION", 1); instance.Scope.Write("BindTarget", -4);
        return instance;
    }
    private bool ShowVisual(GmInstance actor)
    {
        if (preview is null || activeTarget == NativeActionTargets.Enemy || registry.Characters.Count == 0) return false;
        preview.Show(actor.Id, registry.Characters.Values.First());
        return true;
    }
    private string VisualLabel(bool shown)
    {
        if (activeTarget == NativeActionTargets.Enemy) return L("怪物（外形未接入）", "monster (visual not wired)");
        if (!shown) return L("无数据", "no data");
        return L("默认（", "default (") + registry.Characters.Values.First().BasePreset + "）";
    }
    private void SetStatus(string text) { if (status is not null) status.Text = NativeLocalization.Text(text); }
    internal void RequestClose()
    {
        if (closing) return; closing = true;
        Hide(); Closed?.Invoke(); QueueFree();
    }
    public override void _ExitTree()
    {
        preview?.Dispose(); preview = null; previewReady = false;
        canvas = null; viewport = null;
    }
}
