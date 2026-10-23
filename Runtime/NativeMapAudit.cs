using Godot;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

/// <summary>Standalone reproducible data, real editor event routing, loading and rendered gameplay audits.</summary>
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
            if (mode is "loads" or "cold")
            {
                var times = new List<double>();
                int count = args.Length > 1 ? int.Parse(args[1]) : 5;
                if (mode == "loads") await world.LoadRoomAsync(world.Constants["TestRoom_Loading"].Int);
                for (int i = 0; i < count; i++)
                {
                    long begin = Stopwatch.GetTimestamp(); if (mode == "cold") await world.LoadRoomAsync(world.Constants["TestRoom_Loading"].Int); var result = service.Load(path); int id = service.InstallForPlay(result.Document); await world.LoadRoomAsync(id); service.ApplyProperties(result.Document); graphics.Build();
                    var player = world.Instances.First(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objPlayerDoll"].Int));
                    world.Call("place_meeting", [player.Scope.Read("x"), player.Scope.Read("y").Number + 1, world.Constants["objBlock"]], new(player));
                    times.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                    Expect(world.Tiles.Count == 1647, "Complete tiles before ready");
                    Expect(world.Instances.Any(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objBlock"].Int)), "Colliders ready");
                    Expect(times[^1] < 3000, "Map load within 3s including creation and collision");
                    GD.Print("MAP_COMPLETE_LOAD index=", i, " ms=", times[^1].ToString("F2"), " instances=", world.Instances.Count, " tiles=", world.Tiles.Count);
                }
                Finish(new { kind = "complete-load", cache = mode == "cold" ? "fresh process; original map dependency loader included; OS page cache not purged" : "same process with dependencies prepared", times, assertions }); return;
            }
            if (mode is not ("baseline" or "custom")) throw new InvalidOperationException("Mode must be ui, loads, baseline or custom");
            await world.LoadRoomAsync(world.Constants["TestRoom_Loading"].Int);
            int room = mode == "baseline" ? service.TestRoomId : service.InstallForPlay(service.Load(path).Document);
            long loading = Stopwatch.GetTimestamp(); await world.LoadRoomAsync(room); loadMilliseconds = Stopwatch.GetElapsedTime(loading).TotalMilliseconds;
            if (mode == "custom") service.ApplyProperties(original);
            Expect(world.Tiles.Count == 1647, "Performance scenario contains all tiles");
            GetWindow().ContentScaleSize = new(640,480); Engine.MaxFps = 60;
            int argFrames = args.Length > 1 ? int.Parse(args[1]) : 600; frames = argFrames; started = Stopwatch.GetTimestamp(); running = true;
        }
        catch (Exception exception) { GD.PushError(exception.ToString()); Finish(new { kind = mode, passed = false, error = exception.ToString() }, 1); }
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
            graphics.Build(); canvas.QueueRedraw();
            if (renderFrame < warmup + frames) return;
            running = false; frameTimes.Sort(); stepTimes.Sort();
            Finish(new { kind = "rendered", scenario = mode, frames, warmup, room = world.CurrentRoom, loadMilliseconds, renderFps = measuredRenders / Stopwatch.GetElapsedTime(measurementStarted).TotalSeconds, gmFps = measuredGmSteps / Stopwatch.GetElapsedTime(measurementStarted).TotalSeconds, completedRenders, measuredRenders, measuredGmSteps, scheduler = "original-room-speed-accumulator; native bridge is host-owned", engine = Engine.GetVersionInfo()["string"].AsString(), display = DisplayServer.GetName(), os = OS.GetName(), cpu = OS.GetProcessorName(), gpu = RenderingServer.GetVideoAdapterName(), p95Milliseconds = frameTimes[(int)((frameTimes.Count - 1) * .95)], maxMilliseconds = frameTimes[^1], stepP95Milliseconds = stepTimes[(int)((stepTimes.Count - 1) * .95)], tiles = world.Tiles.Count, instances = world.Instances.Count, seconds = Stopwatch.GetElapsedTime(started).TotalSeconds });
        }
        catch (Exception exception) { running = false; GD.PushError(exception.ToString()); Finish(new { kind = mode, passed = false, error = exception.ToString() }, 1); }
    }
    private void Finish(object result, int code = 0) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, NativeContentStore.Json)); GD.Print("MAP_AUDIT ", JsonSerializer.Serialize(result)); graphics?.Shutdown(); audio?.Shutdown(); GetTree().Quit(code); }
}
