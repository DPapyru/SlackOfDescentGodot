using Godot;
using System.Globalization;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private CodeEdit sourceView = null!, rawView = null!;
    private string sourceSnapshot = "", rawSnapshot = "";

    private void BuildSourcePages(TabContainer pages)
    {
        sourceView = SourcePage(pages, "节点属性修改", "SourceView", CommitFrame, true);
        rawView = SourcePage(pages, "原始数据", "RawView", CommitGraph, false);
    }

    private CodeEdit SourcePage(TabContainer pages, string title, string name, Action commit, bool framePage)
    {
        var page = new VBoxContainer { Name = title }; pages.AddChild(page);
        var edit = CodeEditView(name); page.AddChild(edit);
        edit.FocusExited += commit;
        edit.GuiInput += input =>
        {
            if (input is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter, CtrlPressed: true }) return;
            commit(); edit.AcceptEvent();
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
        if (sourceView is null || rawView is null) return;
        Guard(() =>
        {
            if (!sourceView.HasFocus()) sourceView.Text = sourceSnapshot = MotionToml.RenderFrame(Clip, frame.Value);
            if (!rawView.HasFocus()) rawView.Text = rawSnapshot = MotionToml.Render(document.Graph);
            HighlightSourceBone();
        });
    }

    private void CommitFrame()
    {
        if (sourceView is null || refreshing || sourceView.Text == sourceSnapshot) return;
        Guard(() =>
        {
            var edit = MotionToml.ParseFrame(sourceView.Text, Clip.Id, frame.Value);
            if (!MotionToml.FrameChanged(Clip, edit)) { status.Text = "当前帧内容未修改。"; return; }
            if (!TryChange(graph => MotionToml.ApplyFrame(graph, Clip.Id, edit))) return;
            sourceSnapshot = sourceView.Text;
            status.Text = "当前帧已应用。";
        });
    }

    private void CommitGraph()
    {
        if (rawView is null || refreshing || rawView.Text == rawSnapshot) return;
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