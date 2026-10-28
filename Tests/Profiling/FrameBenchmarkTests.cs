using System.Diagnostics;
using SlackOfDescent;

public readonly record struct FrameBenchmarkReport(
    int Steps,
    int Instances,
    int FramesPerBuild,
    int ImmediateBuilds,
    int CoalescedBuilds,
    long ImmediateCommands,
    long CoalescedCommands,
    double ImmediateMs,
    double CoalescedMs,
    long Published,
    long Coalesced);

public static class FrameBenchmarkTests
{
    public static FrameBenchmarkReport Last{get;private set;}
    public static int Run()
    {
        int count=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);count++;}
        const int steps=3000,instances=1200,framesPerBuild=4;
        var scene=BenchScene.Create(instances);
        var report=Measure(scene,steps,framesPerBuild);
        Last=report;
        Check(report.ImmediateBuilds==steps,"uncoalesced path builds once per logic step");
        Check(report.CoalescedBuilds==steps,"presentation batching preserves per-step builds");
        Check(report.CoalescedBuilds==report.ImmediateBuilds,"both presentation paths execute every draw");
        Check(report.ImmediateCommands==report.ImmediateBuilds*(long)instances*3,"uncoalesced commands match builds");
        Check(report.CoalescedCommands==report.CoalescedBuilds*(long)instances*3,"coalesced commands match builds");
        Check(report.CoalescedCommands==report.ImmediateCommands,"presentation batching preserves generated command volume");
        Check(report.Published==steps/framesPerBuild+1,"the trailing logic step leaves one pending publish");
        Check(report.Coalesced==steps-report.Published,"every redundant publish is counted once");
        var single=Measure(scene,64,1);
        Check(single.CoalescedBuilds==64&&single.ImmediateBuilds==64,"one frame per build matches the uncoalesced path");
        var remainder=Measure(scene,65,4);
        Check(remainder.CoalescedBuilds==65,"partial trailing presentation never drops draw work");
        Check(remainder.Published==17,"remainder coalesces into its own frame");
        var idle=new GmFramePlan();
        var idleBuilds=0;
        for(int step=0;step<steps;step++)if(idle.Consume() is not null)idleBuilds++;
        Check(idleBuilds==0,"idle logic never builds");
        Check(idle.Stats.Skipped==steps,"idle consumes are skipped");
        var switched=new GmFramePlan();
        switched.Publish(0,11);switched.Publish(1,11);switched.Reset(12);switched.Consume();
        switched.Publish(2,12);
        Check(switched.Consume()!.Value.Room==12,"room switch renders the new room");
        Check(switched.Stats.Rendered==1&&switched.Stats.Coalesced==1,"room switch keeps coalescing counters");
        return count;
    }

    private static FrameBenchmarkReport Measure(BenchScene scene,int steps,int framesPerBuild)
    {
        for(int pass=0;pass<2;pass++)
        {
            var warm=new GmFramePlan();
            for(int step=0;step<steps;step++)
            {
                warm.Publish(step,11);
                if(step%framesPerBuild==0)warm.Consume();
                scene.Build();
            }
        }
        var immediate=new GmFramePlan();
        int immediateBuilds=0;long immediateCommands=0;
        var immediateClock=Stopwatch.StartNew();
        for(int step=0;step<steps;step++)
        {
            immediate.Publish(step,11);
            if(immediate.Consume() is null)continue;
            immediateBuilds++;immediateCommands+=scene.Build();
        }
        immediateClock.Stop();
        var coalesced=new GmFramePlan();
        int coalescedBuilds=0;long coalescedCommands=0;
        var coalescedClock=Stopwatch.StartNew();
        for(int step=0;step<steps;step++)
        {
            coalescedBuilds++;coalescedCommands+=scene.Build();
            coalesced.Publish(step,11);
            if(step%framesPerBuild==0)coalesced.Consume();
        }
        coalescedClock.Stop();
        var stats=coalesced.Stats;
        return new(steps,scene.Instances.Length,framesPerBuild,immediateBuilds,coalescedBuilds,immediateCommands,coalescedCommands,
            immediateClock.Elapsed.TotalMilliseconds,coalescedClock.Elapsed.TotalMilliseconds,stats.Published,stats.Coalesced);
    }

    private sealed class BenchScene
    {
        private readonly double[] x,y,scale;
        private readonly int[] sprite;
        private double sink;
        public readonly int[] Instances;
        private BenchScene(int[] instances)
        {
            Instances=instances;x=new double[instances.Length];y=new double[instances.Length];scale=new double[instances.Length];sprite=new int[instances.Length];
            for(int i=0;i<instances.Length;i++){x[i]=i%317;y[i]=i%211;scale[i]=1+(i%3)*.5;sprite[i]=i%16;}
        }
        public static BenchScene Create(int count)=>new(Enumerable.Range(0,count).ToArray());
        public long Sink=>(long)sink;
        public long Build()
        {
            for(int i=0;i<Instances.Length;i++)
            {
                double value=x[i]*scale[i]+y[i];
                if(sprite[i]%4==0)value+=1;
                sink+=value;
            }
            return (long)Instances.Length*3;
        }
    }
}
