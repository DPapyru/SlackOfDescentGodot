namespace SlackOfDescent;

public static class NativeEventNames
{
    public const string InstanceCreated="instance.created",InstanceDestroyed="instance.destroyed",StepBegin="step.begin",StepEnd="step.end",Room="room",Collision="collision",Alarm="alarm",KeyDown="key.down",KeyPressed="key.pressed",KeyReleased="key.released",CallPrefix="call.",EnginePrefix="engine.";
    public static string Call(string function)=>CallPrefix+function;
}

public enum NativeKeyPhase { Down, Pressed, Released }

public delegate bool NativeCallInterceptor(string name,GmContext context,out GmValue value);
public delegate GmValue NativeCallObserver(string name,GmContext context,GmValue result);

public sealed class NativeEventService : IDisposable
{
    private readonly NativeModServices services;
    private readonly ContentRegistry registry;
    private readonly GmWorld world;
    private readonly HashSet<(string Name,long Frame)> callGuards=new();
    private bool disposed;
    public NativeEventService(NativeModServices services)
    {
        this.services=services;registry=services.Registry;world=services.World;
        world.Events=this;registry.Changed+=Refresh;Refresh();
    }
    public bool HasHandlers(string eventName)=>registry.HasEventHandlers(eventName);
    public int HandlerCount(string eventName)=>registry.CountEventHandlers(eventName);
    internal void Refresh()
    {
        if(disposed)return;
        registry.InvalidateEventIndex();registry.RebuildEventIndex();
        world.CallInterceptor=registry.HasEventPrefix(NativeEventNames.CallPrefix,NativeEventPhase.Before)?Intercept:null;
        world.CallObserver=registry.HasEventPrefix(NativeEventNames.CallPrefix,NativeEventPhase.After)?Observe:null;
        world.CreatedObserver=registry.CountEventHandlers(NativeEventNames.InstanceCreated)>0?Created:null;
        world.DestroyedObserver=registry.CountEventHandlers(NativeEventNames.InstanceDestroyed)>0?Destroyed:null;
        world.StepBeginObserver=registry.CountEventHandlers(NativeEventNames.StepBegin)>0?BeginStep:null;
        world.StepEndObserver=registry.CountEventHandlers(NativeEventNames.StepEnd)>0?EndStep:null;
        world.CollisionObserver=registry.CountEventHandlers(NativeEventNames.Collision)>0?Collide:null;
        world.AlarmObserver=registry.CountEventHandlers(NativeEventNames.Alarm)>0?Ring:null;
        world.KeyObserver=registry.CountEventHandlers(NativeEventNames.KeyDown)+registry.CountEventHandlers(NativeEventNames.KeyPressed)+registry.CountEventHandlers(NativeEventNames.KeyReleased)>0?Key:null;
        world.Events=this;
    }
    internal NativeGameEvent Dispatch(NativeGameEvent value,NativeEventPhase phase)
    {
        string[] keys=registry.HandlersFor(value.Name,phase);
        if(Stale(keys,value.Name,phase)){registry.InvalidateEventIndex();keys=registry.HandlersFor(value.Name,phase);}
        foreach(string key in keys)
        {
            if(!registry.EventHandlers.TryGetValue(key,out var handler)||handler.Event!=value.Name||handler.Phase!=phase)continue;
            using var scope=services.EnterScope(registry.ScopeFor("event-handler",key,value.Actor?.Id??0));
            try{handler.Handle(value);}catch(Exception error){registry.ReportLifecycle(services.CurrentScope,"EVENT",error);}
        }
        return value;
    }
    private bool Stale(string[] keys,string name,NativeEventPhase phase)
    {
        foreach(string key in keys)if(!registry.EventHandlers.TryGetValue(key,out var handler)||handler.Event!=name||handler.Phase!=phase)return true;
        return keys.Length==0&&registry.EventHandlers.Count!=registry.IndexedHandlerCount;
    }
    private NativeGameEvent Payload(string name,GmInstance? instance)
    {
        var value=new NativeGameEvent(name,instance is null?null:new(world,instance));
        if(instance is null)return value;
        value.Arguments["id"]=instance.Id;value.Arguments["object"]=instance.Object;
        value.Arguments["x"]=instance.Scope.ReadOr("x").Number;value.Arguments["y"]=instance.Scope.ReadOr("y").Number;
        return value;
    }
    private void Notify(string name,GmInstance? instance)
    {
        var value=Payload(name,instance);
        Dispatch(value,NativeEventPhase.Before);Dispatch(value,NativeEventPhase.After);
    }
    private bool Intercept(string name,GmContext context,out GmValue value)
    {
        value=default;
        if(!registry.HasCallFunction(name))return false;
        if(registry.HandlersFor(NativeEventNames.Call(name),NativeEventPhase.Before).Length==0)return false;
        var guard=(name,world.Frame);
        if(!callGuards.Add(guard))return false;
        try
        {
            var request=Payload(NativeEventNames.Call(name),context.Self);
            request.Arguments["function"]=name;request.Arguments["other"]=context.Other.Id;
            request.Arguments["eventKind"]=context.EventKind;request.Arguments["eventSub"]=context.EventSub;
            for(int i=0;i<context.Arguments.Length;i++)request.Arguments["argument"+i]=context.Arguments[i];
            Dispatch(request,NativeEventPhase.Before);
            if(!request.Handled)return false;
            value=request.Result;return true;
        }
        finally{callGuards.Remove(guard);}
    }
    private GmValue Observe(string name,GmContext context,GmValue result)
    {
        if(!registry.HasCallFunction(name))return result;
        if(registry.HandlersFor(NativeEventNames.Call(name),NativeEventPhase.After).Length==0)return result;
        var guard=(name,world.Frame);
        if(!callGuards.Add(guard))return result;
        try
        {
            var request=Payload(NativeEventNames.Call(name),context.Self);
            request.Arguments["function"]=name;request.Arguments["other"]=context.Other.Id;
            request.Arguments["eventKind"]=context.EventKind;request.Arguments["eventSub"]=context.EventSub;
            for(int i=0;i<context.Arguments.Length;i++)request.Arguments["argument"+i]=context.Arguments[i];
            request.Result=result;
            return Dispatch(request,NativeEventPhase.After).Result;
        }
        finally{callGuards.Remove(guard);}
    }
    private void Created(GmInstance instance)=>Notify(NativeEventNames.InstanceCreated,instance);
    private void Destroyed(GmInstance instance)=>Notify(NativeEventNames.InstanceDestroyed,instance);
    private void BeginStep()=>Notify(NativeEventNames.StepBegin,null);
    private void EndStep()=>Notify(NativeEventNames.StepEnd,null);
    private void Collide(GmInstance self,GmInstance other,int target)
    {
        var value=Payload(NativeEventNames.Collision,self);
        value.Arguments["other"]=other.Id;value.Arguments["target"]=target;
        Dispatch(value,NativeEventPhase.Before);Dispatch(value,NativeEventPhase.After);
    }
    private void Ring(GmInstance instance,int index)
    {
        var value=Payload(NativeEventNames.Alarm,instance);
        value.Arguments["alarm"]=index;
        Dispatch(value,NativeEventPhase.Before);Dispatch(value,NativeEventPhase.After);
    }
    private void Key(int code,NativeKeyPhase phase)
    {
        var name=phase switch{NativeKeyPhase.Pressed=>NativeEventNames.KeyPressed,NativeKeyPhase.Released=>NativeEventNames.KeyReleased,_=>NativeEventNames.KeyDown};
        var value=new NativeGameEvent(name);
        value.Arguments["key"]=code;
        Dispatch(value,NativeEventPhase.Before);Dispatch(value,NativeEventPhase.After);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        registry.Changed-=Refresh;
        if(world.Events!=this)return;
        world.CallInterceptor=null;world.CallObserver=null;world.CreatedObserver=null;world.DestroyedObserver=null;
        world.StepBeginObserver=null;world.StepEndObserver=null;world.CollisionObserver=null;world.AlarmObserver=null;world.KeyObserver=null;
        world.Events=null;
    }
}
