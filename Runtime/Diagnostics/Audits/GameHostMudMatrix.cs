using Godot;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public partial class GameHost
{
    private sealed record MudMatrixOptions(int WarmupFrames,int SampleFrames,int Repeats,int Seed);

    private sealed class MudMatrixScene : IDisposable
    {
        public readonly Node Owner;
        public readonly GmWorld World;
        public readonly GmGraphics Graphics;
        public readonly GmCanvas Canvas;
        public readonly GmInput Input = new();
        private readonly GmAudio audio;
        private readonly GmNativeBridge bridge;
        private readonly Window window;
        public readonly List<int> RootIds = new();
        public readonly HashSet<int> RoomInstanceIds = new();
        public string FixturePath = "";
        public double LayoutStepX,LayoutStepY;
        private NativeMapService? maps;
        public MudMatrixScene(GameHost host,string root,string source,string user,int seed)
        {
            window=host.GetWindow();
            Owner=new Node();host.AddChild(Owner);
            World=new(root,source,user);World.Random.Seed=seed;
            Graphics=new(Owner,World);audio=new(Owner,World);bridge=new(World,new ContentRegistry());
            Canvas=new GmCanvas();Owner.AddChild(Canvas);Canvas.Paint=Graphics.Render;
            Input.LoadBindings(World);
            World.Platform=(name,args,context)=>Input.Call(name,args,World)??audio.Call(name,args)??Graphics.Call(name,args,context)??name switch
            {
                "show_message" or "show_message_ext" or "show_question"=>1,
                "show_menu"=>args.Length>1?args[1]:0,
                "get_string"=>"Nana",
                "message_background" or "message_button" or "message_mouse_color" or "message_text_font" or "message_button_font" or "io_handle" or "texture_set_interpolation"=>0,
                "window_get_width"=>640,"window_get_height"=>480,"window_handle"=>1,
                _=>(GmValue?)null
            };
            World.Wait=(name,args,context)=>ValueTask.FromResult(World.Platform(name,args,context)??0);
            World.InputEventsAsync=()=>Input.DispatchAsync(World);
            World.Redraw=Graphics.Build;
        }
        public async Task InitializeAsync(string kind,string mode,int rootCount)
        {
            GD.Print("MUD_MATRIX_SETUP init window=",window.Mode);
            await World.LoadRoomAsync(World.Manifest.GetProperty("roomOrder")[0].GetInt32());
            for(int n=0;n<240&&(World.CurrentRoom!=World.Constants["Title"].Int||World.IsLoadingRoom||World.PendingRoom is not null);n++)await World.StepAsync();
            var context=new GmContext(World.Instances.FirstOrDefault(i=>!i.Destroyed)??new GmInstance());
            GD.Print("MUD_MATRIX_SETUP data room=",World.CurrentRoom," window=",window.Mode);
            World.Evaluator.ResetBudget();World.Globals.Write("StartArea",0);
            await World.Evaluator.CallAsync("DataItemReset",[],context);await World.Evaluator.CallAsync("DataItemLoad",[],context);
            InstallFixture(kind,mode,rootCount);
            await World.LoadRoomAsync(World.Constants["Playground_Loading"].Int);
            GD.Print("MUD_MATRIX_SETUP loading room=",World.CurrentRoom," window=",window.Mode);
            for(int n=0;World.CurrentRoom!=World.Constants["Playground"].Int||World.IsLoadingRoom||World.PendingRoom is not null;n++)
            {
                if(n>=600)throw new InvalidOperationException("Playground load timeout");
                await World.StepAsync();
            }
            GD.Print("MUD_MATRIX_SETUP ready room=",World.CurrentRoom," instances=",World.Instances.Count," window=",window.Mode);
        }
        private void InstallFixture(string kind,string mode,int rootCount)
        {
            maps=new(World,new ContentRegistry());
            int room=World.Constants["Playground"].Int;
            var document=maps.ImportOriginal(room).Document;
            var required=new[]{"objController","objDisplay","objSetRespawn","objSwampReload"}.Select(name=>World.Constants[name].Int).ToHashSet();
            var retained=document.Instances.OfType<JsonObject>().Where(item=>required.Contains(NativeMapService.Int(item,"object"))).Select(item=>item.DeepClone()).ToArray();
            document.Instances.Clear();document.Tiles.Clear();
            document.Room["height"]=8192;
            double playerX=mode=="contact"?2336:2080,playerY=1384;
            foreach(var item in retained)
            {
                item!["x"]=playerX;item["y"]=playerY;document.Instances.Add(item);
            }
            int rootObject=World.Constants[kind=="quicksand"?"objQuicksand":"objMud"].Int;
            var probe=World.Create(rootObject,0,0,run:false);
            var bounds=World.Bounds(probe);
            LayoutStepX=bounds.Right-bounds.Left+1;LayoutStepY=bounds.Bottom-bounds.Top+129;
            int x=0,y=0,dx=1,dy=0,leg=1,remaining=1,turns=0;
            for(int index=0;index<rootCount;index++)
            {
                var item=maps.AddEntity(document,rootObject,2320+x*LayoutStepX,1440+y*LayoutStepY);
                RootIds.Add(NativeMapService.Int(item,"id"));
                x+=dx;y+=dy;
                if(--remaining==0){(dx,dy)=(-dy,dx);if(++turns%2==0)leg++;remaining=leg;}
            }
            for(int floorX=1920;floorX<2816;floorX+=32)maps.AddEntity(document,World.Constants["objBlock"].Int,floorX,1448);
            Directory.CreateDirectory(World.UserRoot);
            FixturePath=Path.Combine(World.UserRoot,"fixture.map.json");
            RoomInstanceIds.UnionWith(document.Instances.OfType<JsonObject>().Select(item=>NativeMapService.Int(item,"id")));
            maps.Save(document,FixturePath);
            maps.InstallForPlay(maps.Load(FixturePath).Document,room);
        }
        public void Dispose()
        {
            Canvas.Paint=null;World.Redraw=null;World.StepBeginObserver=null;
            Input.Clear(World);bridge.Dispose();audio.Shutdown();Graphics.ReleaseCharacterPreview();maps?.RestoreAfterPlaytest();Owner.QueueFree();
        }
    }

    private sealed class MudMatrixSample
    {
        public readonly List<double> LogicMs = new();
        public readonly List<double> BuildMs = new();
        public readonly List<double> PresentMs = new();
        public readonly List<long> AllocatedBytes = new();
        public readonly List<double> CpuMs = new();
        public readonly List<double> MainThreadCpuMs = new();
        public readonly List<double> StepMs = new();
        public readonly List<double> RenderIntervalsMs = new();
        public readonly List<int> StepsPerProcess = new();
        public readonly List<int> Contacts = new();
        public readonly List<int> ContactFlags = new();
        public readonly List<double> PlayerX = new();
        public readonly List<double> PlayerY = new();
        public readonly List<double> FootDepth = new();
        public readonly List<double> ViewX = new();
        public readonly List<double> ViewY = new();
        public readonly List<int> FluidEffects = new();
        public readonly List<int> BubbleEffects = new();
        public readonly List<int> ActiveRoots = new();
        public readonly List<int> VisibleRoots = new();
        public readonly List<int> RootsInCamera = new();
        public readonly List<int> ObservedRoots = new();
        public readonly List<int> ObservedActiveRoots = new();
        public readonly List<int> CommandCounts = new();
        public readonly List<int> DerivedEntities = new();
        public readonly List<int> Particles = new();
        public int RootEntities;
        public int InitialInstances;
        public int FinalInstances;
        public long CollisionCandidates;
        public long CollisionExactTests;
        public bool PresentationAvailable;
        public readonly HashSet<string> InvalidReasons = new(StringComparer.Ordinal);
    }

    private async void AuditMudMatrix(string[] args)
    {
        try
        {
        var options = new MudMatrixOptions(
            Option(args,"--matrix-warmup",450),
            Option(args,"--matrix-samples",1800),
            Option(args,"--matrix-repeats",3),
            Option(args,"--matrix-seed",0));
        if(options.WarmupFrames<0||options.SampleFrames<=0||options.Repeats<=0)throw new InvalidOperationException("Invalid mud matrix duration");
        string? condition=Array.IndexOf(args,"--matrix-condition") is int conditionIndex&&conditionIndex>=0&&conditionIndex+1<args.Length?args[conditionIndex+1]:null;
        int caseLimit=Math.Min((condition is null?21:1)*options.Repeats,Option(args,"--matrix-limit",21*options.Repeats));
        var cases = new List<object>();
        string output=System.Environment.GetEnvironmentVariable("SLACK_MUD_MATRIX_REPORT")??"/tmp/slack-mud-matrix.json";
        void SaveReport(bool complete)
        {
            var report=new
            {
                kind="mud-matrix",measurementMode="asynchronous-fixed-frame-runtime-benchmark",complete,
                limitations=new[]{"Logical steps yield to Godot rendering; they are not paced by a 30 Hz wall-clock accumulator, and catch-up is not measured.","Canvas draws and framesDrawn confirm rendering work; neither proves monitor scanout nor measures GPU time.","Each case installs a generated fixture.map.json before room load, retaining the original controller, display, respawn and swamp-reload entry points with a fixed support floor; this is a controlled scene, not the original Playground workload.","Player position and velocity are initialized once; subsequent movement, sinking and floor contact run through gameplay. Per-root place_meeting counts and touchingQS flags are reported separately; shallow depth uses the gameplay y+59 foot probe.","Visible flags, collision bounds intersecting the fixed camera, and actual canvas command counts are separate observations; individual root draw commands are not attributed.","Moving mode alternates fixed player direction keys every 15 logical ticks; roots remain fixed. Contact loss is observed without repositioning.","Native GmParticles counts and legacy fluid/bubble effect entities are distinct. Native particle update timing is not separated; zero-native-particle cases cannot measure native particle cost.","This fixture does not replace the full 15-second warmup and 60-second wall-clock sampling matrix."},
                performanceAcceptanceComplete=false,
                seed=options.Seed,warmupFrames=options.WarmupFrames,sampleFrames=options.SampleFrames,
                repeats=options.Repeats,conditions=condition is null?21:1,selectedCondition=condition,samples=cases.Count,plannedSamples=caseLimit,
                userRoot=world.UserRoot,
                inputSequence="moving: alternate right/left every 15 logical ticks; others: none",
                presentationAvailable=DisplayServer.GetName()!="headless",
                cases
            };
            string temporary=output+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            File.Move(temporary,output,true);
        }
        SaveReport(false);
        GetWindow().Mode=Window.ModeEnum.Windowed;GetWindow().Show();GetWindow().GrabFocus();
        canvas.Visible=false;
        world.Redraw=null;audio.Shutdown();graphics.Shutdown();
        foreach(string kind in new[]{"none","mud","quicksand"})
        foreach(string mode in new[]{"static","contact","moving"})
        {
            foreach(int roots in kind=="none"?new[]{0}:new[]{1,20,50})
            {
                if(condition is not null&&condition!=$"{kind}:{mode}:{roots}")continue;
                for(int repeat=0;repeat<options.Repeats;repeat++)
                {
                    if(cases.Count>=caseLimit)break;
                    GD.Print("MUD_MATRIX_START ",cases.Count+1,"/",21*options.Repeats," kind=",kind," mode=",mode," roots=",roots," repeat=",repeat);
                    try{cases.Add(await RunMudMatrixCase(kind,mode,roots,repeat,options));}
                    catch(Exception e)
                    {
                        cases.Add(new{kind,mode,rootEntities=roots,repeat,valid=false,invalidReason="setup:"+e.GetType().Name+":"+e.Message,invalidReasons=new[]{"setup"},sampleStatus="invalid",plannedFrames=options.SampleFrames,measuredFrames=0});
                        if(e is TimeoutException){SaveReport(false);throw;}
                    }
                    finally{world.StepBeginObserver=null;}
                    SaveReport(false);
                    GD.Print("MUD_MATRIX_PROGRESS ",cases.Count,"/",21*options.Repeats," report=",output);
                    await MudMatrixSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                    GC.Collect();GC.WaitForPendingFinalizers();
                }
            }
        }
        SaveReport(true);
        GD.Print("MUD_MATRIX_OK ",output," cases=",cases.Count);
        audio.Shutdown();GetTree().Quit();
        }
        catch(Exception e){Fail(e);GD.Print("MUD_MATRIX_FAILED ",e);GetTree().Quit(1);}
    }

    private async Task<object> RunMudMatrixCase(string kind,string mode,int rootCount,int repeat,MudMatrixOptions options)
    {
        await MudMatrixSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        string user=Path.Combine(this.world.UserRoot,$"case-{kind}-{mode}-{rootCount}-{repeat}");
        using var scene=new MudMatrixScene(this,this.world.Root,this.world.SourceRoot,user,options.Seed);
        await scene.InitializeAsync(kind,mode,rootCount).WaitAsync(TimeSpan.FromSeconds(30));
        var world=scene.World;var graphics=scene.Graphics;var canvas=scene.Canvas;
        world.Redraw=null;
        world.InputEvents=null;
        var player=world.Select(world.Constants["objPlayerDoll"].Int,new(new GmInstance())).FirstOrDefault();
        if(player is null)throw new InvalidOperationException("Mud matrix room has no player");
        int rootObject=kind switch{"mud"=>world.Constants["objMud"].Int,"quicksand"=>world.Constants["objQuicksand"].Int,_=>-1};
        var roots=world.Instances.Where(instance=>scene.RootIds.Contains(instance.Id)&&!instance.Destroyed).ToList();
        int initialInstances=world.Instances.Count;
        double layoutStepX=scene.LayoutStepX,layoutStepY=scene.LayoutStepY;
        world.Write(player,"y",player.Scope.ReadOr("y").Number+1447-world.Bounds(player).Bottom);
        world.Write(player,"hspeed",0);world.Write(player,"vspeed",0);
        int inputTick=0;
        int keyOffset=world.Globals.ReadOr("controllMode").Equals(world.Constants["CTRL_KEY"])?12:0;
        async Task Step()
        {
            bool moveRight=mode=="moving"&&(inputTick++/15)%2==0;
            if(mode=="moving")
            {
                scene.Input.Key(world,world.Globals.Read("key",[keyOffset]).Int,!moveRight);
                scene.Input.Key(world,world.Globals.Read("key",[keyOffset+1]).Int,moveRight);
            }
            scene.Input.Poll(world);
            try{await world.StepAsync();}finally{scene.Input.EndStep();}
        }
        var initializedRootStates=roots.Select(root=>new{id=root.Id,surfaceId=root.Scope.ReadOr("qsTEnt").Int,alarm=root.Scope.ReadOr("alarm",0).Number}).ToArray();
        int missingInitialSurfaces=initializedRootStates.Count(root=>!world.Instances.Any(instance=>instance.Id==root.surfaceId&&!instance.Destroyed));
        int mudObject=world.Constants["objMud"].Int,quicksandObject=world.Constants["objQuicksand"].Int;
        bool IsRoot(GmInstance instance)=>!instance.Destroyed&&(world.IsObject(instance.Object,mudObject)||world.IsObject(instance.Object,quicksandObject));
        bool overlaps=false;
        bool InCamera(GmInstance instance)
        {
            var bounds=world.Bounds(instance);
            return bounds.Right>=2000&&bounds.Left<=2640&&bounds.Bottom>=1240&&bounds.Top<=1720;
        }
        void CheckLayout()
        {
            for(int a=0;a<roots.Count;a++)for(int b=a+1;b<roots.Count;b++)
            {
                var first=world.Bounds(roots[a]);var second=world.Bounds(roots[b]);
                if(first.Left<=second.Right&&first.Right>=second.Left&&first.Top<=second.Bottom&&first.Bottom>=second.Top)overlaps=true;
            }
        }
        CheckLayout();
        double currentBuildMs=0;
        void FixCamera()
        {
            world.Engine.Write("view_enabled",true);world.Engine.Write("view_visible",true,0);
            world.Engine.Write("view_xview",2000,0);world.Engine.Write("view_yview",1240,0);
            world.Engine.Write("view_wview",640,0);world.Engine.Write("view_hview",480,0);
        }
        world.StepBeginObserver=FixCamera;
        world.Redraw=()=>
        {
            FixCamera();
            long started=Stopwatch.GetTimestamp();graphics.Build();
            currentBuildMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            canvas.QueueRedraw();
        };
        if(DisplayServer.GetName()!="headless")
        {
            GetWindow().Mode=Window.ModeEnum.Windowed;GetWindow().AlwaysOnTop=true;GetWindow().Show();GetWindow().GrabFocus();
            for(int frame=0;frame<5;frame++)await MudMatrixSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            GD.Print("MUD_MATRIX_WINDOW mode=",GetWindow().Mode," canDraw=",DisplayServer.WindowCanDraw());
        }
        for(int frame=0;frame<12+options.WarmupFrames;frame++)
        {
            await Step();
            await MudMatrixSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        }
        GD.Print("MUD_MATRIX_SAMPLING kind=",kind," mode=",mode," roots=",rootCount);
        var sample=new MudMatrixSample{RootEntities=rootCount,InitialInstances=initialInstances,PresentationAvailable=DisplayServer.GetName()!="headless"};
        if(!sample.PresentationAvailable)sample.InvalidReasons.Add("no-present");
        long candidateStart=world.CollisionCandidateVisits,exactStart=world.CollisionExactTests;
        long allocatedStart=GC.GetAllocatedBytesForCurrentThread();
        int[] gcStart=Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();
        double cpuStart=Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        double mainStart=world.Performance.MainThreadCpuNowMs();
        long previousAllocated=allocatedStart;
        double previousCpu=cpuStart;
        long wallStart=Stopwatch.GetTimestamp(),renderedStart=Engine.GetFramesDrawn();
        long canvasDraws=0;
        long framePostDraws=0;
        long lastPostDraw=0;
        var frameIntervals=new List<double>();
        void PostDraw()
        {
            framePostDraws++;
            long now=Stopwatch.GetTimestamp();
            if(lastPostDraw>0)frameIntervals.Add(Stopwatch.GetElapsedTime(lastPostDraw,now).TotalMilliseconds);
            lastPostDraw=now;
        }
        RenderingServer.FramePostDraw+=PostDraw;
        long lastDraw=0;
        var previousPaint=canvas.Paint;
        canvas.Paint=target=>
        {
            previousPaint?.Invoke(target);canvasDraws++;
            long now=Stopwatch.GetTimestamp();
            if(lastDraw>0)sample.RenderIntervalsMs.Add(Stopwatch.GetElapsedTime(lastDraw,now).TotalMilliseconds);
            lastDraw=now;
        };
        try
        {
        for(int frame=0;frame<options.SampleFrames;frame++)
        {
            long logicStart=Stopwatch.GetTimestamp();
            currentBuildMs=0;
            await Step();
            double stepMs=Stopwatch.GetElapsedTime(logicStart).TotalMilliseconds;
            sample.StepMs.Add(stepMs);sample.LogicMs.Add(Math.Max(0,stepMs-currentBuildMs));
            sample.BuildMs.Add(currentBuildMs);
            sample.CommandCounts.Add(graphics.MudMatrixCommandCount);
            long presentStart=Stopwatch.GetTimestamp();
            canvas.QueueRedraw();
            await MudMatrixSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            sample.PresentMs.Add(Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds);
            long allocated=GC.GetAllocatedBytesForCurrentThread();
            sample.AllocatedBytes.Add(allocated-previousAllocated);previousAllocated=allocated;
            double cpu=Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            sample.CpuMs.Add(Math.Max(0,cpu-previousCpu));previousCpu=cpu;
            sample.ContactFlags.Add(!player.Destroyed&&player.Scope.ReadOr("touchingQS").Truth?1:0);
            var touching=player.Destroyed?[]:roots.Where(root=>!root.Destroyed&&root.Active&&world.Bounds(player).Overlaps(world.Bounds(root))&&world.Call("place_meeting",[player.Scope.ReadOr("x"),player.Scope.ReadOr("y"),root.Id],new(player)).Truth).ToArray();
            sample.Contacts.Add(touching.Length);
            foreach(var root in touching)sample.FootDepth.Add(player.Scope.ReadOr("y").Number+59-root.Scope.ReadOr("y").Number);
            sample.ViewX.Add(world.Engine.Read("view_xview",0).Number);sample.ViewY.Add(world.Engine.Read("view_yview",0).Number);
            sample.PlayerX.Add(player.Scope.ReadOr("x").Number);sample.PlayerY.Add(player.Scope.ReadOr("y").Number);

            sample.ActiveRoots.Add(roots.Count(root=>!root.Destroyed&&root.Active));
            sample.VisibleRoots.Add(roots.Count(root=>!root.Destroyed&&root.Active&&root.Scope.ReadOr("visible").Truth));
            sample.RootsInCamera.Add(roots.Count(root=>!root.Destroyed&&root.Active&&root.Scope.ReadOr("visible").Truth&&InCamera(root)));
            sample.ObservedRoots.Add(world.Instances.Count(IsRoot));
            sample.ObservedActiveRoots.Add(world.Instances.Count(root=>IsRoot(root)&&root.Active));
            sample.DerivedEntities.Add(world.Instances.Count(i=>!i.Destroyed&&!scene.RoomInstanceIds.Contains(i.Id)));
            CheckLayout();
            sample.Particles.Add(world.Particles.AliveCount);
            sample.FluidEffects.Add(world.Instances.Count(instance=>!instance.Destroyed&&world.IsObject(instance.Object,world.Constants["objFluid"].Int)));
            sample.BubbleEffects.Add(world.Instances.Count(instance=>!instance.Destroyed&&world.IsObject(instance.Object,world.Constants["objWaterBubble"].Int)));
            if(GetWindow().Mode==Window.ModeEnum.Minimized)sample.InvalidReasons.Add("window-minimized");
            if(sample.PresentationAvailable&&!DisplayServer.WindowCanDraw())sample.InvalidReasons.Add("window-unavailable");
            if(player.Destroyed){sample.InvalidReasons.Add("player-dead");break;}
            if(world.CurrentRoom!=world.Constants["Playground"].Int){sample.InvalidReasons.Add("room-switch");break;}
        }
        }
        finally{RenderingServer.FramePostDraw-=PostDraw;canvas.Paint=previousPaint;world.StepBeginObserver=null;}
        double wallMs=Stopwatch.GetElapsedTime(wallStart).TotalMilliseconds;
        long renderedFrames=Engine.GetFramesDrawn()-renderedStart;
        double processCpuMs=Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds-cpuStart;
        sample.FinalInstances=world.Instances.Count;
        sample.CollisionCandidates=world.CollisionCandidateVisits-candidateStart;
        sample.CollisionExactTests=world.CollisionExactTests-exactStart;
        double mainEnd=world.Performance.MainThreadCpuNowMs();
        if(mainStart>=0&&mainEnd>=mainStart)sample.MainThreadCpuMs.Add(mainEnd-mainStart);
        int[] gcEnd=Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();
        if(canvasDraws==0||renderedFrames==0)sample.InvalidReasons.Add("no-actual-render");
        if(roots.Count(root=>!root.Destroyed)!=rootCount)sample.InvalidReasons.Add("created-root-count-mismatch");
        if(sample.ObservedRoots.Any(count=>count!=rootCount))sample.InvalidReasons.Add("world-root-count-mismatch");
        if(overlaps)sample.InvalidReasons.Add("overlapping-created-roots");
        if(mode=="contact"&&rootCount>0&&sample.Contacts.Any(c=>c==0))sample.InvalidReasons.Add("contact-not-sustained");
        if(mode=="static"&&sample.Contacts.Any(c=>c>0))sample.InvalidReasons.Add("unexpected-contact");
        if(mode=="contact"&&sample.FootDepth.Any(depth=>depth<0||depth>=30))sample.InvalidReasons.Add("not-shallow-contact");
        if(mode=="moving"&&sample.PlayerX.Max()-sample.PlayerX.Min()<1)sample.InvalidReasons.Add("player-did-not-move");
        int brokenNeighbors=roots.Count(root=>new[]{("leftSOS",-32), ("rightSOS",32)}.Any(side=>
        {
            var expected=roots.FirstOrDefault(other=>Math.Abs(other.Scope.ReadOr("x").Number-root.Scope.ReadOr("x").Number-side.Item2)<.01&&Math.Abs(other.Scope.ReadOr("y").Number-root.Scope.ReadOr("y").Number)<.01);
            return expected is not null&&root.Scope.ReadOr(side.Item1).Int!=expected.Id;
        }));
        if(brokenNeighbors>0)sample.InvalidReasons.Add("adjacency-not-initialized");
        int initializedSurfaces=roots.Count(root=>world.Instances.Any(instance=>instance.Id==root.Scope.ReadOr("qsTEnt").Int&&!instance.Destroyed));
        if(missingInitialSurfaces>0)sample.InvalidReasons.Add("root-create-not-initialized");
        bool controllerActive=world.Instances.Any(instance=>!instance.Destroyed&&instance.Active&&world.IsObject(instance.Object,world.Constants["objController"].Int));
        if(!controllerActive)sample.InvalidReasons.Add("controller-inactive");
        world.StepBeginObserver=null;
        return new
        {
            kind,mode,rootEntities=rootCount,repeat,room=world.CurrentRoom,roomName=world.Rooms[world.CurrentRoom].GetProperty("name").GetString(),
            instances=new{initial=sample.InitialInstances,final=sample.FinalInstances,derivedFromRoomCreates=Summary(sample.DerivedEntities),controllerActive},
            isolation=new{world="fresh per case",userRoot=user,resources="disposed after case",enabledMods=0,fixtureMap=scene.FixturePath},
            layout=new{stepX=layoutStepX,stepY=layoutStepY,nonOverlapping=!overlaps,brokenNeighbors,neighborExpectation="32px adjacent roots initialized by original objSwampReload alarm",initializedSurfaces,missingInitialSurfaces,initializedRootStates,finalRootStates=roots.Select(root=>new{id=root.Id,x=root.Scope.ReadOr("x").Number,y=root.Scope.ReadOr("y").Number,active=root.Active,left=root.Scope.ReadOr("leftSOS").Int,right=root.Scope.ReadOr("rightSOS").Int,alarm=root.Scope.ReadOr("alarm",0).Number,surfaceId=root.Scope.ReadOr("qsTEnt").Int}).ToArray(),settleTicks=12},
            camera=new{x=2000,y=1240,width=640,height=480,observedX=Range(sample.ViewX),observedY=Range(sample.ViewY)},
            player=new{resource=world.Globals.ReadOr("Chara").ToString(),x=Range(sample.PlayerX),y=Range(sample.PlayerY),footDepth=Range(sample.FootDepth),floorTop=1448,input=mode=="moving"?"alternate right/left every 15 logical ticks":"none",positionControl="once before settling"},
            activeRoots=Summary(sample.ActiveRoots),visibleRootFlags=Summary(sample.VisibleRoots),rootsIntersectingCameraByCollisionBounds=Summary(sample.RootsInCamera),observedWorldRoots=Summary(sample.ObservedRoots),observedWorldActiveRoots=Summary(sample.ObservedActiveRoots),drawCommands=Summary(sample.CommandCounts),contacts=Summary(sample.Contacts),touchingQsFlags=Summary(sample.ContactFlags),nativeParticles=Summary(sample.Particles),effectEntities=new{fluid=Summary(sample.FluidEffects),waterBubble=Summary(sample.BubbleEffects)},
            collision=new{candidateVisits=sample.CollisionCandidates,exactTests=sample.CollisionExactTests,scope="GmWorld collision-event candidate and exact-test counters; place_meeting observations do not increment these counters"},
            logic=Timing(sample.LogicMs),build=Timing(sample.BuildMs),stepIncludingBuild=Timing(sample.StepMs),frameWait=Timing(sample.PresentMs),renderInterval=Timing(frameIntervals),canvasPaintInterval=Timing(sample.RenderIntervalsMs),
            allocation=Timing(sample.AllocatedBytes),
            gc=new{collections=gcEnd.Select((v,i)=>v-gcStart[i]).ToArray()},
            cpu=Timing(sample.CpuMs),
            mainThreadCpu=sample.MainThreadCpuMs.Count==0?null:Timing(sample.MainThreadCpuMs),
            cpuUsage=new{wallMs,processCpuMs,processPercent=wallMs>0?100*processCpuMs/wallMs:0,mainThreadCpuMs=sample.MainThreadCpuMs.Count==0?(double?)null:sample.MainThreadCpuMs[0],mainThreadPercent=sample.MainThreadCpuMs.Count==0||wallMs<=0?(double?)null:100*sample.MainThreadCpuMs[0]/wallMs,scale="one fully utilized CPU core = 100 percent"},
            rendering=new{attempts=sample.PresentMs.Count,canvasDraws,renderedFrames,framePostDraws,canvasVisible=canvas.IsVisibleInTree(),windowVisible=GetWindow().Visible,windowCanDraw=DisplayServer.WindowCanDraw(),displayDriver=DisplayServer.GetName(),windowMode=GetWindow().Mode.ToString()},
            valid=sample.InvalidReasons.Count==0,
            invalidReason=sample.InvalidReasons.Count==0?null:string.Join(",",sample.InvalidReasons),
            invalidReasons=sample.InvalidReasons.Order().ToArray(),
            plannedFrames=options.SampleFrames,measuredFrames=sample.LogicMs.Count,
            sampleStatus=sample.InvalidReasons.Count==0?"measured":"invalid",
            presentationAvailable=sample.PresentationAvailable
        };
    }

    private async Task MudMatrixSignal(GodotObject source,StringName signal)
    {
        var completion=new TaskCompletionSource();
        var awaiting=ToSignal(source,signal);
        awaiting.OnCompleted(()=>completion.TrySetResult());
        try{await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));}
        catch(TimeoutException){throw new TimeoutException("Waiting for "+signal);}
    }

    private static int Option(string[] args,string name,int fallback)
    {
        int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length&&int.TryParse(args[index+1],out var value)?value:fallback;
    }
    private static object Summary(List<int> values)
    {
        if(values.Count==0)return new{count=0,mean=0.0,p50=0,p95=0,p99=0,max=0};
        var sorted=values.Order().ToArray();int At(double p)=>sorted[Math.Clamp((int)Math.Ceiling((sorted.Length-1)*p),0,sorted.Length-1)];
        return new{count=values.Count,mean=values.Average(),p50=At(.5),p95=At(.95),p99=At(.99),max=sorted[^1]};
    }
    private static object Range(List<double> values)=>new{count=values.Count,min=values.Count==0?(double?)null:values.Min(),max=values.Count==0?(double?)null:values.Max(),first=values.Count==0?(double?)null:values[0],last=values.Count==0?(double?)null:values[^1]};
    private static object Timing(List<double> values)
    {
        if(values.Count==0)return new{count=0,meanMs=0.0,p50Ms=0.0,p95Ms=0.0,p99Ms=0.0,maxMs=0.0};
        var sorted=values.Order().ToArray();double At(double p)=>sorted[Math.Clamp((int)Math.Ceiling((sorted.Length-1)*p),0,sorted.Length-1)];
        return new{count=values.Count,meanMs=values.Average(),p50Ms=At(.5),p95Ms=At(.95),p99Ms=At(.99),maxMs=sorted[^1]};
    }
    private static object Timing(List<long> values)
    {
        if(values.Count==0)return new{count=0,meanBytes=0.0,p50Bytes=0L,p95Bytes=0L,p99Bytes=0L,maxBytes=0L,totalBytes=0L};
        var sorted=values.Order().ToArray();long At(double p)=>sorted[Math.Clamp((int)Math.Ceiling((sorted.Length-1)*p),0,sorted.Length-1)];
        return new{count=values.Count,meanBytes=values.Average(),p50Bytes=At(.5),p95Bytes=At(.95),p99Bytes=At(.99),maxBytes=sorted[^1],totalBytes=values.Sum()};
    }
}

public sealed partial class GmGraphics
{
    internal int MudMatrixCommandCount=>main.Count;
}
