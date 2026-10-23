using Godot;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeMapAudit : Node2D
{
    private GmWorld world = null!;
    private GmGraphics graphics = null!;
    private GmAudio audio = null!;
    private GmCanvas canvas = null!;
    private readonly GmInput controls = new();
    private readonly ContentRegistry registry = new();
    private bool running;
    private double elapsed;
    private long renderFrame;
    private readonly List<double> frameTimes = new();
    private readonly List<double> stepTimes = new();
    private double loadMilliseconds;
    private int warmup = 180, frames = 600;
    private string output = "", mode = "";
    private long started;
    private int assertions;
    private long completedRenders, measuredRenders, measuredGmSteps, previousRender;
    private long measurementStarted;
    private void RenderCompleted()
    {
        completedRenders++;
        if (!running || renderFrame <= warmup) { previousRender = Stopwatch.GetTimestamp(); return; }
        long now = Stopwatch.GetTimestamp(); if (previousRender > 0) frameTimes.Add(Stopwatch.GetElapsedTime(previousRender, now).TotalMilliseconds); previousRender = now; measuredRenders++;
    }
    public override void _ExitTree() { RenderingServer.FramePostDraw -= RenderCompleted; }

    public override async void _Ready()
    {
        try
        {
            string project = ProjectSettings.GlobalizePath("res://");
            output = System.Environment.GetEnvironmentVariable("SLACK_MAP_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "ccgame-map-audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
            var args = OS.GetCmdlineUserArgs(); mode = args.FirstOrDefault() ?? "ui";
            world = new(Path.Combine(project, "GameData"), System.Environment.GetEnvironmentVariable("SLACK_SOURCE") ?? Path.Combine(project, "GameData/assets"), Path.Combine(output, "user"));
            world.Random.Seed = 0; audio = new(this, world); graphics = new(this, world); canvas = new GmCanvas(); AddChild(canvas); canvas.Paint = graphics.Render;
            world.Platform = Platform; world.Wait = (name, values, context) => new ValueTask<GmValue>(name == "get_string" ? values[1] : name == "show_message_ext" ? 1 : 0);
            world.InputEventsAsync = () => controls.DispatchAsync(world); world.Redraw = () => { graphics.Build(); canvas.QueueRedraw(); };
            RenderingServer.FramePostDraw += RenderCompleted;
            if (mode == "ui")
            {
                var editor = new NativeMapEditor(world, registry, new(world.UserRoot)); AddChild(editor); editor.PopupCentered();
                editor.PlayRequested += async document => { await InitializeGame(); await world.LoadRoomAsync(world.Constants["TestRoom_Loading"].Int); await world.LoadRoomAsync(editor.Service.InstallForPlay(document)); editor.Service.ApplyProperties(document); for (int i = 0; i < 30; i++) await Step(); editor.ResumeAfterPlaytest(); };
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await editor.RunUiAudit(output); Finish(new { kind = "ui", passed = true }); return;
            }
            await InitializeGame();
            var service = new NativeMapService(world, registry);
            var original = service.ImportOriginal(service.TestRoomId).Document;
            string path = Path.Combine(output, "中文 空格", "完整测试.map.json"); service.Save(original, path);
            if (mode == "collision") { await AuditPlayerCollision(service, original); return; }
            if (mode is "loads" or "cold")
            {
                var times = new List<double>(); var phases = new List<object>();
                int count = args.Length > 1 ? int.Parse(args[1]) : 5;
                if (mode == "loads") await world.LoadRoomAsync(world.Constants["TestRoom_Loading"].Int);
                for (int i = 0; i < count; i++)
                {
                    long begin = Stopwatch.GetTimestamp();
                    if (mode == "cold") await world.LoadRoomAsync(world.Constants["TestRoom_Loading"].Int);
                    double dependenciesMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    long phase = Stopwatch.GetTimestamp(); var result = service.Load(path); double dataMs = Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                    phase = Stopwatch.GetTimestamp(); int id = service.InstallForPlay(result.Document); double installMs = Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                    Dictionary<string, (double Total, int Count)>? scriptProfile = null;
                    if (world.Performance.ScriptsEnabled) { scriptProfile = (Dictionary<string, (double, int)>)typeof(GmPerformance).GetField("scripts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(world.Performance)!; scriptProfile.Clear(); }
                    phase = Stopwatch.GetTimestamp(); await world.LoadRoomAsync(id); service.ApplyProperties(result.Document); double entitiesMs = Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                    phase = Stopwatch.GetTimestamp(); graphics.Build(); double graphicsMs = Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                    phase = Stopwatch.GetTimestamp();
                    var player = world.Instances.First(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objPlayerDoll"].Int));
                    world.Call("place_meeting", [player.Scope.Read("x"), player.Scope.Read("y").Number + 1, world.Constants["objBlock"]], new(player));
                    times.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                    Expect(world.Tiles.Count == 1647, "Complete tiles before ready");
                    Expect(world.Instances.Any(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objBlock"].Int)), "Colliders ready");
                    phases.Add(new { dependenciesMs, dataMs, installMs, entitiesMs, graphicsMs, collisionMs = Stopwatch.GetElapsedTime(phase).TotalMilliseconds, scripts = scriptProfile?.OrderByDescending(p => p.Value.Total).Take(20).ToDictionary(p => p.Key, p => new { p.Value.Total, p.Value.Count }) });
                    File.WriteAllText(Path.Combine(output, "loads-progress.json"), JsonSerializer.Serialize(new { times, phases, ready = true, belowThreeSeconds = times.All(t => t < 3000) }, NativeContentStore.Json));
                    GD.Print("MAP_COMPLETE_LOAD index=", i, " ms=", times[^1].ToString("F2"), " instances=", world.Instances.Count, " tiles=", world.Tiles.Count);
                }
                Finish(new { kind = "complete-load", cache = mode == "cold" ? "fresh process; original map dependency loader included; OS page cache not purged" : "same process with dependencies prepared", times, phases, assertions, belowThreeSeconds = times.All(t => t < 3000) }); return;
            }
            if (mode is not ("baseline" or "custom")) throw new InvalidOperationException("Mode must be ui, cold, loads, collision, baseline or custom");
            await world.LoadRoomAsync(world.Constants["TestRoom_Loading"].Int);
            int room = mode == "baseline" ? service.TestRoomId : service.InstallForPlay(service.Load(path).Document);
            long loading = Stopwatch.GetTimestamp(); await world.LoadRoomAsync(room); loadMilliseconds = Stopwatch.GetElapsedTime(loading).TotalMilliseconds;
            if (mode == "custom") service.ApplyProperties(original); graphics.Build();
            Expect(world.Tiles.Count == 1647, "Performance scenario contains all tiles");
            GetWindow().ContentScaleSize = new(640,480); Engine.MaxFps = 60;
            int argFrames = args.Length > 1 ? int.Parse(args[1]) : 600; frames = argFrames; started = Stopwatch.GetTimestamp(); running = true;
        }
        catch (Exception exception) { GD.PushError(exception.ToString()); Finish(new { kind = mode, passed = false, error = exception.ToString() }, 1); }
    }
    private async Task AuditPlayerCollision(NativeMapService service, NativeMapDocument original)
    {
        await world.LoadRoomAsync(world.Constants["TestRoom_Loading"].Int);
        var arena = original.Clone(); arena.Name = "编辑器碰撞操作验证"; arena.Room["width"] = 640; arena.Room["height"] = 480;
        var keep = new HashSet<int>(new[] { "objSetRespawn", "objDisplay", "objController", "objMultiScroll" }.Select(n => world.Constants[n].Int));
        foreach (var item in arena.Instances.OfType<JsonObject>().Where(i => !keep.Contains(Int(i, "object"))).ToArray()) arena.Instances.Remove(item);
        arena.Tiles.Clear(); int block = world.Constants["objBlock"].Int;
        for (int x = 0; x < 640; x += 32) service.AddEntity(arena, block, x, 320);
        for (int y = 0; y < 320; y += 32) { service.AddEntity(arena, block, 0, y); service.AddEntity(arena, block, 608, y); }
        for (int x = 288; x < 416; x += 32) service.AddEntity(arena, block, x, 224);
        string path = Path.Combine(output, "edited-collision.map.json"); service.Save(arena, path); arena = service.Load(path).Document;
        await world.LoadRoomAsync(service.InstallForPlay(arena)); service.ApplyProperties(arena);
        var player = world.Instances.First(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objPlayerDoll"].Int));
        async Task Place(double x, double y)
        {
            controls.Clear(world); world.Write(player, "x", x); world.Write(player, "y", y); world.Write(player, "hspeed", 0); world.Write(player, "vspeed", 0);
            for (int i = 0; i < 90; i++) await Step();
        }
        await Place(128, 128); var floorBounds = world.Bounds(player);
        Expect(floorBounds.Bottom >= 315 && floorBounds.Bottom < 321, "Actual character lands on edited floor");
        int offset = world.Globals.Read("controllMode").Int == world.Constants["CTRL_KEY"].Int ? 12 : 0;
        int jump = world.Globals.Read("key", [offset + 5]).Int;
        double startY = player.Scope.Read("y").Number, topY = startY;
        controls.Key(world, jump, true); await Step(); controls.Key(world, jump, false);
        for (int i = 0; i < 90; i++) { await Step(); topY = Math.Min(topY, player.Scope.Read("y").Number); }
        Expect(topY < startY - 10, "Actual jump input moves character above edited floor");
        await Place(336, 96); var platformBounds = world.Bounds(player);
        Expect(platformBounds.Bottom >= 219 && platformBounds.Bottom < 225, "Actual character lands on edited raised platform");
        await Place(480, 128); int right = world.Globals.Read("key", [offset + 1]).Int;
        double initialX = player.Scope.Read("x").Number; controls.Key(world, right, true); for (int i = 0; i < 120; i++) await Step(); controls.Key(world, right, false);
        var rightBounds = world.Bounds(player); Expect(player.Scope.Read("x").Number > initialX + 10 && rightBounds.Right >= 600 && rightBounds.Right < 609, $"Actual right input meets edited wall at room boundary: key={right}, x={initialX}->{player.Scope.Read("x").Number}, bounds={rightBounds}");
        await Place(128, 128); int left = world.Globals.Read("key", [offset]).Int;
        double nearestLeft = double.PositiveInfinity;
        controls.Key(world, left, true); for (int i = 0; i < 120; i++) { await Step(); nearestLeft = Math.Min(nearestLeft, world.Bounds(player).Left); } controls.Key(world, left, false);
        var leftBounds = world.Bounds(player);
        double stoppedX = player.Scope.Read("x").Number;
        Expect(nearestLeft >= 32 && stoppedX < 112, $"Actual left input approaches edited wall without crossing: x={stoppedX}, bounds={leftBounds}");
        foreach (var item in arena.Instances.OfType<JsonObject>().Where(i => Int(i, "object") == block && Number(i, "x") == 0 && Number(i, "y") < 320)) item["x"] = 32;
        string shiftedPath = Path.Combine(output, "edited-collision-shifted.map.json"); service.Save(arena, shiftedPath); arena = service.Load(shiftedPath).Document;
        await world.LoadRoomAsync(service.InstallForPlay(arena)); service.ApplyProperties(arena);
        player = world.Instances.First(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objPlayerDoll"].Int));
        await Place(128, 128); controls.Key(world, left, true); for (int i = 0; i < 120; i++) await Step(); controls.Key(world, left, false);
        double shiftedStoppedX = player.Scope.Read("x").Number;
        Expect(Math.Abs(shiftedStoppedX - stoppedX - 32) <= 8, $"Moving saved wall one block moves actual character stop: {stoppedX}->{shiftedStoppedX}");
        Finish(new { kind = "actual-character-collision", assertions, passed = true, playerObject = world.Objects[player.Object].GetProperty("name").GetString(), floorBounds, platformBounds, rightBounds, leftBounds, nearestLeft, stoppedX, shiftedStoppedX, jumpRise = startY - topY, path, shiftedPath });
    }
    private void Expect(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
    private async Task InitializeGame()
    {
        if (world.Instances.Count > 0 && world.Globals.Values.Count > 100) return;
        await world.LoadRoomAsync(world.Manifest.GetProperty("roomOrder")[0].GetInt32()); for (int i = 0; i < 180; i++) await Step();
        var context = new GmContext(world.Instances.First(i => world.IsObject(i.Object, world.Constants["objTitle"].Int))); world.Globals.Write("StartArea", 0); world.Call("DataItemReset", [], context); world.Call("DataItemLoad", [], context);

    }
    private async Task Step() { controls.Poll(world); await world.StepAsync(); controls.EndStep(); }
    private GmValue? Platform(string name, GmValue[] values, GmContext context)
    {
        var input = controls.Call(name, values, world); if (input is not null) return input;
        var sound = audio.Call(name, values); if (sound is not null) return sound;
        var graphic = graphics.Call(name, values, context); if (graphic is not null) return graphic;
        return name switch { "texture_set_interpolation" => 0, "window_get_width" => 640, "window_get_height" => 480, "window_handle" => 1, "message_background" or "message_button" or "message_mouse_color" or "message_text_font" or "message_button_font" => 0, _ => null };
    }
    public override async void _Process(double delta)
    {
        if (!running) return;
        try
        {
            renderFrame++; if (renderFrame == warmup + 1) measurementStarted = Stopwatch.GetTimestamp();
            elapsed += delta;
            double interval = 1.0 / Math.Max(1, world.Engine.Read("room_speed").Number);
            while (elapsed >= interval)
            {
                elapsed -= interval; long begin = Stopwatch.GetTimestamp(); await Step();
                if (renderFrame > warmup) { measuredGmSteps++; stepTimes.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds); }
            }
            canvas.QueueRedraw();
            if (renderFrame < warmup + frames) return;
            running = false; frameTimes.Sort(); stepTimes.Sort();
            Finish(new { kind = "rendered", scenario = mode, frames, warmup, room = world.CurrentRoom, loadMilliseconds, renderFps = measuredRenders / Stopwatch.GetElapsedTime(measurementStarted).TotalSeconds, gmFps = measuredGmSteps / Stopwatch.GetElapsedTime(measurementStarted).TotalSeconds, completedRenders, measuredRenders, measuredGmSteps, scheduler = "original-room-speed-accumulator; native bridge is host-owned", engine = Engine.GetVersionInfo()["string"].AsString(), display = DisplayServer.GetName(), os = OS.GetName(), cpu = OS.GetProcessorName(), gpu = RenderingServer.GetVideoAdapterName(), p95Milliseconds = frameTimes[(int)((frameTimes.Count - 1) * .95)], maxMilliseconds = frameTimes[^1], stepP95Milliseconds = stepTimes[(int)((stepTimes.Count - 1) * .95)], tiles = world.Tiles.Count, instances = world.Instances.Count, seconds = Stopwatch.GetElapsedTime(started).TotalSeconds });
        }
        catch (Exception exception) { running = false; GD.PushError(exception.ToString()); Finish(new { kind = mode, passed = false, error = exception.ToString() }, 1); }
    }
    private void Finish(object result, int code = 0) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, NativeContentStore.Json)); GD.Print("MAP_AUDIT ", JsonSerializer.Serialize(result)); graphics?.Shutdown(); audio?.Shutdown(); GetTree().Quit(code); }
}
