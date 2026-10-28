using SlackOfDescent;

public static class FrameSeparationTests
{
    public static int Run()
    {
        int count=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);count++;}
        var plan=new GmFramePlan();
        Check(plan.Consume() is null,"idle frame plan builds nothing");
        Check(plan.Stats.Skipped==1&&plan.Stats.Published==0,"skipped consumes are counted");
        var stamp=plan.Publish(7,11);
        Check(stamp.Tick==7&&stamp.Room==11&&stamp.Sequence==1,"publish stamps tick room and sequence");
        Check(plan.Dirty,"publish marks the plan dirty");
        Check(plan.Pending==stamp,"pending exposes the unpublished frame");
        var consumed=plan.Consume();
        Check(consumed==stamp,"consume returns the published frame");
        Check(plan.Dirty==false,"consume clears the dirty flag");
        Check(plan.Consume() is null,"second consume in the same frame builds nothing");
        Check(plan.Stats.Rendered==1,"one build per published frame");
        Check(plan.Stats.Skipped==2,"unchanged frames count as skipped");
        for(int step=0;step<3;step++)plan.Publish(8+step,11);
        Check(plan.Stats.Coalesced==2,"repeated publishes inside one frame coalesce");
        Check(plan.Pending!.Value.Sequence==2,"coalescing keeps the first unpublished frame");
        Check(plan.Pending!.Value.Tick==8,"coalescing keeps the earliest tick");
        Check(plan.Consume() is not null&&plan.Consume() is null,"coalesced publish renders once");
        Check(plan.Sequence==2,"sequence advances only on new frames");
        Check(plan.Stats.Published==2&&plan.Stats.Rendered==2,"coalesced stats stay consistent");
        plan.Publish(20,11);
        var switched=plan.Publish(21,12);
        Check(switched.Room==12&&switched.Sequence==4,"room change publishes a fresh frame");
        Check(plan.Room==12,"room change updates the plan room");
        var afterSwitch=plan.Consume();
        Check(afterSwitch!.Value.Room==12,"room change discards the stale room frame");
        Check(plan.Stats.Coalesced==2,"room change is never counted as a coalesced frame");
        plan.Publish(22,12);plan.Publish(23,12);
        Check(plan.Stats.Coalesced==3,"same room publishes coalesce again");
        Check(plan.Stats.Published==5&&plan.Stats.Rendered==3,"published and rendered track switches");
        plan.Reset();
        Check(plan.Consume() is null,"reset drops a pending build");
        Check(plan.Stats.Published==5&&plan.Stats.Rendered==3,"reset keeps published and rendered counters");
        plan.Reset(12);
        Check(plan.Room==12&&plan.Dirty==false,"reset accepts a room hint");
        Check(plan.Publish(-5,-1).Tick==-5,"negative tick and room are carried verbatim");
        Check(plan.Consume()!.Value.Room==-1,"negative room survives consume");
        var stats=plan.Stats;
        Check(stats.Rendered==4&&plan.Dirty==false,"completed frames leave no pending work");
        Check(new GmFramePlan().Stats.CoalesceRatio==0,"empty stats report a zero ratio");
        var shared=new GmFramePlan();
        shared.Publish(1,0);shared.Publish(2,0);shared.Publish(3,0);
        Check(shared.Stats.Published==1&&shared.Stats.Coalesced==2,"three logic steps publish one build");
        Check(shared.Stats.RenderRatio==0,"unconsumed frames report no renders");
        return count;
    }
}
