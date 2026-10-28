namespace SlackOfDescent;

public sealed partial class GmNativeBridge
{
    internal bool HasFullController(int id)=>actions.Concat(runtimeActions).Any(p=>p.Key.Item1==id&&p.Value.Mode==CharacterActionMode.Controller);
    private void ValidateGraphBinding(GmInstance actor,CharacterDefinition definition)
    {
        if(definition.MotionGraph is not {} id)return;
        if(!registry.MotionGraphs.TryGetValue(id,out var graph))throw new InvalidDataException("Character motion graph missing: "+id);
        if(definition.Actions.Any(p=>registry.Actions.TryGetValue(p.Value,out var factory)&&factory().Mode==CharacterActionMode.Controller))throw new InvalidDataException("Character has conflicting full controllers.");
        Services.Motions.Validate(new(world,actor),graph,definition.BasePreset);
    }
    private static string ConfigSignature(CharacterDefinition definition,CharacterActionSlot slot)=>definition.ActionConfigs.TryGetValue(slot,out var config)?System.Text.Json.JsonSerializer.Serialize(config,NativeContentStore.Json):"";
    private ICharacterAction ConfiguredAction(CharacterDefinition definition,CharacterActionSlot slot,string id,Func<ICharacterAction> factory)
    {
        if(!definition.ActionConfigs.TryGetValue(slot,out var config))return factory();
        var schema=registry.ActionSchema(id)??throw new InvalidDataException("Action schema missing: "+id);config.Validate(schema);
        if(config.Target!=NativeActionTargets.Player)throw new InvalidDataException("Action configuration targets a different entity type.");
        return registry.CreateAction(id,config.Parameters);
    }
    internal void SuspendMovement(int id)
    {
        foreach(var key in actions.Keys.Where(k=>k.Item1==id&&k.Item2 is CharacterActionSlot.Jump or CharacterActionSlot.Dash).ToArray())
        {
            if(Find(id) is {} actor)Detach(actions[key],actor);
            actions.Remove(key);
        }
        foreach(var key in runtimeActions.Keys.Where(k=>k.Item1==id&&k.Item2 is CharacterActionSlot.Jump or CharacterActionSlot.Dash).ToArray())RemoveRuntime(id,key.Item2);
    }
    internal void ResumeMovement(int id)
    {
        if(!characters.TryGetValue(id,out var definition))return;
        foreach(var pair in definition.Actions.Where(p=>p.Key is CharacterActionSlot.Jump or CharacterActionSlot.Dash))
            if(!actions.ContainsKey((id,pair.Key))&&registry.Actions.TryGetValue(pair.Value,out var factory))
            {actions[(id,pair.Key)]=Create("action",pair.Value,id,()=>ConfiguredAction(definition,pair.Key,pair.Value,factory));actionFactories[(id,pair.Key)]=factory;actionConfigurations[(id,pair.Key)]=ConfigSignature(definition,pair.Key);}
    }
}
