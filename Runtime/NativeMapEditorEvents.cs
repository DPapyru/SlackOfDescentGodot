using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeMapEditor
{
    private void OpenEventEditor()
    {
        var staged = JsonSerializer.Deserialize<List<NativeMapEvent>>(JsonSerializer.SerializeToUtf8Bytes(Document.Events, NativeContentStore.Json), NativeContentStore.Json)!;
        var dialog = new ConfirmationDialog { Name = "MapEventEditor", Title = L("地图事件", "Map events"), OkButtonText = L("应用", "Apply"), CancelButtonText = L("取消", "Cancel"), Size = new(920, 720) }; AddChild(dialog);
        var split = new HSplitContainer { CustomMinimumSize = new(880, 620), SplitOffsets = [220] }; dialog.AddChild(split);
        var left = new VBoxContainer { CustomMinimumSize = new(200, 0) }; split.AddChild(left);
        var list = new ItemList { Name = "MapEventList", AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; left.AddChild(list);
        var buttons = new HBoxContainer(); left.AddChild(buttons);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; split.AddChild(scroll);
        var form = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(620, 0) }; scroll.AddChild(form);
        int selection = 0;
        void RefreshList()
        {
            list.Clear(); foreach (var item in staged) { int index = list.AddItem(item.Name.Length == 0 ? item.Trigger.ToString() : item.Name); list.SetItemTooltip(index, "ID: " + item.Id); }
            selection = Math.Clamp(selection, 0, Math.Max(0, staged.Count - 1)); if (staged.Count > 0) list.Select(selection);
        }
        OptionButton Choice(Node parent, IEnumerable<(string Id, string Name)> values, string current, Action<string> change)
        {
            var input = new OptionButton { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; parent.AddChild(input);
            foreach (var (id, name) in values) { input.AddItem(name); input.SetItemMetadata(input.ItemCount - 1, id); input.GetPopup().SetItemTooltip(input.ItemCount - 1, "ID: " + id); if (id == current) input.Select(input.ItemCount - 1); }
            if (current.Length > 0 && !Enumerable.Range(0, input.ItemCount).Any(i => input.GetItemMetadata(i).AsString() == current)) { input.AddItem(current + L("（缺少依赖）", " (missing)")); input.SetItemMetadata(input.ItemCount - 1, current); input.Select(input.ItemCount - 1); }
            input.ItemSelected += index => change(input.GetItemMetadata((int)index).AsString()); return input;
        }
        IEnumerable<(string Id, string Name)> Targets() => new[] { ("", L("选择目标实体", "Select target entity")) }.Concat(Document.Instances.OfType<JsonObject>().Select(item =>
        {
            string type = item["entityType"]?.GetValue<string>() ?? (world.Objects.TryGetValue(Int(item, "object", -1), out var obj) ? obj.GetProperty("name").GetString()! : "?");
            return (NativeMapEntityIdentity.Of(item), ContentName(type, type) + "  [" + Int(item, "id") + "]");
        }));
        void TextField(Node parent, string label, string text, Action<string> change)
        { parent.AddChild(new Label { Text = label }); var input = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = text }; parent.AddChild(input); input.TextChanged += value => change(value); }
        string TriggerLabel(NativeMapTrigger trigger) => trigger switch { NativeMapTrigger.EnterArea => L("进入区域", "Enter area"), NativeMapTrigger.Interact => L("交互", "Interact"), NativeMapTrigger.TargetDestroyed => L("目标被毁灭", "Target destroyed"), _ => L("敌人清空", "Enemies cleared") };
        string ActionLabel(NativeMapEventActionKind action) => action switch { NativeMapEventActionKind.Message => L("显示提示", "Show message"), NativeMapEventActionKind.SetSwitch => L("开关目标 / 状态", "Switch target / state"), NativeMapEventActionKind.Spawn => L("生成实体", "Spawn entity"), NativeMapEventActionKind.Destroy => L("销毁目标", "Destroy target"), NativeMapEventActionKind.ChangeRoom => L("切换房间", "Change room"), NativeMapEventActionKind.CompleteLevel => L("完成关卡", "Complete level"), _ => L("重新开始", "Restart") };
        void RefreshForm()
        {
            foreach (var child in form.GetChildren()) { form.RemoveChild(child); child.QueueFree(); }
            if (staged.Count == 0) { form.AddChild(new Label { Text = L("添加事件以设置触发条件与动作。", "Add an event to configure a trigger and actions.") }); return; }
            var item = staged[selection];
            TextField(form, L("事件名称", "Event name"), item.Name, value => { item.Name = value; list.SetItemText(selection, value); });
            form.AddChild(new Label { Text = "ID: " + item.Id, AutoTranslateMode = AutoTranslateModeEnum.Disabled });
            Choice(form, Enum.GetValues<NativeMapTrigger>().Select(value => (value.ToString(), TriggerLabel(value))), item.Trigger.ToString(), value => { item.Trigger = Enum.Parse<NativeMapTrigger>(value); RefreshForm(); });
            var once = new CheckBox { Text = L("仅触发一次", "Trigger once"), ButtonPressed = item.Once }; form.AddChild(once); once.Toggled += value => item.Once = value;
            if (item.Trigger is NativeMapTrigger.EnterArea or NativeMapTrigger.Interact)
            {
                Field(form, L("区域 X", "Area X"), "EventX", -1000000, 1000000, item.X).ValueChanged += value => item.X = value;
                Field(form, L("区域 Y", "Area Y"), "EventY", -1000000, 1000000, item.Y).ValueChanged += value => item.Y = value;
                Field(form, L("区域宽度", "Area width"), "EventWidth", 1, 1000000, item.Width).ValueChanged += value => item.Width = value;
                Field(form, L("区域高度", "Area height"), "EventHeight", 1, 1000000, item.Height).ValueChanged += value => item.Height = value;
            }
            if (item.Trigger is NativeMapTrigger.TargetDestroyed or NativeMapTrigger.Interact) Choice(form, Targets(), item.TargetId, value => item.TargetId = value);
            form.AddChild(new HSeparator()); form.AddChild(new Label { Text = L("按顺序执行动作", "Run actions in order") });
            foreach (var action in item.Actions.ToArray())
            {
                var panel = new VBoxContainer(); form.AddChild(panel);
                var row = new HBoxContainer(); panel.AddChild(row);
                Choice(row, Enum.GetValues<NativeMapEventActionKind>().Select(value => (value.ToString(), ActionLabel(value))), action.Kind.ToString(), value => { action.Kind = Enum.Parse<NativeMapEventActionKind>(value); RefreshForm(); });
                Button(row, L("删除动作", "Remove action"), "RemoveEventAction", () => { item.Actions.Remove(action); RefreshForm(); });
                if (action.Kind == NativeMapEventActionKind.Message) TextField(panel, L("提示文字", "Message text"), action.Text, value => action.Text = value);
                if (action.Kind is NativeMapEventActionKind.Destroy or NativeMapEventActionKind.SetSwitch) Choice(panel, Targets(), action.TargetId, value => action.TargetId = value);
                if (action.Kind == NativeMapEventActionKind.SetSwitch)
                {
                    TextField(panel, L("命名状态键（可选）", "Named state key (optional)"), action.StateKey, value => action.StateKey = value);
                    var enabled = new CheckBox { Text = L("启用目标 / 状态为真", "Enable target / set state true"), ButtonPressed = action.Enabled }; panel.AddChild(enabled); enabled.Toggled += value => action.Enabled = value;
                }
                if (action.Kind == NativeMapEventActionKind.Spawn)
                {
                    var nativeTypes = registry.EntityDefinitions.Keys.Concat(registry.Entities.Keys.Where(id => !registry.EntityDefinitions.Values.Any(d => d.BehaviorId == id))).Distinct().Order();
                    var types = world.Objects.OrderBy(x => x.Value.GetProperty("name").GetString()).Select(pair => { string name = pair.Value.GetProperty("name").GetString()!; return ("object:" + name, ContentName(name, name)); }).Concat(nativeTypes.Select(id => ("native:" + id, ContentName(id, registry.EntityDefinitions.GetValueOrDefault(id)?.Name ?? ""))));
                    Choice(panel, new[] { ("", L("选择实体类型", "Select entity type")) }.Concat(types), action.EntityType, value => action.EntityType = value);
                    Field(panel, "X", "SpawnX", -1000000, 1000000, action.X).ValueChanged += value => action.X = value;
                    Field(panel, "Y", "SpawnY", -1000000, 1000000, action.Y).ValueChanged += value => action.Y = value;
                }
                if (action.Kind == NativeMapEventActionKind.ChangeRoom)
                {
                    var maps = registry.Maps.Select(pair => (pair.Key, ContentName(pair.Key, NativeContentStore.Load<NativeMapDocument>(pair.Value).Name))).Append((Document.Id, Document.Name));
                    Choice(panel, new[] { ("", L("选择目标房间", "Select target room")) }.Concat(maps).DistinctBy(x => x.Item1), action.MapId, value => action.MapId = value);
                }
                panel.AddChild(new HSeparator());
            }
            Button(form, L("添加动作", "Add action"), "AddEventAction", () => { item.Actions.Add(new()); RefreshForm(); });
        }
        Button(buttons, L("添加事件", "Add event"), "AddMapEvent", () => { staged.Add(new() { Name = L("新事件", "New event") }); selection = staged.Count - 1; RefreshList(); RefreshForm(); });
        Button(buttons, L("删除", "Remove"), "RemoveMapEvent", () => { if (staged.Count == 0) return; staged.RemoveAt(selection); RefreshList(); RefreshForm(); });
        list.ItemSelected += index => { selection = (int)index; RefreshForm(); };
        dialog.Confirmed += () => { try { var candidate = Document.Clone(); candidate.Events = staged; NativeMapEventValidation.Validate(candidate); Remember(); Document.Events = staged; Changed(); dialog.QueueFree(); } catch (Exception error) { Message(error.Message); } };
        dialog.Canceled += dialog.QueueFree; RefreshList(); RefreshForm(); dialog.PopupCentered();
    }
}
