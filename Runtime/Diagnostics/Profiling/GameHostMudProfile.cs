using Godot;
using System.Diagnostics;
using System.Text.Json;

namespace SlackOfDescent;

public partial class GameHost
{
    private void AuditMudProfile()
    {
        var player=world.Select(world.Constants["objPlayerDoll"].Int,new(new GmInstance())).Single();
        int surface=world.Constants["objSwampSurface"].Int;
        var scripts=new Dictionary<string,(double Ms,long Bytes,int Count)>();
        var windows=new List<object>();
        int[] slot=new int[1];
        void Position()
        {
            world.Write(player,"x",2478);world.Write(player,"y",1628);
            world.Write(player,"hspeed",0);world.Write(player,"vspeed",0);
        }
        for(int frame=0;frame<180;frame++){Position();StepFrame();}
        world.Evaluator.DiagnosticSample=(key,ms,bytes)=>
        {
            var previous=scripts.GetValueOrDefault(key);
            scripts[key]=(previous.Ms+ms,previous.Bytes+bytes,previous.Count+1);
        };
        try
        {
            for(int window=0;window<4;window++)
            {
                scripts.Clear();double elapsed=0;long bytes=0;int nodes=0,activeNodes=0,pendingNodes=0,surfaces=0,touching=0;
                int[] gcBefore=Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();
                for(int frame=0;frame<60;frame++)
                {
                    Position();
                    long allocated=GC.GetAllocatedBytesForCurrentThread(),started=Stopwatch.GetTimestamp();
                    StepFrame();
                    elapsed+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    bytes+=GC.GetAllocatedBytesForCurrentThread()-allocated;
                    if(player.Scope.ReadOr("touchingQS").Truth)touching++;
                    foreach(var instance in world.Instances)
                    {
                        if(instance.Destroyed||!instance.Active||instance.Object!=surface)continue;
                        surfaces++;
                        for(int index=0;index<=instance.Scope.Read("m_count").Int;index++)
                        {
                            slot[0]=index;nodes++;
                            if(instance.Scope.Read("moss_m",slot).Number>0.001)activeNodes++;
                            if(instance.Scope.Read("moss_mt",slot).Number>0.001)pendingNodes++;
                        }
                    }
                }
                windows.Add(new{window,frames=60,stepMeanMs=elapsed/60,allocatedBytesPerFrame=bytes/60,
                    surfacesMean=surfaces/60.0,nodesMean=nodes/60.0,activeNodesMean=activeNodes/60.0,pendingNodesMean=pendingNodes/60.0,touchingFrames=touching,
                    gcCollections=Enumerable.Range(0,3).Select(g=>GC.CollectionCount(g)-gcBefore[g]).ToArray(),
                    scripts=scripts.ToDictionary(p=>p.Key,p=>new{calls=p.Value.Count,totalMs=p.Value.Ms,meanMs=p.Value.Ms/p.Value.Count,allocatedBytes=p.Value.Bytes})});
            }
        }
        finally{world.Evaluator.DiagnosticSample=null;}
        if(!windows.Any())throw new InvalidOperationException("No mud profile samples");
        string output=System.Environment.GetEnvironmentVariable("SLACK_MUD_REPORT")??"/tmp/mud-profile.json";
        File.WriteAllText(output,JsonSerializer.Serialize(new{mode=System.Environment.GetEnvironmentVariable("SLACK_MUD_EXPERIMENT")??"full",room=world.CurrentRoom,seed=0,warmupFrames=180,position=new{x=2478,y=1628},windows},new JsonSerializerOptions{WriteIndented=true}));
        GD.Print("MUD_PROFILE_OK ",output);
    }
}
