using Godot;
using System.Globalization;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private CodeEdit sourceView = null!, rawView = null!;
    private string sourceSnapshot = "", rawSnapshot = "";
    private MotionGraphDocument? renderedDocument;
    private bool showRawPage;
    private readonly MotionSourceDebounce frameDebounce = new(.4), graphDebounce = new(.4);
    private MotionGraphDocument? frameArmedDocument, graphArmedDocument;
    private int frameArmedRevision, graphArmedRevision;
    private string SettingsPath => Path.Combine(store.Root, "Editor", "action-editor.json");
    private sealed class ActionEditorSettings
    {
        public bool ShowRawData { get; set; }
    }

    private void LoadEditorSettings()
    {
        if (!File.Exists(SettingsPath)) return;
        try { showRawPage = NativeContentStore.Load<ActionEditorSettings>(SettingsPath).ShowRawData; }
        catch (Exception error) when (error is IOException or JsonException) { GD.PrintErr(error.Message); }
    }

    private void SaveEditorSettings()
    {
        try { NativeContentStore.Save(SettingsPath, new ActionEditorSettings { ShowRawData = showRawPage }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { GD.PrintErr(error.Message); }
    }

    private void BuildSourcePages(TabContainer pages)
    {
        sourceView = SourcePage(pages, "节点属性修改", "SourceView", true);
        if (showRawPage) AddRawPage();
    }

    private void AddRawPage()
    {
        if (rawView is not null) return;
        rawView = SourcePage(leftPages, "原始数据", "RawView", false);
    }

    private void RemoveRawPage()
    {
        if (rawView is null) return;
        var page = rawView.GetParent();
        rawView = null!; rawSnapshot = "";
        page.Free();
    }

    private void ToggleRawPage(bool on)
    {
        showRawPage = on;
        if (on) AddRawPage(); else RemoveRawPage();
        SaveEditorSettings();
        RefreshCodeViews();
    }

    private CodeEdit SourcePage(TabContainer pages, string title, string name, bool framePage)
    {
        var page = new VBoxContainer { Name = title }; pages.AddChild(page);
        var edit = CodeEditView(name); page.AddChild(edit);
        edit.TextChanged += framePage ? ArmFrameSource : ArmGraphSource;
        edit.GuiInput += input =>
        {
            if (input is not InputEventKey { Pressed: true, Echo: false, CtrlPressed: true, Keycode: Key.Space }) return;
            edit.RequestCodeCompletion(true); edit.AcceptEvent();
        };
        edit.CodeCompletionRequested += () => FillCompletion(edit, framePage);
        return edit;
    }

    private static CodeEdit CodeEditView(string name) => new()
    {
        Name = name, AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        CustomMinimumSize = new(320, 200), CodeCompletionEnabled = true, SyntaxHighlighter = new TomlSyntaxHighlighter()
    };

    private void RefreshCodeViews()
    {
        if (sourceView is null) return;
        Guard(() =>
        {
            bool replaced = renderedDocument != document;
            if (!sourceView.HasFocus() && (replaced || sourceView.Text == sourceSnapshot)) ReplaceSourceText(sourceView, MotionToml.RenderFrame(Clip, frame.Value), ref sourceSnapshot);
            if (rawView is not null && !rawView.HasFocus() && (replaced || rawView.Text == rawSnapshot)) ReplaceSourceText(rawView, MotionToml.Render(document.Graph), ref rawSnapshot);
            renderedDocument = document;
            HighlightSourceBone();
        });
    }

    private void ArmFrameSource() { frameDebounce.Restart(); frameArmedDocument = document; frameArmedRevision = document.Revision; }
    private void ArmGraphSource() { graphDebounce.Restart(); graphArmedDocument = document; graphArmedRevision = document.Revision; }
    private bool SourceStale(MotionGraphDocument? armed, int revision) => armed is not null && (!ReferenceEquals(armed, document) || armed.Revision != revision);
    private void DiscardSource(bool framePage)
    {
        if (framePage) { frameArmedDocument = null; if (!sourceView.HasFocus()) ReplaceSourceText(sourceView, MotionToml.RenderFrame(Clip, frame.Value), ref sourceSnapshot); }
        else { graphArmedDocument = null; if (rawView is not null && !rawView.HasFocus()) ReplaceSourceText(rawView, MotionToml.Render(document.Graph), ref rawSnapshot); }
        status.Text = "文档已更新，源码未应用。";
    }

    private static void ReplaceSourceText(CodeEdit edit, string text, ref string snapshot)
    {
        snapshot = text;
        if (edit.Text == text) return;
        int line = edit.GetCaretLine(), column = edit.GetCaretColumn(), horizontal = edit.ScrollHorizontal;
        double vertical = edit.ScrollVertical;
        edit.Text = text;
        int lines = edit.GetLineCount(), target = lines == 0 ? 0 : Math.Clamp(line, 0, lines - 1);
        edit.SetCaretLine(target);
        edit.SetCaretColumn(lines == 0 ? 0 : Math.Min(column, edit.GetLine(target).Length));
        edit.ScrollVertical = vertical;
        edit.ScrollHorizontal = horizontal;
    }

    private void TickSourceUpdates(double delta)
    {
        if (frameDebounce.Tick(delta)) CommitFrame();
        if (graphDebounce.Tick(delta)) CommitGraph();
    }

    private void CommitFrame()
    {
        frameDebounce.Cancel();
        if (sourceView is null || refreshing || sourceView.Text == sourceSnapshot) return;
        if (SourceStale(frameArmedDocument, frameArmedRevision)) { DiscardSource(true); return; }
        Guard(() =>
        {
            var edit = MotionToml.ParseFrame(sourceView.Text, Clip.Id);
            if (edit.Index < 0 || edit.Index > Clip.Duration) throw new InvalidDataException("帧号超出片段长度：" + edit.Index.ToString("R", CultureInfo.InvariantCulture) + " / " + Clip.Duration.ToString("R", CultureInfo.InvariantCulture) + "。");
            bool changed = MotionToml.FrameChanged(Clip, edit), moved = Math.Abs(frame.Value - edit.Index) > 1e-9;
            if (changed && !TryChange(graph => MotionToml.ApplyFrame(graph, Clip.Id, edit))) return;
            sourceSnapshot = sourceView.Text;
            if (moved) frame.Value = edit.Index;
            status.Text = changed ? "当前帧已应用。" : moved ? "播放头已移到第 " + edit.Index.ToString("R", CultureInfo.InvariantCulture) + " 帧。" : "当前帧内容未修改。";
        });
    }

    private void CommitGraph()
    {
        graphDebounce.Cancel();
        if (rawView is null || refreshing || rawView.Text == rawSnapshot) return;
        if (SourceStale(graphArmedDocument, graphArmedRevision)) { DiscardSource(false); return; }
        Guard(() =>
        {
            var parsed = MotionToml.Parse(rawView.Text);
            if (!TryChange(graph => graph.CopyFrom(parsed))) return;
            rawSnapshot = rawView.Text;
            status.Text = "源码已应用。";
        });
    }

    private void HighlightSourceBone()
    {
        if (sourceView is null || sourceView.HasFocus() || highlightedBone is null) return;
        for (int line = 0; line < sourceView.GetLineCount(); line++)
        {
            string text = sourceView.GetLine(line);
            if (!text.StartsWith(highlightedBone + ".", StringComparison.Ordinal)) continue;
            sourceView.Select(line, 0, line, text.Length);
            sourceView.CenterViewportToCaret();
            return;
        }
    }

    internal void FillCompletion(CodeEdit edit, bool framePage)
    {
        void Add(CodeEdit.CodeCompletionKind kind, string display, string insert) => edit.AddCodeCompletionOption(kind, display, insert);
        foreach (string bone in MotionSkeleton.Bones) Add(CodeEdit.CodeCompletionKind.Class, bone, bone);
        Add(CodeEdit.CodeCompletionKind.Class, "root", "root");
        foreach (string field in MotionToml.PoseFields) Add(CodeEdit.CodeCompletionKind.Member, field, field);
        foreach (string key in new[] { "index", "clip" }) Add(CodeEdit.CodeCompletionKind.Keyword, key, key);
        foreach (string clip in document.Graph.Clips.Select(item => item.Id)) Add(CodeEdit.CodeCompletionKind.Enum, clip, clip);
        foreach (string state in document.Graph.States.Select(item => item.Id)) Add(CodeEdit.CodeCompletionKind.Enum, state, state);
        string index = frame.Value.ToString("R", CultureInfo.InvariantCulture);
        Add(CodeEdit.CodeCompletionKind.Keyword, "[[frame]] 帧块", framePage ? "[[frame]]\nindex = " + index + "\n" : "[[frame]]\nclip = " + MotionToml.Quote(Clip.Id) + "\nindex = " + index + "\n");
        edit.UpdateCodeCompletionOptions(true);
    }
}
