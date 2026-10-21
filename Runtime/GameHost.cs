using Godot;

namespace SlackOfDescent;

public partial class GameHost : Node2D
{
    private GmWorld world=null!;
    private string? failure;
    private double elapsed;
    private bool audit;
    private GmAudio audio=null!;
    private GmGraphics graphics=null!;
    private GmCanvas canvas=null!;
    private readonly GmInput controls=new();
    private int rendered;
    private bool captured;
    private int Dialog(string text,string[] choices)
    {
        if(audit)
        {
            var args=OS.GetCmdlineUserArgs();int option=Array.IndexOf(args,"--dialog-choice");
            if(option<0||option+1>=args.Length)throw new InvalidOperationException("Audit requires --dialog-choice for: "+text);
            int selected=int.Parse(args[option+1]);GD.Print("AUDIT_DIALOG choice=",selected," text=",text);return selected;
        }
        var start=new System.Diagnostics.ProcessStartInfo("zenity"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"--list","--radiolist","--title=Slack Of Descent","--text="+text.Replace('#','\n'),"--column=","--column=Choice","--hide-header"})start.ArgumentList.Add(arg);
        for(int i=0;i<choices.Length;i++){start.ArgumentList.Add(i==0?"TRUE":"FALSE");start.ArgumentList.Add(choices[i]);}
        using var process=System.Diagnostics.Process.Start(start)!;
        var output=process.StandardOutput.ReadToEnd().Trim();process.WaitForExit();
        return process.ExitCode==0?Array.IndexOf(choices,output)+1:0;
    }
    private static int VirtualKey(Key key)=>key switch
    {
        Key.Backspace=>8,Key.Tab=>9,Key.Enter=>13,Key.Shift=>16,Key.Ctrl=>17,Key.Alt=>18,Key.Escape=>27,Key.Space=>32,
        Key.Pageup=>33,Key.Pagedown=>34,Key.End=>35,Key.Home=>36,Key.Left=>37,Key.Up=>38,Key.Right=>39,Key.Down=>40,Key.Insert=>45,Key.Delete=>46,
        >=Key.F1 and <=Key.F12=>112+(int)(key-Key.F1),_ =>(int)key
    };
    public override void _Input(InputEvent input)
    {
        if(world is null)return;
        if(input is InputEventMouseButton button)
        {
            int code=button.ButtonIndex switch{MouseButton.Left=>1,MouseButton.Right=>2,MouseButton.Middle=>3,_=>0};
            controls.Mouse(world,code,button.Pressed);
            if(button.Pressed&&button.ButtonIndex==MouseButton.WheelUp)controls.Wheel=1;
            if(button.Pressed&&button.ButtonIndex==MouseButton.WheelDown)controls.Wheel=-1;
        }
        if(input is InputEventKey key&&!key.Echo)
        {
            int code=VirtualKey(key.Keycode);
            controls.Key(world,code,key.Pressed);
        }
    }

    public override void _Ready()
    {
        TextureFilter=TextureFilterEnum.Linear;
        audit=OS.GetCmdlineUserArgs().Contains("--audit");
        var root=ProjectSettings.GlobalizePath("res://GameData");
        var source=System.Environment.GetEnvironmentVariable("SLACK_SOURCE")??System.IO.Path.Combine(root,"assets");
        try
        {
            world=new(root,source);
            if(audit)world.Random.Seed=0;
            audio=new(this,world);
            graphics=new(this,world);
            canvas=new GmCanvas();AddChild(canvas);
            canvas.Paint=c=>{if(failure is null)try{graphics.Render(c);}catch(Exception e){Fail(e);}};
            world.Platform=Platform;
            world.InputEvents=DispatchInput;
            world.Redraw=()=>{graphics.Build();canvas.QueueRedraw();};
            world.LoadRoom(world.Manifest.GetProperty("roomOrder")[0].GetInt32());
            graphics.Build();
            if(OS.GetCmdlineUserArgs().Contains("--capture"))for(int i=0;i<120;i++)StepFrame();
            GD.Print("Loaded original room ",world.CurrentRoom);
        }
        catch(Exception e){Fail(e);}
        if(audit)
        {
            if(failure is null)try
            {
                int frames=120;
                var commandArgs=OS.GetCmdlineUserArgs();
                var index=Array.IndexOf(commandArgs,"--frames");
                if(index>=0&&index+1<commandArgs.Length)frames=int.Parse(commandArgs[index+1]);
                var roomIndex=Array.IndexOf(commandArgs,"--room");
                var menuIndex=Array.IndexOf(commandArgs,"--menu");
                if(menuIndex>=0&&menuIndex+1<commandArgs.Length)AuditMenu(commandArgs[menuIndex+1]);
                if(roomIndex>=0&&roomIndex+1<commandArgs.Length)
                {
                    for(int i=0;i<180;i++)StepFrame();
                    var context=new GmContext(world.Instances.First(i=>world.IsObject(i.Object,world.Constants["objTitle"].Int)));
                    world.Globals.Write("StartArea",0);
                    world.Call("DataItemReset",[],context);world.Call("DataItemLoad",[],context);
                    world.LoadRoom(int.Parse(commandArgs[roomIndex+1]));
                }
                for(int i=0;i<frames;i++)StepFrame();
                GD.Print("AUDIT room=",world.CurrentRoom," frames=",world.Frame," instances=",world.Instances.Count);
            }
            catch(Exception e){Fail(e);}
            if(failure is null&&OS.GetCmdlineUserArgs().Contains("--capture"))
            {
                SetProcess(false);canvas.QueueRedraw();GD.Print("AUDIT_OK");Capture();return;
            }
            canvas.Paint=null;
            audio?.Shutdown();
            GD.Print(failure is null?"AUDIT_OK":"AUDIT_FAILED");GetTree().Quit(failure is null?0:1);
        }
    }

    private void StepFrame(){world.Step();controls.EndStep();}

    private void AuditMenu(string mode)
    {
        for(int i=0;i<180;i++)StepFrame();
        var title=world.Instances.Single(i=>!i.Destroyed&&world.IsObject(i.Object,world.Constants["objTitle"].Int));
        int offset=world.Globals.Read("controllMode").Int==world.Constants["CTRL_KEY"].Int?12:0;
        if(world.Globals.Read("controllMode").Int==world.Constants["CTRL_JOYPAD"].Int)throw new InvalidOperationException("Menu audit requires keyboard configuration");
        void Tap(int binding)
        {
            int code=world.Globals.Read("key",[offset+binding]).Int;
            controls.Key(world,code,true);StepFrame();controls.Key(world,code,false);
            for(int i=0;i<4;i++)StepFrame();
        }
        void Expect(string field,GmValue value)
        {
            var actual=title.Scope.Read(field);
            if(!actual.Equals(value))throw new InvalidOperationException($"Menu {field}: expected {value}, got {actual}");
        }
        Expect("View",title.Scope.Read("VIEW_TITLE"));
        Expect("Cursor",title.Scope.Read("GAMESTART"));
        Tap(10);Expect("View",title.Scope.Read("VIEW_GAMESTART"));
        string selection=mode switch{"Playground"=>"PLAYGROUND","TestRoom"=>"TEST","Survival"=>"SURVIVALPLAYGROUND","SurvivalWorld"=>"SURVIVAL","Stage1"=>"CRACKLECRADLE",_=>throw new ArgumentException("Unknown audit menu "+mode)};
        int wanted=title.Scope.Read(selection).Int;
        for(int attempt=0;title.Scope.Read("Cursor").Int!=wanted&&attempt<10;attempt++)Tap(3);
        Expect("Cursor",wanted);
        Tap(10);
        if(mode=="Stage1")
        {
            Expect("View",title.Scope.Read("VIEW_GAMESTART_CC"));Expect("Cursor",title.Scope.Read("NEWGAME"));Tap(10);
        }
        int loading=world.Constants[mode+"_Loading"].Int;
        string destination=mode switch{"TestRoom"=>"EnemyTestRoom","Stage1"=>"Stage1_1",_=>mode};
        if(world.CurrentRoom!=loading&&world.CurrentRoom!=world.Constants[destination].Int)throw new InvalidOperationException("Unexpected menu destination "+world.CurrentRoom);
        GD.Print("AUDIT_MENU mode=",mode," room=",world.CurrentRoom);
    }

    private void Fail(Exception e)
    {
        failure=e.Message;
        GD.PushError(audit?e.Message:e.ToString());
        SetProcess(false);
    }

    public override void _Process(double delta)
    {
        if(failure is not null){if(audit)GetTree().Quit(1);return;}
        try
        {
            var mouse=GetGlobalMousePosition();world.Engine.Write("mouse_x",mouse.X);world.Engine.Write("mouse_y",mouse.Y);
            elapsed+=delta;
            var step=1.0/Math.Max(1,world.Engine.Read("room_speed").Number);
            int count=0;
            while(elapsed>=step&&count++<100)
            {
                StepFrame();elapsed-=step;
            }
            if(world.Ended){audio.Shutdown();GetTree().Quit();}
            QueueRedraw();canvas.QueueRedraw();
            rendered++;
            if(rendered>=180&&!captured&&OS.GetCmdlineUserArgs().Contains("--capture"))
            {captured=true;Capture();}
        }
        catch(Exception e){Fail(e);QueueRedraw();}
    }

    private void DispatchInput()
    {
        controls.Dispatch(world);
    }

    private async void Capture()
    {
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        var image=GetViewport().GetTexture().GetImage();
        var path=System.Environment.GetEnvironmentVariable("SLACK_CAPTURE")??"/home/dpapyru/.cache/ccgame-migration/godot.png";
        image.SavePng(path);image.Dispose();GD.Print("CAPTURE ",path);audio.Shutdown();GetTree().Quit(failure is null?0:1);
    }

    public override void _ExitTree(){audio?.Shutdown();}

    public override void _Draw()
    {
        if(world is null)return;
        if(world.Rooms.TryGetValue(world.CurrentRoom,out var room))
        {
            DrawRect(new Rect2(Vector2.Zero,GetViewportRect().Size),GmGraphics.Colour(room.GetProperty("colour").GetDouble()));
        }
    }

    private GmValue? Platform(string name,GmValue[] args,GmContext context)
    {
        var input=controls.Call(name,args,world);
        if(input is not null)return input;
        var sound=audio.Call(name,args);
        if(sound is not null)return sound;
        var graphic=graphics.Call(name,args,context);
        if(graphic is not null)return graphic;
        int I(int i)=>i<args.Length?args[i].Int:0;
        switch(name)
        {
            case "show_message_ext":return Dialog(args[0].ToString(),args.Skip(1).Select(v=>v.ToString()).ToArray());
            case "show_question":return Dialog(args[0].ToString(),["Yes","No"])==1;
            case "show_message":Dialog(args[0].ToString(),["OK"]);return 0;
            case "message_background":case "message_button":case "message_mouse_color":case "message_text_font":case "message_button_font":return 0;
            case "io_handle":Input.FlushBufferedEvents();return 0;
            case "texture_set_interpolation":TextureFilter=I(0)>0?TextureFilterEnum.Linear:TextureFilterEnum.Nearest;return 0;
            case "window_get_width":return GetWindow().Size.X;
            case "window_get_height":return GetWindow().Size.Y;
            case "window_handle":return 1;
        }
        return null;
    }
}
