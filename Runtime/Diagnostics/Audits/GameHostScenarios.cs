using Godot;
using System.Diagnostics;
using System.Text.Json;

namespace SlackOfDescent;

public partial class GameHost
{
    private async void AuditGraphics()
    {
        try{await graphics.Audit();await graphics.AuditEffects();GD.Print("GRAPHICS_OK");audio.Shutdown();GetTree().Quit();}
        catch(Exception e){Fail(e);audio.Shutdown();GetTree().Quit(1);}
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
    private async void Capture()
    {
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        var image=GetViewport().GetTexture().GetImage();
        var path=System.Environment.GetEnvironmentVariable("SLACK_CAPTURE")??"/home/dpapyru/.cache/ccgame-migration/godot.png";
        image.SavePng(path);image.Dispose();GD.Print("CAPTURE ",path);audio.Shutdown();GetTree().Quit(failure is null?0:1);
    }
    private void StepFrame(){var plan=world.FramePlan;world.FramePlan=null;PositionMudWindow();controls.Poll(world);world.Step();controls.EndStep();SyncViewport();world.FramePlan=plan;if(traceRooms)GD.Print("TRACE_ROOM frame=",world.Frame," room=",world.CurrentRoom);}
}
