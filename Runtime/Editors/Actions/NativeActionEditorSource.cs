using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private TextEdit sourceText = null!;
    private bool sourceSyncing;

    private void BuildSourcePage(TabContainer pages)
    {
        var page = Page(pages, "源码");
        sourceText = new TextEdit { Name = "MotionSource", AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(240, 120) };
        page.AddChild(sourceText);
        Button(Row(page), "重新加载", ReloadSource);
        sourceText.FocusExited += CommitSource;
        sourceText.GuiInput += input =>
        {
            if (input is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter, CtrlPressed: true }) return;
            CommitSource();
            sourceText.AcceptEvent();
        };
    }

    private void RefreshSource()
    {
        if (sourceText is null || sourceText.HasFocus()) return;
        sourceSyncing = true;
        try { sourceText.Text = MotionToml.Render(document.Graph); }
        finally { sourceSyncing = false; }
    }

    private void ReloadSource()
    {
        if (sourceText is null) return;
        sourceText.Text = MotionToml.Render(document.Graph);
        status.Text = "源码已重新加载。";
    }

    private void CommitSource()
    {
        if (sourceSyncing || sourceText is null || refreshing) return;
        try
        {
            var parsed = MotionToml.Parse(sourceText.Text);
            document.Edit(graph =>
            {
                graph.FormatVersion = parsed.FormatVersion;
                graph.Id = parsed.Id;
                graph.Skeleton = parsed.Skeleton;
                graph.Entry = parsed.Entry;
                graph.Clips = parsed.Clips;
                graph.States = parsed.States;
                graph.Parameters = parsed.Parameters;
                graph.IkChains = parsed.IkChains;
                graph.EntitySlots = parsed.EntitySlots;
            });
            Refresh();
            sourceText.Text = MotionToml.Render(document.Graph);
            status.Text = "源码已应用。";
        }
        catch (Exception error) { status.Text = error.GetBaseException().Message; }
    }
}
