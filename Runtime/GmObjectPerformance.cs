using System.Diagnostics;

namespace SlackOfDescent;

public sealed class GmObjectPerformance
{
    public bool Enabled;
    private struct Frame
    {
        public int Object,Kind,Thread;
        public long Started,Allocated,ChildTicks,ChildBytes;
    }
    private struct Sample
    {
        public long Ticks,Bytes,Calls;
    }
    private readonly List<Frame> stack=new();
    private readonly Dictionary<(int Object,int Kind),Sample> samples=new();
    private readonly Dictionary<int,(string Name,string Category)> labels=new();
    private long skippedAsync;
    public readonly struct Scope : IDisposable
    {
        private readonly GmObjectPerformance? owner;
        internal Scope(GmObjectPerformance owner){this.owner=owner;}
        public void Dispose()=>owner?.End(false);
        public void Skip()=>owner?.End(true);
    }
    public Scope Begin(int obj,int kind)
    {
        if(!Enabled||obj==-1||obj< -2)return default;
        stack.Add(new Frame{Object=obj,Kind=kind,Thread=Environment.CurrentManagedThreadId,
            Started=Stopwatch.GetTimestamp(),Allocated=GC.GetAllocatedBytesForCurrentThread()});
        return new(this);
    }
    private void End(bool skip)
    {
        long now=Stopwatch.GetTimestamp(),allocated=GC.GetAllocatedBytesForCurrentThread();
        var frame=stack[^1];stack.RemoveAt(stack.Count-1);
        long ticks=now-frame.Started,bytes=allocated-frame.Allocated;
        skip|=frame.Thread!=Environment.CurrentManagedThreadId||bytes<0;
        if(stack.Count>0)
        {
            var parent=stack[^1];parent.ChildTicks+=ticks;parent.ChildBytes+=Math.Max(0,bytes);stack[^1]=parent;
        }
        if(skip){skippedAsync++;return;}
        var key=(frame.Object,frame.Kind);var sample=samples.GetValueOrDefault(key);
        sample.Ticks+=Math.Max(0,ticks-frame.ChildTicks);sample.Bytes+=Math.Max(0,bytes-frame.ChildBytes);sample.Calls++;
        samples[key]=sample;
    }
    public void Clear(){samples.Clear();skippedAsync=0;}
    private (string Name,string Category) Label(GmWorld world,int obj)
    {
        if(labels.TryGetValue(obj,out var label))return label;
        if(obj==-2)return ("drawEngine","engine");
        string name=world.Objects[obj].GetProperty("name").GetString()!;
        bool Is(string parent)=>world.Constants.TryGetValue(parent,out var id)&&world.IsObject(obj,id.Int);
        string category=Is("objPlayerDoll")||Is("objEnemyDoll")||Is("objEnemyDollNoDeactivate")||Is("objPlayerParts")||Is("objEnemyParts")||Is("objExtParts")?"character"
            :name=="objSwPlants"||name.Contains("Mud",StringComparison.Ordinal)||name.Contains("Swamp",StringComparison.Ordinal)||name.Contains("Quicksand",StringComparison.Ordinal)?"mud":"other";
        return labels[obj]=(name,category);
    }
    public object Snapshot(GmWorld world,double seconds,long steps,long allocatedBytes,double processMs)
    {
        var entries=samples.Select(p=>(p.Key.Object,p.Key.Kind,p.Value.Ticks,p.Value.Bytes,p.Value.Calls)).ToArray();
        double drawTicks=entries.Where(p=>p.Kind==8).Sum(p=>(double)p.Ticks);
        object Summarize(IEnumerable<(int Object,int Kind,long Ticks,long Bytes,long Calls)> source)
        {
            var rows=source.ToArray();double ms=rows.Sum(r=>(double)r.Ticks)*1000/Stopwatch.Frequency;
            double drawMs=rows.Where(r=>r.Kind==8).Sum(r=>(double)r.Ticks)*1000/Stopwatch.Frequency;
            long bytes=rows.Sum(r=>r.Bytes);
            return new{scopeCalls=rows.Sum(r=>r.Calls),allocatedBytes=bytes,allocatedMB=bytes/1e6,
                allocatedMBPerSecond=bytes/seconds/1e6,allocatedMBPerLogicStep=steps==0?0:bytes/(double)steps/1e6,
                allocationPercentOfProcess=allocatedBytes==0?0:bytes*100.0/allocatedBytes,cpuMs=ms,
                cpuPercentOfProcess=processMs==0?0:ms*100/processMs,drawBuildMs=drawMs,
                drawBuildMsPerLogicStep=steps==0?0:drawMs/steps,
                drawBuildPercentOfObjects=drawTicks==0?0:drawMs*Stopwatch.Frequency/(drawTicks*10)};
        }
        var instances=world.Instances.Where(i=>!i.Destroyed&&i.Active).GroupBy(i=>i.Object).ToDictionary(g=>g.Key,g=>g.Count());
        var result=new{enabled=Enabled,skippedAsyncScopes=skippedAsync,
            attributedAllocatedBytes=entries.Sum(r=>r.Bytes),
            unattributedAllocatedBytes=Math.Max(0,allocatedBytes-entries.Sum(r=>r.Bytes)),
            groups=entries.GroupBy(r=>Label(world,r.Object).Category).ToDictionary(g=>g.Key,g=>Summarize(g)),
            objects=entries.GroupBy(r=>r.Object).Select(g=>new{type=Label(world,g.Key).Name,category=Label(world,g.Key).Category,
                activeInstances=instances.GetValueOrDefault(g.Key),createdTotal=world.Created.GetValueOrDefault(g.Key),total=Summarize(g),
                phases=g.GroupBy(r=>r.Kind==8?"drawBuild":r.Kind==0?"create":r.Kind==3?"step":r.Kind==4?"collision":"other")
                    .ToDictionary(p=>p.Key,p=>Summarize(p))}).ToArray()};
        Clear();return result;
    }
}
