namespace SlackOfDescent;

public sealed record GmFrameSample(double Seconds,double LogicFps,double? RenderFps,double? LogicBaseline,double? RenderBaseline,bool Dropped);

public sealed class GmFrameMonitor
{
    private int room=int.MinValue;
    private double target;
    private double entered;
    private double window;
    private long firstFrame;
    private int renders;
    private double lastDrop=double.NegativeInfinity;
    private bool dropping;
    private readonly List<double> logic=new();
    private readonly List<double> render=new();
    public void Reset(){room=int.MinValue;}
    public GmFrameSample? Update(double now,long frame,int currentRoom,double roomSpeed,bool rendering)
    {
        if(currentRoom!=room||target!=roomSpeed)
        {
            room=currentRoom;target=roomSpeed;entered=window=now;firstFrame=frame;renders=0;
            logic.Clear();render.Clear();lastDrop=double.NegativeInfinity;dropping=false;return null;
        }
        if(rendering)renders++;
        double seconds=now-window;
        if(seconds<1)return null;
        double logicFps=(frame-firstFrame)/seconds;
        double? renderFps=rendering?renders/seconds:null;
        window=now;firstFrame=frame;renders=0;
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
        return new(seconds,logicFps,renderFps,logicBaseline,renderBaseline,dropped);
    }
}
