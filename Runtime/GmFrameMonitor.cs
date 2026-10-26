namespace SlackOfDescent;

public sealed record GmFrameSample(double Seconds,double LogicFps,double? RenderFps,double? LogicBaseline,double? RenderBaseline,bool Dropped,long Builds=0,long LogicSteps=0)
{
    public double? BuildFps=>Seconds<=0?null:Builds/Seconds;
    public double? BuildsPerLogicStep=>LogicSteps<=0?null:(double)Builds/LogicSteps;
}

public sealed class GmFrameMonitor
{
    private int room=int.MinValue;
    private double target;
    private double entered;
    private double window;
    private long firstFrame;
    private long firstBuild;
    private int renders;
    private double lastDrop=double.NegativeInfinity;
    private bool dropping;
    private readonly List<double> logic=new();
    private readonly List<double> render=new();
    public void Reset(){room=int.MinValue;}
    public GmFrameSample? Update(double now,long frame,int currentRoom,double roomSpeed,bool rendering,long builds=0)
    {
        if(currentRoom!=room||target!=roomSpeed)
        {
            room=currentRoom;target=roomSpeed;entered=window=now;firstFrame=frame;firstBuild=builds;renders=0;
            logic.Clear();render.Clear();lastDrop=double.NegativeInfinity;dropping=false;return null;
        }
        if(rendering)renders++;
        double seconds=now-window;
        if(seconds<1)return null;
        long steps=frame-firstFrame,buildCount=builds-firstBuild;
        double logicFps=steps/seconds;
        double? renderFps=rendering?renders/seconds:null;
        window=now;firstFrame=frame;firstBuild=builds;renders=0;
        double? logicBaseline=logic.Count==5?logic.Order().ElementAt(2):null;
        double? renderBaseline=render.Count==5?render.Order().ElementAt(2):null;
        bool below=logicBaseline-logicFps>=5||renderBaseline-renderFps>=5;
        bool dropped=below&&(!dropping||now-lastDrop>=5);
        dropping=below;
        if(dropped)lastDrop=now;
        if(now-entered>3&&logic.Count<5)
        {
            logic.Add(logicFps);
            if(renderFps is double fps)render.Add(fps);
        }
        return new(seconds,logicFps,renderFps,logicBaseline,renderBaseline,dropped,buildCount,steps);
    }
}
