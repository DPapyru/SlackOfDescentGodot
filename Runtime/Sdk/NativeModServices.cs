using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class NativeModServices : IDisposable
{
    internal readonly GmWorld World;
    internal readonly ContentRegistry Registry;
    public NativeInputService Input { get; }
    public NativeEntityService Entities { get; }
    public NativeCombatService Combat { get; }
    public NativePresentation Presentation { get; }
    public NativeAudioService Audio { get; }
    public NativeMapFlowService Maps { get; }
    public NativeInstanceService Instances { get; }
    public NativeEventService Events { get; }
    public NativeActorService Actors { get; }
    public NativeMotionService Motions { get; }
    internal NativeResourceScope CurrentScope { get; private set; }
    internal IDisposable EnterScope(NativeResourceScope scope)
    {
        var previous = CurrentScope; CurrentScope = scope; return new ScopeLease(this, previous);
    }
    private sealed class ScopeLease(NativeModServices services, NativeResourceScope previous) : IDisposable
    { public void Dispose() => services.CurrentScope = previous; }
    internal void ClearScope(NativeResourceScope scope) { Entities.ClearScope(scope); Presentation.ClearScope(scope); Audio.ClearScope(scope); Instances.ClearScope(scope); Actors.ClearScope(scope); Motions.ClearScope(scope); }
    private void ClearOwner(NativeModOwner owner) { Entities.ClearOwner(owner); Presentation.ClearOwner(owner); Audio.ClearOwner(owner); Instances.ClearOwner(owner); Actors.ClearOwner(owner); }
    internal Action? LeaveRoom;
    internal Action? EnterRoom;
    public FrameContext Frame { get; internal set; }
    public NativeModServices(GmWorld world, ContentRegistry registry)
    {
        World = world; Registry = registry; registry.ModDataRoot = Path.Combine(world.UserRoot, "Content", "ModData"); Input = new(registry, world.UserRoot);
        Entities = new(this); Combat = new(this); Presentation = new(this); Audio = new(this); Maps = new(this); world.NativeServices = this; Instances = new(this); Events = new(this); Actors = new(this); Motions = new(this); registry.OwnerRetired += ClearOwner;
    }
    public NativeGameEvent Dispatch(NativeGameEvent value, NativeEventPhase phase)
    {
        foreach (var pair in Registry.EventHandlers.Where(p => p.Value.Event == value.Name && p.Value.Phase == phase).ToArray())
        {
            using var scope = EnterScope(Registry.ScopeFor("event-handler", pair.Key, value.Actor?.Id ?? 0));
            try { pair.Value.Handle(value); } catch (Exception error) { Registry.ReportLifecycle(CurrentScope, "EVENT", error); }
        }
        return value;
    }
    internal NativeGameEvent? BeforeCode(string key,GmContext context)
    {
        string name="engine."+(key.StartsWith("scripts/",StringComparison.Ordinal)?key[8..].ToLowerInvariant():key);
        if(!Registry.HasEventHandlers(name))return null;
        var value=new NativeGameEvent(name,new(World,context.Self));
        value.Arguments["code"]=key;value.Arguments["other"]=context.Other.Id;
        value.Arguments["eventKind"]=context.EventKind;value.Arguments["eventSub"]=context.EventSub;
        for(int i=0;i<context.Arguments.Length;i++)value.Arguments["argument"+i]=context.Arguments[i];
        return Dispatch(value,NativeEventPhase.Before);
    }
    internal GmValue AfterCode(NativeGameEvent? value,GmValue result)
    {
        if(value is null)return result;
        value.Result=result;return Dispatch(value,NativeEventPhase.After).Result;
    }
    public GmValue Invoke(string name, ActorContext actor, Func<GmValue> original)
    {
        var value = Dispatch(new(name, actor), NativeEventPhase.Before);
        if (!value.Handled) value.Result = original();
        Dispatch(value, NativeEventPhase.After); return value.Result;
    }
    internal void ClearActor(int id) { Presentation.ClearActor(id); Audio.ClearActor(id); Entities.ClearOwned(id); }
    public void Dispose() { Motions.Dispose(); Registry.OwnerRetired -= ClearOwner; Events.Dispose(); Entities.Dispose(); Presentation.ClearAll(); Audio.Dispose(); Instances.Dispose(); if (World.NativeServices == this) World.NativeServices = null; }
}
