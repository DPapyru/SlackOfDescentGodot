using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

internal readonly record struct NativeModOwner(string Id, string Generation);
internal readonly record struct NativeResourceScope(NativeModOwner Owner, string Contribution, int Actor);

public sealed partial class ContentRegistry
{
    internal NativeModOwner OwnerScope { get; private set; }
    private readonly Dictionary<(string, string), NativeModOwner> contributionOwners = new();
    internal event Action<NativeModOwner>? OwnerRetired;
    internal event Action<IReadOnlyCollection<string>>? ResourcesCommitted;
    internal readonly List<string> LifecycleDiagnostics = new();
    internal void AssignOwner(string id)
    {
        OwnerScope = new(id, Guid.NewGuid().ToString("N"));
        foreach (var key in ContributionIds()) contributionOwners[key] = OwnerScope;
    }
    internal NativeResourceScope ScopeFor(string kind, string id, int actor) => new(contributionOwners.GetValueOrDefault((kind, id)), kind + ":" + id, actor);
    internal void RetireOwner(NativeModOwner owner) => OwnerRetired?.Invoke(owner);
    internal void CommitResources(IEnumerable<string> paths)
    {
        if (ResourcesCommitted is null) return;
        var changed = paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        foreach (Action<IReadOnlyCollection<string>> observer in ResourcesCommitted.GetInvocationList())
            try { observer(changed); } catch (Exception error) { Console.Error.WriteLine("MOD_RESOURCE_OBSERVER " + error.GetBaseException().Message); }
    }
    internal void ReportLifecycle(NativeResourceScope scope, string phase, Exception error)
    {
        string message = "MOD_" + phase + " " + (string.IsNullOrEmpty(scope.Owner.Id) ? scope.Contribution : scope.Owner.Id + "/" + scope.Contribution) + ": " + error.GetBaseException().Message;
        if (CollectingLifecycleDiagnostics) LifecycleDiagnostics.Add(message); Console.Error.WriteLine(message);
    }
    internal string? ModDataRoot { get; set; }
    public Dictionary<string, NativeInputAction> InputActions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, NativeEntityDefinition> EntityDefinitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, NativeEventHandler> EventHandlers { get; } = new(StringComparer.Ordinal);
    public void RegisterInput(NativeInputAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Id) || action.DefaultKey is < 2 or > 255) throw new InvalidDataException("输入操作需要稳定标识和有效默认按键。");
        InputActions.Add(action.Id, action); Refresh();
    }
    public void RegisterEntity(NativeEntityDefinition definition, Func<IEntityBehavior> factory)
    {
        if (string.IsNullOrWhiteSpace(definition.Id) || definition.Width <= 0 || definition.Height <= 0) throw new InvalidDataException("实体需要稳定标识和有效碰撞尺寸。");
        if (string.IsNullOrEmpty(definition.BehaviorId)) definition.BehaviorId = definition.Id;
        EntityDefinitions.Add(definition.Id, definition); Entities.Add(definition.BehaviorId, factory); Refresh();
    }
    partial void AppendSdkIds(List<(string Kind, string Id)> ids)
    {
        ids.AddRange(InputActions.Keys.Select(id => ("input", id)));
        ids.AddRange(EntityDefinitions.Keys.Select(id => ("entity-definition", id)));
        ids.AddRange(EventHandlers.Keys.Select(id => ("event-handler", id)));
    }
    partial void CopySdkTo(ContentRegistry target)
    {
        foreach (var pair in contributionOwners) target.contributionOwners.Add(pair.Key, pair.Value);
        foreach (var pair in InputActions) target.InputActions.Add(pair.Key, pair.Value);
        foreach (var pair in EntityDefinitions) target.EntityDefinitions.Add(pair.Key, pair.Value);
        foreach (var pair in EventHandlers) target.EventHandlers.Add(pair.Key, pair.Value);
    }
    partial void RemoveSdkFrom(ContentRegistry target)
    {
        foreach (var key in contributionOwners.Keys) target.contributionOwners.Remove(key);
        foreach (string id in InputActions.Keys) target.InputActions.Remove(id);
        foreach (string id in EntityDefinitions.Keys) target.EntityDefinitions.Remove(id);
        foreach (string id in EventHandlers.Keys) target.EventHandlers.Remove(id);
    }
    partial void ClearSdk() { InputActions.Clear(); EntityDefinitions.Clear(); EventHandlers.Clear(); contributionOwners.Clear(); }
}
