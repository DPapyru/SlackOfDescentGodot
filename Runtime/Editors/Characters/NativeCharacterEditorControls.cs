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
        for (int i = 0; i < Document.UndoCount; i++) historyList.AddItem((i + 1) + L("  编辑步骤", "  Edit"));
        historyList.AddItem(L("当前状态", "Current state")); historyList.Select(Document.UndoCount);
        for (int i = 0; i < Document.RedoCount; i++) historyList.AddItem(L("已撤销", "Undone") + " " + (i + 1));
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
        if (canvas.GetParent() is not ScrollContainer scroll) { FitCanvas(); return; }
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
            case "assetsPanel": leftDock.Visible = !leftDock.Visible; break;
            case "inspectorPanel": rightDock.Visible = !rightDock.Visible; break;
            case "timelinePanel": timelineDock.Visible = !timelineDock.Visible; break;
            case "resetLayout": leftDock.Show(); rightDock.Show(); timelineDock.Show(); mainSplit.SplitOffsets = [260, 900]; FitEditorView(); break;
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
