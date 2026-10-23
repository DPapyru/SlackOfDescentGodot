using SlackOfDescent;

public static class FixedClockTests
{
    public static int Run()
    {
        int count=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);count++;}
        foreach(int rate in new[]{20,30,60,120,144})
        {
            var clock=new GmFixedClock();int legacy=0,ticks=0;
            for(int frame=0;frame<rate*10;frame++)
                for(int t=clock.Advance(1.0/rate);t>0;t--){clock.BeginTick();ticks++;if(clock.LegacyDue(30))legacy++;}
            Check(ticks==600,"native ticks preserve ten seconds at "+rate);
            Check(legacy==300,"legacy discrete events retain frequency at "+rate);
            Check(clock.GameTime==10,"game time uses completed fixed ticks");
        }
        var paused=new GmFixedClock();paused.Advance(.01);paused.Pause();
        Check(paused.Advance(.01)==0,"modal resume discards fractional backlog");
        Check(paused.Advance(5)==15,"system stall cannot enqueue seconds of actions");
        bool rejected=false;try{paused.LegacyDue(3000);}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"loading frequency never becomes gameplay frequency");
        return count;
    }
}
