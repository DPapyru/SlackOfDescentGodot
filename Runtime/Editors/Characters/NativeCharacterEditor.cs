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
    private CheckButton animate = null!, runtimePlay = null!, left = null!, right = null!, showReference = null!, overallMode = null!;
    private SpinBox referenceOpacity = null!, zoomSpin = null!, brushSize = null!;
    private Action<NativeCharacterTool>? SetTool;
    private CheckBox partVisible = null!, replaceOriginal = null!;
    private OptionButton targetChoice = null!;
    private ItemList originalAssets = null!;
    private TabContainer workspace = null!;
    private readonly List<(string Name, JsonElement Data)> assets = new();
    private double previewDirtyTime;
    private bool previewDirty;
    private int selectedPart, selectedFrame;
    private bool refreshing, jumpQueued, dashQueued, autoFit = true, fitting;
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
        Title = L("角色编辑器", "Character editor"); Name = "NativeCharacterEditor";
        Size = new(1440, 900); MinSize = new(960, 700); Unresizable = false; Transient = true;
        Theme = NativeEditorTheme.Create();
        CloseRequested += RequestClose;
        BuildEditorLayout();
        confirmation = new ConfirmationDialog { Title = L("请确认", "Confirm"), OkButtonText = L("确定", "OK"), CancelButtonText = L("取消", "Cancel") }; AddChild(confirmation);
        confirmation.Confirmed += () => { var action = pendingConfirmation; pendingConfirmation = null; action?.Invoke(); }; confirmation.Canceled += () => pendingConfirmation = null;
        message = new AcceptDialog { Title = L("角色编辑器", "Character editor"), OkButtonText = L("确定", "OK") }; AddChild(message);
        FilesDropped += DroppedFiles;
        LoadEditorSettings(); RefreshSaved(); Refresh();
        CallDeferred(nameof(CheckDraft));
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
        Document.Edit(action); draftPending = true; previewDirty = true; previewDirtyTime = 0; RefreshTitle(); composite.Invalidate(); canvas.InvalidateOrder(); canvas.QueueRedraw();
        RefreshEditorPanels();
        if (CurrentPart is { } part && selectedPart < parts.ItemCount) parts.SetItemText(selectedPart, $"{part.Name}  [{part.Layer}]  {part.Width}×{part.Height}  {part.Target}");
    }
    private void Refresh()
    {
        refreshing = true;
        try
        {
            selectedPart = Math.Clamp(selectedPart, 0, Math.Max(0, Document.Definition.Parts.Count - 1));
            parts.Clear(); foreach (var part in Document.Definition.Parts) parts.AddItem($"{part.Name}  [{part.Layer}]  {part.Width}×{part.Height}  {part.Target}");
            if (FindChild("EmptyHint", true, false) is CanvasItem hint) hint.Visible = Document.Definition.Parts.Count == 0 || Document.Definition.Parts.All(part => !HasPixels(part));
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
            canvas.ShowReference = showReference.ButtonPressed; canvas.ReferenceOpacity = (float)(referenceOpacity.Value / 100);
            var wanted = overallMode.ButtonPressed ? NativeCharacterCanvasMode.Overall : NativeCharacterCanvasMode.Part;
            if (canvas.Mode != wanted) { canvas.Mode = wanted; autoFit = true; FitCanvas(); }
            zoomSpin.Value = canvas.Zoom;
            canvas.Bind(Document, p?.Id, selectedFrame); canvas.SelectedFrame = selectedFrame; composite.Document = Document; composite.SelectedFrame = selectedFrame; composite.Invalidate(); RefreshEditorPanels(); RefreshTitle();
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
        RefreshDocumentTab();
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
        try { clipboard = Document.Copy(part.Id, selectedFrame, PartSelection(part)); if (DisplayServer.GetName() != "headless") DisplayServer.ClipboardSet(clipboard.Encode()); status.Text = string.Format(NativeLocalization.Text("已复制 {0}×{1}，包含全部 RGBA 通道。"), clipboard.Width, clipboard.Height); }
        catch (Exception e) { ShowMessage(e.Message); }
    }
    private void Paste()
    {
        if (CurrentPart is not { } part) return;
        var data = DisplayServer.GetName() != "headless" ? NativePixelClipboard.Decode(DisplayServer.ClipboardGet()) ?? clipboard : clipboard;
        if (data is null) { ShowMessage("请先复制像素选区或整帧。"); return; }
        var position = canvas.Selection is { } rect ? new Vector2I(rect.X, rect.Y) : canvas.Cursor;
        if (canvas.Mode == NativeCharacterCanvasMode.Overall) { var c = NativeCharacterLayout.Corner(part); position = canvas.ToCharacter(position) - new Vector2I(c.X, c.Y); }
        var pasted = Document.Paste(part.Id, selectedFrame, data, position.X, position.Y);
        if (canvas.Mode == NativeCharacterCanvasMode.Overall)
        {
            var corner = NativeCharacterLayout.Corner(part); var origin = canvas.Origin;
            canvas.Selection = new(pasted.X + corner.X - origin.X, pasted.Y + corner.Y - origin.Y, pasted.Width, pasted.Height);
        }
        else canvas.Selection = pasted;
        OnPixelsEdited();
    }
    private void FitCanvas()
    {
        if (canvas is null || canvas.Mode != NativeCharacterCanvasMode.Overall) return;
        var area = new Vector2(430, 470);
        if (canvas.GetParent() is ScrollContainer scroll) { var size = scroll.GetRect().Size; if (size.X > 8 && size.Y > 8) area = size; }
        fitting = true;
        try { canvas.FitZoom(area.X - 16, area.Y - 16); if(zoomSpin is not null)zoomSpin.SetValueNoSignal(canvas.Zoom); }
        finally { fitting = false; }
    }
    private void ClearSelection()
    {
        if (CurrentPart is null || canvas.Selection is null) return;
        FillSelection(new Color(0, 0, 0, 0));
        status.Text = NativeLocalization.Text("已清空选区。");
    }
    private void FillSelection(Color value)
    {
        if (CurrentPart is not { } part) return;
        if (canvas.Mode == NativeCharacterCanvasMode.Overall) { FillOverallSelection(value); return; }
        var area = (canvas.Selection ?? new NativePixelRect(0, 0, part.Width, part.Height)).Clip(part.Width, part.Height);
        if (area.Width <= 0 || area.Height <= 0) return;
        byte r = (byte)Math.Clamp((int)Math.Round(value.R * 255), 0, 255), g = (byte)Math.Clamp((int)Math.Round(value.G * 255), 0, 255), b = (byte)Math.Clamp((int)Math.Round(value.B * 255), 0, 255), a = (byte)Math.Clamp((int)Math.Round(value.A * 255), 0, 255);
        Document.BeginEdit();
        try { for (int y = area.Y; y < area.Y + area.Height; y++) for (int x = area.X; x < area.X + area.Width; x++) Document.Paint(part.Id, selectedFrame, x, y, r, g, b, a); }
        finally { Document.EndEdit(); }
        OnPixelsEdited();
    }
    private void OnPixelsEdited() { previewDirty = true; previewDirtyTime = 0; draftPending = true; canvas.Invalidate(); composite.Invalidate(); RefreshEditorPanels(); RefreshTitle(); }
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
        AdvanceTimeline(delta);
        SaveDraftOnIdle(delta);
        if (!runtimePlay.ButtonPressed) { previewAccumulator = 0; return; }
        previewAccumulator += Math.Min(delta, 0.1);
        while (previewAccumulator >= FrameContext.FixedDelta) { previewAccumulator -= FrameContext.FixedDelta; StepRuntime(); }
    }
    public override void _ExitTree()
    {
        SaveEditorSettings(); SaveDraft(false);
        foreach (var texture in editorTextures.Values) texture.Dispose(); editorTextures.Clear();
        if (Directory.Exists(PreviewDirectory)) Directory.Delete(PreviewDirectory, true);
    }
    public override void _UnhandledKeyInput(InputEvent input)
    {
        HandleEditorShortcut(input);
    }
}
