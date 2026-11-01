using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeCharacterEditor
{
    private void RefreshEditorPanels()
    {
        if (layerList is null || timelineList is null) return;
        layerList.Clear();
        foreach (var part in Document.Definition.Parts)
        {
            int row = layerList.AddItem((part.Visible ? "◉ " : "◌ ") + part.Name + "  [" + part.Layer + "]", PartThumbnail(part, Math.Clamp(selectedFrame, 0, part.Frames.Count - 1)));
            layerList.SetItemTooltip(row, part.Target + "  " + part.Width + "×" + part.Height);
        }
        if (Document.Definition.Parts.Count > 0) layerList.Select(Math.Clamp(selectedPart, 0, Document.Definition.Parts.Count - 1));
        timelineList.Clear();
        if (CurrentPart is { } current)
        {
            for (int i = 0; i < current.Frames.Count; i++) timelineList.AddItem((i + 1) + "  " + current.Frames[i].Duration + L("拍", " ticks"), PartThumbnail(current, i));
            if (selectedFrame < current.Frames.Count) timelineList.Select(selectedFrame);
            frameStatus.Text = current.Name + " · " + (selectedFrame + 1) + " / " + current.Frames.Count;
        }
        else frameStatus.Text = L("请添加部位", "Add a part");
        historyList.Clear();
        var labels = Document.UndoLabels;
        for (int i = 0; i < labels.Count; i++) historyList.AddItem((i + 1) + "  " + (labels[i].Length == 0 ? L("编辑", "Edit") : labels[i]));
        historyList.AddItem(L("当前状态", "Current state")); historyList.Select(labels.Count);
        foreach (string label in Document.RedoLabels) historyList.AddItem(L("已撤销", "Undone") + " " + (label.Length == 0 ? L("编辑", "Edit") : label));
        draftPending = true;
    }

    private ImageTexture PartThumbnail(NativeCharacterPart part, int frame)
    {
        string key = part.Frames[frame].Id;
        using var image = Image.CreateFromData(part.Width, part.Height, false, Image.Format.Rgba8, Document.Pixels(part, frame));
        if (editorTextures.TryGetValue(key, out var existing)) { existing.Update(image); return existing; }
        var texture = ImageTexture.CreateFromImage(image); editorTextures.Add(key, texture); return texture;
    }

    private void RefreshDocumentTab()
    {
        if (FindChild("DocumentName", true, false) is Label label) label.Text = (Document.Dirty ? "● " : "") + Document.Definition.Name + ".character.json";
    }

    private void SelectAsset(int index)
    {
        if (index < 0 || index >= originalAssets.ItemCount) return;
        var asset = assets[originalAssets.GetItemMetadata(index).AsInt32()]; selectedAsset = asset.Name;
        var frames = asset.Data.GetProperty("frames");
        assetInfo.Text = Path.GetFileNameWithoutExtension(asset.Name) + "\n" + frames.GetArrayLength() + L(" 帧", " frames") + " · " + frames[0].GetProperty("width") + "×" + frames[0].GetProperty("height");
        assetPreview.Texture = AssetThumbnail(asset.Name, asset.Data);
    }

    private ImageTexture? AssetThumbnail(string name, JsonElement data)
    {
        string key = "asset:" + name;
        if (editorTextures.TryGetValue(key, out var existing)) return existing;
        try
        {
            var imageData = ReadPng(Path.Combine(world.Root, data.GetProperty("frames")[0].GetProperty("path").GetString()!), true);
            using var image = Image.CreateFromData(imageData.Width, imageData.Height, false, Image.Format.Rgba8, imageData.Rgba);
            var texture = ImageTexture.CreateFromImage(image); editorTextures.Add(key, texture); return texture;
        }
        catch (Exception) { return null; }
    }

    private void RemoveSelectedPart()
    {
        if (CurrentPart is not { } part) return;
        Confirm(L("删除当前部位及全部动画帧？", "Delete this part and every frame?"), () => { Document.RemovePart(part.Id); selectedPart = Math.Max(0, selectedPart - 1); selectedFrame = 0; Refresh(); OnPixelsEdited(); });
    }

    private void JumpHistory(int index)
    {
        int current = Document.UndoCount; canvas.FinishStroke();
        while (current > index && Document.CanUndo) { Document.Undo(); current--; }
        while (current < index && Document.CanRedo) { Document.Redo(); current++; }
        Refresh(); OnPixelsEdited();
    }

    private void FitEditorView()
    {
        if (canvas is null) return;
        if (canvasCenter?.GetParent() is not ScrollContainer scroll) { FitCanvas(); return; }
        fitting = true;
        try { canvas.FitZoom(Math.Max(50, scroll.Size.X - 24), Math.Max(50, scroll.Size.Y - 24)); zoomSpin.SetValueNoSignal(canvas.Zoom); scroll.ScrollHorizontal = scroll.ScrollVertical = 0; }
        finally { fitting = false; }
    }

    private void StepFrame(int delta)
    {
        if (CurrentPart is not { } part) return;
        SetTimelinePlaying(false); selectedFrame = Math.Clamp(selectedFrame + delta, 0, part.Frames.Count - 1); Refresh();
    }

    private void ToggleTimeline() => SetTimelinePlaying(!timelinePlaying);
    private void SetTimelinePlaying(bool value)
    {
        timelinePlaying = value; playButton.SetPressedNoSignal(value); animate.SetPressedNoSignal(value);
        canvas.Playing = value; composite.Playing = value;
        if (value) timelineElapsed = 0;
        canvas.QueueRedraw(); composite.QueueRedraw();
    }

    private void AdvanceTimeline(double delta)
    {
        if (!timelinePlaying) return;
        timelineElapsed += delta;
        canvas.Playing = composite.Playing = true; canvas.Elapsed = composite.Elapsed = timelineElapsed;
        canvas.QueueRedraw(); composite.QueueRedraw();
        if (CurrentPart is { } part)
        {
            int frame = NativeCharacterAnimation.FrameIndex(part, selectedFrame, true, timelineElapsed, Document.Definition.AnimationFps);
            if (frame < timelineList.ItemCount && !timelineList.IsSelected(frame)) timelineList.Select(frame);
            frameStatus.Text = part.Name + " · " + (frame + 1) + " / " + part.Frames.Count;
        }
    }

    private NativePixelRect? PartSelection(NativeCharacterPart part)
    {
        if (canvas.Selection is not { } selected || canvas.Mode == NativeCharacterCanvasMode.Part) return canvas.Selection;
        var corner = NativeCharacterLayout.Corner(part); var origin = canvas.Origin;
        return new(selected.X + origin.X - corner.X, selected.Y + origin.Y - corner.Y, selected.Width, selected.Height);
    }

    private void FillOverallSelection(Color color)
    {
        var bounds = canvas.ContentBounds;
        var selection = canvas.Selection ?? new NativePixelRect(0, 0, bounds.Width, bounds.Height);
        var ordered = NativeCharacterLayout.Ordered(Document.Definition);
        byte Byte(float value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
        Document.BeginEdit();
        try
        {
            for (int y = selection.Y; y < selection.Y + selection.Height; y++) for (int x = selection.X; x < selection.X + selection.Width; x++)
            {
                var point = canvas.ToCharacter(new(x, y));
                string? hit = NativeCharacterLayout.Hit(ordered, part => Document.Pixels(part, Math.Clamp(selectedFrame, 0, part.Frames.Count - 1)), point.X, point.Y);
                if (hit is null) continue;
                var part = ordered.First(p => p.Id == hit); var corner = NativeCharacterLayout.Corner(part);
                Document.Paint(hit, Math.Clamp(selectedFrame, 0, part.Frames.Count - 1), point.X - corner.X, point.Y - corner.Y, Byte(color.R), Byte(color.G), Byte(color.B), Byte(color.A));
            }
        }
        finally { Document.EndEdit(); }
        OnPixelsEdited();
    }

    private void ApplyViewMode(NativeCharacterCanvasMode mode)
    {
        if (canvas is null) return;
        canvas.Mode = mode;
        modeButton.SetPressedNoSignal(mode == NativeCharacterCanvasMode.Overall);
        partButton.SetPressedNoSignal(mode == NativeCharacterCanvasMode.Part);
        overallMode.SetPressedNoSignal(mode == NativeCharacterCanvasMode.Overall);
        if (workspace is not null) workspace.CurrentTab = mode == NativeCharacterCanvasMode.Overall ? 0 : 1;
        autoFit = true; FitEditorView(); Refresh();
    }

    private void RefreshToolOptions()
    {
        if (canvas is null) return;
        string active = canvas.Tool.ToString();
        foreach (var pair in toolOptions) pair.Value.Visible = pair.Key == "Edits" || pair.Key == active;
    }

    private void UpdateStatus()
    {
        if (status is null) return;
        int frames = Document.Definition.Parts.Sum(part => part.Frames.Count);
        string pointer = "—";
        if (pointerKnown && canvas is not null) pointer = canvas.Mode == NativeCharacterCanvasMode.Part ? "x " + canvas.Cursor.X + "  y " + canvas.Cursor.Y : L("整体 ", "All ") + canvas.Cursor.X + ", " + canvas.Cursor.Y;
        string selection = canvas?.Selection is { } rect ? string.Format(L("选区 {0}×{1} @ {2},{3}", "Selection {0}×{1} @ {2},{3}"), rect.Width, rect.Height, rect.X, rect.Y)
            : CurrentPart is { } part ? part.Name + " · " + string.Format(L("帧 {0}/{1}", "frame {0}/{1}"), selectedFrame + 1, part.Frames.Count) : L("未选择", "Nothing selected");
        string text = (Document.Dirty ? L("未保存", "Unsaved") : L("已保存", "Saved")) + "　" + pointer + "　ID：" + Document.Definition.Id
            + "　" + L("部位", "Parts") + "：" + Document.Definition.Parts.Count + "　" + L("帧", "Frames") + "：" + frames
            + "　" + L("撤销", "Undo") + "：" + Document.UndoCount + "　" + selection;
        if (Document.Definition.Dependencies.Count > 0) text += "　" + L("依赖", "Dependencies") + "：" + string.Join("、", Document.Definition.Dependencies);
        if (Document.MissingFrames.Count > 0) text += "\n" + NativeLocalization.Text("缺少或损坏的帧（保留原始引用）：") + string.Join("、", Document.MissingFrames.Values);
        status.Text = text;
    }

    private void LoadOriginalPreset(string name)
    {
        if (!Presets.Contains(name)) return;
        Change(() => { Document.Definition.BasePreset = name; Document.Definition.Appearance = "Character-" + name; }, L("基础角色", "Base character"));
        LoadOriginalCharacter();
    }

    private void RunEditorAction(string id)
    {
        if (editorActions.TryGetValue(id, out var action)) { action(); return; }
        switch (id)
        {
            case "loadOriginal": LoadOriginalCharacter(); break;
            case "generateBase": GenerateBase(); break;
            case "saveProject": SaveToProject(); break;
            case "loadSaved": ConfirmUnsaved(LoadSelected); break;
            case "close": RequestClose(); break;
            case "grid": gridButton.ButtonPressed = !gridButton.ButtonPressed; break;
            case "body": bodyButton.ButtonPressed = !bodyButton.ButtonPressed; break;
            case "modeOverall": ApplyViewMode(NativeCharacterCanvasMode.Overall); break;
            case "modePart": ApplyViewMode(NativeCharacterCanvasMode.Part); break;
            case "assetsPanel": leftDock.Visible = !leftDock.Visible; break;
            case "layersPanel": layerPage.Visible = !layerPage.Visible; break;
            case "propsPanel": propertyPage.Visible = !propertyPage.Visible; break;
            case "historyPanel": historyPage.Visible = !historyPage.Visible; break;
            case "timelinePanel": timelineDock.Visible = !timelineDock.Visible; break;
            case "resetLayout":
                leftDock.Show(); layerPage.Show(); propertyPage.Show(); historyPage.Show(); timelineDock.Show(); timelineBody.Show();
                editorRegion.SplitOffsets = [48]; centerGroup.SplitOffsets = [250]; mainSplit.SplitOffsets = [810]; FitEditorView(); break;
            case var preset when preset.StartsWith("preset:", StringComparison.Ordinal): LoadOriginalPreset(preset[7..]); break;
            case "about": ShowMessage(L("角色编辑器\n原版素材来自 GameData/Chara/Doll；保存内容可在 Mod 项目和游戏中使用。", "Character editor\nAssets from GameData/Chara/Doll. Saved characters can be used in Mod projects and the game.")); break;
        }
    }

    private void HandleEditorShortcut(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true, Echo: false } key || confirmation.Visible || message.Visible) return;
        if (GuiGetFocusOwner() is LineEdit or TextEdit) return;
        string combo = KeyCombo(key);
        string? action = editorKeys.FirstOrDefault(pair => pair.Value.Equals(combo, StringComparison.OrdinalIgnoreCase)).Key;
        if (action is null) { if (key.Keycode == Key.Escape) { canvas.Selection = null; canvas.QueueRedraw(); } return; }
        RunEditorAction(action); GetViewport().SetInputAsHandled();
    }

    private static string KeyCombo(InputEventKey key)
    {
        string name = OS.GetKeycodeString(key.Keycode);
        return (key.CtrlPressed ? "Ctrl+" : "") + (key.AltPressed ? "Alt+" : "") + (key.ShiftPressed ? "Shift+" : "") + name;
    }

    private void ShowShortcuts()
    {
        var dialog = new ConfirmationDialog { Name = "CharacterShortcuts", Title = L("键盘快捷键设置", "Keyboard shortcuts"), OkButtonText = L("保存", "Save"), CancelButtonText = L("取消", "Cancel"), Size = new(560, 650) }; AddChild(dialog);
        var scroll = new ScrollContainer { CustomMinimumSize = new(520, 560), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; dialog.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(list);
        var inputs = new Dictionary<string, LineEdit>();
        foreach (var pair in editorKeys)
        {
            var row = Row(list); row.AddChild(new Label { Text = ActionLabel(pair.Key), CustomMinimumSize = new(170, 0) });
            var edit = new LineEdit { Text = pair.Value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = L("按下所需组合键，或输入 Ctrl+S 这样的组合", "Press a key combination, or type Ctrl+S") }; row.AddChild(edit); inputs.Add(pair.Key, edit);
            edit.GuiInput += input => { if (input is InputEventKey { Pressed: true } key) { edit.Text = KeyCombo(key); edit.AcceptEvent(); } };
        }
        dialog.Confirmed += () =>
        {
            var values = inputs.Values.Select(e => e.Text.Trim()).Where(v => v.Length > 0).ToList();
            if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count) { ShowMessage(L("快捷键有冲突，请为每项选择不同组合。", "Shortcut conflict: choose a unique combination for each action.")); return; }
            foreach (var pair in inputs) editorKeys[pair.Key] = pair.Value.Text.Trim(); SaveEditorSettings(); dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }

    private IReadOnlyList<(NativeCharacterBaseSlot Slot, string Asset, string Part)> OriginalPlacements(string presetName)
    {
        var result = new List<(NativeCharacterBaseSlot, string, string)>();
        void Add(string folder, string file, string target, int layer)
        {
            string path = "Chara/Doll/" + folder + "/Character-" + presetName + "_" + file + ".gif";
            if (assets.Any(a => a.Name == path)) result.Add((new(target, layer, folder, [file]), path, file));
        }
        Add("Body", "Body_Bottom", "BODY_BOTTOM", 0);
        if (!result.Any(x => x.Item3 == "Body_Bottom")) Add("Body", "Body_Bottom_Pants", "BODY_BOTTOM", 0);
        Add("Body", "Body_Top", "BODY_TOP", 10);
        Add("Body", "BodySocks0_below", "BODY_BOTTOM", -5); Add("Body", "BodySocks0_above", "BODY_BOTTOM", 5);
        Add("Body", "BodySocks1_below", "BODY_BOTTOM", -4); Add("Body", "BodySocks1_above", "BODY_BOTTOM", 6);
        Add("Head", "Hair", "HEAD", 30); Add("Head", "Eye", "HEAD", 31); Add("Head", "Mouth", "HEAD", 32);
        foreach (string side in new[] { "Below", "Above" })
        {
            string suffix = side.ToLowerInvariant(), boneSide = side.ToUpperInvariant(); int sign = side == "Above" ? 1 : -1;
            Add("Foot_" + side, "Socks0_" + suffix, "FOOT1_" + boneSide, sign * 40);
            Add("Foot_" + side, "Socks1_" + suffix, "FOOT2_" + boneSide, sign * 41);
            Add("Foot_" + side, "Socks2_" + suffix, "FOOT3_" + boneSide, sign * 42);
            Add("Foot_" + side, "Boots0_" + suffix, "FOOT1_" + boneSide, sign * 43);
            Add("Foot_" + side, "Boots1_" + suffix, "FOOT2_" + boneSide, sign * 44);
            Add("Foot_" + side, "Shoes_" + suffix, "FOOT3_" + boneSide, sign * 45);
            Add("Hand_" + side, "Hand1_" + suffix, "HAND1_" + boneSide, sign * 50);
            Add("Hand_" + side, "Hand2_" + suffix, "HAND2_" + boneSide, sign * 51);
            Add("Hand_" + side, "Gloves_" + suffix, "HAND3_" + boneSide, sign * 52);
        }
        Add("Tail", "Tail", "TAIL", -10);
        return result.OrderBy(x => x.Item1.Layer).ToArray();
    }

    private void LoadOriginalCharacter()
    {
        string presetName = Document.Definition.BasePreset;
        void Apply()
        {
            canvas.FinishStroke(); Document.BeginEdit();
            try
            {
                foreach (var part in Document.Definition.Parts.ToList()) Document.RemovePart(part.Id);
                foreach (var item in OriginalPlacements(presetName))
                {
                    var asset = assets.First(a => a.Name == item.Asset);
                    var part = Document.AddPart(item.Part, asset.Data.GetProperty("frames")[0].GetProperty("width").GetInt32(), asset.Data.GetProperty("frames")[0].GetProperty("height").GetInt32(), item.Slot.Layer);
                    part.Target = item.Slot.Target; ApplyBaseAssets(part, asset);
                }
                Document.Definition.Name = presetName; Document.Definition.Appearance = "Character-" + presetName;
            }
            finally { Document.EndEdit(); }
            selectedPart = Math.Max(0, Document.Definition.Parts.Count - 1); selectedFrame = 0; SetTimelinePlaying(false); overallMode.ButtonPressed = true; Refresh(); FitEditorView(); OnPixelsEdited();
        }
        if (Document.Definition.Parts.Any(HasPixels)) Confirm(L("载入原版角色会替换当前全部部位；可撤销此操作。", "Loading an original replaces every part. You can undo this operation."), Apply); else Apply();
    }
}
