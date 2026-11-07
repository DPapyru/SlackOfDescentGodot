using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private GraphEdit graphView = null!;
    private OptionButton stateChoice = null!, stateClip = null!, transitionTarget = null!, conditionKind = null!, conditionComparison = null!;
    private ItemList transitionList = null!, conditionList = null!;
    private LineEdit stateId = null!, stateRename = null!, machineStateId = null!, conditionParameter = null!;
    private SpinBox transitionPriority = null!, conditionKey = null!, conditionValue = null!, velocityX = null!, velocityY = null!, gravity = null!, maxFall = null!, inputSpeed = null!, jumpSpeed = null!, displaceX = null!, displaceY = null!;
    private CheckButton velocityXOn = null!, velocityYOn = null!, inputSpeedOn = null!, jumpSpeedOn = null!;
    private bool syncingGraph;
    private readonly HashSet<string> autoParameters = new(StringComparer.Ordinal);
    private void BuildStatePage(TabContainer tabs)
    {
        var page = Page(tabs, "状态");
        var identity = Row(page); stateChoice = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; identity.AddChild(stateChoice);
        stateChoice.ItemSelected += _ => { if (!refreshing) RefreshStateInspector(); };
        stateId = new LineEdit { PlaceholderText = "新状态标识", CustomMinimumSize = new(100, 0) }; identity.AddChild(stateId); Button(identity, "新增", AddState);
        var rename = Row(page); stateRename = new LineEdit { PlaceholderText = "重命名", CustomMinimumSize = new(100, 0) }; rename.AddChild(stateRename); Button(rename, "重命名", RenameSelectedState); Button(rename, "删除", RemoveSelectedState);
        var clip = Row(page); clip.AddChild(new Label { Text = "片段" }); stateClip = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; clip.AddChild(stateClip);
        stateClip.ItemSelected += index => { if (!refreshing) ChangeState(state => state.Clip = stateClip.GetItemText((int)index),"state:"+SelectedStateId+":clip"); };
        var velocity = Row(page); velocityXOn = new CheckButton { Text = "速度X" }; velocityX = OptionalSpin(velocity, velocityXOn, (state, value) => state.Movement.VelocityX = value, "velocityX"); velocityYOn = new CheckButton { Text = "速度Y" }; velocityY = OptionalSpin(velocity, velocityYOn, (state, value) => state.Movement.VelocityY = value, "velocityY");
        var fall = Row(page); gravity = MovementSpin(fall, "重力", (state, value) => state.Movement.Gravity = value, "gravity"); maxFall = MovementSpin(fall, "最大下落", (state, value) => state.Movement.MaxFall = value, "maxFall");
        var input = Row(page); inputSpeedOn = new CheckButton { Text = "输入速度" }; inputSpeed = OptionalSpin(input, inputSpeedOn, (state, value) => state.Movement.InputSpeed = value, "inputSpeed"); jumpSpeedOn = new CheckButton { Text = "跳跃速度" }; jumpSpeed = OptionalSpin(input, jumpSpeedOn, (state, value) => state.Movement.JumpSpeed = value, "jumpSpeed");
        var displace = Row(page); displaceX = MovementSpin(displace, "位移X", (state, value) => state.Movement.DisplaceX = value, "displaceX"); displaceY = MovementSpin(displace, "位移Y", (state, value) => state.Movement.DisplaceY = value, "displaceY");
    }
    private void BuildStateMachinePage(TabContainer tabs)
    {
        var page = Page(tabs, "状态机");
        var studio = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; page.AddChild(studio);
        graphView = new GraphEdit { ShowGrid = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; studio.AddChild(graphView);
        var sidebar = new VBoxContainer { CustomMinimumSize = new(230, 0) }; studio.AddChild(sidebar);
        var add = Row(sidebar); machineStateId = new LineEdit { PlaceholderText = "新状态标识", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(90, 0) }; add.AddChild(machineStateId); Button(add, "新增", AddMachineState); Button(add, "删除选中", RemoveSelectedState);
        sidebar.AddChild(new HSeparator()); sidebar.AddChild(new Label { Text = "转换" });
        transitionList = List(sidebar, 60); transitionList.ItemSelected += _ => ReadTransition();
        var priority = Row(sidebar); priority.AddChild(new Label { Text = "优先级" }); transitionPriority = new SpinBox { MinValue = -100, MaxValue = 100, Step = 1, CustomMinimumSize = new(70, 0) }; priority.AddChild(transitionPriority);
        transitionPriority.ValueChanged += value => { if (!refreshing) ChangeTransition(transition => transition.Priority = (int)value,"trans:"+SelectedStateId+":"+SelectedIndex(transitionList)+":priority"); };
        var transition = Row(sidebar); transitionTarget = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; transition.AddChild(transitionTarget); Button(transition, "添加", AddTransition); Button(transition, "删除", RemoveTransition);
        sidebar.AddChild(new HSeparator()); sidebar.AddChild(new Label { Text = "条件" });
        conditionList = List(sidebar, 50); conditionList.ItemSelected += _ => ReadCondition();
        var kinds = Row(sidebar);
        conditionKind = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; foreach (string name in Enum.GetNames<MotionConditionKind>()) conditionKind.AddItem(name); kinds.AddChild(conditionKind);
        conditionComparison = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; foreach (string name in Enum.GetNames<MotionComparison>()) conditionComparison.AddItem(name); kinds.AddChild(conditionComparison);
        conditionKind.ItemSelected += _ => { if (!refreshing) EditCondition(condition => condition.Kind = (MotionConditionKind)conditionKind.Selected,"kind"); };
        conditionComparison.ItemSelected += _ => { if (!refreshing) EditCondition(condition => condition.Comparison = (MotionComparison)conditionComparison.Selected,"comparison"); };
        var numbers = Row(sidebar); conditionKey = new SpinBox { MinValue = 0, MaxValue = 400, Step = 1, CustomMinimumSize = new(50, 0) }; numbers.AddChild(conditionKey); conditionValue = new SpinBox { MinValue = -4096, MaxValue = 4096, Step = .01, CustomMinimumSize = new(60, 0) }; numbers.AddChild(conditionValue);
        conditionParameter = new LineEdit { PlaceholderText = "参数名", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(60, 0) }; numbers.AddChild(conditionParameter);
        conditionKey.ValueChanged += value => { if (!refreshing) EditCondition(condition => condition.Key = (int)value,"key"); };
        conditionValue.ValueChanged += value => { if (!refreshing) EditCondition(condition => condition.Value = value,"value"); };
        conditionParameter.TextChanged += text => { if (!refreshing) EditConditionParameter(text); };
        var conditionButtons = Row(sidebar); Button(conditionButtons, "添加条件", AddCondition); Button(conditionButtons, "删除条件", RemoveCondition);
        graphView.NodeSelected += node => { if (refreshing || syncingGraph) return; int index = document.Graph.States.FindIndex(state => NodeName(state.Id) == node.Name.ToString()); if (index < 0) return; stateChoice.Select(index); RefreshStateInspector(); };
        graphView.ConnectionRequest += (from, fromPort, to, toPort) => ConnectStates(from, to);
        graphView.DisconnectionRequest += (from, fromPort, to, toPort) => DisconnectStates(from, to);
        graphView.GuiInput += SelectConnection;
    }
    private void ConnectStates(StringName from, StringName to)
    {
        int source = document.Graph.States.FindIndex(state => NodeName(state.Id) == from.ToString());
        int target = document.Graph.States.FindIndex(state => NodeName(state.Id) == to.ToString());
        if (source < 0 || target < 0) return;
        int index = document.Graph.States[source].Transitions.Count;
        Change(graph => graph.States[source].Transitions.Add(new() { Target = graph.States[target].Id }));
        stateChoice.Select(source); RefreshStateInspector();
        if (index < transitionList.ItemCount) { transitionList.Select(index); ReadTransition(); }
    }
    private void DisconnectStates(StringName from, StringName to)
    {
        int source = document.Graph.States.FindIndex(state => NodeName(state.Id) == from.ToString());
        int target = document.Graph.States.FindIndex(state => NodeName(state.Id) == to.ToString());
        if (source < 0 || target < 0) return;
        string targetId = document.Graph.States[target].Id;
        Change(graph => graph.States[source].Transitions.RemoveAll(transition => transition.Target == targetId));
        stateChoice.Select(source); RefreshStateInspector();
    }
    private void SelectConnection(InputEvent input)
    {
        if (refreshing || input is not InputEventMouseButton mouse || !mouse.Pressed || mouse.ButtonIndex != MouseButton.Left) return;
        var connection = graphView.GetClosestConnectionAtPoint(mouse.Position, 6);
        if (connection.Count == 0) return;
        StringName from = connection["from_node"].AsStringName(), to = connection["to_node"].AsStringName();
        int source = document.Graph.States.FindIndex(state => NodeName(state.Id) == from.ToString());
        if (source < 0) return;
        string target = document.Graph.States.FirstOrDefault(state => NodeName(state.Id) == to.ToString())?.Id ?? "";
        int index = document.Graph.States[source].Transitions.FindIndex(transition => transition.Target == target);
        if (index < 0) return;
        stateChoice.Select(source); RefreshStateInspector();
        transitionList.Select(index); ReadTransition();
    }
    private void RefreshStateSource()
    {
        var graph = document.Graph;
        int index = Math.Clamp(stateChoice.Selected < 0 ? 0 : (int)stateChoice.Selected, 0, graph.States.Count - 1);
        stateChoice.Clear(); foreach (var state in graph.States) stateChoice.AddItem(state.Id); stateChoice.Select(index);
        RefreshStateInspector();
    }
    private void RefreshGraphSelection() => SyncGraphSelection();
    private void SyncGraphSelection()
    {
        if (!IsInstanceValid(this)) return;
        int index = (int)stateChoice.Selected;
        if (graphView is null || index < 0 || index >= document.Graph.States.Count) return;
        string name = NodeName(document.Graph.States[index].Id);
        var node = graphView.GetChildren().OfType<GraphNode>().FirstOrDefault(child => child.Name.ToString() == name);
        if (node is null || node.Selected) return;
        syncingGraph = true;
        try { graphView.SetSelected(node); } finally { syncingGraph = false; }
    }
    private void EditConditionParameter(string text)
    {
        int index = SelectedIndex(conditionList);
        if (SelectedTransition is not { } transition || index < 0 || index >= transition.Conditions.Count) return;
        int stateIndex = (int)stateChoice.Selected, transitionIndex = SelectedIndex(transitionList);
        Change(graph =>
        {
            var condition = graph.States[stateIndex].Transitions[transitionIndex].Conditions[index];
            condition.Parameter = text;
            EnsureParameter(graph, text);
            PruneParameters(graph);
        }, "cond:" + SelectedStateId + ":" + transitionIndex + ":" + index + ":parameter");
    }
    private void EnsureParameter(MotionGraph graph, string name)
    {
        if (name.Length == 0 || graph.Parameters.ContainsKey(name)) return;
        graph.Parameters[name] = 0; autoParameters.Add(name);
    }
    private void PruneParameters(MotionGraph graph)
    {
        if (autoParameters.Count == 0) return;
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in graph.States) foreach (var transition in state.Transitions) foreach (var condition in transition.Conditions) if (condition.Parameter.Length > 0) referenced.Add(condition.Parameter);
        foreach (var clip in graph.Clips) foreach (var item in clip.Events) if (item.Value.Length > 0) referenced.Add(item.Value);
        foreach (string name in autoParameters.Where(candidate => !referenced.Contains(candidate)).ToArray()) { graph.Parameters.Remove(name); autoParameters.Remove(name); }
    }
    private void ChangeState(Action<MotionState> change,string? coalesce=null)
    {
        if (refreshing || stateChoice.Selected < 0) return;
        int index = (int)stateChoice.Selected;
        Change(graph => change(graph.States[index]),coalesce);
    }
    private void ChangeTransition(Action<MotionTransition> change,string? coalesce=null)
    {
        if (refreshing || stateChoice.Selected < 0 || SelectedIndex(transitionList) < 0) return;
        int stateIndex = (int)stateChoice.Selected, transitionIndex = SelectedIndex(transitionList);
        Change(graph => { var state = graph.States[stateIndex]; if (transitionIndex < state.Transitions.Count) change(state.Transitions[transitionIndex]); },coalesce);
    }
    private void EditCondition(Action<MotionCondition> change,string field)
    {
        int index = SelectedIndex(conditionList);
        if (SelectedTransition is not { } transition || index < 0 || index >= transition.Conditions.Count) return;
        ChangeTransition(selected => change(selected.Conditions[index]),"cond:"+SelectedStateId+":"+SelectedIndex(transitionList)+":"+index+":"+field);
    }
    private void AddState() => AddState(stateId.Text);
    private void AddMachineState() => AddState(machineStateId.Text);
    private void AddState(string id) => Change(graph => graph.States.Add(new() { Id = id, Clip = Clip.Id }));
    private void RenameSelectedState() => Change(graph => MotionGraphEdits.RenameState(graph, SelectedStateId, stateRename.Text));
    private void RemoveSelectedState() => Change(graph => MotionGraphEdits.RemoveState(graph, SelectedStateId));
    private void AddTransition() => ChangeState(state => { if (transitionTarget.Selected >= 0) state.Transitions.Add(new() { Target = transitionTarget.GetItemText((int)transitionTarget.Selected), Priority = (int)transitionPriority.Value }); });
    private void RemoveTransition() => ChangeState(state => { int index = SelectedIndex(transitionList); if (index >= 0) state.Transitions.RemoveAt(index); });
    private void AddCondition()
    {
        if (refreshing || stateChoice.Selected < 0 || SelectedIndex(transitionList) < 0) return;
        int stateIndex = (int)stateChoice.Selected, transitionIndex = SelectedIndex(transitionList);
        Change(graph =>
        {
            var state = graph.States[stateIndex];
            if (transitionIndex >= state.Transitions.Count) return;
            var condition = new MotionCondition { Kind = (MotionConditionKind)conditionKind.Selected, Comparison = (MotionComparison)conditionComparison.Selected, Key = (int)conditionKey.Value, Value = conditionValue.Value, Parameter = conditionParameter.Text };
            if (condition.Kind == MotionConditionKind.Parameter) EnsureParameter(graph, condition.Parameter);
            state.Transitions[transitionIndex].Conditions.Add(condition);
        });
    }
    private void RemoveCondition() => ChangeTransition(transition => { int index = SelectedIndex(conditionList); if (index >= 0) transition.Conditions.RemoveAt(index); });
    private void RefreshStateInspector()
    {
        bool previous = refreshing; refreshing = true;
        try { RefreshStateInspectorCore(); RefreshStateList(); } finally { refreshing = previous; }
        if (previous) Callable.From(RefreshGraphSelection).CallDeferred(); else SyncGraphSelection();
    }
    private void RefreshStateList()
    {
        if (stateList is null) return;
        syncingStateList = true;
        try
        {
            stateList.Clear(); foreach (var state in document.Graph.States) stateList.AddItem(state.Id);
            if (stateChoice.Selected >= 0 && stateChoice.Selected < stateList.ItemCount) stateList.Select((int)stateChoice.Selected); else stateList.DeselectAll();
        }
        finally { syncingStateList = false; }
    }
    private void RefreshStateInspectorCore()
    {
        var graph = document.Graph;
        int stateIndex = Math.Clamp(stateChoice.Selected < 0 ? 0 : (int)stateChoice.Selected, 0, graph.States.Count - 1);
        var current = graph.States[stateIndex];
        stateRename.Text = current.Id;
        stateClip.Clear(); foreach (var clip in graph.Clips) stateClip.AddItem(clip.Id);
        int clipIndex = Math.Max(0, graph.Clips.FindIndex(clip => clip.Id == current.Clip)); stateClip.Select(clipIndex);
        var movement = current.Movement;
        velocityXOn.ButtonPressed = movement.VelocityX is not null; velocityX.Value = movement.VelocityX ?? 0;
        velocityYOn.ButtonPressed = movement.VelocityY is not null; velocityY.Value = movement.VelocityY ?? 0;
        gravity.Value = movement.Gravity; maxFall.Value = movement.MaxFall;
        inputSpeedOn.ButtonPressed = movement.InputSpeed is not null; inputSpeed.Value = movement.InputSpeed ?? 0;
        jumpSpeedOn.ButtonPressed = movement.JumpSpeed is not null; jumpSpeed.Value = movement.JumpSpeed ?? 0;
        displaceX.Value = movement.DisplaceX; displaceY.Value = movement.DisplaceY;
        transitionTarget.Clear(); foreach (var state in graph.States) transitionTarget.AddItem(state.Id);
        int transitionIndex = SelectedIndex(transitionList);
        transitionList.Clear(); foreach (var transition in current.Transitions) transitionList.AddItem("→ " + transition.Target + "  [priority " + transition.Priority + "]");
        if (transitionIndex >= 0 && transitionIndex < transitionList.ItemCount) transitionList.Select(transitionIndex);
        RefreshConditions();
        transitionPriority.Value = SelectedTransition?.Priority ?? 0;
    }
    private void ReadTransition()
    {
        conditionList.DeselectAll();
        RefreshConditions();
        bool previous = refreshing; refreshing = true;
        try { transitionPriority.Value = SelectedTransition?.Priority ?? 0; } finally { refreshing = previous; }
    }
    private void RefreshConditions()
    {
        int index = SelectedIndex(conditionList);
        conditionList.Clear();
        if (SelectedTransition is not { } transition) return;
        foreach (var condition in transition.Conditions) conditionList.AddItem(condition.Kind + " " + condition.Comparison + " " + condition.Key + " " + condition.Value + (condition.Parameter.Length > 0 ? "  " + condition.Parameter : ""));
        if (index >= 0 && index < conditionList.ItemCount) conditionList.Select(index);
    }
    private void ReadCondition()
    {
        int index = SelectedIndex(conditionList);
        if (SelectedTransition is not { } transition || index < 0 || index >= transition.Conditions.Count) return;
        var condition = transition.Conditions[index];
        bool previous = refreshing; refreshing = true;
        try
        {
            conditionKind.Select((int)condition.Kind); conditionComparison.Select((int)condition.Comparison);
            conditionKey.Value = condition.Key; conditionValue.Value = condition.Value; conditionParameter.Text = condition.Parameter;
        }
        finally { refreshing = previous; }
    }
    private SpinBox MovementSpin(HBoxContainer row, string label, Action<MotionState, double> write, string field)
    {
        row.AddChild(new Label { Text = label }); var spin = new SpinBox { MinValue = -4096, MaxValue = 4096, Step = .01, CustomMinimumSize = new(86, 0) }; row.AddChild(spin);
        spin.ValueChanged += value => { if (!refreshing) ChangeState(state => write(state, value),"state:"+SelectedStateId+":"+field); };
        return spin;
    }
    private SpinBox OptionalSpin(HBoxContainer row, CheckButton toggle, Action<MotionState, double?> write, string field)
    {
        row.AddChild(toggle); var spin = new SpinBox { MinValue = -4096, MaxValue = 4096, Step = .01, CustomMinimumSize = new(86, 0) }; row.AddChild(spin);
        spin.ValueChanged += value => { if (!refreshing) ChangeState(state => write(state, value),"state:"+SelectedStateId+":"+field); };
        toggle.Toggled += on => { if (!refreshing) ChangeState(state => write(state, on ? spin.Value : null),"state:"+SelectedStateId+":"+field); };
        return spin;
    }
}
