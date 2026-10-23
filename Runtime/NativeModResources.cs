using Godot;

namespace SlackOfDescent;

/// <summary>Resources registered through ModContext are disposed in reverse order on reload and load failure.</summary>
public sealed class NativeModResources : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<IDisposable> resources = new();
    private bool disposed;
    public CancellationToken Cancellation => cancellation.Token;
    public void Track(IDisposable resource)
    {
        if (disposed) { resource.Dispose(); throw new ObjectDisposedException(nameof(NativeModResources)); }
        resources.Add(resource);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { cancellation.Cancel(); } catch (Exception error) { Console.Error.WriteLine("MOD_CANCEL " + error.Message); }
        for (int i = resources.Count - 1; i >= 0; i--)
            try { resources[i].Dispose(); } catch (Exception e) { Console.Error.WriteLine("MOD_RESOURCE " + e.Message); }
        resources.Clear(); cancellation.Dispose();
    }
}

public static class NativeModContextExtensions
{
    public static T TrackNode<T>(this ModContext context, T node) where T : Node
    {
        context.Track(new Cleanup(() => { if (GodotObject.IsInstanceValid(node)) { node.GetParent()?.RemoveChild(node); node.Free(); } }));
        return node;
    }
    public static void ConnectSignal(this ModContext context, GodotObject source, StringName signal, Callable callback)
    {
        if (source.Connect(signal, callback) != Error.Ok) throw new InvalidOperationException("Cannot connect mod signal " + signal);
        context.Track(new Cleanup(() => { if (GodotObject.IsInstanceValid(source) && source.IsConnected(signal, callback)) source.Disconnect(signal, callback); }));
    }
    public static Godot.Timer StartTimer(this ModContext context, Node parent, double seconds, Action callback, bool oneShot = false)
    {
        var timer = context.TrackNode(new Godot.Timer { WaitTime = seconds, OneShot = oneShot });
        parent.AddChild(timer); context.ConnectSignal(timer, Godot.Timer.SignalName.Timeout, Callable.From(callback)); timer.Start();
        return timer;
    }
    /// <summary>Task callbacks must observe Cancellation and must not access Godot objects from a worker thread.</summary>
    public static Task RunTask(this ModContext context, Func<CancellationToken, Task> operation)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        var task = Task.Run(() => operation(cancellation.Token), cancellation.Token);
        context.Track(new Cleanup(() =>
        {
            cancellation.Cancel();
            try { if (!task.Wait(TimeSpan.FromSeconds(2))) Console.Error.WriteLine("MOD_RESOURCE task did not stop within 2 seconds"); }
            catch (AggregateException error) when (error.InnerExceptions.All(e => e is OperationCanceledException)) { }
            finally { cancellation.Dispose(); }
        }));
        return task;
    }
    private sealed class Cleanup(Action action) : IDisposable
    {
        private Action? cleanup = action;
        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }
}
