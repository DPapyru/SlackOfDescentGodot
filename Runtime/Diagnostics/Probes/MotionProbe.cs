using Godot;

namespace SlackOfDescent;

public partial class MotionProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            string project = ProjectSettings.GlobalizePath("res://");
            string output = System.Environment.GetEnvironmentVariable("SLACK_PLAY_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "slack-motion-probe");
            Directory.CreateDirectory(output);
            string user = System.Environment.GetEnvironmentVariable("SLACK_USER_ROOT") ?? Path.Combine(output, "user");
            var world = new GmWorld(Path.Combine(project, "GameData"), System.Environment.GetEnvironmentVariable("SLACK_SOURCE") ?? Path.Combine(project, "GameData/assets"), user);
            world.Random.Seed = 0;
            var store = new NativeContentStore(user); var registry = new ContentRegistry(); store.DiscoverCharacters(registry);
            var repository = new NativeCharacterRepository(store);
            using var preview = new NativeCharacterPreview(this, world, registry, repository);
            await preview.InitializeAsync();
            var definition = registry.Characters.Values.First();
            await preview.ApplyAsync(definition);
            var actor = preview.Player!;
            foreach (string name in new[] { "STAND_0", "STAND_1", "WALK", "RUN", "JUMPUP", "JUMPDOWN", "DASH", "RELOAD", "MotionNum", "KeyFrmNum" })
                GD.Print("PROBE constant ", name, "=", actor.Scope.ReadOr(name).Number);
            foreach (string motion in new[] { "STAND_0", "WALK", "RUN", "JUMPUP", "DASH" })
            {
                var value = actor.Scope.ReadOr(motion);
                actor.Scope.Write("MotionNum", value); actor.Scope.Write("KeyFrmNum", 0);
                for (int index = 0; index < 10; index++) await preview.AdvanceAsync(new());
                preview.Rendering.Decorations(new List<GmGraphics.DepthDraw>());
                var audit = preview.Rendering.Audit.ToArray();
                GD.Print("PROBE motion ", motion, " value=", value.Number, " parts=", audit.Length, " motionNumNow=", actor.Scope.ReadOr("MotionNum").Number);
                foreach (var entry in audit.Take(3))
                    GD.Print("  PROBE part ", entry.Target, " pos=", entry.Position, " scale=", entry.Scale, " angle=", entry.Angle);
            }
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("MOTION_PROBE_FAILED " + error); GetTree().Quit(1); }
    }
}
