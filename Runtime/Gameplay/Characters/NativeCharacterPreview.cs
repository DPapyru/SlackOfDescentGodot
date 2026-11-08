using Godot;

namespace SlackOfDescent;

public sealed class NativeCharacterPreview : IDisposable
{
    public GmWorld World { get; }
    public GmInput Input { get; } = new();
    public GmFixedClock Clock { get; } = new();
    public GmNativeBridge Bridge { get; }
    public GmGraphics Graphics { get; }
    private readonly GmAudio audio;
    public NativeCharacterRendering Rendering { get; }
    public ContentRegistry Registry { get; } = new();
    public GmInstance? Player => World.Instances.FirstOrDefault(i => !i.Destroyed && World.IsObject(i.Object, World.Constants["objPlayerDoll"].Int));
    public string TemporaryDirectory { get; }
    private readonly NativeModManager manager;
    private readonly Dictionary<int, CharacterDefinition> visuals = new();
    private bool disposed;
    public NativeCharacterPreview(Node owner, GmWorld source, ContentRegistry registry, NativeCharacterRepository repository, IEnumerable<string>? mods = null)
    {
        TemporaryDirectory = Path.Combine(Path.GetTempPath(), "slack-character-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TemporaryDirectory);
        World = new(source.Root, source.SourceRoot, TemporaryDirectory);
        manager = new(Registry);
        var packages = mods?.ToArray() ?? [];
        if (packages.Length > 0)
        {
            var result = manager.ReloadMany(packages); if (!result.Success) throw new InvalidDataException(result.Diagnostics);
        }
        foreach (var pair in registry.Actions) Registry.Actions.TryAdd(pair.Key, pair.Value);
        foreach (var pair in registry.Entities) Registry.Entities.TryAdd(pair.Key, pair.Value);
        foreach (var pair in registry.Resources) Registry.Resources.TryAdd(pair.Key, pair.Value);
        foreach (var pair in registry.MotionGraphs) Registry.MotionGraphs.TryAdd(pair.Key,pair.Value);
        foreach (var pair in registry.Characters) Registry.Characters.TryAdd(pair.Key, pair.Value);
        foreach (var pair in registry.CharacterDirectories) Registry.CharacterDirectories.TryAdd(pair.Key, pair.Value);
        foreach (var pair in registry.InputActions) Registry.InputActions.TryAdd(pair.Key, pair.Value);
        Bridge = new(World, Registry);
        Graphics = new(owner, World); audio = new(owner, World);
        Rendering = new(World, Graphics, repository, Registry, visuals, () => Clock.GameTime, Bridge);
        World.Platform = (name, args, context) => Input.Call(name, args, World) ?? audio.Call(name, args) ?? Graphics.Call(name, args, context) ?? name switch
        {
            "show_message" or "show_message_ext" or "show_question" => 0,
            "show_menu" => args.Length > 1 ? args[1] : 0,
            "get_string" => args.Length > 1 ? args[1] : "",
            "message_background" or "message_button" or "message_mouse_color" or "message_text_font" or "message_button_font" or "io_handle" or "texture_set_interpolation" => 0,
            "window_get_width" => 640, "window_get_height" => 480,
            "window_handle" => 1,
            _ => (GmValue?)null
        };
        World.Wait = (_, _, _) => ValueTask.FromResult<GmValue>(0);
        World.InputEventsAsync = () => Input.DispatchAsync(World);
        World.Redraw = Graphics.Build;
    }
    public async Task InitializeAsync()
    {
        await World.LoadRoomAsync(World.Manifest.GetProperty("roomOrder")[0].GetInt32());
        for (int n = 0; n < 240 && (World.CurrentRoom != World.Constants["Title"].Int || World.IsLoadingRoom || World.PendingRoom is not null); n++) await World.StepAsync();
        var context = new GmContext(World.Instances.FirstOrDefault(i => !i.Destroyed) ?? new GmInstance());
        World.Evaluator.ResetBudget(); World.Globals.Write("StartArea", 0);
        await World.Evaluator.CallAsync("DataItemReset", [], context); await World.Evaluator.CallAsync("DataItemLoad", [], context);
        await World.LoadRoomAsync(World.Constants["TestRoom_Loading"].Int);
        for (int n = 0; World.IsLoadingRoom || World.PendingRoom is not null; n++)
        {
            if (n >= 600) throw new InvalidOperationException("测试房间加载超时");
            await World.StepAsync();
        }
        Clock.RoomChanged();
    }
    public void Show(int instanceId, CharacterDefinition definition)
    {
        visuals[instanceId] = definition;
        Graphics.Build();
    }
    public async Task ApplyAsync(CharacterDefinition definition)
    {
        await NativeCharacterRuntime.BindAsync(World, Bridge, definition);
        visuals.Clear(); visuals[Player!.Id] = definition; Graphics.Build();
    }
    public async Task AdvanceAsync(NativeCharacterPreviewInput input)
    {
        int offset = World.Globals.ReadOr("controllMode").Equals(World.Constants["CTRL_KEY"]) ? 12 : 0;
        void Key(int slot, bool held) => Input.Key(World, World.Globals.Read("key", [offset + slot]).Int, held);
        int jump = World.Globals.Read("key", [offset + 5]).Int;
        Key(0, input.Left); Key(1, input.Right); Input.Key(World, jump, input.Jump);
        if (input.Dash) { Key(1, true); Key(1, false); Key(1, true); }
        async Task<bool> Tick()
        {
            Clock.BeginTick(); var state = Input.TakeNativeInput();
            Bridge.Update(new(FrameContext.FixedDelta, Clock.GameTime, Clock.Tick, new(state.Held, state.Pressed, state.Released)));
            if (!Clock.LegacyDue(World.Engine.Read("room_speed").Number)) return false;
            Input.Poll(World); await World.StepAsync(); Input.EndStep(); return true;
        }
        try
        {
            if (input.Jump)
            {
                while (!await Tick()) { }
                Input.Key(World, jump, false);
                while (!await Tick()) { }
            }
            else await Tick();
        }
        finally { if (input.Jump) Input.Key(World, jump, false); }
    }
    public void Reset()
    {
        Input.Clear(World);
        if (Player is { } player) { World.Write(player, "x", 96); World.Write(player, "y", 0); World.Write(player, "vspeed", 0); }
        Graphics.Build();
    }
    public void FrameOn(double centerX, double centerY, double width, double height, int portWidth = 0, int portHeight = 0)
    {
        portWidth = portWidth > 0 ? portWidth : (int)width;
        portHeight = portHeight > 0 ? portHeight : (int)height;
        World.Engine.Write("view_enabled", 1);
        World.Engine.Write("view_wview", width, 0);
        World.Engine.Write("view_hview", height, 0);
        World.Engine.Write("view_xview", centerX - width / 2, 0);
        World.Engine.Write("view_yview", centerY - height / 2, 0);
        World.Engine.Write("view_xport", 0, 0);
        World.Engine.Write("view_yport", 0, 0);
        World.Engine.Write("view_wport", portWidth, 0);
        World.Engine.Write("view_hport", portHeight, 0);
        World.Engine.Write("view_visible", true, 0);
        Graphics.Build();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Input.Clear(World); Bridge.Dispose(); manager.Dispose(); audio.Shutdown(); Graphics.ReleaseCharacterPreview(); World.CharacterSpriteBlur = null;
        if (Directory.Exists(TemporaryDirectory)) Directory.Delete(TemporaryDirectory, true);
    }
}
