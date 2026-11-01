using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeCharacterEditor
{
    private bool draftDecisionPending;
    private string SettingsPath => Path.Combine(repository.Store.Root, "Editor", "character-settings.json");
    private string DraftPath => Path.Combine(repository.Store.Root, "Editor", "character-draft.zip");
    private sealed class CharacterEditorSettings
    {
        public Dictionary<string, string> Keys { get; set; } = new();
        public bool Assets { get; set; } = true;
        public bool Layers { get; set; } = true;
        public bool Props { get; set; } = true;
        public bool History { get; set; } = true;
        public bool Timeline { get; set; } = true;
        public bool TimelineBody { get; set; } = true;
        public bool Grid { get; set; } = true;
        public bool Body { get; set; } = true;
        public int Region { get; set; } = 48;
        public int Split { get; set; } = 250;
        public int Side { get; set; } = 810;
    }

    private void SaveEditorSettings()
    {
        if (leftDock is null) return;
        try
        {
            NativeContentStore.Save(SettingsPath, new CharacterEditorSettings
            {
                Keys = editorKeys, Assets = leftDock.Visible, Layers = layerPage.Visible, Props = propertyPage.Visible, History = historyPage.Visible,
                Timeline = timelineDock.Visible, TimelineBody = timelineBody.Visible, Grid = gridButton.ButtonPressed, Body = bodyButton.ButtonPressed,
                Region = editorRegion.SplitOffsets[0], Split = centerGroup.SplitOffsets[0], Side = mainSplit.SplitOffsets[0],
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { GD.PrintErr(error.Message); }
    }

    private void LoadEditorSettings()
    {
        if (!File.Exists(SettingsPath)) return;
        try
        {
            var settings = NativeContentStore.Load<CharacterEditorSettings>(SettingsPath);
            foreach (var item in settings.Keys) if (editorKeys.ContainsKey(item.Key)) editorKeys[item.Key] = item.Value;
            leftDock.Visible = settings.Assets; layerPage.Visible = settings.Layers; propertyPage.Visible = settings.Props; historyPage.Visible = settings.History;
            timelineDock.Visible = settings.Timeline; timelineBody.Visible = settings.TimelineBody;
            editorRegion.SplitOffsets = [settings.Region]; centerGroup.SplitOffsets = [settings.Split]; mainSplit.SplitOffsets = [settings.Side];
            gridButton.ButtonPressed = settings.Grid; bodyButton.ButtonPressed = settings.Body;
        }
        catch (Exception error) when (error is IOException or JsonException) { GD.PrintErr(error.Message); }
    }

    private void ChooseCharacterFile(bool save, Action<string> selected)
    {
        var dialog = new FileDialog { Name = save ? "ExportCharacterPackage" : "ImportCharacterPackage", UseNativeDialog = true, Access = FileDialog.AccessEnum.Filesystem, FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile, Title = L(save ? "导出角色包" : "导入角色包", save ? "Export character package" : "Import character package"), Filters = save ? ["*.zip ; Character package"] : ["*.zip ; Character package", "*.character.json ; Character definition"], Size = new(800, 560) };
        AddChild(dialog); if (save) dialog.CurrentFile = Document.Definition.Id + ".zip";
        dialog.FileSelected += path => { try { selected(path); } catch (Exception error) { ShowMessage(error.Message); } finally { dialog.QueueFree(); } };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }

    private void ExportPackage()
    {
        ChooseCharacterFile(true, path => { NativeContentStore.WriteAtomic(path, NativeCharacterPackage.Encode(Document)); Document.MarkSaved(); RefreshTitle(); SaveDraft(false); });
    }

    private void ImportPackage() => ConfirmUnsaved(() => ChooseCharacterFile(false, LoadCharacterFile));
    private void LoadCharacterFile(string path)
    {
        Document = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? NativeCharacterPackage.Decode(File.ReadAllBytes(path))
            : repository.LoadDefinition(NativeContentStore.Load<CharacterDefinition>(path), Path.GetDirectoryName(Path.GetFullPath(path)));
        selectedPart = selectedFrame = 0; SetTimelinePlaying(false); Refresh(); FitEditorView();
    }

    private void DroppedFiles(string[] paths)
    {
        if (paths.Length == 1 && (paths[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || paths[0].EndsWith(".character.json", StringComparison.OrdinalIgnoreCase)))
        { ConfirmUnsaved(() => { try { LoadCharacterFile(paths[0]); } catch (Exception error) { ShowMessage(error.Message); } }); return; }
        if (CurrentPart is null || paths.Any(path => !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))) { ShowMessage(L("请选择 PNG、角色包 ZIP 或角色定义。", "Choose PNG images, a character ZIP, or a character definition.")); return; }
        try
        {
            canvas.FinishStroke(); var images = paths.Order(StringComparer.Ordinal).Select(p => ReadPng(p)).ToArray();
            Document.ImportFrames(CurrentPart.Id, images); selectedFrame = 0; Refresh(); OnPixelsEdited();
        }
        catch (Exception error) { ShowMessage(error.Message); }
    }

    private void SaveDraftOnIdle(double delta)
    {
        if (draftDecisionPending || !draftPending || (draftElapsed += delta) < 2) return;
        draftElapsed = 0; SaveDraft(false);
    }

    private void SaveDraft(bool notify)
    {
        if (canvas is null || draftDecisionPending) return;
        try
        {
            NativeContentStore.WriteAtomic(DraftPath, NativeCharacterPackage.Encode(Document)); draftPending = false;
            if (notify) ShowMessage(L("草稿已保存。", "Draft saved."));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { GD.PrintErr(error.Message); }
    }

    private void CheckDraft()
    {
        if (!File.Exists(DraftPath) || DisplayServer.GetName() == "headless") return;
        try
        {
            var draft = NativeCharacterPackage.Decode(File.ReadAllBytes(DraftPath));
            if (!draft.Definition.Parts.Any(part => draft.Pixels(part, 0).Any(value => value != 0))) return;
            draftDecisionPending = true;
            confirmation.Canceled += () => draftDecisionPending = false;
            Confirm(L("发现上次的角色草稿，恢复编辑？", "A character draft is available. Restore it?"), () => { draftDecisionPending = false; Document = draft; selectedPart = selectedFrame = 0; Refresh(); FitEditorView(); });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { GD.PrintErr(error.Message); }
    }
}
