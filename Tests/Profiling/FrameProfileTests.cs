using SlackOfDescent;

public static class FrameProfileTests
{
    public static int Run()
    {
        int count=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);count++;}

        var empty=GmFrameSummary.From(new List<double>());
        Check(empty.Count==0,"empty summary has zero count");
        Check(empty.MeanMs==0&&empty.MaxMs==0,"empty summary has zero timing");
        Check(GmFrameSummary.Percentile(new List<double>(),0.5)==0,"empty percentile is zero");

        var single=GmFrameSummary.From(new List<double>{7.5});
        Check(single.Count==1,"single sample count");
        Check(single.MeanMs==7.5,"single sample mean");
        Check(single.P50Ms==7.5&&single.P95Ms==7.5&&single.P99Ms==7.5&&single.MaxMs==7.5,"single sample percentiles equal value");

        var odd=GmFrameSummary.From(new List<double>{5,1,3});
        Check(odd.Count==3,"odd count");
        Check(odd.MeanMs==3,"odd mean");
        Check(odd.P50Ms==3,"odd p50 picks middle");
        Check(odd.MaxMs==5,"odd max picks largest");

        var even=GmFrameSummary.From(new List<double>{4,1,3,2});
        Check(even.Count==4,"even count");
        Check(even.MeanMs==2.5,"even mean");
        Check(even.P50Ms==2,"even p50 uses ceiling index");
        Check(even.P99Ms==4,"even p99 clamps to max");

        var hundred=GmFrameSummary.From(Enumerable.Range(1,100).Select(i=>(double)i).ToList());
        Check(hundred.P50Ms==50,"p50 of hundred is 50");
        Check(hundred.P95Ms==95,"p95 of hundred is 95");
        Check(hundred.P99Ms==99,"p99 of hundred is 99");
        Check(hundred.MaxMs==100,"max of hundred is 100");

        var sortedCheck=GmFrameSummary.From(new List<double>{9,2,7});
        Check(sortedCheck.MaxMs==9,"source list is sorted in place");

        var counter=new GmFrameCounter();
        Check(counter.AdvanceCount(10)==10,"first count delta from zero");
        Check(counter.AdvanceCount(25)==15,"count delta is incremental");
        Check(counter.AdvanceCount(25)==0,"repeated count delta is zero");
        Check(counter.AdvanceTotal(4.5)==4.5,"first total delta from zero");
        Check(Math.Abs(counter.AdvanceTotal(10)-5.5)<1e-9,"total delta is incremental");
        Check(counter.AdvanceTotal(10)==0,"repeated total delta is zero");
        counter.Reset();
        Check(counter.AdvanceCount(3)==3,"reset restores count baseline");
        Check(counter.AdvanceTotal(2)==2,"reset restores total baseline");

        Check(GmFrameSummary.From(new List<double>{1,1,1,1}).P99Ms==1,"repeated values stay stable");

        Check(GmPerformance.PresentBucket(0)==0,"zero drawn frames bucket");
        Check(GmPerformance.PresentBucket(-3)==0,"negative drawn delta bucket");
        Check(GmPerformance.PresentBucket(1)==1,"single drawn frame bucket");
        Check(GmPerformance.PresentBucket(2)==2,"two drawn frames bucket");
        Check(GmPerformance.PresentBucket(17)==2,"many drawn frames bucket");

        var counts=GmCountSummary.From(new List<double>{1,1,1,2,3});
        Check(counts.Count==5,"count summary size");
        Check(counts.Mean==1.6,"count summary mean");
        Check(counts.P50==1,"count summary median");
        Check(counts.Max==3,"count summary max");

        Check(GmPerformance.WallPartitionComplete(0,100)==false,"incomplete partition rejected");
        Check(GmPerformance.WallPartitionComplete(30,100)==false,"partition missing segment rejected");
        Check(GmPerformance.WallPartitionComplete(100,100),"partition segments sum to period");
        Check(GmPerformance.WallPartitionComplete(99.5,100),"partition tolerates rounding");

        Check(GmPerformance.ProcessStepBucket(0)==0,"zero steps bucket");
        Check(GmPerformance.ProcessStepBucket(1)==1,"single step bucket");
        Check(GmPerformance.ProcessStepBucket(2)==2,"two steps bucket");
        Check(GmPerformance.ProcessStepBucket(3)==3,"three steps bucket");
        Check(GmPerformance.ProcessStepBucket(9)==3,"many steps bucket");

        return count;
    }
}
