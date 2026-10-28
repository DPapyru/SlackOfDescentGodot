namespace SlackOfDescent;

public sealed partial class GmWorld
{
    internal NativeEventService? Events;
    internal NativeCallInterceptor? CallInterceptor;
    internal NativeCallObserver? CallObserver;
    internal Action<GmInstance>? CreatedObserver;
    internal Action<GmInstance>? DestroyedObserver;
    internal Action? StepBeginObserver;
    internal Action? StepEndObserver;
    internal Action<GmInstance,GmInstance,int>? CollisionObserver;
    internal Action<GmInstance,int>? AlarmObserver;
    internal Action<int,NativeKeyPhase>? KeyObserver;
    internal void NotifyDestroyed(GmInstance instance)
    {
        if(instance.DestroyedNotified)return;instance.DestroyedNotified=true;DestroyedObserver?.Invoke(instance);
    }
    internal void NotifyCollision(GmInstance self,GmInstance other,int target)=>CollisionObserver?.Invoke(self,other,target);
    internal void NotifyAlarm(GmInstance instance,int index)=>AlarmObserver?.Invoke(instance,index);
}
