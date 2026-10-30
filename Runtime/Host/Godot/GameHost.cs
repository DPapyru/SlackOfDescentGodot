using System.Diagnostics;
using System.Text.Json;
using Godot;

namespace SlackOfDescent;

public partial class GameHost : Node2D
{
    private GmWorld world = null!;
    private string? failure;
    private double elapsed;
    private bool audit;
    private GmAudio audio = null!;
    private GmGraphics graphics = null!;
    private GmCanvas canvas = null!;
    private readonly GmInput controls = new();
    private readonly GmFrameMonitor frameMonitor = new();
    private readonly GmFramePlan framePlan = new();
    private int cadenceRoom = int.MinValue;
    private readonly long frameClock = Stopwatch.GetTimestamp();
    private Label? fpsLabel;
    private bool renderSignalConnected;
    private long lastFramesDrawn;
    private static readonly bool fbDiag =
        System.Environment.GetEnvironmentVariable("SLACK_FB_DIAG") == "1";
    private long buildCount;
    private Task? pendingFrame;
    private int modalDepth;
    private Button? grenadeBinding;
    private ConfirmationDialog? bindingDialog;

    private string? Prompt(string name, string text, string initial)
    {
        var args = OS.GetCmdlineUserArgs();
        int option = Array.IndexOf(args, name);
#if INCLUDE_DIAGNOSTICS
#if INCLUDE_DIAGNOSTICS
#if INCLUDE_DIAGNOSTICS
        if (audit)
        {
            if (option < 0 || option + 1 >= args.Length)
                throw new InvalidOperationException("Audit requires " + name);
            return args[option + 1] == "cancel" ? null : args[option + 1];
        }
#endif
#endif
#endif
        var start = new ProcessStartInfo("zenity")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        foreach (
            var arg in new[]
            {
                "--entry",
                "--title=Slack Of Descent",
                "--text=" + text,
                "--entry-text=" + initial,
            }
        )
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
        process.WaitForExit();
        controls.Call("io_clear", [], world);
        elapsed = 0;
        return process.ExitCode == 0 ? output : null;
    }

    private int Menu(string text, int fallback)
    {
        var choices = text.Split('|');
        if (audit)
        {
            var args = OS.GetCmdlineUserArgs();
            int option = Array.IndexOf(args, "--menu-choice");
            if (option < 0 || option + 1 >= args.Length)
                throw new InvalidOperationException("Audit requires --menu-choice");
            string selection = args[option + 1];
            int auditSelection = int.TryParse(selection, out int index)
                ? index
                : Array.IndexOf(choices, selection);
            if (!int.TryParse(selection, out _) && auditSelection < 0)
                throw new InvalidOperationException("Menu choice not found: " + selection);
            GD.Print(
                "AUDIT_CHOICES count=",
                choices.Count(c => c.Length > 0),
                " selected=",
                auditSelection >= 0 && auditSelection < choices.Length
                    ? choices[auditSelection]
                    : "cancel"
            );
            return auditSelection < 0 ? fallback : auditSelection;
        }
        var start = new ProcessStartInfo("zenity")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        foreach (
            var arg in new[]
            {
                "--list",
                "--title=Slack Of Descent",
                "--column=ID",
                "--column=Character",
                "--hide-column=1",
                "--print-column=1",
                "--hide-header",
            }
        )
            start.ArgumentList.Add(arg);
        for (int i = 0; i < choices.Length; i++)
        {
            if (choices[i].Length == 0)
                continue;
            start.ArgumentList.Add(i.ToString());
            start.ArgumentList.Add(choices[i]);
        }
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        controls.Call("io_clear", [], world);
        elapsed = 0;
        return process.ExitCode == 0 && int.TryParse(output.Trim(), out int selected)
            ? selected
            : fallback;
    }

    private int Dialog(string text, string[] choices)
    {
        if (audit)
        {
            var args = OS.GetCmdlineUserArgs();
            int option = Array.IndexOf(args, "--dialog-choice");
            if (option < 0 || option + 1 >= args.Length)
                throw new InvalidOperationException("Audit requires --dialog-choice for: " + text);
            int selected = int.Parse(args[option + 1]);
            GD.Print("AUDIT_DIALOG choice=", selected, " text=", text);
            return selected;
        }
        var start = new System.Diagnostics.ProcessStartInfo("zenity")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            var arg in new[]
            {
                "--list",
                "--radiolist",
                "--title=Slack Of Descent",
                "--text=" + text.Replace('#', '\n'),
                "--column=",
                "--column=Choice",
                "--hide-header",
            }
        )
            start.ArgumentList.Add(arg);
        for (int i = 0; i < choices.Length; i++)
        {
            start.ArgumentList.Add(i == 0 ? "TRUE" : "FALSE");
            start.ArgumentList.Add(choices[i]);
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 ? Array.IndexOf(choices, output) + 1 : 0;
    }

    private static int VirtualKey(Key key) =>
        key switch
        {
            Key.Backspace => 8,
            Key.Tab => 9,
            Key.Enter => 13,
            Key.Shift => 16,
            Key.Ctrl => 17,
            Key.Alt => 18,
            Key.Escape => 27,
            Key.Space => 32,
            Key.Pageup => 33,
            Key.Pagedown => 34,
            Key.End => 35,
            Key.Home => 36,
            Key.Left => 37,
            Key.Up => 38,
            Key.Right => 39,
            Key.Down => 40,
            Key.Insert => 45,
            Key.Delete => 46,
            >= Key.F1 and <= Key.F12 => 112 + (int)(key - Key.F1),
            _ => (int)key,
        };

    public override void _Input(InputEvent input)
    {
        if (bindingDialog is not null)
        {
            CaptureGrenadeBinding(input);
            return;
        }
        if (world is null || modalDepth > 0)
            return;
        if (input is InputEventMouseButton button)
        {
            int code = button.ButtonIndex switch
            {
                MouseButton.Left => 1,
                MouseButton.Right => 2,
                MouseButton.Middle => 3,
                _ => 0,
            };
            controls.QueueMouse(code, button.Pressed);
            if (button.Pressed && button.ButtonIndex == MouseButton.WheelUp)
                controls.Wheel = 1;
            if (button.Pressed && button.ButtonIndex == MouseButton.WheelDown)
                controls.Wheel = -1;
        }
        if (input is InputEventKey key && !key.Echo)
        {
            int code = VirtualKey(key.Keycode);
            controls.QueueKey(code, key.Pressed);
        }
    }

    private Vector2I? forcedContentScale;
    private string? stretchMode;
    private bool stretchReported;

    private void ApplyStretchOverride()
    {
        stretchMode = System.Environment.GetEnvironmentVariable("SLACK_STRETCH");
        if (stretchMode is null)
            return;
        var window = GetWindow();
        if (stretchMode == "viewport")
            window.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
        else if (stretchMode == "canvas_items")
            window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        var spec = System.Environment.GetEnvironmentVariable("SLACK_CONTENT_SCALE");
        if (spec is not null)
        {
            var parts = spec.Split('x');
            if (
                parts.Length == 2
                && int.TryParse(parts[0], out var w)
                && int.TryParse(parts[1], out var h)
                && w > 0
                && h > 0
            )
                forcedContentScale = new Vector2I(w, h);
        }
        if (forcedContentScale is { } forced)
            window.ContentScaleSize = forced;
        Console.Error.WriteLine(
            "STRETCH_REQUEST mode="
                + stretchMode
                + " contentScale="
                + (forcedContentScale is { } f ? f.ToString() : "auto")
        );
    }

    public override void _Ready()
    {
        Engine.MaxFps = 60;
        TextureFilter = TextureFilterEnum.Linear;
        ApplyStretchOverride();
#if INCLUDE_DIAGNOSTICS
        audit = OS.GetCmdlineUserArgs().Contains("--audit");
        traceRooms = OS.GetCmdlineUserArgs().Contains("--trace-rooms");
#endif
        try
        {
            if (audit)
                GD.Print("AUDIT_START initializing game data");
            var root = OS.HasFeature("editor")
                ? ProjectSettings.GlobalizePath("res://GameData")
                : GmPaths.FindData(
                    Path.GetDirectoryName(OS.GetExecutablePath())!,
                    AppContext.BaseDirectory
                );
            var source =
                System.Environment.GetEnvironmentVariable("SLACK_SOURCE")
                ?? Path.Combine(root, "assets");
            world = new(root, source, System.Environment.GetEnvironmentVariable("SLACK_USER_ROOT"));
            NativeLocalization.Initialize(root, world.UserRoot);
            world.Performance.Configure(world.UserRoot, OS.IsDebugBuild());
            if (DisplayServer.GetName() != "headless")
            {
                RenderingServer.FramePostDraw += OnFramePostDraw;
                renderSignalConnected = true;
            }
            controls.LoadBindings(world);
            controls.JoystickExists = number => Input.GetConnectedJoypads().Contains(number - 1);
            controls.JoystickButtons = number =>
                Input.GetConnectedJoypads().Contains(number - 1) ? 16 : 0;
            controls.JoystickButton = (number, button) =>
                button > 0 && Input.IsJoyButtonPressed(number - 1, (JoyButton)(button - 1));
            controls.JoystickAxis = (number, axis) =>
            {
                float value = Input.GetJoyAxis(
                    number - 1,
                    axis == 0 ? JoyAxis.LeftX : JoyAxis.LeftY
                );
                if (Math.Abs(value) > .5)
                    return value;
                return axis == 0
                    ? (Input.IsJoyButtonPressed(number - 1, JoyButton.DpadRight) ? 1 : 0)
                        - (Input.IsJoyButtonPressed(number - 1, JoyButton.DpadLeft) ? 1 : 0)
                    : (Input.IsJoyButtonPressed(number - 1, JoyButton.DpadDown) ? 1 : 0)
                        - (Input.IsJoyButtonPressed(number - 1, JoyButton.DpadUp) ? 1 : 0);
            };
            if (audit)
                world.Random.Seed = 0;
            audio = new(this, world);
            graphics = new(this, world);
            InitializeNative();
            canvas = new GmCanvas { Perf = world.Performance };
            AddChild(canvas);
            var hud = new CanvasLayer { Layer = 100 };
            AddChild(hud);
            fpsLabel = new Label
            {
                Text = "FPS: --",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            hud.AddChild(fpsLabel);
            fpsLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            fpsLabel.OffsetLeft = -100;
            fpsLabel.OffsetTop = 4;
            fpsLabel.OffsetRight = -8;
            fpsLabel.OffsetBottom = 26;
            canvas.Paint = c =>
            {
                if (failure is null)
                    try
                    {
                        graphics.Render(c);
                    }
                    catch (Exception e)
                    {
                        Fail(e);
                    }
            };
            world.Platform = Platform;
            world.Wait = Wait;
            world.InputEventsAsync = () => controls.DispatchAsync(world);
            world.Redraw = () =>
            {
                graphics.Build();
                canvas.QueueRedraw();
            };
            if (
                !audit
                || OS.GetCmdlineUserArgs().Contains("--fps-audit")
                || OS.GetCmdlineUserArgs().Contains("--live-frames")
            )
                world.FramePlan = framePlan;
            if (audit)
                GD.Print("AUDIT_DATA_READY");
#if INCLUDE_DIAGNOSTICS
            if (StartDiagnostics())
                return;
#endif
            world.LoadRoom(world.Manifest.GetProperty("roomOrder")[0].GetInt32());
            graphics.Build();
#if INCLUDE_DIAGNOSTICS
            if (OS.GetCmdlineUserArgs().Contains("--capture"))
                for (int i = 0; i < 120; i++)
                    StepFrame();
#endif
            GD.Print("Loaded original room ", world.CurrentRoom);
#if INCLUDE_DIAGNOSTICS
            if (StartRoomDiagnostics())
                return;
#endif
        }
        catch (Exception e)
        {
            Fail(e);
        }
#if INCLUDE_DIAGNOSTICS
        RunDiagnostics();
#endif
    }

    private void SyncViewport()
    {
        var size = world.OutputSize();
        var window = GetWindow();
        var wanted = new Vector2I(size.Width, size.Height);
        if (forcedContentScale is not { } forced && window.ContentScaleSize != wanted)
            window.ContentScaleSize = wanted;
        if (stretchMode is not null && world.Performance.Enabled && !stretchReported)
        {
            stretchReported = true;
            var tex = GetViewport().GetTexture();
            Console.Error.WriteLine(
                "STRETCH_ACTIVE mode="
                    + window.ContentScaleMode
                    + " contentScale="
                    + window.ContentScaleSize
                    + " winSize="
                    + window.Size
                    + " visRect="
                    + GetViewport().GetVisibleRect().Size
                    + " tex="
                    + tex?.GetWidth()
                    + "x"
                    + tex?.GetHeight()
            );
        }
        if (world.Performance.Enabled || fbDiag)
        {
            var canvasSize = GetViewport().GetVisibleRect().Size;
            world.Performance.WindowSize = (window.Size.X, window.Size.Y);
            world.Performance.CanvasSize = ((int)canvasSize.X, (int)canvasSize.Y);
            world.Performance.RenderTargetSize = (
                window.ContentScaleSize.X,
                window.ContentScaleSize.Y
            );
            var texture = GetViewport().GetTexture();
            if (texture is not null)
                world.Performance.FramebufferSize = (texture.GetWidth(), texture.GetHeight());
            if (fbDiag && texture is not null)
                Console.Error.WriteLine(
                    $"FB_DIAG texW={texture.GetWidth()} texH={texture.GetHeight()} visRect={canvasSize} win={window.Size} cs={window.ContentScaleSize}"
                );
        }
    }

    private void UpdateMouse()
    {
        var position = GetGlobalMousePosition();
        var mouse = world.MouseToRoom(position.X, position.Y);
        world.Engine.Write("mouse_x", mouse.X);
        world.Engine.Write("mouse_y", mouse.Y);
    }

    partial void PositionMudWindow();

    private bool RenderFrame()
    {
        if (framePlan.Consume() is null)
            return false;
        buildCount++;
        canvas.QueueRedraw();
        return true;
    }

    private bool lastFocus = true;
    private string? lastFpsText;
    private Vector2 lastMouse;

    private bool NativeRefresh()
    {
        if (
            audit
            || actionEditor is not null
            || characterEditor is not null
            || mapEditor is not null
        )
            return true;
        if (modalDepth > 0 || world.IsLoadingRoom)
            return true;
        bool focus = GetWindow().HasFocus();
        if (focus != lastFocus)
        {
            lastFocus = focus;
            return true;
        }
        if (fpsLabel is not null && fpsLabel.Text != lastFpsText)
        {
            lastFpsText = fpsLabel.Text;
            return true;
        }
        var mouse = GetGlobalMousePosition();
        if (mouse != lastMouse)
        {
            lastMouse = mouse;
            return true;
        }
        return false;
    }

    private async Task StepFrameAsync()
    {
        PositionMudWindow();
        controls.Poll(world);
        await world.StepAsync();
        controls.EndStep();
        SyncViewport();
    }

    private async ValueTask<GmValue> Wait(string name, GmValue[] args, GmContext context)
    {
        switch (name)
        {
            case "show_menu":
                return await NativeCharacterMenu(args[0].ToString(), args[1].Int, context);
            case "get_string":
                return audit && !OS.GetCmdlineUserArgs().Contains("--controls-ui-audit")
                    ? Prompt("--input-text", args[0].ToString(), args[1].ToString()) ?? args[1]
                    : await InputText(args[0].ToString(), args[1].ToString());
            case "show_message_ext":
                return
                    audit
                    && !OS.GetCmdlineUserArgs().Contains("--transition-audit")
                    && !OS.GetCmdlineUserArgs().Contains("--regression-audit")
                    ? Dialog(args[0].ToString(), args.Skip(1).Select(v => v.ToString()).ToArray())
                    : await ShowDialog(
                        args[0].ToString(),
                        args.Skip(1).Select(v => v.ToString()).ToArray()
                    );
            case "show_question":
                return (
                        audit
                            ? Dialog(args[0].ToString(), ["Yes", "No"])
                            : await ShowDialog(args[0].ToString(), ["Yes", "No"])
                    ) == 1;
            case "show_message":
                if (audit)
                    Dialog(args[0].ToString(), ["OK"]);
                else
                    await ShowDialog(args[0].ToString(), ["OK"]);
                return 0;
            case "sleep":
                await ToSignal(
                    GetTree().CreateTimer(Math.Max(0, args[0].Number) / 1000),
                    SceneTreeTimer.SignalName.Timeout
                );
                return 0;
            case "io_handle":
                Input.FlushBufferedEvents();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                controls.EndStep();
                controls.Poll(world);
                UpdateMouse();
                return 0;
            case "keyboard_wait":
                controls.Call("io_clear", [], world);
                do
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    controls.EndStep();
                    controls.Poll(world);
                } while (!controls.Call("keyboard_check_pressed", [1], world)!.Value.Truth);
                return 0;
            default:
                throw new NotSupportedException(name);
        }
    }

    private async Task<int> ShowPopupMenu(string text, int fallback, string[]? names = null)
    {
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var popup = new PopupMenu
        {
            Name = "CharacterMenu",
            ForceNative = true,
            MaxSize = new Vector2I(420, Math.Max(120, (int)GetViewportRect().Size.Y - 32)),
        };
        var original = text.Split('|');
        var entries = original.Concat(names ?? []).ToArray();
        if (names?.Length > 0)
            popup.AddSeparator(NativeLocalization.IsChinese ? "原版" : "Original");
        for (int i = 0; i < entries.Length; i++)
        {
            if (names?.Length > 0 && i == original.Length)
                popup.AddSeparator("Mod");
            if (i >= original.Length)
                popup.AddItem(entries[i], i);
            else if (entries[i].Length == 0)
                popup.AddSeparator();
            else if (entries[i][0] == '-')
                popup.AddSeparator(entries[i][1..], i);
            else
            {
                popup.AddItem(
                    NativeLocalization.Text(entries[i][0] == '/' ? entries[i][1..] : entries[i]),
                    i
                );
                if (entries[i][0] == '/')
                    popup.SetItemDisabled(popup.ItemCount - 1, true);
            }
        }
        popup.IdPressed += id => completion.TrySetResult((int)id);
        popup.PopupHide += () =>
            Callable.From(() => completion.TrySetResult(fallback)).CallDeferred();
        AddChild(popup);
        modalDepth++;
        try
        {
            popup.PopupOnParent(
                new Rect2I((Vector2I)GetViewport().GetMousePosition(), Vector2I.Zero)
            );
            return await completion.Task;
        }
        finally
        {
            modalDepth--;
            controls.Clear(world);
            elapsed = 0;
            if (popup.Visible)
                popup.Hide();
            RemoveChild(popup);
            popup.QueueFree();
        }
    }

    private async Task<string> InputText(string text, string initial)
    {
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var dialog = new ConfirmationDialog
        {
            Name = "CharacterInput",
            Title = "Slack Of Descent",
            DialogText = NativeLocalization.Text(text),
        };
        dialog.GetOkButton().Text = NativeLocalization.Text("确定");
        dialog.GetCancelButton().Text = NativeLocalization.Text("取消");
        var entry = new LineEdit { Text = initial, CustomMinimumSize = new Vector2(300, 32) };
        dialog.AddChild(entry);
        dialog.Confirmed += () => completion.TrySetResult(entry.Text);
        dialog.Canceled += () => completion.TrySetResult(initial);
        dialog.CloseRequested += () => completion.TrySetResult(initial);
        entry.TextSubmitted += value => completion.TrySetResult(value);
        AddChild(dialog);
        modalDepth++;
        try
        {
            dialog.PopupCentered();
            entry.GrabFocus();
            entry.SelectAll();
            return await completion.Task;
        }
        finally
        {
            modalDepth--;
            controls.Clear(world);
            elapsed = 0;
            if (dialog.Visible)
                dialog.Hide();
            RemoveChild(dialog);
            dialog.QueueFree();
        }
    }

    private async Task<int> ShowDialog(string text, string[] choices)
    {
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var dialog = new AcceptDialog
        {
            Title = "Slack Of Descent",
            DialogText = NativeLocalization.Text(text).Replace('#', '\n'),
        };
        dialog.GetOkButton().Hide();
        for (int i = 0; i < choices.Length; i++)
            if (choices[i].Length > 0)
                dialog.AddButton(NativeLocalization.Text(choices[i]), false, (i + 1).ToString());
        dialog.CustomAction += action => completion.TrySetResult(int.Parse(action));
        dialog.Canceled += () => completion.TrySetResult(0);
        dialog.CloseRequested += () => completion.TrySetResult(0);
        AddChild(dialog);
        modalDepth++;
        try
        {
            dialog.PopupCentered();
            return await completion.Task;
        }
        finally
        {
            modalDepth--;
            controls.Clear(world);
            elapsed = 0;
            if (dialog.Visible)
                dialog.Hide();
            RemoveChild(dialog);
            dialog.QueueFree();
        }
    }

    private void Fail(Exception e)
    {
        if (failure is not null)
            return;
        failure = e.Message;
        GD.PushError(audit ? e.Message : e.ToString());
        SetProcess(false);
        if (!audit)
        {
            audio?.Shutdown();
            var dialog = new AcceptDialog
            {
                Title = "Slack Of Descent - Error",
                DialogText = e.Message,
                MinSize = new Vector2I(480, 160),
            };
            dialog.Confirmed += () => GetTree().Quit(1);
            dialog.Canceled += () => GetTree().Quit(1);
            dialog.CloseRequested += () => GetTree().Quit(1);
            AddChild(dialog);
            dialog.PopupCentered();
        }
    }

    public override void _Process(double delta)
    {
        if (failure is not null)
        {
            if (audit)
                GetTree().Quit(1);
            return;
        }
        long processStarted = world.Performance.Start(),
            firstFrame = world.Frame;
        if (world.Performance.Enabled)
            world.Performance.MarkProcessEntry(Stopwatch.GetTimestamp());
        double processCpuStart = world.Performance.Enabled
            ? world.Performance.MainThreadCpuNowMs()
            : -1;
        try
        {
            long perfSync = world.Performance.Start();
            try
            {
                SyncGrenadeBinding();
                SyncNativeBindings();
                SyncNativeUi();
                SyncMapRuntime();
            }
            finally
            {
                world.Performance.EndProcess("process_sync", perfSync);
            }
            nativeBridge.Services.Input.Context =
                mapEditor is not null || characterEditor is not null || actionEditor is not null
                    ? NativeInputContext.Editor
                : modalDepth > 0 || world.CurrentRoom == world.Constants["Title"].Int
                    ? NativeInputContext.Menu
                : NativeInputContext.Game;
            if (cadenceRoom != world.CurrentRoom)
            {
                cadenceRoom = world.CurrentRoom;
                world.Performance.ResetCadence();
                lastFramesDrawn = 0;
                nativeClock.RoomChanged();
            }
            if (!audit && !GetWindow().HasFocus() && modalDepth == 0)
            {
                ResetNativeInput();
                lastFramesDrawn = 0;
                canvas.QueueRedraw();
                return;
            }
            if (modalDepth > 0 && pendingFrame is null)
            {
                elapsed = 0;
                nativeClock.Pause();
                frameMonitor.Reset();
                world.Performance.ResetCadence();
                lastFramesDrawn = 0;
                canvas.QueueRedraw();
                return;
            }
            if (pendingFrame is not null)
            {
                if (!pendingFrame.IsCompleted)
                {
                    elapsed = 0;
                    nativeClock.Pause();
                    frameMonitor.Reset();
                    world.Performance.ResetCadence();
                    lastFramesDrawn = 0;
                    canvas.QueueRedraw();
                    return;
                }
                pendingFrame.GetAwaiter().GetResult();
                pendingFrame = null;
                elapsed = 0;
                nativeClock.Pause();
            }
            long perfPresent = world.Performance.Start();
            try
            {
                if (world.Performance.Enabled && DisplayServer.GetName() != "headless")
                {
                    long drawn = Engine.GetFramesDrawn();
                    world.Performance.Present(
                        drawn - lastFramesDrawn,
                        (long)
                            RenderingServer.GetRenderingInfo(
                                RenderingServer.RenderingInfo.TotalPrimitivesInFrame
                            ),
                        (long)
                            RenderingServer.GetRenderingInfo(
                                RenderingServer.RenderingInfo.TotalDrawCallsInFrame
                            ),
                        RenderingServer.GetFrameSetupTimeCpu()
                    );
                    lastFramesDrawn = drawn;
                }
                var measurement = frameMonitor.Update(
                    Stopwatch.GetElapsedTime(frameClock).TotalSeconds,
                    world.Frame,
                    world.CurrentRoom,
                    world.Engine.Read("room_speed").Number,
                    DisplayServer.GetName() != "headless",
                    buildCount
                );
                if (measurement is not null)
                {
                    world.Engine.Write("fps", measurement.LogicFps);
                    world.Globals.Write("current_fps", measurement.LogicFps);
                    if (fpsLabel is not null)
                        fpsLabel.Text = "FPS: " + measurement.LogicFps.ToString("F0");
                    world.Performance.Record(measurement, world, () => graphics.Statistics());
                    world.Performance.Cadence(measurement, world, () => graphics.Statistics());
#if INCLUDE_DIAGNOSTICS
                    if (fpsAuditStart > 0)
                        fpsAuditSamples.Add(measurement);
#endif
                }
            }
            finally
            {
                world.Performance.EndProcess("process_present", perfPresent);
            }
#if INCLUDE_DIAGNOSTICS
            if (ProcessDiagnostics(delta))
                return;
#endif
            UpdateMouse();
            if (world.IsLoadingRoom)
            {
                long loadingStarted = Stopwatch.GetTimestamp();
                for (
                    int jobs = 0;
                    jobs < 128
                        && world.IsLoadingRoom
                        && Stopwatch.GetElapsedTime(loadingStarted).TotalMilliseconds < 8;
                    jobs++
                )
                {
                    pendingFrame = StepFrameAsync();
                    if (!pendingFrame.IsCompleted)
                        break;
                    pendingFrame.GetAwaiter().GetResult();
                    pendingFrame = null;
                }
                nativeClock.RoomChanged();
            }
            else
            {
                long perfNative = world.Performance.Start();
                try
                {
                    for (int count = nativeClock.Advance(delta); count > 0; count--)
                    {
                        nativeClock.BeginTick();
                        controls.Poll(world);
                        var state = controls.TakeNativeInput();
                        nativeBridge.Update(
                            new(
                                FrameContext.FixedDelta,
                                nativeClock.GameTime,
                                nativeClock.Tick,
                                new(state.Held, state.Pressed, state.Released)
                            )
                        );
                        if (!nativeClock.LegacyDue(world.Engine.Read("room_speed").Number))
                            continue;
                        pendingFrame = StepFrameAsync();
                        if (!pendingFrame.IsCompleted)
                        {
                            nativeClock.Pause();
                            break;
                        }
                        pendingFrame.GetAwaiter().GetResult();
                        pendingFrame = null;
                        if (world.IsLoadingRoom)
                        {
                            nativeClock.RoomChanged();
                            break;
                        }
                        if (world.Ended)
                            break;
                    }
                }
                finally
                {
                    world.Performance.EndProcess("process_native", perfNative);
                }
            }
            if (world.Ended)
            {
                audio.Shutdown();
                GetTree().Quit();
            }
            long perfRender = world.Performance.Start();
            try
            {
                bool rebuilt = RenderFrame();
                if (rebuilt || NativeRefresh())
                {
                    QueueRedraw();
                    canvas.QueueRedraw();
                }
            }
            finally
            {
                world.Performance.EndProcess("process_render", perfRender);
            }
#if INCLUDE_DIAGNOSTICS
            FinishDiagnostics();
#endif
        }
        catch (Exception e)
        {
            Fail(e);
            QueueRedraw();
            if (audit)
            {
                audio.Shutdown();
                GetTree().Quit(1);
            }
        }
        finally
        {
            world.Performance.Process(processStarted, world.Frame - firstFrame);
            if (world.Performance.Enabled)
            {
                long exitTicks = Stopwatch.GetTimestamp();
                world.Performance.MarkProcessExit(exitTicks);
                world.Performance.ProcessEnd(
                    exitTicks,
                    processCpuStart,
                    world.Performance.MainThreadCpuNowMs()
                );
            }
        }
    }

    private void OnFramePostDraw()
    {
        world.Performance.MarkPostDraw(Stopwatch.GetTimestamp());
        world.Performance.RenderCompleted();
    }

    public override void _ExitTree()
    {
        if (renderSignalConnected)
            RenderingServer.FramePostDraw -= OnFramePostDraw;
        isolatedPreview?.Dispose();
        nativeBridge?.Dispose();
        mods?.Dispose();
        graphics?.Shutdown();
        audio?.Shutdown();
    }

    public override void _Draw()
    {
        if (world is null)
            return;
        if (world.Rooms.TryGetValue(world.CurrentRoom, out var room))
        {
            if (world.Engine.Read("background_showcolor").Truth)
                DrawRect(
                    new Rect2(Vector2.Zero, GetViewportRect().Size),
                    GmGraphics.Colour(world.Engine.Read("background_color").Number)
                );
        }
    }

    private GmValue? Platform(string name, GmValue[] args, GmContext context)
    {
        var input = controls.Call(name, args, world);
        if (input is not null)
            return input;
        var sound = audio.Call(name, args);
        if (sound is not null)
            return sound;
        var graphic = graphics.Call(name, args, context);
        if (graphic is not null)
            return graphic;
        int I(int i) => i < args.Length ? args[i].Int : 0;
        switch (name)
        {
            case "slack_mod_menu":
                OpenModMenuEntry(I(0));
                return 0;
            case "slack_map_select":
                OpenMapSelector();
                return 0;
            case "slack_language":
                return MenuLanguage(I(0) != 0);
            case "show_menu":
                return Menu(args[0].ToString(), args[1].Int);
            case "get_string":
                return Prompt("--input-text", args[0].ToString(), args[1].ToString()) ?? args[1];
            case "show_message_ext":
                return Dialog(args[0].ToString(), args.Skip(1).Select(v => v.ToString()).ToArray());
            case "show_question":
                return Dialog(args[0].ToString(), ["Yes", "No"]) == 1;
            case "show_message":
                Dialog(args[0].ToString(), ["OK"]);
                return 0;
            case "message_background":
            case "message_button":
            case "message_mouse_color":
            case "message_text_font":
            case "message_button_font":
                return 0;
            case "io_handle":
                Input.FlushBufferedEvents();
                return 0;
            case "texture_set_interpolation":
                TextureFilter = I(0) > 0 ? TextureFilterEnum.Linear : TextureFilterEnum.Nearest;
                return 0;
            case "window_get_width":
                return GetWindow().Size.X;
            case "window_get_height":
                return GetWindow().Size.Y;
            case "window_handle":
                return 1;
        }
        return null;
    }
}
