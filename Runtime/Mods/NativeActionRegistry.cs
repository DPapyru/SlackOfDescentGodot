namespace SlackOfDescent;

public sealed partial class ContentRegistry
{
    public Dictionary<string, Func<ICharacterAction>> Actions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Func<IReadOnlyDictionary<string, GmValue>, ICharacterAction>> ActionFactories { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, NativeActionDefinition> ActionSettings { get; } = new(StringComparer.Ordinal);
    public NativeActionDefinition? ActionSchema(string id)
    {
        if (ActionSettings.TryGetValue(id, out var definition)) return definition;
        if (!Actions.TryGetValue(id, out var factory)) return null;
        try { return (factory() as IActionSchema)?.Definition; } catch { return null; }
    }
    public Dictionary<string, Func<IEntityBehavior>> Entities { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CharacterDefinition> Characters { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Resources { get; } = new(StringComparer.Ordinal);
    public long Revision { get; private set; }
    public event Action? Changed;
    internal event Action? Committed;
    internal bool DeferredCommit;
    internal bool CollectingLifecycleDiagnostics;
    internal void Commit()
    {
        LifecycleDiagnostics.Clear();
        if (Committed is null) return;
        CollectingLifecycleDiagnostics = true;
        try
        {
            foreach (Action observer in Committed.GetInvocationList())
                try { observer(); } catch (Exception error)
                {
                    string message = "MOD_COMMIT_OBSERVER " + error.GetBaseException().Message;
                    LifecycleDiagnostics.Add(message); Console.Error.WriteLine(message);
                }
        }
        finally { CollectingLifecycleDiagnostics = false; }
    }
    public void Refresh() { Revision++; Changed?.Invoke(); if (!DeferredCommit) Commit(); }
    public void RegisterAction(string id, Func<ICharacterAction> factory) { Actions.Add(id, factory); Refresh(); }
    public void RegisterAction(string id, NativeActionDefinition definition, Func<IReadOnlyDictionary<string, GmValue>, ICharacterAction> factory)
    {
        NativeActionValues.ValidateDefinition(definition);
        ActionFactories.Add(id, factory);
        Actions.Add(id, () => factory(NativeActionValues.Resolve(definition, null)));
        ActionSettings[id] = definition;
        Refresh();
    }
    public ICharacterAction CreateAction(string id, IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (!Actions.TryGetValue(id, out var factory)) throw new InvalidDataException("动作未加载：" + id);
        if(ActionFactories.TryGetValue(id,out var configuredFactory))
        {
            var settings=ActionSettings.GetValueOrDefault(id)??throw new InvalidDataException("动作缺少参数定义："+id);
            return configuredFactory(NativeActionValues.Resolve(settings,parameters));
        }
        var action = factory();
        if (parameters is null || parameters.Count == 0) return action;
        if (action is not IActionConfigurable configurable) throw new InvalidDataException("动作不接受参数：" + id);
        var definition = ActionSettings.TryGetValue(id, out var registered) ? registered
            : action is IActionSchema schema ? schema.Definition : throw new InvalidDataException("动作缺少参数定义：" + id);
        configurable.Configure(NativeActionValues.Resolve(definition, parameters));
        return action;
    }
    public void RegisterEntity(string id, Func<IEntityBehavior> factory) { Entities.Add(id, factory); Refresh(); }
    public void RegisterCharacter(CharacterDefinition character) { Characters[character.Id] = character; Refresh(); }
}
