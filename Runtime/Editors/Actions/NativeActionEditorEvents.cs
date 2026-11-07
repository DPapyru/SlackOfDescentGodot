using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private Tree eventTable = null!;
    private ItemList parameterView = null!;
    private OptionButton eventKind = null!;
    private CheckButton eventContact = null!;
    private string? currentEventId;
    private bool syncingEvents;
    private void BuildEventsPage(TabContainer tabs)
    {
        var page = Page(tabs, "事件");
        page.AddChild(new Label { Text = "指定帧或接触区间；Damage / Interact / Spawn / Destroy / SdkEvent / MapEvent", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        eventTable = new Tree { Columns = 5, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row, ColumnTitlesVisible = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        string[] titles = ["起始", "结束", "类型", "值", "接触"];
        for (int i = 0; i < titles.Length; i++) eventTable.SetColumnTitle(i, titles[i]);
        eventTable.SetColumnCustomMinimumWidth(0, 56); eventTable.SetColumnCustomMinimumWidth(1, 56); eventTable.SetColumnCustomMinimumWidth(2, 76); eventTable.SetColumnExpand(3, true); eventTable.SetColumnCustomMinimumWidth(4, 44);
        eventTable.ItemSelected += SelectEvent; eventTable.ItemEdited += CommitEvent;
        page.AddChild(eventTable);
        var controls = Row(page); eventKind = new OptionButton(); foreach (string name in Enum.GetNames<MotionEventKind>()) eventKind.AddItem(name); controls.AddChild(eventKind);
        eventKind.ItemSelected += _ => { if (!syncingEvents && currentEventId is not null) EditEvent(currentEventId, item => item.Kind = (MotionEventKind)eventKind.Selected,"kind"); };
        eventContact = new CheckButton { Text = "接触" }; controls.AddChild(eventContact);
        eventContact.Toggled += on => { if (!syncingEvents && currentEventId is not null) EditEvent(currentEventId, item => item.Contact = on,"contact"); };
        var buttons = Row(page); Button(buttons, "添加事件", AddEvent); Button(buttons, "删除事件", RemoveEvent);
        page.AddChild(new HSeparator()); page.AddChild(new Label { Text = "图参数" });
        parameterView = List(page, 90); parameterView.Name = "图参数";
    }
    private void AddEvent()
    {
        string id = Guid.NewGuid().ToString("N"); currentEventId = id;
        Change(_ => Clip.Events.Add(new() { Id = id, Start = frame.Value, End = frame.Value + 10, Kind = MotionEventKind.SdkEvent }));
    }
    private void RemoveEvent()
    {
        if (currentEventId is null) return;
        string id = currentEventId; currentEventId = null;
        Change(_ => Clip.Events.RemoveAll(item => item.Id == id));
    }
    private void EditEvent(string id, Action<MotionEvent> change, string field) => Change(_ => { var item = Clip.Events.FirstOrDefault(candidate => candidate.Id == id); if (item is not null) change(item); },"event:"+id+":"+field);
    private void SelectEvent()
    {
        if (syncingEvents) return;
        var selected = eventTable.GetSelected();
        currentEventId = selected is null ? null : selected.GetMetadata(0).AsString();
        SyncEventControls();
    }
    private void CommitEvent()
    {
        if (syncingEvents || eventTable.GetEdited() is not { } item) return;
        CommitEvent(item, eventTable.GetEditedColumn());
    }
    private void CommitEvent(TreeItem item, int column)
    {
        string id = item.GetMetadata(0).AsString(); currentEventId = id;
        if (column == 0) { if (double.TryParse(item.GetText(0), out double start)) EditEvent(id, entry => entry.Start = start,"start"); }
        else if (column == 1) { string text = item.GetText(1).Trim(); if (text.Length == 0) EditEvent(id, entry => entry.End = null,"end"); else if (double.TryParse(text, out double end)) EditEvent(id, entry => entry.End = end,"end"); }
        else if (column == 3) EditEvent(id, entry => entry.Value = item.GetText(3),"value");
        SyncEventControls();
    }
    internal void EditEventCell(string id, int column, string text)
    {
        var row = eventTable.GetRoot() is { } root ? root.GetChildren().FirstOrDefault(item => item.GetMetadata(0).AsString() == id) : null;
        if (row is null) return;
        row.SetText(column, text);
        CommitEvent(row, column);
    }
    private void SyncEventControls()
    {
        var item = Clip.Events.FirstOrDefault(candidate => candidate.Id == currentEventId);
        eventKind.Disabled = eventContact.Disabled = item is null;
        if (item is null) return;
        eventKind.Select((int)item.Kind); eventContact.ButtonPressed = item.Contact;
    }
    private void RefreshEvents()
    {
        bool previous = syncingEvents; syncingEvents = true;
        try
        {
            eventTable.Clear(); TreeItem? selected = null; var root = eventTable.CreateItem();
            foreach (var item in Clip.Events)
            {
                var row = eventTable.CreateItem(root);
                row.SetText(0, item.Start.ToString("F1")); row.SetText(1, item.End?.ToString("F1") ?? ""); row.SetText(2, item.Kind.ToString()); row.SetText(3, item.Value ?? ""); row.SetText(4, item.Contact ? "是" : "");
                row.SetMetadata(0, item.Id);
                foreach (int column in new[] { 0, 1, 3 }) row.SetEditable(column, true);
                if (item.Id == currentEventId) selected = row;
            }
            if (selected is null) currentEventId = null; else eventTable.SetSelected(selected, 0);
            SyncEventControls(); RefreshParameters();
        }
        finally { syncingEvents = previous; }
    }
    private void RefreshParameters()
    {
        parameterView.Clear();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var state in document.Graph.States) foreach (var transition in state.Transitions) foreach (var condition in transition.Conditions) if (!string.IsNullOrEmpty(condition.Parameter)) names.Add(condition.Parameter);
        var values = document.Graph.Clips.SelectMany(clip => clip.Events).Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        foreach (string key in document.Graph.Parameters.Keys) if (values.Contains(key)) names.Add(key);
        foreach (string name in names) parameterView.AddItem(name);
    }
}
