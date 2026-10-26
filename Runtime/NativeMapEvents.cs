using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

public enum NativeMapTrigger { EnterArea, Interact, TargetDestroyed, RoomCleared }
public enum NativeMapEventActionKind { Message, SetSwitch, Spawn, Destroy, ChangeRoom, CompleteLevel, Restart }

public sealed class NativeMapEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public NativeMapTrigger Trigger { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 64;
    public double Height { get; set; } = 64;
    public string TargetId { get; set; } = "";
    public bool Once { get; set; } = true;
    public List<NativeMapEventAction> Actions { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class NativeMapEventAction
{
    public NativeMapEventActionKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string StateKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string TargetId { get; set; } = "";
    public string EntityType { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public string MapId { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public static class NativeMapEntityIdentity
{
    public static string Of(JsonObject instance) => instance["logicalId"]?.GetValue<string>() ?? "entity:" + NativeMapService.Int(instance, "id");
}

public static class NativeMapEventValidation
{
    public static void Validate(NativeMapDocument document)
    {
        if (document.Events is null) throw new InvalidDataException("地图事件列表不能为空。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Events)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) || !Enum.IsDefined(item.Trigger)) throw new InvalidDataException("地图事件标识或触发条件无效。");
            if (!double.IsFinite(item.X) || !double.IsFinite(item.Y) || !double.IsFinite(item.Width) || !double.IsFinite(item.Height) || item.Width <= 0 || item.Height <= 0) throw new InvalidDataException("地图事件区域无效。");
            if (item.Trigger == NativeMapTrigger.TargetDestroyed && string.IsNullOrWhiteSpace(item.TargetId)) throw new InvalidDataException("目标毁灭事件需要目标实体。");
            if (item.Actions is null || item.Actions.Count == 0) throw new InvalidDataException("地图事件至少需要一个动作。");
            foreach (var action in item.Actions)
            {
                if (action is null || !Enum.IsDefined(action.Kind) || !double.IsFinite(action.X) || !double.IsFinite(action.Y)) throw new InvalidDataException("地图事件动作无效。");
                if (action.Kind == NativeMapEventActionKind.Destroy && string.IsNullOrWhiteSpace(action.TargetId)) throw new InvalidDataException("销毁动作需要目标实体。");
                if (action.Kind == NativeMapEventActionKind.SetSwitch && string.IsNullOrWhiteSpace(action.TargetId) && string.IsNullOrWhiteSpace(action.StateKey)) throw new InvalidDataException("开关动作需要目标实体或状态键。");
                if (action.Kind == NativeMapEventActionKind.Spawn && !(action.EntityType.StartsWith("object:", StringComparison.Ordinal) || action.EntityType.StartsWith("native:", StringComparison.Ordinal))) throw new InvalidDataException("生成动作需要稳定实体类型。");
                if (action.Kind == NativeMapEventActionKind.ChangeRoom && string.IsNullOrWhiteSpace(action.MapId)) throw new InvalidDataException("切房动作需要目标房间 ID。");
            }
        }
    }
}
