using Godot;

namespace SlackOfDescent;

public partial class ActionEditorInputProbe : Node
{
    public override async void _Ready()
    {
        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("INPUT_PROBE_SKIP reason=headless");
            GetTree().Quit();
            return;
        }
        string temp = Path.Combine(Path.GetTempPath(), "ccgame-input-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            string root = ProjectSettings.GlobalizePath("res://GameData");
            var world = new GmWorld(root, Path.Combine(root, "assets"), Path.Combine(temp, "live"));
            var store = new NativeContentStore(Path.Combine(temp, "content"));
            var registry = new ContentRegistry();
            NativeLocalization.Initialize(root, world.UserRoot);
            var editor = new NativeActionEditor(world, registry, store, []);
            AddChild(editor);
            await editor.InputRouteAuditAsync();
            GD.Print("INPUT_PROBE_OK");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("INPUT_PROBE_FAILED " + error); GetTree().Quit(1); }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }
}
