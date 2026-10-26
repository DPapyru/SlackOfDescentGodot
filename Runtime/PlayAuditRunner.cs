using Godot;

namespace SlackOfDescent;

public partial class PlayAuditRunner : Node
{
    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        if (args.Contains("--editors"))
        {
            AddChild(new NativeEditorPlayAudit());
            return;
        }
        var host = new GameHost();
        AddChild(host);
        host.SetProcess(false);
        Run(host, args.Contains("--testroom"));
    }
    private async void Run(GameHost host, bool testRoom)
    {
        try
        {
            if (testRoom) await GameHost.RunPlayTestRoomAuditAsync(host);
            else await GameHost.RunPlayAuditAsync(host);
        }
        catch (Exception error) { GD.PushError("PLAY_AUDIT_FAILED " + error); host.GetTree().Quit(1); }
    }
}
