using Godot;

namespace SlackOfDescent;

public partial class NativeCharacterAuditRunner : Node
{
    public override async void _Ready()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs(); int option = Array.IndexOf(args, "--output-dir");
            string output = option >= 0 && option + 1 < args.Length ? args[option + 1] : Path.Combine(Path.GetTempPath(), "slack-character-ui-audit");
            string user = System.Environment.GetEnvironmentVariable("SLACK_USER_ROOT") ?? Path.Combine(output, "user");
            var world = new GmWorld(ProjectSettings.GlobalizePath("res://GameData"), System.Environment.GetEnvironmentVariable("SLACK_SOURCE") ?? ProjectSettings.GlobalizePath("res://GameData/assets"), user);
            var store = new NativeContentStore(user); var registry = new ContentRegistry(); store.DiscoverCharacters(registry);
            var editor = new NativeCharacterEditor(world, registry, store); AddChild(editor); editor.PopupCentered();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GD.Print(editor.RunUiAudit(output));
            GD.Print("CHARACTER_CANVAS_AUDIT_OK checks=", await AuditCanvasContractsAsync(world, registry, store));
            GD.Print("CHARACTER_RUNTIME_AUDIT_OK checks=", await AuditRuntimeAsync(world, registry, store, output));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (args.Contains("--capture-ui") && DisplayServer.GetName() != "headless")
            {
                await editor.CaptureBaseShotsAsync(output);
                RenderingServer.ForceDraw(true);
                using var image = editor.GetViewport().GetTexture().GetImage(); image.SavePng(Path.Combine(output, "ui.png"));
            }
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr(e); GetTree().Quit(1); }
    }
}
