using Godot;
using System.Globalization;
using System.Text.Json;

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
    private readonly List<MotionProject> projects = new();
    private int activeProject;
    private ItemList catalog = null!;
    private OptionButton targetChoice = null!;
    private VBoxContainer parameterForm = null!;
    private VBoxContainer actionWorkspace = null!, motionWorkspace = null!;
    private HBoxContainer projectBar = null!;
    private TabBar workspaceTabs = null!;
    private Label status = null!, previewState = null!;
    private CheckButton play = null!;
    private TextureRect actionSurface = null!;
    private SubViewport? actionViewport;
    private GmCanvas? actionCanvas;
    private NativeCharacterPreview? actionPreview;
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
    private TextureRect surface = null!;
    private SubViewport viewport = null!;
    private GmCanvas canvas = null!;
    private NativeMotionOverlay? overlay;
    private NativeCharacterPreview? preview;
    private GmInstance? targetPreview;
    private ConfirmationDialog confirmation = null!;
    private AcceptDialog message = null!;
    private Action? pendingConfirmation;
    private bool busy, refreshing, refreshingTabs;
    private double playbackSpeed = 1;
    private string? lastState, lastTransition;
    private double lastFrame;
    private bool sessionStale;
    private readonly List<string> lastEvents = new();
    private bool rightHeld;
    private double lastRightTap;
    private bool dragging;
    private string? draggedBone;
    private double dragX, dragY;
    private double elapsed;
    private MotionGraphDocument document { get => Project.Document; set => Project.Document = value; }
    private string? path { get => Project.Path; set => Project.Path = value; }
    private MotionProject Project => projects[Math.Clamp(activeProject, 0, projects.Count - 1)];
    public NativeActionEditor(GmWorld world, ContentRegistry registry, NativeContentStore store, IEnumerable<string>? packages = null)
    {
        this.world = world; this.registry = registry; this.store = store; repository = new(store);
        this.packages = packages?.ToArray() ?? [];
        projects.Add(new MotionProject(new(MotionTargetKind.PlayerPreset, "Nana"), new(DefaultGraph())));
    }
    internal MotionGraphDocument Document => document;
    internal IReadOnlyList<MotionProject> Projects => projects;
    private static MotionGraph DefaultGraph() => new() { Clips = [new()], States = [new() { Movement = new() { InputSpeed = 4, Gravity = .6, JumpSpeed = 10 } }] };
    private MotionClip Clip => document.Graph.Clips[(int)Math.Max(0, clipChoice.Selected)];
    private string SelectedStateId => stateChoice.Selected < 0 ? document.Graph.States[0].Id : stateChoice.GetItemText((int)stateChoice.Selected);
    private MotionTransition? SelectedTransition => stateChoice.Selected < 0 || SelectedIndex(transitionList) < 0 ? null : document.Graph.States[(int)stateChoice.Selected].Transitions[SelectedIndex(transitionList)];
    private ActorContext? PreviewActor => preview is null ? null : targetPreview is { Destroyed: false } target ? new(preview.World, target) : preview.Player is { } player ? new(preview.World, player) : null;
    private string? SelectedBone => bone is null || bone.Selected < 0 ? null : bone.GetItemText((int)bone.Selected);
    public override void _Ready() => BuildEditor();
    public override void _Process(double delta)
    {
        if (motionWorkspace.Visible) { TickPlayback(delta); return; }
        if (!play.ButtonPressed || actionPreview is null || previewBusy) { accumulator = 0; return; }
        accumulator += Math.Min(delta, 0.1);
        if (accumulator < FrameContext.FixedDelta) return;
        accumulator -= FrameContext.FixedDelta; StepPreview();
    }
    private static string L(string chinese, string english) => NativeLocalization.IsChinese ? chinese : english;
    private void StepPreview()
    {
        if (actionPreview is null || previewBusy) return;
        previewBusy = true;
        try { StepAction(); }
        catch (Exception error) { previewBusy = false; SetStatus(L("预览失败：", "Preview failed: ") + NativeLocalization.Text(error.GetBaseException().Message)); }
    }
    private async void StepAction()
    {
        try { await StepOnce(); }
        finally { previewBusy = false; }
    }
    private async Task StepOnce()
    {
        var target = actionPreview!;
        if (activeTarget == NativeActionTargets.Player) { await target.AdvanceAsync(new(false, false, false, false)); actionCanvas?.QueueRedraw(); return; }
        var clock = target.Clock; clock.BeginTick();
        target.Bridge.Update(new(FrameContext.FixedDelta, clock.GameTime, clock.Tick, InputSnapshot.Empty));
        actionCanvas?.QueueRedraw();
    }
    private void Refresh()
    {
        refreshing = true;
        int clipIndex = Project.ClipIndex; clipChoice.Clear(); foreach (var c in document.Graph.Clips) clipChoice.AddItem(c.Id); clipChoice.Select(Math.Clamp(clipIndex, 0, clipChoice.ItemCount - 1));
        parametersText.Text = JsonSerializer.Serialize(document.Graph.Parameters, NativeContentStore.Json);
        RefreshTabs(); RefreshStateInspector(); RefreshGraph();
        refreshing = false; RefreshTimeline(); SyncOverlay();
        if (targetLabel is not null) targetLabel.Text = TargetLabel();
        if (bone.ItemCount > 0) bone.Select(Math.Clamp(Project.BoneIndex, 0, bone.ItemCount - 1));
    }
    private static string NodeName(string id) => "state_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(id));
    private void PopulateSkeleton()
    {
        if (PreviewActor is not { } active) return;
        var nodes = active.Services.Motions.Skeleton(active);
        highlightedBone = null; int selected = bone.Selected; bone.Clear(); bone.AddItem("root"); foreach (var node in nodes) bone.AddItem(node.Name); syncingBone = true; bone.Select(Math.Clamp(selected, 0, bone.ItemCount - 1)); syncingBone = false;
        boneList.Clear(); foreach (var node in nodes) boneList.AddItem(node.Name);
        RefreshIkList(); status.Text = "预览骨架已加载：" + nodes.Count + " 个节点。";
    }
    private void RefreshIkList()
    {
        ikList.Clear();
        foreach (var chain in IkChains) ikList.AddItem(chain.Root + " → " + chain.Mid + " → " + chain.End);
    }
    private void BindSelectedIk()
    {
        Guard(() =>
        {
            var active = PreviewActor; if (active is null) throw new InvalidOperationException("请先加载预览骨架。");
            string end = bone.GetItemText((int)bone.Selected); if (end == "root") throw new InvalidOperationException("根节点不能作为 IK 末端。");
            var nodes = active.Services.Motions.Skeleton(active); if (!nodes.Any(node => node.Name == end)) throw new InvalidOperationException("所选节点不在预览骨架中。"); var current = nodes.Single(node => node.Name == end); if (!nodes.Any(node => node.Instance == current.Parent)) throw new InvalidOperationException("所选节点缺少父节点。"); var mid = nodes.Single(node => node.Instance == current.Parent); if (!nodes.Any(node => node.Instance == mid.Parent)) throw new InvalidOperationException("所选节点没有连续的两骨父链。"); var root = nodes.Single(node => node.Instance == mid.Parent);
            Change(g => { MotionIkPresets.Materialize(g); g.IkChains!.RemoveAll(chain => chain.End == end); g.IkChains.Add(new() { Root = root.Name, Mid = mid.Name, End = end }); }); status.Text = "已绑定 IK：" + root.Name + " → " + mid.Name + " → " + end;
        });
    }
    private void UnbindSelectedIk() => Guard(() => { string end = bone.GetItemText((int)bone.Selected); Change(g => { MotionIkPresets.Materialize(g); g.IkChains!.RemoveAll(chain => chain.Root == end || chain.Mid == end || chain.End == end); }); status.Text = "已解除所选节点的 IK。"; });
    private void SelectBone(string name)
    {
        highlightedBone = name;
        for (int i = 0; i < bone.ItemCount; i++) if (bone.GetItemText(i) == name) { syncingBone = true; bone.Select(i); syncingBone = false; break; }
        if (boneList is not null) for (int i = 0; i < boneList.ItemCount; i++) if (boneList.GetItemText(i) == name) { syncingBone = true; boneList.Select(i); syncingBone = false; break; }
        ReadKey(); SyncOverlay();
    }
    private void ClearBoneSelection()
    {
        highlightedBone = null;
        syncingBone = true; bone.Selected = -1; if (boneList is not null) boneList.DeselectAll(); syncingBone = false;
        x.Value = 0; y.Value = 0; angle.Value = 0; sx.Value = 1; sy.Value = 1; picture.Value = 0;
        if (keyList is not null) keyList.Clear();
        SyncOverlay();
    }
    private IReadOnlyList<MotionIkChain> IkChains => MotionIkPresets.For(document.Graph);
    private bool IsIkEnd(string name) => IkChains.Any(chain => chain.Enabled && chain.End == name);
    private void GenerateReferencePose() => Guard(() =>
    {
        if (PreviewActor is not { } actor) throw new InvalidOperationException("请先加载预览骨架。");
        var descriptor = MotionSkeletonDescriptor.FromActor(actor.World, actor.Instance, document.Graph.Skeleton);
        Change(graph => MotionRigTemplates.EnsureReferencePose(graph, descriptor));
        status.Text = "已生成参考姿态；一次拖动即可撤销。";
    });
    private void RefreshGraph()
    {
        graphView.ClearConnections(); foreach (var node in graphView.GetChildren().OfType<GraphNode>()) { graphView.RemoveChild(node); node.QueueFree(); }
        for (int i = 0; i < document.Graph.States.Count; i++)
        {
            var state = document.Graph.States[i]; var node = new GraphNode { Name = NodeName(state.Id), Title = (document.Graph.Entry == state.Id ? "▶ " : "") + state.Id, PositionOffset = new(30 + (i % 3) * 220, 30 + (i / 3) * 150), CustomMinimumSize = new(180, 80) };
            node.AddChild(new Label { Text = state.Clip }); node.SetSlot(0, true, 0, Colors.SkyBlue, true, 0, Colors.Orange); graphView.AddChild(node);
            node.GuiInput += input => { if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { stateChoice.Select(document.Graph.States.FindIndex(s => s.Id == state.Id)); RefreshStateInspector(); } };
        }
        foreach (var state in document.Graph.States) foreach (var transition in state.Transitions) graphView.ConnectNode(NodeName(state.Id), 0, NodeName(transition.Target), 0);
    }
    private void Guard(Action action) { try { action(); } catch (Exception e) { status.Text = e.GetBaseException().Message; } }
    private void Change(Action<MotionGraph> action) { if (refreshing) return; Guard(() => { document.Edit(action); Refresh(); ApplyDocumentPose(); if (playing.ButtonPressed) RestartPlayback(); status.Text = "已编辑。"; }); }
    private void Confirm(string text, Action action) { pendingConfirmation = action; confirmation.DialogText = text; confirmation.PopupCentered(); }
    private void ShowMessage(string text) { message.DialogText = text; message.PopupCentered(); }
    internal void RequestClose()
    {
        if (closing) return;
        void Close() { closing = true; Hide(); Closed?.Invoke(); QueueFree(); }
        ConfirmDirtyClose(Close);
    }
    private void ImportOriginal() => Guard(() => { string name = original.GetItemText((int)original.Selected); document = new(MotionImport.FromAst(world.Code["external/Chara/Motion/" + name + ".txt"], name.Replace('/', '.'), document.Graph.Skeleton)); path = null; Refresh(); InitializePreview(); status.Text = "已导入：" + name; });
    private void Save() { if (path is null) { ChooseFile(true); return; } Guard(() => { document.Save(path); Refresh(); status.Text = "已保存：" + path; }); }
    private void ChooseFile(bool save)
    {
        var dialog = new FileDialog { FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true }; dialog.AddFilter("*.motion.json", "Motion graph"); AddChild(dialog);
        dialog.CurrentDir = Path.Combine(store.Root, "Actions"); Directory.CreateDirectory(dialog.CurrentDir); dialog.CurrentFile = document.Graph.Id + ".motion.json";
        dialog.FileSelected += file => Guard(() => { if (save) { document.Save(file); } else { document = new(MotionGraph.Load(file)); Project.Target = MotionSkeleton.IsLegacy(document.Graph.Skeleton) ? new(MotionTargetKind.Entity, document.Graph.Skeleton[7..]) : new(MotionTargetKind.PlayerPreset, document.Graph.Skeleton); } path = file; Refresh(); InitializePreview(); status.Text = file; dialog.QueueFree(); }); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCenteredRatio(.8f);
    }
    private async void InitializePreview()
    {
        if (busy) return; busy = true; playing.ButtonPressed = false;
        try
        {
            preview?.Dispose(); targetPreview = null; preview = new(viewport, world, registry, new(store), packages); await preview.InitializeAsync();
            if (closing) return;
            var target = Project.Target;
            ActorContext actor;
            if (target.Kind == MotionTargetKind.Entity) { targetPreview = CreateLegacyPreview(target.Id); actor = new(preview.World, targetPreview); }
            else { await preview.ApplyAsync(CharacterDefinition(target)); targetPreview = null; actor = new(preview.World, preview.Player!); }
            PopulateSkeleton(); preview.Bridge.Services.Motions.Play(actor, document.Graph);
            canvas.Paint = c => preview.Graphics.Render(c); FitView(); status.Text = "预览已加载。";
        }
        catch (Exception e) { status.Text = e.GetBaseException().Message; }
        finally { busy = false; }
    }
    private CharacterDefinition CharacterDefinition(MotionProjectTarget target)
    {
        if (target.Kind == MotionTargetKind.PlayerPreset) return new() { BasePreset = target.Id, Appearance = "Character-" + target.Id };
        if (!registry.Characters.TryGetValue(target.Id, out var definition)) throw new InvalidDataException("角色不存在：" + target.Id);
        var copy = JsonSerializer.Deserialize<CharacterDefinition>(JsonSerializer.SerializeToUtf8Bytes(definition, NativeContentStore.Json), NativeContentStore.Json)!;
        return copy;
    }
    private string CharacterPreset(MotionProjectTarget target) => target.Kind == MotionTargetKind.PlayerCharacter && registry.Characters.TryGetValue(target.Id, out var definition) ? definition.BasePreset : "Nana";
    private GmInstance CreateLegacyPreview(string resource)
    {
        if (preview is null) throw new InvalidOperationException("预览尚未初始化。");
        int objectId = world.Objects.FirstOrDefault(pair => pair.Value.GetProperty("name").GetString() == resource).Key;
        if (objectId == 0 && !world.Objects.ContainsKey(objectId)) throw new InvalidDataException("实体资源不存在：" + resource);
        double x = preview.Player?.Scope.ReadOr("x").Number ?? 96, y = preview.Player?.Scope.ReadOr("y").Number ?? 0;
        var root = preview.World.Create(objectId, x, y, run: false); var child = preview.World.Create(objectId, x + 24, y, run: false); child.Scope.Write("parent", root.Id); child.Scope.Write("PartPosDir", 0); child.Scope.Write("PartPosDis", 24); root.Scope.Write("Process", child.Object, 0); return root;
    }
    internal void TickPlayback(double delta)
    {
        if (!playing.ButtonPressed || busy || preview is null) return;
        elapsed += Math.Min(delta, .5) * playbackSpeed;
        int steps = 0; while (elapsed >= FrameContext.FixedDelta && steps < 30) { elapsed -= FrameContext.FixedDelta; steps++; }
        if (steps > 0) Advance(steps);
    }
    private void Step() => Advance(1);
    private async void Advance(int steps)
    {
        var activePreview = preview; if (busy || activePreview is null || PreviewActor is not { } active) return; busy = true;
        try
        {
            if (sessionStale) { sessionStale = false; RestartPlayback(); }
            lastEvents.Clear();
            for (int i = 0; i < steps; i++)
            {
                await activePreview.AdvanceAsync(PreviewInput());
                var state = active.Services.Motions.State(active);
                if (state is null) continue;
                if (lastState is not null && lastState != state.State) lastTransition = lastState + " → " + state.State;
                var clip = document.Graph.Clips.FirstOrDefault(item => item.Id == document.Graph.States.FirstOrDefault(s => s.Id == state.State)?.Clip);
                if (clip is not null) foreach (var item in clip.Events.Where(e => !e.Contact && e.Start > lastFrame && e.Start <= state.Frame)) lastEvents.Add(item.Value.Length > 0 ? item.Value : item.Id);
                lastState = state.State; lastFrame = state.Frame;
                playState.Text = $"{state.State}  frame={state.Frame:F1}  x={active.X:F2} y={active.Y:F2} suspended={state.Suspended}";
            }
            canvas.QueueRedraw(); SyncOverlay();
        }
        catch (Exception e) { playing.ButtonPressed = false; status.Text = e.GetBaseException().Message; }
        finally { busy = false; }
    }
    private NativeCharacterPreviewInput PreviewInput()
    {
        if (PreviewActor is not { } actor) return new(false, false, false, false);
        bool Held(int slot) => Input.IsKeyPressed(GodotKey(NativeActorActions.Key(actor, slot)));
        bool right = Held(1), dash = false;
        if (right && !rightHeld) { double now = Time.GetTicksMsec(); dash = now - lastRightTap < 250; lastRightTap = now; }
        rightHeld = right;
        return new(Held(0), right, Held(5), dash);
    }
    private static Key GodotKey(int code) => code switch
    {
        8 => Key.Backspace, 9 => Key.Tab, 13 => Key.Enter, 16 => Key.Shift, 17 => Key.Ctrl, 18 => Key.Alt, 27 => Key.Escape, 32 => Key.Space,
        33 => Key.Pageup, 34 => Key.Pagedown, 35 => Key.End, 36 => Key.Home, 37 => Key.Left, 38 => Key.Up, 39 => Key.Right, 40 => Key.Down, 45 => Key.Insert, 46 => Key.Delete,
        >= 112 and <= 123 => Key.F1 + (code - 112), _ => (Key)code
    };
    private string KeyLegend()
    {
        if (PreviewActor is not { } actor) return "";
        string Name(int slot) => OS.GetKeycodeString(GodotKey(NativeActorActions.Key(actor, slot)));
        return "左 " + Name(0) + "   右 " + Name(1) + "   跳 " + Name(5) + "   冲刺 双击" + Name(1);
    }
    private void RestartPlayback()
    {
        Guard(() =>
        {
            if (preview is null || PreviewActor is not { } actor) return;
            preview.Bridge.Services.Motions.Play(actor, document.Graph);
            preview.Bridge.Services.Motions.Seek(actor, frame.Value);
            lastState = document.Graph.Entry; lastFrame = frame.Value; lastTransition = null; lastEvents.Clear(); elapsed = 0; sessionStale = false;
            UpdatePlaybackOverlay();
        });
    }
    private void UpdatePlaybackOverlay()
    {
        if (overlay is null) return;
        if (PreviewActor is not { } actor) { overlay.Legend = ""; overlay.Playback = ""; return; }
        overlay.Legend = "键位  " + KeyLegend();
        var state = actor.Services.Motions.State(actor);
        if (state is null) { overlay.Playback = "状态：未播放"; return; }
        var clip = document.Graph.Clips.FirstOrDefault(item => item.Id == document.Graph.States.FirstOrDefault(s => s.Id == state.State)?.Clip);
        var text = new System.Text.StringBuilder();
        text.Append("状态 ").Append(state.State).Append("  帧 ").Append(state.Frame.ToString("F1"));
        if (clip is not null) text.Append('/').Append(clip.Duration.ToString("F0"));
        if (lastTransition is not null) text.Append("  转换 ").Append(lastTransition);
        if (lastEvents.Count > 0) text.Append("  事件 ").Append(string.Join(",", lastEvents));
        overlay.Playback = text.ToString();
    }
    private void ApplyToGame() => Guard(() =>
    {
        var instance = world.Instances.FirstOrDefault(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objPlayerDoll"].Int));
        if (MotionSkeleton.IsLegacy(document.Graph.Skeleton))
        {
            string resource = document.Graph.Skeleton[7..]; int objectId = world.Objects.FirstOrDefault(pair => pair.Value.GetProperty("name").GetString() == resource).Key; instance = world.Instances.FirstOrDefault(i => !i.Destroyed && i.Object == objectId) ?? throw new InvalidOperationException("没有目标实体实例：" + resource);
        }
        if (instance is null) throw new InvalidOperationException("没有游戏角色");
        var actor = new ActorContext(world, instance); actor.Services.Motions.Play(actor, document.Graph); status.Text = (MotionSkeleton.IsLegacy(document.Graph.Skeleton) ? "已应用到目标实体" : "已应用到游戏角色") + "；关闭编辑器后继续运行；要持久化请保存到 Mod 项目或角色。";
    });
    private void BindSavedCharacter() => Guard(() =>
    {
        if (path is null) throw new InvalidOperationException("请先保存动作图。");
        document.Save(path); registry.MotionGraphs[document.Graph.Id] = MotionGraph.Load(path);
        var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true }; dialog.AddFilter("*.character.json", "Character"); AddChild(dialog); dialog.CurrentDir = store.Characters;
        dialog.FileSelected += file => Guard(() => { var character = NativeContentStore.Load<CharacterDefinition>(file); if (character.BasePreset != document.Graph.Skeleton) throw new InvalidDataException("角色骨架不匹配"); character.MotionGraph = document.Graph.Id; NativeContentStore.Save(file, character); registry.RegisterCharacter(character); dialog.QueueFree(); status.Text = "角色已显式启用动作图。"; }); dialog.PopupCenteredRatio(.8f);
    });
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
        if (definition is null) return;
        var config = Describe();
        if (config.Target != NativeActionTargets.Player) { SetStatus(L("请选择玩家动作。", "Select a player action.")); return; }
        var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true }; dialog.AddFilter("*.character.json", "Character"); AddChild(dialog); dialog.CurrentDir = store.Characters;
        dialog.FileSelected += file =>
        {
            try
            {
                var character = NativeContentStore.Load<CharacterDefinition>(file); character.Actions[config.Slot] = config.Action; character.ActionConfigs[config.Slot] = config;
                NativeContentStore.Save(file, character); registry.RegisterCharacter(character); SetStatus(L("已保存角色动作参数。", "Character action parameters saved."));
            }
            catch (Exception error) { SetStatus(error.GetBaseException().Message); }
            finally { dialog.QueueFree(); }
        };
        dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCenteredRatio(.8f);
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
        if (actionPreview is null || !previewReady) return;
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
            actionPreview!.Bridge.Services.Actors.Bind(new(actionPreview.World, actor), value.Slot, action);
            lastApplied = Describe();
            activeTarget = target;
            bool shown = ShowVisual(actor);
            await SettleAsync(actor);
            previewState.Text = string.Format(NativeLocalization.Text("{0} 已绑定 {1}（{2}）；预览角色：{3}"), target, value.Id, value.Slot, VisualLabel(shown));
            SetStatus(L("已应用到预览。", "Applied to preview."));
            actionCanvas?.QueueRedraw();
            return actor;
        }
        finally { previewBusy = false; }
    }
    internal async Task AdvancePreviewAsync()
    {
        if (actionPreview is null) throw new InvalidOperationException("预览尚未就绪。");
        await StepOnce();
    }
    internal async Task SettleAsync(GmInstance actor)
    {
        var target = actionPreview!;
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
        actionPreview!.FrameOn(x, y, PreviewFrame.X, PreviewFrame.Y, PreviewViewport.X, PreviewViewport.Y);
    }
    private CharacterDefinition? PlayerDefinition => registry.Characters.Count > 0 ? registry.Characters.Values.First() : null;
    internal NativeCharacterPreview Preview => actionPreview ?? throw new InvalidOperationException("预览尚未就绪。");
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
        if (actionPreview is not null && previewReady) return;
        if (actionViewport is null)
        {
            actionViewport = new SubViewport { Size = new(PreviewViewport.X, PreviewViewport.Y), TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Disable3D = true };
            AddChild(actionViewport); actionCanvas = new GmCanvas(); actionViewport.AddChild(actionCanvas);
            actionSurface.Texture = actionViewport.GetTexture();
        }
        actionPreview ??= new(this, world, registry, repository, packages);
        actionCanvas!.Paint = target => actionPreview?.Graphics.Render(target);
        if (!previewReady)
        {
            previewState.Text = L("正在初始化独立预览…", "Initializing isolated preview…");
            await actionPreview.InitializeAsync(); previewReady = true;
        }
        previewClock();
        actionCanvas.QueueRedraw();
        void previewClock() => actionPreview?.Clock.RoomChanged();
    }
    private GmInstance? TargetActor(string target)
    {
        if (actionPreview is null) return null;
        var world = actionPreview.World;
        if (target == NativeActionTargets.Player) return actionPreview.Player;
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
        if (actionPreview is null || activeTarget == NativeActionTargets.Enemy || registry.Characters.Count == 0) return false;
        actionPreview.Show(actor.Id, registry.Characters.Values.First());
        return true;
    }
    private string VisualLabel(bool shown)
    {
        if (activeTarget == NativeActionTargets.Enemy) return L("怪物（外形未接入）", "monster (visual not wired)");
        if (!shown) return L("无数据", "no data");
        return L("默认（", "default (") + registry.Characters.Values.First().BasePreset + "）";
    }
    private void SetStatus(string text) { if (status is not null) status.Text = NativeLocalization.Text(text); }
    public override void _ExitTree()
    {
        actionPreview?.Dispose(); actionPreview = null; previewReady = false;
        actionCanvas = null; actionViewport = null;
        closing = true; preview?.Dispose(); preview = null;
    }
}
