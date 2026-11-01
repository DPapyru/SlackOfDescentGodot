using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeCharacterEditor
{
    public void SaveForAudit() => SaveRequested();
    public ConfirmationDialog ConfirmationForAudit => confirmation;
    public void SetAutoFitForAudit(bool value) => autoFit = value;
    public string RunUiAudit(string outputDirectory)
    {
        if (!IsNodeReady()) throw new InvalidOperationException("编辑器必须先加入场景树。");
        Directory.CreateDirectory(outputDirectory); RunAssetUiAudit(outputDirectory); RunBaseUiAudit(outputDirectory); NewDocument();
        int previewRequests = 0; Action<CharacterDefinition> counted = _ => previewRequests++; PreviewRequested += counted;
        try
        {
            ApplyRuntimePreview();
            if (previewRequests != 0) throw new InvalidOperationException("空文档不应替换运行时预览，已请求 " + previewRequests + " 次");
            Document.Paint(CurrentPart!.Id, 0, 0, 0, 200, 20, 40, 255); OnPixelsEdited(); ApplyRuntimePreview();
            if (previewRequests != 1) throw new InvalidOperationException("有像素后应恰好应用一次运行时预览，实际 " + previewRequests);
        }
        finally { PreviewRequested -= counted; }
        characterName.Text = "UI 像素审核"; characterName.EmitSignal(LineEdit.SignalName.TextChanged, characterName.Text); Document.Definition.Dependencies.Add("missing.example.dependency");
        Document.Definition.Extra = new() { ["futureMetadata"] = JsonSerializer.SerializeToElement(new { keep = true }) };
        SelectPart(1); canvas.PaintColor = new Color(0.8f, 0.2f, 0.4f, 0.5f); canvas.Tool = NativeCharacterTool.Brush;
        canvas.AuditStroke([new(3, 4), new(4, 4), new(5, 4), new(5, 5)]);
        Document.Paint(CurrentPart!.Id, selectedFrame, 4, 5, 19, 71, 211, 0); OnPixelsEdited();
        canvas.Selection = new(3, 4, 4, 3); _UnhandledKeyInput(new InputEventKey { Keycode = Key.C, CtrlPressed = true, Pressed = true });
        var source = clipboard!; File.WriteAllBytes(Path.Combine(outputDirectory, "source.png"), NativeCharacterPng.Encode(source.Width, source.Height, source.Rgba));
        SelectPart(2); canvas.Selection = new(7, 8, 4, 3); _UnhandledKeyInput(new InputEventKey { Keycode = Key.V, CtrlPressed = true, Pressed = true });
        var pasted = Document.Copy(CurrentPart!.Id, 0, canvas.Selection); File.WriteAllBytes(Path.Combine(outputDirectory, "pasted.png"), NativeCharacterPng.Encode(pasted.Width, pasted.Height, pasted.Rgba));
        _UnhandledKeyInput(new InputEventKey { Keycode = Key.Z, CtrlPressed = true, Pressed = true }); var undo = Document.Copy(CurrentPart!.Id, 0, new(7, 8, 4, 3)); File.WriteAllBytes(Path.Combine(outputDirectory, "undo.png"), NativeCharacterPng.Encode(undo.Width, undo.Height, undo.Rgba)); Redo();
        AddFrame(true); canvas.PaintColor = new Color(0.1f, 0.9f, 0.3f, 0.75f); canvas.AuditStroke([new(9, 9), new(10, 9)]);
        anchorX.Value = 11; anchorY.Value = 15; offsetX.Value = 6; offsetY.Value = -3; layer.Value = -7;
        _UnhandledKeyInput(new InputEventKey { Keycode = Key.S, CtrlPressed = true, Pressed = true });
        if (Document.Dirty) throw new InvalidOperationException("首次保存未建立基线。");
        canvas.Selection = null; canvas.AuditStroke([new(12, 12)]);
        bool discarded = false; ConfirmUnsaved(() => discarded = true);
        if (!confirmation.Visible) throw new InvalidOperationException("未保存修改未显示确认。");
        confirmation.EmitSignal(ConfirmationDialog.SignalName.Canceled); confirmation.Hide();
        if (discarded || !Document.Dirty) throw new InvalidOperationException("取消丢弃未保留修改。");
        _UnhandledKeyInput(new InputEventKey { Keycode = Key.S, CtrlPressed = true, Pressed = true });
        if (!confirmation.Visible) throw new InvalidOperationException("覆盖保存未显示确认。");
        confirmation.EmitSignal(ConfirmationDialog.SignalName.Canceled); confirmation.Hide();
        if (!Document.Dirty) throw new InvalidOperationException("取消覆盖保存未保留修改。");
        _UnhandledKeyInput(new InputEventKey { Keycode = Key.S, CtrlPressed = true, Pressed = true });
        confirmation.EmitSignal(ConfirmationDialog.SignalName.Confirmed); confirmation.Hide();
        if (Document.Dirty) throw new InvalidOperationException("确认覆盖后角色仍然脏。");
        string id = Document.Definition.Id; var before = Document.Duplicate();
        LoadSelected(); if (Document.Definition.Name != "UI 像素审核" || Document.Definition.Id != id || Document.Dirty || !Document.Definition.Dependencies.Contains("missing.example.dependency") || Document.Definition.Extra?["futureMetadata"].GetProperty("keep").GetBoolean() != true) throw new InvalidOperationException("UI 载入未完整恢复角色定义。");
        int count = 0;
        foreach (var part in Document.Definition.Parts) for (int f = 0; f < part.Frames.Count; f++)
        {
            var expected = before.Pixels(before.Definition.Parts.Single(p => p.Id == part.Id), f); var actual = Document.Pixels(part, f);
            File.WriteAllBytes(Path.Combine(outputDirectory, $"before-{count}.png"), NativeCharacterPng.Encode(part.Width, part.Height, expected));
            File.WriteAllBytes(Path.Combine(outputDirectory, $"loaded-{count}.png"), NativeCharacterPng.Encode(part.Width, part.Height, actual)); count++;
        }
        NativeContentStore.Save(Path.Combine(outputDirectory, "ui-audit.json"), new { id, frames = count, copyRect = new { x = 3, y = 4, width = 4, height = 3 }, pasteRect = new { x = 7, y = 8, width = 4, height = 3 }, png = repository.DefinitionPath(id) });
        return $"CHARACTER_UI_AUDIT_OK id={id} parts={Document.Definition.Parts.Count} frames={count} rgba=separate-python-audit";
    }}
