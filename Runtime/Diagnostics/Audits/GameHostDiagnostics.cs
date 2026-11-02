using Godot;
using System.Diagnostics;
using System.Text.Json;

namespace SlackOfDescent;

public partial class GameHost
{
    private IEnumerator<int>? animationAudit;
    private readonly List<GmFrameSample> fpsAuditSamples=new();
    private bool fpsAuditSlow;
    private long fpsAuditStart;
    private bool traceRooms;
    private readonly List<double> liveTimes=new();
    private long liveFirstFrame;
    private long liveStart;
    private long liveEnd;
    private bool liveWalk;
    private int liveWalkOffset;
    private int liveWalkTick;
    private bool liveWalkRight=true;
    private bool liveWalkJump;
    private bool captured;
    private int rendered;
    private bool StartDiagnostics()
    {
            if(audit&&OS.GetCmdlineUserArgs().Contains("--package-resources-audit"))
            {
                SetProcess(false);
                var context=new GmContext(new GmInstance());
                world.Evaluator.ResetBudget();
                RegressionExpect(world.Evaluator.Run(world.Compile("return \"package-\u65e5\u672c\u8a9e\";"),context).ToString()=="package-\u65e5\u672c\u8a9e","packaged dynamic compiler");
                RegressionExpect(world.Call("file_find_first",["Chara/Doll/Head/Character-Nana_*.gif",16],context).ToString().Length>0,"packaged character selection resources");
                GD.Print("PACKAGE_RUNTIME_OK images=",graphics.AuditResourceImages()," sounds=",audio.AuditResourceSounds()," source=",world.SourceRoot);
                audio.Shutdown();GetTree().Quit();return true;
            }
            if(audit&&OS.GetCmdlineUserArgs().Contains("--graphics-audit")){SetProcess(false);AuditGraphics();return true;}
        return false;
    }
    private bool StartRoomDiagnostics()
    {
            if(audit&&OS.GetCmdlineUserArgs().Contains("--native-jump-audit")){SetProcess(false);AuditNativeJump();return true;}
            if(audit&&OS.GetCmdlineUserArgs().Contains("--native-sniper-audit")){SetProcess(false);AuditNativeSniper();return true;}
            if(audit&&OS.GetCmdlineUserArgs().Contains("--native-map-runtime-audit")){SetProcess(false);AuditNativeMapRuntime();return true;}
            if(audit&&OS.GetCmdlineUserArgs().Contains("--native-integration-audit")){SetProcess(false);AuditNativeIntegration();return true;}
        return false;
    }
    private void RunDiagnostics()
    {
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
                    var context=new GmContext(menuIndex<0?world.Instances.First(i=>world.IsObject(i.Object,world.Constants["objTitle"].Int)):new GmInstance());
                    world.Globals.Write("StartArea",0);
                    world.Call("DataItemReset",[],context);world.Call("DataItemLoad",[],context);
                    world.LoadRoom(int.Parse(commandArgs[roomIndex+1]));
                    if(traceRooms)GD.Print("TRACE_LOADED room=",world.CurrentRoom);
                }
                for(int i=0;i<frames;i++)StepFrame();
                if(traceRooms)GD.Print("TRACE_FINAL room=",world.CurrentRoom);
                if(commandArgs.Contains("--mud-matrix")){SetProcess(false);AuditMudMatrix(commandArgs);return;}
                if(commandArgs.Contains("--mud-window"))
                {
                    mudWindowPlayer=world.Select(world.Constants["objPlayerDoll"].Int,new(new GmInstance())).Single();
                    for(int i=0;i<120;i++)StepFrame();
                    GetWindow().Mode=Window.ModeEnum.Windowed;GetWindow().Show();GetWindow().GrabFocus();
                }
                if(commandArgs.Contains("--mud-profile"))AuditMudProfile();
                ContentAudit(commandArgs);
                if(commandArgs.Contains("--transition-audit")){SetProcess(false);AuditTransitions();return;}
                if(commandArgs.Contains("--controls-ui-audit")){SetProcess(false);AuditInteractiveControls();return;}
                int shoot=Array.IndexOf(commandArgs,"--shoot");
                if(shoot>=0&&shoot+1<commandArgs.Length)AuditShoot(int.Parse(commandArgs[shoot+1]));
                int animation=Array.IndexOf(commandArgs,"--animation-audit");
                if(animation>=0&&animation+1<commandArgs.Length){animationAudit=(commandArgs[animation+1]=="rain"?AuditRain():commandArgs[animation+1]=="route"?AuditRoute(true):commandArgs[animation+1].StartsWith("bind:",StringComparison.Ordinal)?AuditBinding(commandArgs[animation+1][5..],true):AuditAnimation(commandArgs[animation+1])).GetEnumerator();return;}
                GD.Print("AUDIT room=",world.CurrentRoom," frames=",world.Frame," instances=",world.Instances.Count);
                if(world.Performance.Enabled)GD.Print("PERF_CONTENT tiles=",world.Tiles.Count," objects=",string.Join(" ",world.Instances.GroupBy(i=>i.Object).OrderByDescending(g=>g.Count()).Take(12).Select(g=>$"{g.Key}:{g.Count()}")));
                int updateDraw=Array.IndexOf(commandArgs,"--update-draw-benchmark");
                if(updateDraw>=0&&updateDraw+1<commandArgs.Length){AuditUpdateDraw(int.Parse(commandArgs[updateDraw+1]),updateDraw+2<commandArgs.Length?int.Parse(commandArgs[updateDraw+2]):1);}
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
                if(live>=0&&live+1<commandArgs.Length)
                {
                    if(commandArgs.Contains("--live-walk"))
                    {
                        liveWalk=true;
                        liveWalkOffset=world.Globals.ReadOr("controllMode").Int==world.Constants["CTRL_KEY"].Int?12:0;
                    }
                    liveFirstFrame=world.Frame;liveEnd=world.Frame+int.Parse(commandArgs[live+1]);liveStart=Stopwatch.GetTimestamp();return;
                }
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
    private bool ProcessDiagnostics(double delta)
    {
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
                    GD.Print("FPS_AUDIT_OK label=",fpsLabel.Text," log=",world.Performance.LogPath);audio.Shutdown();GetTree().Quit();return true;
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
                return true;
            }
            if(liveWalk&&liveEnd>0&&world.Frame<liveEnd)
            {
                liveWalkTick++;
                bool right=liveWalkTick/45%2==0;
                if(right!=liveWalkRight)
                {
                    liveWalkRight=right;
                    _Input(new InputEventKey{Keycode=GodotKey(world.Globals.Read("key",[liveWalkOffset]).Int),Pressed=!right});
                    _Input(new InputEventKey{Keycode=GodotKey(world.Globals.Read("key",[liveWalkOffset+1]).Int),Pressed=right});
                }
                bool jump=liveWalkTick%20>=15;
                if(jump!=liveWalkJump)
                {
                    liveWalkJump=jump;
                    _Input(new InputEventKey{Keycode=GodotKey(world.Globals.Read("key",[liveWalkOffset+5]).Int),Pressed=jump});
                }

            }
            if(liveEnd>0)liveTimes.Add(delta*1000);
        return false;
    }
    private void FinishDiagnostics()
    {
            if(liveEnd>0&&world.Frame>=liveEnd)
            {
                if(liveWalk)
                {
                    _Input(new InputEventKey{Keycode=GodotKey(world.Globals.Read("key",[liveWalkOffset]).Int),Pressed=false});
                    _Input(new InputEventKey{Keycode=GodotKey(world.Globals.Read("key",[liveWalkOffset+1]).Int),Pressed=false});
                    _Input(new InputEventKey{Keycode=GodotKey(world.Globals.Read("key",[liveWalkOffset+5]).Int),Pressed=false});
                    liveWalk=false;
                }
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
}
