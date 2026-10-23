using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using SlackOfDescent;

namespace LifecycleExample;

public sealed class LifecycleMod : IMod
{
    public string Id => "example.lifecycle";
    private string directory = "";
    public void Load(ModContext context)
    {
        directory = context.Directory;
        var tree = (SceneTree)Engine.GetMainLoop();
        var node = context.TrackNode(new Node { Name = "NativeLifecycleNode" });
        tree.Root.AddChild(node); node.AddToGroup("native_lifecycle");
        context.ConnectSignal(tree, SceneTree.SignalName.ProcessFrame, Callable.From(() => node.SetMeta("frames", node.GetMeta("frames", 0).AsInt32() + 1)));
        context.StartTimer(node, .01, () => node.SetMeta("timer_ticks", node.GetMeta("timer_ticks", 0).AsInt32() + 1));
        context.RunTask(async cancellation =>
        {
            File.WriteAllText(Path.Combine(context.Directory, "task-started.txt"), "started");
            try { await Task.Delay(-1, cancellation); }
            finally { File.WriteAllText(Path.Combine(context.Directory, "task-stopped.txt"), "stopped"); }
        });
        context.Content.RegisterEntity("example.lifecycle-counter", () => new Counter());
    }
    public void Unload() => File.WriteAllText(Path.Combine(directory, "unloaded.txt"), "unloaded");
}

public sealed class Counter : IEntityBehavior
{
    public string Id => "example.lifecycle-counter";
    public void Update(ActorContext actor, FrameContext frame) => actor.Write("nativeLifecycleTicks", actor.Read("nativeLifecycleTicks") + 1);
}
