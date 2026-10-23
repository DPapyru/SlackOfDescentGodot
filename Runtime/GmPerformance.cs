using System.Diagnostics;
using System.Text.Json;

namespace SlackOfDescent;

public sealed class GmPerformance
{
    public bool Enabled{get;private set;}=Environment.GetEnvironmentVariable("SLACK_PERF")=="1";
    public readonly bool ScriptsEnabled=Environment.GetEnvironmentVariable("SLACK_PERF_SCRIPTS")=="1";
    private readonly Dictionary<string,(double Total,double Max,int Count)> samples=new();
    private readonly Dictionary<string,(double Total,int Count)> scripts=new();
    public string? LogPath{get;private set;}
    private bool logFailed;
    private readonly Dictionary<string,(double Total,double Max,int Count)> recentSamples=new();
    private readonly List<double> processTimes=new(),renderIntervals=new();
    private long processSteps;
    private int catchUpProcesses,maxSteps;
    private long lastRender;
    public void Process(long started,long steps)
    {
        if(!Enabled)return;
        processTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);processSteps+=steps;
        if(steps>1)catchUpProcesses++;
        maxSteps=Math.Max(maxSteps,(int)steps);
    }
    public void RenderCompleted()
    {
        if(!Enabled)return;
        long now=Stopwatch.GetTimestamp();
        if(lastRender!=0)renderIntervals.Add(Stopwatch.GetElapsedTime(lastRender,now).TotalMilliseconds);
        lastRender=now;
    }
    public void ResetCadence(){processTimes.Clear();renderIntervals.Clear();processSteps=0;catchUpProcesses=maxSteps=0;lastRender=0;}
    public void Cadence(GmFrameSample sample,GmWorld world,Func<object> graphics)
    {
        if(!Enabled)return;
        object Summary(List<double> values)
        {
            values.Sort();int count=values.Count;
            return new{count,meanMs=count==0?0:values.Average(),p95Ms=count==0?0:values[(int)Math.Ceiling(count*0.95)-1],maxMs=count==0?0:values[^1]};
        }
        WriteLog(new{type="cadence",time=DateTimeOffset.UtcNow,frame=world.Frame,room=world.CurrentRoom,
            targetLogicFps=world.Engine.Read("room_speed").Number,logicFps=sample.LogicFps,processFps=sample.RenderFps,
            completedRenderFps=renderIntervals.Count/sample.Seconds,process=Summary(processTimes),renderInterval=Summary(renderIntervals),
            logicSteps=processSteps,catchUpProcesses,maxStepsPerProcess=maxSteps,graphics=graphics(),
            dynamicDark=world.Globals.ReadOr("DynamicDark").Truth,darkness=world.Globals.ReadOr("Darkness").Number,
            collision=new{snapshotRebuilds=world.CollisionSnapshotRebuilds,candidateVisits=world.CollisionCandidateVisits,exactTests=world.CollisionExactTests}});
        processTimes.Clear();renderIntervals.Clear();processSteps=0;catchUpProcesses=maxSteps=0;
    }
    public void Configure(string userRoot,bool debug)
    {
        Enabled=Environment.GetEnvironmentVariable("SLACK_PERF") switch{"0"=>false,"1"=>true,_=>debug};
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
