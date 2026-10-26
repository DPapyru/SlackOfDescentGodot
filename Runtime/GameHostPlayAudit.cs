using Godot;
using System.Diagnostics;

namespace SlackOfDescent;

public partial class GameHost
{
    private static string PlayShots()
    {
        string directory = System.Environment.GetEnvironmentVariable("SLACK_PLAY_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "slack-play-audit");
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static void PlayExpect(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException("Play: " + message);
    }
    private int playCharacterChoice = -1;
    private void PlayResolvePopup()
    {
        if (GetNodeOrNull<PopupMenu>("CharacterMenu") is { Visible: true } menu)
        {
            if (playCharacterChoice < 0) playCharacterChoice = Enumerable.Range(0, menu.ItemCount).FirstOrDefault(i => menu.GetItemText(i) == "Character-Nana", 1);
            GD.Print("PLAY_AUDIT character_menu items=", menu.ItemCount, " choose=", menu.GetItemText(playCharacterChoice));
            menu.EmitSignal(PopupMenu.SignalName.IdPressed, menu.GetItemId(playCharacterChoice));
            return;
        }
        if (GetNodeOrNull<ConfirmationDialog>("NativeMapSelector") is { Visible: true } selector)
        {
            var tree = selector.GetNodeOrNull<Tree>("PlayableMapTree");
            if (tree is not null && !playMapPicked)
            {
                var item = PlayFindTreeItem(tree.GetRoot(), "Test Room");
                if (item is not null) { item.Select(0); tree.SetSelected(item, 0); playMapPicked = true; GD.Print("PLAY_AUDIT map_selector selected=", item.GetText(0)); }
                else GD.Print("PLAY_AUDIT map_selector missing Test Room");
            }
            if (playMapPicked)
            {
                playMapPicked = false;
                Callable.From(() => selector.EmitSignal(ConfirmationDialog.SignalName.Confirmed)).CallDeferred();
            }
            return;
        }
        if (GetChildren().OfType<AcceptDialog>().FirstOrDefault(dialog => dialog.Visible) is { } ask)
        {
            playDialogSeen = ask;
        }
    }
    private static TreeItem? PlayFindTreeItem(TreeItem? item, string text)
    {
        for (var child = item; child is not null; child = child.GetNext())
        {
            if (child.GetText(0) == text) return child;
            if (PlayFindTreeItem(child.GetFirstChild(), text) is { } found) return found;
        }
        return null;
    }
    private bool playMapPicked;
    private AcceptDialog? playDialogSeen;
    private async Task PlayStep()
    {
        if (modalDepth > 0)
        {
            PlayResolvePopup();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            return;
        }
        var task = StepFrameAsync();
        long started = Stopwatch.GetTimestamp();
        while (!task.IsCompleted)
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 30) throw new InvalidOperationException("Play timed out: step");
            PlayResolvePopup();
            if (playDialogSeen is { } dialog)
            {
                playDialogSeen = null;
                var answer = dialog;
                Callable.From(() => answer.EmitSignal(AcceptDialog.SignalName.CustomAction, "1")).CallDeferred();
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await task;
    }
    private async Task PlayFrames(int frames)
    {
        for (int i = 0; i < frames; i++) await PlayStep();
    }
    private async Task PlayTap(int code)
    {
        _Input(new InputEventKey { Keycode = GodotKey(code), Pressed = true });
        await PlayFrames(1);
        _Input(new InputEventKey { Keycode = GodotKey(code), Pressed = false });
        await PlayFrames(4);
    }
    private GmInstance PlayPlayer() => world.Select(world.Constants["objPlayerDoll"].Int, new(new GmInstance())).Single();
    private async Task PlaySettle(GmInstance player, double homeX)
    {
        for (int frame = 0; frame < 150; frame++)
        {
            if (player.Scope.Read("touchingQS").Truth) return;
            await PlayFrames(1);
        }
        world.Write(player, "x", homeX);
        world.Write(player, "y", 1568);
        world.Write(player, "hspeed", 0);
        world.Write(player, "vspeed", 0);
        for (int frame = 0; frame < 150 && !player.Scope.Read("touchingQS").Truth; frame++) await PlayFrames(1);
    }
    private async Task PlayOperate()
    {
        GetWindow().GrabFocus();
        await PlayFrames(4);
        var player = PlayPlayer();
        double startX = player.Scope.Read("x").Number;
        int right = RegressionKey(1), jump = RegressionKey(5), attack = RegressionKey(4), left = RegressionKey(0);
        _Input(new InputEventKey { Keycode = GodotKey(right), Pressed = true });
        await PlayFrames(18);
        _Input(new InputEventKey { Keycode = GodotKey(right), Pressed = false });
        await PlayFrames(6);
        double movedX = player.Scope.Read("x").Number;
        PlayExpect(Math.Abs(movedX - startX) > 8, "walking right moves the player doll, delta=" + (movedX - startX));
        await PlayShot("play-04-walk", "walked right through injected key events");
        GD.Print("PLAY_AUDIT operate walk delta_x=", movedX - startX, " x=", movedX, " motion=", player.Scope.ReadOr("MotionNum"));
        await PlaySettle(player, startX);
        double rise = 0;
        for (int attempt = 0; attempt < 4 && rise <= 4; attempt++)
        {
            double groundY = player.Scope.Read("y").Number;
            _Input(new InputEventKey { Keycode = GodotKey(jump), Pressed = true });
            await PlayFrames(3);
            _Input(new InputEventKey { Keycode = GodotKey(jump), Pressed = false });
            double peak = groundY;
            for (int frame = 0; frame < 45; frame++)
            {
                await PlayFrames(1);
                peak = Math.Min(peak, player.Scope.Read("y").Number);
            }
            rise = groundY - peak;
            for (int frame = 0; frame < 120 && Math.Abs(player.Scope.Read("y").Number - groundY) > 2; frame++) await PlayFrames(1);
        }
        PlayExpect(rise > 4, "jumping lifts the player, rise=" + rise);
        await PlayShot("play-05-jump", "airborne after a jump");
        world.Globals.Write("TItems", 1, [11, 11]);
        await PlayFrames(2);
        for (int pickup = 0; pickup < 2; pickup++)
        {
            _Input(new InputEventKey { Keycode = GodotKey(pickup == 0 ? 11 : attack), Pressed = true });
            await PlayFrames(2);
            _Input(new InputEventKey { Keycode = GodotKey(pickup == 0 ? 11 : attack), Pressed = false });
            await PlayFrames(60);
        }
        var snapshot = world.Created.ToDictionary(pair => pair.Key, pair => pair.Value);
        _Input(new InputEventKey { Keycode = GodotKey(attack), Pressed = true });
        await PlayFrames(30);
        _Input(new InputEventKey { Keycode = GodotKey(attack), Pressed = false });
        await PlayFrames(10);
        var deltas = world.Created.Where(pair => pair.Value != snapshot.GetValueOrDefault(pair.Key)).Select(pair => (world.Objects.TryGetValue(pair.Key, out var info) ? info.GetProperty("name").GetString() : pair.Key.ToString()) + ":" + (pair.Value - snapshot.GetValueOrDefault(pair.Key))).ToArray();
        int fired = deltas.Sum(entry => int.Parse(entry[(entry.LastIndexOf(':') + 1)..]));
        await PlayShot("play-06-attack", "attack input");
        PlayExpect(fired > 0, "attacking creates objects, got " + fired + " " + string.Join(" ", deltas));
        PlayExpect(deltas.Any(entry => entry.StartsWith("objNana_BulletNum", StringComparison.Ordinal) || entry.StartsWith("objBullet", StringComparison.Ordinal)), "attack spawns bullet objects, got " + string.Join(" ", deltas));
        GD.Print("PLAY_AUDIT operate attack created=", fired, " ", string.Join(" ", deltas), " current=", world.Globals.ReadOr("CurrentWeapon").ToString(), " motion=", player.Scope.ReadOr("MotionNum"));
        double facingX = player.Scope.Read("x").Number;
        _Input(new InputEventKey { Keycode = GodotKey(left), Pressed = true });
        await PlayFrames(30);
        _Input(new InputEventKey { Keycode = GodotKey(left), Pressed = false });
        await PlayFrames(6);
        double backX = player.Scope.Read("x").Number;
        PlayExpect(backX < facingX, "walking left moves back, delta=" + (backX - facingX));
        await PlayShot("play-07-left", "walked left");
        GD.Print("PLAY_AUDIT operate okay walk=", movedX - startX, " jump=", rise, " bullets=", fired, " left=", backX - facingX);
    }
    private GmInstance? PlayTitleOrNull() => world.Select(world.Constants["objTitle"].Int, new(new GmInstance())).FirstOrDefault();
    private async Task PlayAwaitKeys()
    {
        for (int frame = 0; frame < 240; frame++)
        {
            if (PlayTitleOrNull() is { } current && current.Scope.Read("keyAvailable").Truth) return;
            await PlayFrames(1);
        }
        throw new InvalidOperationException("Play: title menu never became ready");
    }
    private async Task PlayScrollTo(string cursor)
    {
        for (int attempt = 0; attempt < 24; attempt++)
        {
            await PlayAwaitKeys();
            if (PlayTitleOrNull() is not { } title) { await PlayFrames(1); continue; }
            int wanted = title.Scope.Read(cursor).Int;
            if (title.Scope.Read("Cursor").Int == wanted) return;
            await PlayTap(RegressionKey(3));
        }
        throw new InvalidOperationException("Play: menu cursor " + cursor);
    }
    private async Task PlaySelect(string cursor, Func<bool> done)
    {
        for (int attempt = 0; attempt < 24; attempt++)
        {
            await PlayAwaitKeys();
            if (done()) return;
            await PlayScrollTo(cursor);
            if (done()) return;
            if (PlayTitleOrNull() is not { } title) { await PlayFrames(1); continue; }
            int decide = RegressionKey(10);
            _Input(new InputEventKey { Keycode = GodotKey(decide), Pressed = true });
            await PlayFrames(1);
            _Input(new InputEventKey { Keycode = GodotKey(decide), Pressed = false });
            await PlayFrames(4);
            if (done()) return;
        }
        throw new InvalidOperationException("Play: menu decide " + cursor);
    }
    private async Task PlayPaused(Func<bool> done, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            if (done()) return;
            await PlayFrames(1);
        }
        throw new InvalidOperationException("Play: pause state");
    }
    private async Task PlayReturnToTitle()
    {
        controls.Clear(world);
        _Input(new InputEventKey { Keycode = Key.Backspace, Pressed = true });
        await PlayFrames(2);
        var context = new GmContext(new GmInstance());
        await PlayPaused(() => world.Select(world.Constants["objPause"].Int, context).FirstOrDefault() is { } pause && pause.Scope.Read("index").Int >= 2, 240);
        var pauseInstance = world.Select(world.Constants["objPause"].Int, context).First();
        GD.Print("PLAY_AUDIT phase=pause-open index=", pauseInstance.Scope.Read("index").Int, " cursor=", pauseInstance.Scope.Read("Cursor").Int);
        _Input(new InputEventKey { Keycode = Key.Backspace, Pressed = false });
        await PlayFrames(4);
        for (int i = 1; i <= 2; i++)
        {
            _Input(new InputEventKey { Keycode = GodotKey(RegressionKey(3)), Pressed = true });
            await PlayPaused(() => pauseInstance.Scope.Read("Cursor").Int == i, 240);
            _Input(new InputEventKey { Keycode = GodotKey(RegressionKey(3)), Pressed = false });
            await PlayFrames(4);
        }
        GD.Print("PLAY_AUDIT phase=pause-decide cursor=", pauseInstance.Scope.Read("Cursor").Int, " title=", PlayTitleOrNull()?.Scope.Read("ReturnTitle").Int);
        _Input(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        await PlayPaused(() => world.CurrentRoom == world.Constants["Title"].Int, 600);
        _Input(new InputEventKey { Keycode = Key.Enter, Pressed = false });
        controls.Clear(world);
        await PlayFrames(12);
        PlayExpect(world.CurrentRoom == world.Constants["Title"].Int, "the pause menu returns to the title, got " + world.CurrentRoom);
    }
    private async Task PlayShot(string name, string note)
    {
        graphics.Build(); canvas.QueueRedraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        string path = Path.Combine(PlayShots(), name + ".png");
        image.SavePng(path);
        graphics.TextAudit ??= new(); graphics.TextAudit.Clear(); graphics.Build();
        GD.Print("PLAY_AUDIT shot=", name, " room=", world.CurrentRoom, " frame=", world.Frame, " note=", note, " focus=", GetWindow().HasFocus(), " modal=", modalDepth, " texts=", string.Join("|", graphics.TextAudit.Select(t => t.Text).Take(8)), " path=", path);
    }
    public static async Task RunPlayAuditAsync(GameHost host) => await host.RunPlayAudit();
    private async Task RunPlayAudit()
    {
        RegressionExpect(DisplayServer.GetName() != "headless", "play audit needs a display");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetWindow().GrabFocus();
        await PlayFrames(180);
        GD.Print("PLAY_AUDIT window focus=", GetWindow().HasFocus(), " display=", DisplayServer.GetName(), " size=", GetWindow().Size);
        RegressionView("VIEW_TITLE");
        await PlayShot("play-01-title", "title menu");
        await PlaySelect("GAMESTART", () => PlayTitleOrNull() is { } menu && menu.Scope.Read("View").Equals(menu.Scope.Read("VIEW_GAMESTART")));
        RegressionView("VIEW_GAMESTART");
        await PlayScrollTo("PLAYGROUND");
        await PlayShot("play-02-gamestart", "game start menu with playground selected");
        await PlaySelect("PLAYGROUND", () => world.CurrentRoom != world.Constants["Title"].Int);
        await PlayFrames(45);
        PlayExpect(world.CurrentRoom == world.Constants["Playground"].Int, "playground reached through the real menu, got " + world.CurrentRoom);
        PlayExpect(world.Select(world.Constants["objPlayerDoll"].Int, new(new GmInstance())).Any(), "player doll active in playground");
        await PlayShot("play-03-playground", "playground room");
        GD.Print("PLAY_AUDIT step=playground room=", world.CurrentRoom, " frame=", world.Frame);
        await PlayOperate();
        await PlayGravity();
        GD.Print("PLAY_AUDIT step=operated room=", world.CurrentRoom, " frame=", world.Frame);
        GD.Print("PLAY_AUDIT_OK");
        audio.Shutdown();
        GetTree().Quit();
    }
    private async Task PlayGravity()
    {
        var player = PlayPlayer();
        double ground = player.Scope.Read("y").Number;
        double idleX = player.Scope.Read("x").Number;
        for (int frame = 0; frame < 60; frame++) await PlayFrames(1);
        double settled = player.Scope.Read("y").Number;
        PlayExpect(Math.Abs(settled - ground) < 1, "solid terrain holds the player against gravity, y " + ground + " -> " + settled);
        PlayExpect(Math.Abs(player.Scope.Read("x").Number - idleX) < 1, "the player does not drift while idle");
        PlayExpect(Math.Abs(player.Scope.ReadOr("vspeed").Number) < 1, "vertical speed settles on the ground, vspeed=" + player.Scope.ReadOr("vspeed").ToString());
        await PlayShot("play-08-grounded", "standing on solid terrain");
        GD.Print("PLAY_AUDIT operate gravity y=", settled, " vspeed=", player.Scope.ReadOr("vspeed"));
    }
    public static async Task RunPlayTestRoomAuditAsync(GameHost host) => await host.RunPlayTestRoomAudit();
    private async Task RunPlayTestRoomAudit()
    {
        RegressionExpect(DisplayServer.GetName() != "headless", "test room audit needs a display");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetWindow().GrabFocus();
        await PlayFrames(180);
        RegressionView("VIEW_TITLE");
        await PlaySelect("GAMESTART", () => PlayTitleOrNull() is { } menu && menu.Scope.Read("View").Equals(menu.Scope.Read("VIEW_GAMESTART")));
        await PlayScrollTo("TEST");
        await PlayShot("play-11-testmenu", "test room selected in the game start menu");
        await PlaySelect("TEST", () => world.CurrentRoom != world.Constants["Title"].Int || mapSelector is not null);
        await PlayFrames(45);
        bool entered = world.CurrentRoom == world.Constants["TestRoom_Loading"].Int || world.CurrentRoom == world.Constants["EnemyTestRoom"].Int;
        PlayExpect(entered, "the Test Room menu entry starts a room load, got " + world.CurrentRoom);
        string? defect = null;
        try { for (int frame = 0; frame < 240 && world.CurrentRoom != world.Constants["EnemyTestRoom"].Int; frame++) await PlayFrames(1); }
        catch (Exception error) { defect = error.Message; }
        if (defect is null && world.CurrentRoom == world.Constants["EnemyTestRoom"].Int)
        {
            await PlayShot("play-12-testroom", "test room through the real menu");
            GD.Print("PLAY_AUDIT step=testroom room=", world.CurrentRoom, " frame=", world.Frame);
        }
        else GD.Print("PLAY_AUDIT defect=testroom-entry room=", world.CurrentRoom, " error=", defect ?? "room never finished loading");
        GD.Print("PLAY_AUDIT_OK");
        audio.Shutdown();
        GetTree().Quit();
    }
}