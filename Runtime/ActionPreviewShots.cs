using Godot;

namespace SlackOfDescent;

public partial class ActionPreviewShots : Node
{
    private GmWorld world = null!;
    private NativeContentStore store = null!;
    private ContentRegistry registry = null!;
    private string output = "";
    private Window? _editor;
    public override async void _Ready()
    {
        try
        {
            string project = ProjectSettings.GlobalizePath("res://");
            output = System.Environment.GetEnvironmentVariable("SLACK_PLAY_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "slack-action-shots");
            Directory.CreateDirectory(output);
            string user = System.Environment.GetEnvironmentVariable("SLACK_USER_ROOT") ?? Path.Combine(output, "user");
            world = new GmWorld(Path.Combine(project, "GameData"), System.Environment.GetEnvironmentVariable("SLACK_SOURCE") ?? Path.Combine(project, "GameData/assets"), user);
            world.Random.Seed = 0;
            store = new NativeContentStore(user); registry = new ContentRegistry(); store.DiscoverCharacters(registry);
            foreach (var key in registry.Characters.Keys) GD.Print("SHOT character=", key, " preset=", registry.Characters[key].BasePreset);
            var graphics = new GmGraphics(this, world); var canvas = new GmCanvas(); AddChild(canvas); canvas.Paint = graphics.Render;
            world.Platform = Platform;
            world.Redraw = () => { graphics.Build(); canvas.QueueRedraw(); };
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var editor = new NativeActionEditor(world, registry, store) { Visible = true };
            AddChild(editor);
            _editor = editor;
            editor.PopupCentered();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await Wait(editor, e => e.SelectAction("sample.gravity"));
            await Shot("action-01-opened");
            var player = await editor.Bind(NativeActionTargets.Player, editor.DescribeDefinition, editor.Values);
            GD.Print("SHOT player obj=", player.Object, " x=", player.Scope.ReadOr("x").Number, " y=", player.Scope.ReadOr("y").Number);
            await Shot("action-02-player");
            if (registry.Characters.Count > 0)
            {
                var first = registry.Characters.Values.First();
                editor.Preview.Show(player.Id, first);
                GD.Print("SHOT showed character preset=", first.BasePreset, " on instance=", player.Id);
            }
            await Shot("action-02b-showed");
            if (registry.Characters.Count > 0)
            {
                var def = registry.Characters.Values.First();
                var w = editor.Preview.World;
                int vx = (int)w.Engine.Read("view_xview", [0]).Number, vy = (int)w.Engine.Read("view_yview", [0]).Number;
                int vw = (int)w.Engine.Read("view_wview", [0]).Number, vh = (int)w.Engine.Read("view_hview", [0]).Number;
                float px = (float)player.Scope.ReadOr("x").Number, py = (float)player.Scope.ReadOr("y").Number;
                var bounds = NativeCharacterLayout.Bounds(def);
                GD.Print("SHOT view x=", vx, " y=", vy, " w=", vw, " h=", vh);
                GD.Print("SHOT player x=", px, " y=", py);
                GD.Print("SHOT bounds x=", bounds.X, " y=", bounds.Y, " w=", bounds.Width, " h=", bounds.Height);
                GD.Print("SHOT ratio width=", (100.0 * bounds.Width / vw).ToString("F1"), "% height=", (100.0 * bounds.Height / vh).ToString("F1"), "%");
                GD.Print("SHOT centre offset x=", (px + bounds.X + bounds.Width / 2 - (vx + vw / 2)).ToString("F1"), " y=", (py + bounds.Y + bounds.Height / 2 - (vy + vh / 2)).ToString("F1"));
            }
            editor.TargetChoiceControl.Select(1); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            editor.ReframeTarget();
            var enemy = editor.Preview.World.Instances.FirstOrDefault(i => !i.Destroyed && i.Object == editor.Preview.World.Constants["objEnemyDoll"].Int);
            GD.Print("SHOT enemy instance=", enemy?.Id, " x=", enemy?.Scope.ReadOr("x").Number, " y=", enemy?.Scope.ReadOr("y").Number, " PartsNumber=", enemy?.Scope.ReadOr("PartsNumber").Number);
            GD.Print("SHOT enemyObjectSlot0=", editor.Preview.World.Globals.ReadOr("EnemyObject", 0).Int);
            GD.Print("SHOT globalEnemyNum=", editor.Preview.World.Globals.ReadOr("EnemyNum").Number);
            await Shot("action-03-enemy");
            GD.Print("SHOTS_DONE count=3");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("SHOTS_FAILED " + error); GetTree().Quit(1); }
    }
    private static GmValue? Platform(string name, GmValue[] args, GmContext context) => name switch
    {
        "show_message" or "show_message_ext" => 1,
        "show_question" => false,
        "get_string" => args.Length > 1 ? args[1] : "",
        _ => (GmValue?)null
    };
    private async Task Wait(Node node, Func<NativeActionEditor, bool> ready)
    {
        for (int n = 0; n < 300; n++) { if (node is NativeActionEditor e && ready(e)) return; await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        throw new InvalidOperationException("editor not ready");
    }
    private async Task Shot(string name)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var target = _editor ?? GetWindow();
        using var image = target.GetTexture().GetImage();
        string path = Path.Combine(output, name + ".png");
        image.SavePng(path);
        GD.Print("SHOT name=", name, " path=", path, " size=", image.GetWidth(), "x", image.GetHeight());
    }
}
