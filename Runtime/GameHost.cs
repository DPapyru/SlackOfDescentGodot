using Godot;
using System.Diagnostics;
using System.Text.Json;

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
    private long liveEnd;
    private long liveStart;
    private long liveFirstFrame;
    private readonly List<double> liveTimes=new();
    private readonly GmFrameMonitor frameMonitor=new();
    private readonly long frameClock=Stopwatch.GetTimestamp();
    private Label? fpsLabel;
    private long fpsAuditStart;
    private bool fpsAuditSlow;
    private readonly List<GmFrameSample> fpsAuditSamples=new();
    private IEnumerator<int>? animationAudit;
    private Task? pendingFrame;
    private int modalDepth;
    private Button? grenadeBinding;
    private ConfirmationDialog? bindingDialog;
    private string? Prompt(string name,string text,string initial)
    {
        var args=OS.GetCmdlineUserArgs();int option=Array.IndexOf(args,name);
        if(audit)
        {
            if(option<0||option+1>=args.Length)throw new InvalidOperationException("Audit requires "+name);
            return args[option+1]=="cancel"?null:args[option+1];
        }
        var start=new ProcessStartInfo("zenity"){UseShellExecute=false,RedirectStandardOutput=true};
        foreach(var arg in new[]{"--entry","--title=Slack Of Descent","--text="+text,"--entry-text="+initial})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;
        string output=process.StandardOutput.ReadToEnd().TrimEnd('\r','\n');process.WaitForExit();
        controls.Call("io_clear",[],world);elapsed=0;
        return process.ExitCode==0?output:null;
    }
    private int Menu(string text,int fallback)
    {
        var choices=text.Split('|');
        if(audit)
        {
            var args=OS.GetCmdlineUserArgs();int option=Array.IndexOf(args,"--menu-choice");
            if(option<0||option+1>=args.Length)throw new InvalidOperationException("Audit requires --menu-choice");
            string selection=args[option+1];
            int auditSelection=int.TryParse(selection,out int index)?index:Array.IndexOf(choices,selection);
            if(!int.TryParse(selection,out _)&&auditSelection<0)throw new InvalidOperationException("Menu choice not found: "+selection);
            GD.Print("AUDIT_CHOICES count=",choices.Count(c=>c.Length>0)," selected=",auditSelection>=0&&auditSelection<choices.Length?choices[auditSelection]:"cancel");return auditSelection<0?fallback:auditSelection;
        }
        var start=new ProcessStartInfo("zenity"){UseShellExecute=false,RedirectStandardOutput=true};
        foreach(var arg in new[]{"--list","--title=Slack Of Descent","--column=ID","--column=Character","--hide-column=1","--print-column=1","--hide-header"})start.ArgumentList.Add(arg);
        for(int i=0;i<choices.Length;i++)
        {
            if(choices[i].Length==0)continue;
            start.ArgumentList.Add(i.ToString());start.ArgumentList.Add(choices[i]);
        }
        using var process=Process.Start(start)!;
        string output=process.StandardOutput.ReadToEnd();process.WaitForExit();
        controls.Call("io_clear",[],world);elapsed=0;
        return process.ExitCode==0&&int.TryParse(output.Trim(),out int selected)?selected:fallback;
    }
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
        if(bindingDialog is not null){CaptureGrenadeBinding(input);return;}
        if(world is null||modalDepth>0)return;
        if(input is InputEventMouseButton button)
        {
            int code=button.ButtonIndex switch{MouseButton.Left=>1,MouseButton.Right=>2,MouseButton.Middle=>3,_=>0};
            controls.QueueMouse(code,button.Pressed);
            if(button.Pressed&&button.ButtonIndex==MouseButton.WheelUp)controls.Wheel=1;
            if(button.Pressed&&button.ButtonIndex==MouseButton.WheelDown)controls.Wheel=-1;
        }
        if(input is InputEventKey key&&!key.Echo)
        {
            int code=VirtualKey(key.Keycode);
            controls.QueueKey(code,key.Pressed);
        }
    }

    public override void _Ready()
    {
        TextureFilter=TextureFilterEnum.Linear;
        audit=OS.GetCmdlineUserArgs().Contains("--audit");
        try
        {
            if(audit)GD.Print("AUDIT_START initializing game data");
            var root=OS.HasFeature("editor")?ProjectSettings.GlobalizePath("res://GameData"):GmPaths.FindData(Path.GetDirectoryName(OS.GetExecutablePath())!,AppContext.BaseDirectory);
            var source=System.Environment.GetEnvironmentVariable("SLACK_SOURCE")??Path.Combine(root,"assets");
            world=new(root,source,System.Environment.GetEnvironmentVariable("SLACK_USER_ROOT"));
            world.Performance.Configure(world.UserRoot,OS.IsDebugBuild());
            controls.LoadBindings(world);
            if(audit)world.Random.Seed=0;
            audio=new(this,world);
            graphics=new(this,world);
            canvas=new GmCanvas();AddChild(canvas);
            var hud=new CanvasLayer{Layer=100};AddChild(hud);
            fpsLabel=new Label{Text="FPS: --",MouseFilter=Control.MouseFilterEnum.Ignore,HorizontalAlignment=HorizontalAlignment.Right};
            hud.AddChild(fpsLabel);fpsLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            fpsLabel.OffsetLeft=-100;fpsLabel.OffsetTop=4;fpsLabel.OffsetRight=-8;fpsLabel.OffsetBottom=26;
            canvas.Paint=c=>{if(failure is null)try{graphics.Render(c);}catch(Exception e){Fail(e);}};
            world.Platform=Platform;
            world.Wait=Wait;
            world.InputEventsAsync=()=>controls.DispatchAsync(world);
            world.Redraw=()=>{graphics.Build();canvas.QueueRedraw();};
            if(audit)GD.Print("AUDIT_DATA_READY");
            if(audit&&OS.GetCmdlineUserArgs().Contains("--package-resources-audit"))
            {
                SetProcess(false);
                var context=new GmContext(new GmInstance());
                world.Evaluator.ResetBudget();
                RegressionExpect(world.Evaluator.Run(world.Compile("return \"package-\u65e5\u672c\u8a9e\";"),context).ToString()=="package-\u65e5\u672c\u8a9e","packaged dynamic compiler");
                RegressionExpect(world.Call("file_find_first",["Chara/Doll/Head/Character-Nana_*.gif",16],context).ToString().Length>0,"packaged character selection resources");
                GD.Print("PACKAGE_RUNTIME_OK images=",graphics.AuditResourceImages()," sounds=",audio.AuditResourceSounds()," source=",world.SourceRoot);
                audio.Shutdown();GetTree().Quit();return;
            }
            if(audit&&OS.GetCmdlineUserArgs().Contains("--graphics-audit")){SetProcess(false);AuditGraphics();return;}
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
                if(commandArgs.Contains("--regression-audit")){SetProcess(false);AuditGameplayRegression();return;}
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
                ContentAudit(commandArgs);
                if(commandArgs.Contains("--transition-audit")){SetProcess(false);AuditTransitions();return;}
                if(commandArgs.Contains("--controls-ui-audit")){SetProcess(false);AuditInteractiveControls();return;}
                int shoot=Array.IndexOf(commandArgs,"--shoot");
                if(shoot>=0&&shoot+1<commandArgs.Length)AuditShoot(int.Parse(commandArgs[shoot+1]));
                int animation=Array.IndexOf(commandArgs,"--animation-audit");
                if(animation>=0&&animation+1<commandArgs.Length){animationAudit=(commandArgs[animation+1]=="route"?AuditRoute(true):commandArgs[animation+1].StartsWith("bind:",StringComparison.Ordinal)?AuditBinding(commandArgs[animation+1][5..],true):AuditAnimation(commandArgs[animation+1])).GetEnumerator();return;}
                GD.Print("AUDIT room=",world.CurrentRoom," frames=",world.Frame," instances=",world.Instances.Count);
                if(world.Performance.Enabled)GD.Print("PERF_CONTENT tiles=",world.Tiles.Count," objects=",string.Join(" ",world.Instances.GroupBy(i=>i.Object).OrderByDescending(g=>g.Count()).Take(12).Select(g=>$"{g.Key}:{g.Count()}")));
                int benchmark=Array.IndexOf(commandArgs,"--benchmark");
                if(benchmark>=0&&benchmark+1<commandArgs.Length)
                {
                    var samples=new double[int.Parse(commandArgs[benchmark+1])];
                    for(int i=0;i<samples.Length;i++){long started=Stopwatch.GetTimestamp();StepFrame();samples[i]=Stopwatch.GetElapsedTime(started).TotalMilliseconds;}
                    Array.Sort(samples);
                    GD.Print("BENCHMARK room=",world.CurrentRoom," frames=",samples.Length," mean_ms=",samples.Average().ToString("F3")," p95_ms=",samples[(int)((samples.Length-1)*0.95)].ToString("F3")," max_ms=",samples[^1].ToString("F3"));
                }
                var state=System.Environment.GetEnvironmentVariable("SLACK_STATE");
                if(state is not null)File.WriteAllText(state,JsonSerializer.Serialize(new{room=world.CurrentRoom,frame=world.Frame,seed=world.Random.Seed,globals=world.Globals.Values.ToDictionary(p=>p.Key,p=>p.Value.ToString()),engine=world.Engine.Values.ToDictionary(p=>p.Key,p=>p.Value.ToString()),instances=world.Instances.Select(i=>new{id=i.Id,obj=i.Object,active=i.Active,destroyed=i.Destroyed,values=i.Scope.Values.ToDictionary(p=>p.Key,p=>p.Value.ToString())})}));
                int live=Array.IndexOf(commandArgs,"--live-frames");
                if(commandArgs.Contains("--fps-audit")){fpsAuditStart=Stopwatch.GetTimestamp();Engine.MaxFps=60;return;}
                if(live>=0&&live+1<commandArgs.Length){liveFirstFrame=world.Frame;liveEnd=world.Frame+int.Parse(commandArgs[live+1]);liveStart=Stopwatch.GetTimestamp();return;}
            }
            catch(Exception e){Fail(e);}
            if(failure is null&&OS.GetCmdlineUserArgs().Contains("--capture"))
            {
                SetProcess(false);canvas.QueueRedraw();GD.Print("AUDIT_OK");Capture();return;
            }
            SetProcess(false);if(canvas is not null)canvas.Paint=null;
            audio?.Shutdown();
            GD.Print(failure is null?"AUDIT_OK":"AUDIT_FAILED");GetTree().Quit(failure is null?0:1);
        }
    }

    private void SyncViewport()
    {
        var size=world.OutputSize();var window=GetWindow();var wanted=new Vector2I(size.Width,size.Height);
        if(window.ContentScaleSize!=wanted)window.ContentScaleSize=wanted;
    }
    private void UpdateMouse()
    {
        var position=GetGlobalMousePosition();var mouse=world.MouseToRoom(position.X,position.Y);
        world.Engine.Write("mouse_x",mouse.X);world.Engine.Write("mouse_y",mouse.Y);
    }
    private void StepFrame(){controls.Poll(world);world.Step();controls.EndStep();SyncViewport();}
    private async Task StepFrameAsync(){controls.Poll(world);await world.StepAsync();controls.EndStep();SyncViewport();}
    private async ValueTask<GmValue> Wait(string name,GmValue[] args,GmContext context)
    {
        switch(name)
        {
            case "show_menu":return audit&&!OS.GetCmdlineUserArgs().Contains("--controls-ui-audit")?Menu(args[0].ToString(),args[1].Int):await ShowPopupMenu(args[0].ToString(),args[1].Int);
            case "get_string":return audit&&!OS.GetCmdlineUserArgs().Contains("--controls-ui-audit")?Prompt("--input-text",args[0].ToString(),args[1].ToString())??args[1]:await InputText(args[0].ToString(),args[1].ToString());
            case "show_message_ext":return audit&&!OS.GetCmdlineUserArgs().Contains("--transition-audit")&&!OS.GetCmdlineUserArgs().Contains("--regression-audit")?Dialog(args[0].ToString(),args.Skip(1).Select(v=>v.ToString()).ToArray()):await ShowDialog(args[0].ToString(),args.Skip(1).Select(v=>v.ToString()).ToArray());
            case "show_question":return (audit?Dialog(args[0].ToString(),["Yes","No"]):await ShowDialog(args[0].ToString(),["Yes","No"]))==1;
            case "show_message":
                if(audit)Dialog(args[0].ToString(),["OK"]);else await ShowDialog(args[0].ToString(),["OK"]);
                return 0;
            case "sleep":
                await ToSignal(GetTree().CreateTimer(Math.Max(0,args[0].Number)/1000),SceneTreeTimer.SignalName.Timeout);return 0;
            case "io_handle":
                Input.FlushBufferedEvents();await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);controls.EndStep();controls.Poll(world);UpdateMouse();return 0;
            case "keyboard_wait":
                controls.Call("io_clear",[],world);
                do{await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);controls.EndStep();controls.Poll(world);}while(!controls.Call("keyboard_check_pressed",[1],world)!.Value.Truth);
                return 0;
            default:throw new NotSupportedException(name);
        }
    }
    private async Task<int> ShowPopupMenu(string text,int fallback)
    {
        var completion=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Native popups avoid broken parent-signal cleanup in the 4.7.2 release template.
        var popup=new PopupMenu{Name="CharacterMenu",ForceNative=true,MaxSize=new Vector2I(420,Math.Max(120,(int)GetViewportRect().Size.Y-32))};
        var entries=text.Split('|');
        for(int i=0;i<entries.Length;i++)
        {
            if(entries[i].Length==0)popup.AddSeparator();
            else if(entries[i][0]=='-')popup.AddSeparator(entries[i][1..],i);
            else{popup.AddItem(entries[i][0]=='/'?entries[i][1..]:entries[i],i);if(entries[i][0]=='/')popup.SetItemDisabled(popup.ItemCount-1,true);}
        }
        popup.IdPressed+=id=>completion.TrySetResult((int)id);
        popup.PopupHide+=()=>Callable.From(()=>completion.TrySetResult(fallback)).CallDeferred();
        AddChild(popup);modalDepth++;
        try
        {
            popup.PopupOnParent(new Rect2I((Vector2I)GetViewport().GetMousePosition(),Vector2I.Zero));
            return await completion.Task;
        }
        finally{modalDepth--;controls.Clear(world);elapsed=0;if(popup.Visible)popup.Hide();RemoveChild(popup);popup.QueueFree();}
    }
    private async Task<string> InputText(string text,string initial)
    {
        var completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog=new ConfirmationDialog{Name="CharacterInput",Title="Slack Of Descent",DialogText=text};
        var entry=new LineEdit{Text=initial,CustomMinimumSize=new Vector2(300,32)};dialog.AddChild(entry);
        dialog.Confirmed+=()=>completion.TrySetResult(entry.Text);
        dialog.Canceled+=()=>completion.TrySetResult(initial);
        dialog.CloseRequested+=()=>completion.TrySetResult(initial);
        entry.TextSubmitted+=value=>completion.TrySetResult(value);
        AddChild(dialog);modalDepth++;
        try{dialog.PopupCentered();entry.GrabFocus();entry.SelectAll();return await completion.Task;}
        finally{modalDepth--;controls.Clear(world);elapsed=0;if(dialog.Visible)dialog.Hide();RemoveChild(dialog);dialog.QueueFree();}
    }
    private async Task<int> ShowDialog(string text,string[] choices)
    {
        var completion=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog=new AcceptDialog{Title="Slack Of Descent",DialogText=text.Replace('#','\n')};
        dialog.GetOkButton().Hide();
        for(int i=0;i<choices.Length;i++)if(choices[i].Length>0)dialog.AddButton(choices[i],false,(i+1).ToString());
        dialog.CustomAction+=action=>completion.TrySetResult(int.Parse(action));
        dialog.Canceled+=()=>completion.TrySetResult(0);
        dialog.CloseRequested+=()=>completion.TrySetResult(0);
        AddChild(dialog);modalDepth++;
        try{dialog.PopupCentered();return await completion.Task;}
        finally{modalDepth--;controls.Clear(world);elapsed=0;if(dialog.Visible)dialog.Hide();RemoveChild(dialog);dialog.QueueFree();}
    }
    private async void AuditGraphics()
    {
        try{await graphics.Audit();GD.Print("GRAPHICS_OK");audio.Shutdown();GetTree().Quit();}
        catch(Exception e){Fail(e);audio.Shutdown();GetTree().Quit(1);}
    }

    private void AuditShoot(int frames)
    {
        int offset=world.Globals.Read("controllMode").Int==world.Constants["CTRL_KEY"].Int?12:0;
        int code=world.Globals.Read("key",[offset+4]).Int;
        var player=world.Select(world.Constants["objPlayerDoll"].Int,new(new GmInstance())).First();
        bool mouse=world.Globals.Read("controllMode").Int==world.Constants["CTRL_KEYMOUSE"].Int;
        world.Engine.Write("mouse_x",player.Scope.Read("x").Number+300);world.Engine.Write("mouse_y",player.Scope.Read("y").Number-25);
        GD.Print("AUDIT_SHOOT start frame=",world.Frame," weapon=",world.Globals.ReadOr("Weapon")," current=",world.Globals.ReadOr("CurrentWeapon")," motion=",player.Scope.ReadOr("MotionNum"));
        int created=world.Calls.GetValueOrDefault("instance_create");
        int bullet=world.Constants["objBullet"].Int,shots=world.Created.GetValueOrDefault(bullet);
        for(int frame=0;frame<frames;frame++)
        {
            bool down=frame%10<5;
            if(mouse)controls.Mouse(world,1,down);else controls.Key(world,code,down);
            StepFrame();
            if(frame%30==0)GD.Print("AUDIT_SHOOT progress=",frame," instances=",world.Instances.Count);
        }
        controls.Mouse(world,1,false);controls.Key(world,code,false);
        for(int frame=0;frame<60;frame++)StepFrame();
        int fired=world.Created.GetValueOrDefault(bullet)-shots;
        if(fired==0)throw new InvalidOperationException("Shoot audit did not fire any bullets");
        GD.Print("AUDIT_SHOOT_OK frames=",frames," bullets=",fired," created=",world.Calls.GetValueOrDefault("instance_create")-created);
    }

    private IEnumerable<int> AuditAnimation(string mode)
    {
        var context=new GmContext(new GmInstance());
        var player=world.Select(world.Constants["objPlayerDoll"].Int,context).Single();
        int offset=world.Globals.Read("controllMode").Int==world.Constants["CTRL_KEY"].Int?12:0;
        int left=world.Globals.Read("key",[offset]).Int,right=world.Globals.Read("key",[offset+1]).Int;
        void Sample(string phase)
        {
            GD.Print("ANIMATION ",phase," frame=",world.Frame," ",string.Join(" ",new[]{"x","y","MotionNum","KeyFrmNum","Step","ChangeMotion","ChangeMotionNum","BINDED","HALFBINDED","touchingQS","KOF1"}.Select(n=>n+"="+player.Scope.ReadOr(n).ToString()))," alarm0=",player.Scope.Read("alarm",[0]));
        }
        if(mode=="mud")
        {
            var mud=world.Select(world.Constants["objMud"].Int,context).OrderBy(i=>i.Scope.Read("x").Number).First();
            world.Write(player,"x",mud.Scope.Read("x").Number+48);world.Write(player,"y",mud.Scope.Read("y").Number-48);
            for(int f=0;f<90;f++){StepFrame();yield return 0;}Sample("mud-idle");
            if(!player.Scope.Read("touchingQS").Truth)throw new InvalidOperationException("Mud audit did not contact terrain");
            double idleX=player.Scope.Read("x").Number;
            controls.Key(world,right,true);
            for(int f=0;f<60;f++){StepFrame();yield return 0;}Sample("mud-right");
            double rightX=player.Scope.Read("x").Number;
            if(rightX<=idleX+5)throw new InvalidOperationException("Mud audit did not move right");
            controls.Key(world,right,false);StepFrame();yield return 0;controls.Key(world,left,true);
            for(int f=0;f<60;f++){StepFrame();yield return 0;}Sample("mud-left");
            if(player.Scope.Read("x").Number>=rightX-5)throw new InvalidOperationException("Mud audit did not move left");
            controls.Key(world,left,false);StepFrame();yield return 0;
            double jumpY=player.Scope.Read("y").Number,minY=jumpY;
            int jump=world.Globals.Read("key",[offset+5]).Int;controls.Key(world,jump,true);StepFrame();yield return 0;controls.Key(world,jump,false);
            for(int f=0;f<120;f++){StepFrame();minY=Math.Min(minY,player.Scope.Read("y").Number);yield return 0;}Sample("mud-jump");
            if(minY>=jumpY-5)throw new InvalidOperationException("Mud audit did not jump");
            if(player.Scope.Read("MotionNum").Int!=player.Scope.Read("STAND_1_QUICKSAND").Int&&player.Scope.Read("MotionNum").Int!=player.Scope.Read("STAND_0_QUICKSAND").Int)throw new InvalidOperationException("Mud animation did not return to idle");
        }
        else if(mode=="effects")
        {
            var samples=new List<double>();
            int shotCount=world.Created.GetValueOrDefault(world.Constants["objBullet"].Int);
            foreach(string phase in new[]{"idle","shoot","settle","mud","recover"})
            {
                if(phase=="mud")
                {
                    var mud=world.Select(world.Constants["objMud"].Int,context).OrderBy(i=>i.Scope.Read("x").Number).First();
                    world.Write(player,"x",mud.Scope.Read("x").Number+48);world.Write(player,"y",mud.Scope.Read("y").Number-48);
                }
                if(phase=="recover"){controls.Key(world,13,true);StepFrame();yield return 0;controls.Key(world,13,false);world.Write(player,"x",96);world.Write(player,"y",0);}
                samples.Clear();double renderTime=0;int particles=0,instances=0,drawCalls=0;
                for(int frame=0;frame<180;frame++)
                {
                    if(phase=="shoot")
                    {
                        controls.Mouse(world,1,frame%10<5);
                        world.Engine.Write("mouse_x",player.Scope.Read("x").Number+300);world.Engine.Write("mouse_y",player.Scope.Read("y").Number-25);
                    }
                    long started=Stopwatch.GetTimestamp();StepFrame();samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    renderTime+=GetProcessDeltaTime()*1000;particles=Math.Max(particles,world.Particles.Systems.Values.Sum(s=>s.Particles.Count));instances=Math.Max(instances,world.Instances.Count);
                    drawCalls=Math.Max(drawCalls,(int)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));
                    yield return 0;
                }
                controls.Mouse(world,1,false);samples.Sort();
                GD.Print("EFFECT_PERF phase=",phase," mean_ms=",samples.Average().ToString("F2")," p95_ms=",samples[(int)((samples.Count-1)*0.95)].ToString("F2")," max_ms=",samples[^1].ToString("F2")," render_ms=",(renderTime/samples.Count).ToString("F2")," particles=",particles," instances=",instances," draw_calls=",drawCalls);
            }
            if(world.Created.GetValueOrDefault(world.Constants["objBullet"].Int)==shotCount)throw new InvalidOperationException("Effect audit did not fire bullets");
        }
        else if(mode=="particles")
        {
            var failures=new List<string>();double budget=1000/world.Engine.Read("room_speed").Number;
            foreach(int count in new[]{100,300,600})
            {
                var effectContext=new GmContext(player);
                world.Call("Spark",[count,64,64,0.1,0,360,2,8,150,1],effectContext);
                world.Call("effect_create_above",[5,player.Scope.Read("x"),player.Scope.Read("y"),2,0xffffff],effectContext);
                int created=world.Created.GetValueOrDefault(world.Constants["objSpark"].Int);
                var samples=new List<double>();var renderedTimes=new List<double>();int peak=0;
                for(int frame=0;frame<100;frame++)
                {
                    long started=Stopwatch.GetTimestamp();StepFrame();samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    renderedTimes.Add(GetProcessDeltaTime()*1000);peak=Math.Max(peak,world.Select(world.Constants["objSpark"].Int,context).Count());yield return 0;
                }
                samples.Sort();renderedTimes.Sort();
                GD.Print("PARTICLE_PERF kind=spark count=",count," peak=",peak," created=",created," mean_ms=",samples.Average().ToString("F2")," p95_ms=",samples[94].ToString("F2")," max_ms=",samples[^1].ToString("F2")," render_ms=",renderedTimes.Average().ToString("F2")," render_p95_ms=",renderedTimes[94].ToString("F2"));
                if(renderedTimes[94]>budget)failures.Add("spark-"+count);
                for(int frame=0;frame<60;frame++){StepFrame();yield return 0;}
            }
            foreach(string kind in new[]{"fluid","builtin"})
            {
                double x=player.Scope.Read("x").Number,y=player.Scope.Read("y").Number;
                int requested=kind=="fluid"?180:1200;
                for(int index=0;index<(kind=="fluid"?requested:requested/6);index++)
                {
                    if(kind=="fluid")world.Call("FluidWithScaleType",[x,y,0x427552,1,2,8,0,360,0.75,0.8,-100,0],new(player));
                    else world.Call("effect_create_above",[5,x+index%40,y-index/40,0,0xffffff],new(player));
                }
                var samples=new List<double>();var renderTimes=new List<double>();int peak=0;
                for(int frame=0;frame<120;frame++)
                {
                    long started=Stopwatch.GetTimestamp();StepFrame();samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    renderTimes.Add(GetProcessDeltaTime()*1000);peak=Math.Max(peak,kind=="fluid"?world.Select(world.Constants["objFluid"].Int,context).Count():world.Particles.Systems.Values.Sum(s=>s.Particles.Count));yield return 0;
                }
                samples.Sort();renderTimes.Sort();
                GD.Print("PARTICLE_PERF kind=",kind," count=",requested," peak=",peak," mean_ms=",samples.Average().ToString("F2")," p95_ms=",samples[113].ToString("F2")," max_ms=",samples[^1].ToString("F2")," render_ms=",renderTimes.Average().ToString("F2")," render_p95_ms=",renderTimes[113].ToString("F2"));
                if(renderTimes[113]>budget)failures.Add(kind);
                for(int frame=0;frame<90;frame++){StepFrame();yield return 0;}
            }
            if(failures.Count>0)throw new InvalidOperationException("Particle frame budget exceeded: "+string.Join(",",failures)+" budget_ms="+budget.ToString("F2"));
        }
        else if(mode=="controls")
        {
            int characters=world.Call("ds_list_size",[world.Globals.Read("CustomCharacters")],context).Int;
            if(characters<10)throw new InvalidOperationException("Character menu has too few candidates: "+characters);
            GD.Print("CONTROL characters=",characters);
            foreach(int code in new[]{81,69,67})
            {
                world.Globals.Write("TItems",1,[11,11]);world.Globals.Write("TItems",1,[12,11]);world.Globals.Write("TItems",1,[0,11]);
                int previous=world.Globals.Read("CurrentWeapon").Int;
                _Input(new InputEventKey{Keycode=(Key)code,Pressed=true});StepFrame();yield return 0;_Input(new InputEventKey{Keycode=(Key)code,Pressed=false});
                for(int f=0;f<90;f++){StepFrame();yield return 0;}
                int current=world.Globals.Read("CurrentWeapon").Int;
                GD.Print("CONTROL weapon key=",code," previous=",previous," current=",current," object=",world.Globals.Read("Weapon"));
                if((current!=previous)!=(code==67))throw new InvalidOperationException("Weapon key differs from original mapping: "+code);
            }
            _Input(new InputEventKey{Keycode=Key.V,Pressed=true});StepFrame();yield return 0;_Input(new InputEventKey{Keycode=Key.V,Pressed=false});
            for(int f=0;f<30;f++){StepFrame();yield return 0;}
            var display=world.Select(world.Constants["objDisplay"].Int,context).Single();
            GD.Print("CONTROL menu-complete frame=",world.Frame," character=",display.Scope.ReadOr("chara"));
        }
        else if(mode=="head")
        {
            int jump=world.Globals.Read("key",[offset+5]).Int;
            controls.Key(world,jump,true);StepFrame();yield return 0;controls.Key(world,jump,false);
            for(int f=0;f<8;f++){StepFrame();yield return 0;}
            var head=world.Select(player.Scope.Read("HEAD").Int,context).Single();
            GD.Print("HEAD ",string.Join(" ",new[]{"x","y","sprite_index","PartPicture","image_alpha","ScaleX","ScaleY","depth","Direction","image_angle"}.Select(n=>n+"="+head.Scope.ReadOr(n).ToString()))," camera=",graphics.Camera);
        }
        else if(mode=="ceilmouth")
        {
            var enemy=world.Instances.FirstOrDefault(i=>!i.Destroyed&&i.Scope.ReadOr("ROPE_SPAWN").Truth)??throw new InvalidOperationException("No CeilMouth in audit room");
            world.Write(enemy,"x",96);
            GD.Print("ANIMATION enemy=",enemy.Id," x=",enemy.Scope.Read("x")," y=",enemy.Scope.Read("y"));
            int[] delays=[15,35,60,15,35,60];
            for(int cycle=0;cycle<delays.Length;cycle++)
            {
                world.Write(player,"x",enemy.Scope.Read("x"));world.Write(player,"y",enemy.Scope.Read("y").Number+enemy.Scope.Read("LEN").Number);
                world.Write(player,"speed",0);
                for(int f=0;f<600&&!(enemy.Scope.Read("BINDED").Truth&&enemy.Scope.Read("BindTarget").Int==player.Id);f++){StepFrame();yield return 0;}
                if(!player.Scope.Read("BINDED").Truth||!enemy.Scope.Read("BINDED").Truth||enemy.Scope.Read("BindTarget").Int!=player.Id)throw new InvalidOperationException("CeilMouth audit did not grab player");
                for(int f=0;f<delays[cycle];f++){StepFrame();yield return 0;}Sample("ceilmouth-grab-"+cycle);
                controls.Key(world,13,true);StepFrame();yield return 0;controls.Key(world,13,false);StepFrame();yield return 0;Sample("ceilmouth-reset-"+cycle);
                world.Write(player,"x",enemy.Scope.Read("x").Number+160);
                world.Write(player,"speed",0);
                for(int f=0;f<360;f++){StepFrame();yield return 0;}
                if(player.Scope.Read("BINDED").Truth||enemy.Scope.Read("BINDED").Truth||enemy.Scope.Read("BindTarget").Int!=world.Constants["noone"].Int)throw new InvalidOperationException("CeilMouth did not release after reset");
                GD.Print("ANIMATION enemy-cycle=",cycle," motion=",enemy.Scope.Read("MotionNum")," bound=",enemy.Scope.Read("BINDED")," target=",enemy.Scope.Read("BindTarget"));
            }
            for(int f=0;f<900;f++){StepFrame();yield return 0;}
        }
        else throw new ArgumentException("Unknown animation audit "+mode);
        Sample("complete");
        GD.Print("ANIMATION_OK mode=",mode);
    }

    private void AuditMenu(string mode)=>AuditMenuAsync(mode).GetAwaiter().GetResult();

    private async Task AuditMenuAsync(string mode)
    {
        for(int i=0;i<180;i++)await StepFrameAsync();
        var title=world.Instances.Single(i=>!i.Destroyed&&world.IsObject(i.Object,world.Constants["objTitle"].Int));
        int offset=world.Globals.Read("controllMode").Int==world.Constants["CTRL_KEY"].Int?12:0;
        if(world.Globals.Read("controllMode").Int==world.Constants["CTRL_JOYPAD"].Int)throw new InvalidOperationException("Menu audit requires keyboard configuration");
        async Task Tap(int binding)
        {
            int code=world.Globals.Read("key",[offset+binding]).Int;
            controls.Key(world,code,true);await StepFrameAsync();controls.Key(world,code,false);
            for(int i=0;i<4;i++)await StepFrameAsync();
        }
        void Expect(string field,GmValue value)
        {
            var actual=title.Scope.Read(field);
            if(!actual.Equals(value))throw new InvalidOperationException($"Menu {field}: expected {value}, got {actual}");
        }
        Expect("View",title.Scope.Read("VIEW_TITLE"));
        Expect("Cursor",title.Scope.Read("GAMESTART"));
        await Tap(10);Expect("View",title.Scope.Read("VIEW_GAMESTART"));
        string selection=mode switch{"Playground"=>"PLAYGROUND","TestRoom"=>"TEST","Survival"=>"SURVIVALPLAYGROUND","SurvivalWorld"=>"SURVIVAL","Stage1"=>"CRACKLECRADLE",_=>throw new ArgumentException("Unknown audit menu "+mode)};
        int wanted=title.Scope.Read(selection).Int;
        for(int attempt=0;title.Scope.Read("Cursor").Int!=wanted&&attempt<10;attempt++)await Tap(3);
        Expect("Cursor",wanted);
        await Tap(10);
        if(mode=="Stage1")
        {
            Expect("View",title.Scope.Read("VIEW_GAMESTART_CC"));Expect("Cursor",title.Scope.Read("NEWGAME"));await Tap(10);
        }
        int loading=world.Constants[mode+"_Loading"].Int;
        string destination=mode switch{"TestRoom"=>"EnemyTestRoom","Stage1"=>"Stage1_1",_=>mode};
        if(world.CurrentRoom!=loading&&world.CurrentRoom!=world.Constants[destination].Int)throw new InvalidOperationException("Unexpected menu destination "+world.CurrentRoom);
        GD.Print("AUDIT_MENU mode=",mode," room=",world.CurrentRoom);
    }

    private void Fail(Exception e)
    {
        if(failure is not null)return;
        failure=e.Message;
        GD.PushError(audit?e.Message:e.ToString());
        SetProcess(false);
        if(!audit)
        {
            audio?.Shutdown();
            var dialog=new AcceptDialog{Title="Slack Of Descent - Error",DialogText=e.Message,MinSize=new Vector2I(480,160)};
            dialog.Confirmed+=()=>GetTree().Quit(1);
            dialog.Canceled+=()=>GetTree().Quit(1);
            dialog.CloseRequested+=()=>GetTree().Quit(1);
            AddChild(dialog);dialog.PopupCentered();
        }
    }

    public override void _Process(double delta)
    {
        if(failure is not null){if(audit)GetTree().Quit(1);return;}
        try
        {
            SyncGrenadeBinding();
            if(modalDepth>0&&pendingFrame is null){elapsed=0;frameMonitor.Reset();canvas.QueueRedraw();return;}
            if(pendingFrame is not null)
            {
                if(!pendingFrame.IsCompleted){elapsed=0;frameMonitor.Reset();canvas.QueueRedraw();return;}
                pendingFrame.GetAwaiter().GetResult();pendingFrame=null;elapsed=0;
            }
            var measurement=frameMonitor.Update(Stopwatch.GetElapsedTime(frameClock).TotalSeconds,world.Frame,world.CurrentRoom,world.Engine.Read("room_speed").Number,DisplayServer.GetName()!="headless");
            if(measurement is not null)
            {
                world.Engine.Write("fps",measurement.LogicFps);world.Globals.Write("current_fps",measurement.LogicFps);
                if(fpsLabel is not null)fpsLabel.Text="FPS: "+measurement.LogicFps.ToString("F0");
                world.Performance.Record(measurement,world,()=>graphics.Statistics());
                if(fpsAuditStart>0)fpsAuditSamples.Add(measurement);
            }
            if(fpsAuditStart>0)
            {
                double seconds=Stopwatch.GetElapsedTime(fpsAuditStart).TotalSeconds;
                if(seconds>=12&&!fpsAuditSlow){fpsAuditSlow=true;Engine.MaxFps=12;}
                if(fpsAuditSlow)OS.DelayMsec(90);
                if(seconds>=17)
                {
                    RegressionExpect(fpsAuditSamples.Any(s=>s.LogicFps>25),"FPS label measured stable gameplay");
                    RegressionExpect(fpsAuditSamples.Any(s=>s.LogicFps<25),"FPS measures actual slowdown");
                    RegressionExpect(fpsAuditSamples.Any(s=>s.Dropped),"FPS slowdown triggers logging");
                    RegressionExpect(fpsLabel!.Text=="FPS: "+world.Engine.Read("fps").Number.ToString("F0"),"FPS label follows measured rate");
                    RegressionExpect(world.Performance.LogPath is string log&&File.ReadLines(log).Any(line=>line.Contains("\"type\":\"drop\"",StringComparison.Ordinal)),"real slowdown writes performance log");
                    GD.Print("FPS_AUDIT_OK label=",fpsLabel.Text," log=",world.Performance.LogPath);audio.Shutdown();GetTree().Quit();return;
                }
            }
            if(animationAudit is not null)
            {
                bool next=animationAudit.MoveNext();canvas.QueueRedraw();
                if(!next)
                {
                    animationAudit.Dispose();animationAudit=null;SetProcess(false);GD.Print("AUDIT_OK");
                    if(OS.GetCmdlineUserArgs().Contains("--capture"))Capture();else{audio.Shutdown();GetTree().Quit();}
                }
                return;
            }
            if(liveEnd>0)liveTimes.Add(delta*1000);
            UpdateMouse();
            var step=1.0/Math.Max(1,world.Engine.Read("room_speed").Number);
            elapsed=Math.Min(elapsed+delta,step*2);
            int count=0;
            while(elapsed>=step&&count++<2)
            {
                elapsed-=step;pendingFrame=StepFrameAsync();
                if(!pendingFrame.IsCompleted){elapsed=0;break;}
                pendingFrame.GetAwaiter().GetResult();pendingFrame=null;
            }
            if(world.Ended){audio.Shutdown();GetTree().Quit();}
            QueueRedraw();canvas.QueueRedraw();
            if(liveEnd>0&&world.Frame>=liveEnd)
            {
                liveTimes.Sort();
                GD.Print("LIVE_PERF room=",world.CurrentRoom," logic_fps=",((world.Frame-liveFirstFrame)/Stopwatch.GetElapsedTime(liveStart).TotalSeconds).ToString("F2")," render_fps=",(1000/liveTimes.Average()).ToString("F2")," p95_ms=",liveTimes[(int)((liveTimes.Count-1)*0.95)].ToString("F2")," max_ms=",liveTimes[^1].ToString("F2"));
                SetProcess(false);
                if(OS.GetCmdlineUserArgs().Contains("--capture"))Capture();else{audio.Shutdown();GetTree().Quit();}
                return;
            }
            rendered++;
            if(liveEnd==0&&rendered>=180&&!captured&&OS.GetCmdlineUserArgs().Contains("--capture"))
            {captured=true;Capture();}
        }
        catch(Exception e){Fail(e);QueueRedraw();if(audit){audio.Shutdown();GetTree().Quit(1);}}
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
            if(world.Engine.Read("background_showcolor").Truth)DrawRect(new Rect2(Vector2.Zero,GetViewportRect().Size),GmGraphics.Colour(world.Engine.Read("background_color").Number));
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
            case "show_menu":return Menu(args[0].ToString(),args[1].Int);
            case "get_string":return Prompt("--input-text",args[0].ToString(),args[1].ToString())??args[1];
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
