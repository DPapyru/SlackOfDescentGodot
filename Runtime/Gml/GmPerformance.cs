using System.Diagnostics;
using System.Text.Json;

namespace SlackOfDescent;

public sealed class GmPerformance
{
    public bool Enabled{get;private set;}=Environment.GetEnvironmentVariable("SLACK_PERF")=="1";
    public readonly bool ScriptsEnabled=Environment.GetEnvironmentVariable("SLACK_PERF_SCRIPTS")=="1";
    private readonly Dictionary<string,(double Total,double Max,int Count)> samples=new();
    private readonly Dictionary<string,(double Total,double Max,int Count)> cadencePhases=new();
    private readonly Dictionary<string,(double Total,double Max,int Count)> processPhases=new();
    private readonly Dictionary<string,(double Total,double Max,int Count)> drawPhases=new();
    private readonly Dictionary<string,List<double>> drawPhaseSamples=new();
    private readonly Dictionary<string,long> drawPending=new();
    private long drawFrames,lastSnapshotRebuilds=-1,lastCandidateVisits=-1;
    private readonly Dictionary<string,(double Total,int Count)> scripts=new();
    public string? LogPath{get;private set;}
    private bool logFailed;
    private readonly Dictionary<string,(double Total,double Max,int Count)> recentSamples=new();
    private readonly List<double> processTimes=new(),renderIntervals=new();
    private long processSteps;
    private int catchUpProcesses,maxSteps;
    private long lastRender;
    private long lastBuildCount,lastPublished,lastRendered,lastCoalesced,lastSkipped;
    private readonly List<double> presentDeltas=new();
    private long presentZero,presentOne,presentMulti,presentPrimitives,presentDrawCalls;
    private double presentFrameSetupMs;
    public static int PresentBucket(long drawnDelta)=>drawnDelta<=0?0:(drawnDelta==1?1:2);
    private readonly List<double> processGaps=new(),processCpuInside=new();
    private long lastProcessEndTicks;
    private readonly List<double> processWall=new(),drawLatency=new(),idleWall=new();
    private long lastPostDrawTicks,pendingExitTicks;
    public static bool WallPartitionComplete(double segmentsMs,double periodMs)=>segmentsMs>0&&Math.Abs(segmentsMs-periodMs)<=Math.Max(1,periodMs*0.02);
    public void MarkPostDraw(long ticks)
    {
        if(!Enabled)return;
        if(pendingExitTicks>0&&ticks>=pendingExitTicks)drawLatency.Add(Stopwatch.GetElapsedTime(pendingExitTicks,ticks).TotalMilliseconds);
        lastPostDrawTicks=ticks;pendingExitTicks=0;
    }
    private long entryTicks;
    public void MarkProcessEntry(long ticks)
    {
        if(!Enabled)return;
        if(lastPostDrawTicks>0&&ticks>=lastPostDrawTicks)idleWall.Add(Stopwatch.GetElapsedTime(lastPostDrawTicks,ticks).TotalMilliseconds);
        lastPostDrawTicks=0;entryTicks=ticks;
    }
    public void MarkProcessExit(long ticks)
    {
        if(!Enabled)return;
        if(entryTicks>0&&ticks>=entryTicks)processWall.Add(Stopwatch.GetElapsedTime(entryTicks,ticks).TotalMilliseconds);
        entryTicks=0;pendingExitTicks=ticks;
    }
    public double MainThreadCpuNowMs()=>ReadMainThreadCpuMs();
    public void ProcessEnd(long endTicks,double cpuStartMs,double cpuEndMs)
    {
        if(!Enabled)return;
        if(cpuStartMs>=0&&cpuEndMs>=cpuStartMs)processCpuInside.Add(cpuEndMs-cpuStartMs);
        if(lastProcessEndTicks>0&&endTicks>lastProcessEndTicks)
            processGaps.Add(Stopwatch.GetElapsedTime(lastProcessEndTicks,endTicks).TotalMilliseconds);
        lastProcessEndTicks=endTicks;
    }
    public void BuildCompleted(double ms)
    {
        if(!Enabled)return;
        if(ms>maxBuildMs)maxBuildMs=ms;
    }
    private int presentSeen;
    public void Present(long drawnDelta,long primitives,long drawCalls,double frameSetupMs)
    {
        if(!Enabled)return;
        presentPrimitives=primitives;presentDrawCalls=drawCalls;presentFrameSetupMs=frameSetupMs;
        if(presentSeen++==0)return;
        presentDeltas.Add(drawnDelta);
        switch(PresentBucket(drawnDelta)){case 0:presentZero++;break;case 1:presentOne++;break;default:presentMulti++;break;}
    }
    private double lastBuildTotal,maxBuildMs;
    private TimeSpan cpuBaseline;
    private bool cadenceInitialised;
    private int cachedOsId;
    private double ReadMainThreadCpuMs()
    {
        if(cachedOsId==0)
        {
            cachedOsId=NativeThreadId();
            if(cachedOsId==0)return -1;
        }
        try
        {
            foreach(var line in File.ReadLines("/proc/self/task/"+cachedOsId+"/stat"))
            {
                int close=line.LastIndexOf(')');
                if(close<0)return -1;
                var fields=line[(close+1)..].Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(fields.Length<13)return -1;
                double ticks=double.Parse(fields[11])+double.Parse(fields[12]);
                return ticks*1000.0/100.0;
            }
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or FormatException){return -1;}
        return -1;
    }
    private static int NativeThreadId()
    {
        try
        {
            string stat=File.ReadAllText("/proc/self/stat");
            int close=stat.LastIndexOf(')');
            if(close>0)return int.Parse(stat.Split(' ')[0]);
        }
        catch(Exception e)when(e is IOException or FormatException or IndexOutOfRangeException){}
        return 0;
    }
    private double threadBaseline=-1;
    public readonly GmObjectPerformance Objects=new();
    public (int Width,int Height) RenderTargetSize{get;set;}
    public (int Width,int Height) CanvasSize{get;set;}
    public (int Width,int Height) WindowSize{get;set;}
    public (int Width,int Height) FramebufferSize{get;set;}
    private long allocatedBaseline;
    private TimeSpan pauseBaseline;
    private readonly int[] collectionBaseline=new int[3];
    private void ResetGcBaseline()
    {
        allocatedBaseline=GC.GetTotalAllocatedBytes(true);pauseBaseline=GC.GetTotalPauseDuration();
        for(int generation=0;generation<3;generation++)collectionBaseline[generation]=GC.CollectionCount(generation);
    }
    public void Process(long started,long steps)
    {
        if(!Enabled)return;
        double ms=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        processTimes.Add(ms);processSteps+=steps;
        if(steps>1)catchUpProcesses++;
        maxSteps=Math.Max(maxSteps,(int)steps);
        int bucket=ProcessStepBucket(steps);
        if(!processBySteps.TryGetValue(bucket,out var list))processBySteps[bucket]=list=new List<double>();
        list.Add(ms);
    }
    public static int ProcessStepBucket(long steps)=>steps<=0?0:(steps==1?1:(steps==2?2:3));
    private readonly Dictionary<int,List<double>> processBySteps=new();
    public void RenderCompleted()
    {
        if(!Enabled)return;
        long now=Stopwatch.GetTimestamp();
        if(lastRender!=0)renderIntervals.Add(Stopwatch.GetElapsedTime(lastRender,now).TotalMilliseconds);
        lastRender=now;
    }
    public void ResetCadence(){processTimes.Clear();renderIntervals.Clear();processSteps=0;catchUpProcesses=maxSteps=0;lastRender=0;Objects.Clear();lastBuildCount=0;lastBuildTotal=0;maxBuildMs=0;cadencePhases.Clear();processPhases.Clear();drawPhases.Clear();drawPhaseSamples.Clear();drawPending.Clear();drawFrames=0;lastSnapshotRebuilds=-1;lastCandidateVisits=-1;cadenceInitialised=false;lastPublished=lastRendered=lastCoalesced=lastSkipped=0;presentDeltas.Clear();presentZero=presentOne=presentMulti=0;presentSeen=0;processGaps.Clear();processCpuInside.Clear();lastProcessEndTicks=0;processWall.Clear();drawLatency.Clear();idleWall.Clear();processBySteps.Clear();lastPostDrawTicks=0;pendingExitTicks=0;entryTicks=0;if(Enabled)ResetGcBaseline();}
    public void Cadence(GmFrameSample sample,GmWorld world,Func<object> graphics)
    {
        if(!Enabled)return;
        long allocated=GC.GetTotalAllocatedBytes(true);
        var pause=GC.GetTotalPauseDuration();
        var collections=Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();
        long allocatedBytes=allocated-allocatedBaseline;
        double pauseMs=(pause-pauseBaseline).TotalMilliseconds;
        var gc=new{allocatedBytes,allocatedBytesPerSecond=allocatedBytes/sample.Seconds,
            allocatedBytesPerLogicStep=processSteps==0?0:allocatedBytes/(double)processSteps,
            collections=collections.Select((count,generation)=>count-collectionBaseline[generation]).ToArray(),
            pauseMs,pausePercent=pauseMs/(sample.Seconds*1000)*100};
        allocatedBaseline=allocated;pauseBaseline=pause;
        Array.Copy(collections,collectionBaseline,3);
        var stats=graphics();
        long buildCount=0;double buildTotal=0;
        if(stats is GmGraphicsStats probe){buildCount=probe.Builds;buildTotal=probe.BuildTotalMs;}
        long buildDelta=buildCount-lastBuildCount;
        double buildMsDelta=buildTotal-lastBuildTotal;
        lastBuildCount=buildCount;lastBuildTotal=buildTotal;
        double windowMaxBuildMs=maxBuildMs;maxBuildMs=0;
        var cpuNow=System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        if(!cadenceInitialised){cpuBaseline=cpuNow;cadenceInitialised=true;}
        double cpuMs=(cpuNow-cpuBaseline).TotalMilliseconds;
        cpuBaseline=cpuNow;
        double threadCpuMs=0;
        double threadTotal=ReadMainThreadCpuMs();
        if(threadTotal>=0)
        {
            if(threadBaseline<0)threadBaseline=threadTotal;
            threadCpuMs=Math.Max(0,threadTotal-threadBaseline);
            threadBaseline=threadTotal;
        }
        var output=world.OutputSize();
        var plan=world.FramePlan?.Stats;
        long published=plan?.Published??0,rendered=plan?.Rendered??0,coalesced=plan?.Coalesced??0,skipped=plan?.Skipped??0;
        long publishedDelta=published-lastPublished,renderedDelta=rendered-lastRendered,
             coalescedDelta=coalesced-lastCoalesced,skippedDelta=skipped-lastSkipped;
        lastPublished=published;lastRendered=rendered;lastCoalesced=coalesced;lastSkipped=skipped;
        int aliveInstances=world.Instances.Count(i=>!i.Destroyed&&i.Active);
        int aliveParticles=world.Particles.AliveCount;
        object Summary(List<double> values)=>GmFrameSummary.From(values);

        WriteLog(new{type="cadence",time=DateTimeOffset.UtcNow,frame=world.Frame,room=world.CurrentRoom,
            targetLogicFps=world.Engine.Read("room_speed").Number,logicFps=sample.LogicFps,processFps=sample.ProcessFps,
            completedRenderFps=renderIntervals.Count/sample.Seconds,process=Summary(processTimes),renderInterval=Summary(renderIntervals),
            seconds=sample.Seconds,gc,objectProfile=Objects.Enabled?Objects.Snapshot(world,sample.Seconds,processSteps,allocatedBytes,processTimes.Sum()):null,
            logicSteps=processSteps,catchUpProcesses,maxStepsPerProcess=maxSteps,graphics=stats,
            build=new{count=buildDelta,totalMs=buildMsDelta,meanMs=buildDelta==0?0:buildMsDelta/buildDelta,
                lastMs=stats is GmGraphicsStats p?p.LastBuildMs:0,maxMs=buildDelta==0?0:windowMaxBuildMs},
            renderSize=new{width=RenderTargetSize.Width>0?RenderTargetSize.Width:output.Width,
                height=RenderTargetSize.Height>0?RenderTargetSize.Height:output.Height,
                pixels=(long)(RenderTargetSize.Width>0?RenderTargetSize.Width:output.Width)*(RenderTargetSize.Height>0?RenderTargetSize.Height:output.Height),
                canvasWidth=CanvasSize.Width>0?CanvasSize.Width:output.Width,
                canvasHeight=CanvasSize.Height>0?CanvasSize.Height:output.Height,
                canvasPixels=(long)(CanvasSize.Width>0?CanvasSize.Width:output.Width)*(CanvasSize.Height>0?CanvasSize.Height:output.Height),
                windowWidth=WindowSize.Width,windowHeight=WindowSize.Height,
                windowPixels=(long)WindowSize.Width*WindowSize.Height,
                framebufferWidth=FramebufferSize.Width,framebufferHeight=FramebufferSize.Height,
                framebufferPixels=(long)FramebufferSize.Width*FramebufferSize.Height},
            framePlan=new{published=publishedDelta,rendered=renderedDelta,coalesced=coalescedDelta,skipped=skippedDelta},
            alive=new{instances=aliveInstances,particles=aliveParticles},
            cpuMs,cpuScope="process.allThreads",mainThreadCpuMs=threadCpuMs,mainThreadScope="process.mainThreadOnly",
            dynamicDark=world.Globals.ReadOr("DynamicDark").Truth,darkness=world.Globals.ReadOr("Darkness").Number,
            collision=new{snapshotRebuilds=world.CollisionSnapshotRebuilds,candidateVisits=world.CollisionCandidateVisits,exactTests=world.CollisionExactTests,
                snapshotRebuildsPerStep=DeltaPerStep(world.CollisionSnapshotRebuilds,ref lastSnapshotRebuilds,processSteps),
                candidateVisitsPerStep=DeltaPerStep(world.CollisionCandidateVisits,ref lastCandidateVisits,processSteps),
                raw=Cumulative},
            present=new{count=presentDeltas.Count,deltaUnit="framesDrawnPerProcess",delta=GmCountSummary.From(presentDeltas),zero=presentZero,one=presentOne,multi=presentMulti,primitives=presentPrimitives,drawCalls=presentDrawCalls,frameSetupMs=presentFrameSetupMs},
            processBreakdown=new{gap=Summary(processGaps),cpuInside=Summary(processCpuInside),wallUnit="ms",processWall=Summary(processWall),drawLatency=Summary(drawLatency),idleWall=Summary(idleWall),
                bySteps=processBySteps.Count==0?null:processBySteps.OrderByDescending(p=>p.Key).ToDictionary(p=>p.Key switch{0=>"steps0",1=>"steps1",2=>"steps2",_=>"steps3plus"},p=>Summary(p.Value))},
            phases=cadencePhases.Count==0?null:cadencePhases.ToDictionary(p=>p.Key,p=>new{meanMs=p.Value.Total/p.Value.Count,maxMs=p.Value.Max,count=p.Value.Count,totalMs=p.Value.Total}),
            processPhases=processPhases.Count==0?null:processPhases.ToDictionary(p=>p.Key,p=>new{meanMs=p.Value.Total/p.Value.Count,maxMs=p.Value.Max,count=p.Value.Count,totalMs=p.Value.Total}),
            drawPhases=drawPhases.Count==0?null:drawPhases.ToDictionary(p=>p.Key,p=>{var values=drawPhaseSamples[p.Key].OrderBy(v=>v).ToArray();double Percentile(double q)=>values.Length==0?0:values[Math.Min(values.Length-1,(int)Math.Ceiling(values.Length*q)-1)];return new{meanMs=p.Value.Total/p.Value.Count,p50Ms=Percentile(.5),p95Ms=Percentile(.95),maxMs=p.Value.Max,count=p.Value.Count,totalMs=p.Value.Total};}),
            drawFrames});
        processTimes.Clear();renderIntervals.Clear();processSteps=0;catchUpProcesses=maxSteps=0;
        presentDeltas.Clear();presentZero=presentOne=presentMulti=0;
        processGaps.Clear();processCpuInside.Clear();lastProcessEndTicks=0;processWall.Clear();drawLatency.Clear();idleWall.Clear();processBySteps.Clear();
        cadencePhases.Clear();processPhases.Clear();drawPhases.Clear();drawPhaseSamples.Clear();drawPending.Clear();drawFrames=0;
    }
    public double? Cumulative{get;set;}
    private static double? DeltaPerStep(long current,ref long previous,long steps)
    {
        long delta=previous<0?0:current-previous;previous=current;
        return steps<=0?null:delta/(double)steps;
    }
    public void EndProcess(string name,long start)
    {
        if(!Enabled)return;
        double ms=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var previous=processPhases.GetValueOrDefault(name);
        processPhases[name]=(previous.Total+ms,Math.Max(previous.Max,ms),previous.Count+1);
    }
    public void BeginDraw(string name){if(Enabled)drawPending[name]=Stopwatch.GetTimestamp();}
    public void EndDraw(string name)
    {
        if(!Enabled)return;
        if(!drawPending.TryGetValue(name,out long started))return;
        drawPending.Remove(name);
        if(name=="canvas_draw")drawFrames++;
        double ms=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var previous=drawPhases.GetValueOrDefault(name);
        drawPhases[name]=(previous.Total+ms,Math.Max(previous.Max,ms),previous.Count+1);
        if(!drawPhaseSamples.TryGetValue(name,out var values))drawPhaseSamples[name]=values=new();
        values.Add(ms);
    }
    public DrawScope MeasureDraw(string name)=>new(this,name,Stopwatch.GetTimestamp());
    public readonly struct DrawScope:IDisposable
    {
        private readonly GmPerformance? owner;
        private readonly string? name;
        private readonly long started;
        internal DrawScope(GmPerformance owner,string name,long started){this.owner=owner;this.name=name;this.started=started;}
        public void Dispose(){if(owner is not null&&name is not null)owner.EndDraw(name,started);}
    }
    private void EndDraw(string name,long started)
    {
        if(!Enabled)return;
        double ms=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var previous=drawPhases.GetValueOrDefault(name);
        drawPhases[name]=(previous.Total+ms,Math.Max(previous.Max,ms),previous.Count+1);
        if(!drawPhaseSamples.TryGetValue(name,out var values))drawPhaseSamples[name]=values=new();
        values.Add(ms);
    }
    public void Configure(string userRoot,bool debug)
    {
        Enabled=Environment.GetEnvironmentVariable("SLACK_PERF") switch{"0"=>false,"1"=>true,_=>debug};
        Objects.Enabled=Enabled&&Environment.GetEnvironmentVariable("SLACK_PERF_OBJECTS")=="1";
        if(Enabled)ResetGcBaseline();
        LogPath=Enabled?Path.Combine(userRoot,"logs",$"performance-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl"):null;
        if(Enabled)WriteLog(new{type="session",time=DateTimeOffset.UtcNow,dropFps=5,warmupSeconds=3,baselineWindows=5,repeatSeconds=5});
    }
    private void WriteLog(object record)
    {
        if(LogPath is null||logFailed)return;
        try{Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);File.AppendAllText(LogPath,JsonSerializer.Serialize(record)+Environment.NewLine);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){logFailed=true;Console.Error.WriteLine("PERF_LOG_FAILED "+e.Message);}
    }
    public void Record(GmFrameSample sample,GmWorld world,Func<object> graphics)
    {
        if(!Enabled||!sample.Dropped||LogPath is null||logFailed)return;
        try
        {
            var active=world.Instances.Where(i=>i.Active&&!i.Destroyed).ToArray();
            string Name(int id)=>world.Objects.TryGetValue(id,out var obj)?obj.GetProperty("name").GetString()!:id.ToString();
            string Character(GmInstance i)=>world.IsObject(i.Object,world.Constants["objEnemyDoll"].Int)||world.IsObject(i.Object,world.Constants["objEnemyDollNoDeactivate"].Int)?world.Globals.Values.GetValueOrDefault(GmScope.Key("EnemyName",[i.Scope.ReadOr("index").Int])).ToString():i.Scope.ReadOr("DollChara",i.Scope.ReadOr("chara")).ToString();
            object Actor(GmInstance i)=>new{id=i.Id,type=Name(i.Object),character=Character(i),x=i.Scope.ReadOr("x").Number,y=i.Scope.ReadOr("y").Number,motion=i.Scope.ReadOr("MotionNum").Number,bound=i.Scope.ReadOr("BINDED").Truth};
            var record=new
            {
                type="drop",time=DateTimeOffset.UtcNow,frame=world.Frame,room=new{id=world.CurrentRoom,name=world.Rooms[world.CurrentRoom].GetProperty("name").GetString()},sample,
                character=world.Globals.ReadOr("Chara").ToString(),
                players=active.Where(i=>world.IsObject(i.Object,world.Constants["objPlayerDoll"].Int)).Select(Actor).ToArray(),
                npcs=active.Where(i=>world.IsObject(i.Object,world.Constants["objEnemyDoll"].Int)||world.IsObject(i.Object,world.Constants["objEnemyDollNoDeactivate"].Int)).Select(Actor).ToArray(),
                objects=active.GroupBy(i=>i.Object).Select(g=>new{type=Name(g.Key),count=g.Count()}).ToArray(),
                particles=world.Particles.Systems.Select(p=>new{id=p.Key,count=p.Value.Particles.Count,emitters=p.Value.Emitters.Count}).ToArray(),
                instances=world.Instances.Count,active=active.Length,sprites=world.Sprites.Count,graphics=graphics(),
                phases=(samples.Count>0?samples:recentSamples).ToDictionary(p=>p.Key,p=>new{meanMs=p.Value.Total/p.Value.Count,maxMs=p.Value.Max,count=p.Value.Count}),
                scripts=scripts.OrderByDescending(p=>p.Value.Total).Take(12).ToDictionary(p=>p.Key,p=>new{totalMs=p.Value.Total,count=p.Value.Count})
            };
            WriteLog(record);
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){logFailed=true;Console.Error.WriteLine("PERF_LOG_FAILED "+e.Message);}
    }
    public void Script(string key,long start)
    {
        if(!ScriptsEnabled)return;
        var previous=scripts.GetValueOrDefault(key);
        scripts[key]=(previous.Total+Stopwatch.GetElapsedTime(start).TotalMilliseconds,previous.Count+1);
    }
    public long Start()=>Enabled?Stopwatch.GetTimestamp():0;
    public void End(string name,long start)
    {
        if(!Enabled)return;
        double ms=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var previous=samples.GetValueOrDefault(name);
        samples[name]=(previous.Total+ms,Math.Max(previous.Max,ms),previous.Count+1);
        var cadence=cadencePhases.GetValueOrDefault(name);
        cadencePhases[name]=(cadence.Total+ms,Math.Max(cadence.Max,ms),cadence.Count+1);
    }
    public void Report(long frame,int room)
    {
        if(!Enabled||frame%60!=0)return;
        Console.WriteLine("PERF frame="+frame+" room="+room+" "+string.Join(" ",samples.Select(p=>$"{p.Key}={p.Value.Total/p.Value.Count:F2}ms/max{p.Value.Max:F2}/n{p.Value.Count}")));
        if(ScriptsEnabled)Console.WriteLine("PERF_SCRIPTS "+string.Join(" ",scripts.OrderByDescending(p=>p.Value.Total).Take(12).Select(p=>$"{p.Key}={p.Value.Total:F1}ms/n{p.Value.Count}")));
        recentSamples.Clear();foreach(var pair in samples)recentSamples[pair.Key]=pair.Value;
        samples.Clear();
        scripts.Clear();
    }
}
