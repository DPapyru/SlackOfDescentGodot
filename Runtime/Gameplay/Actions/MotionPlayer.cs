namespace SlackOfDescent;

public readonly record struct MotionFacts(InputSnapshot Input,bool Grounded,double SpeedX,double SpeedY,double Health,bool Hit,bool Dead);
public readonly record struct MotionIntent(double VelocityX,double VelocityY,double DisplaceX,double DisplaceY);
public interface IMotionHost
{
    MotionFacts Facts { get; }
    void Move(MotionIntent intent);
    void Event(MotionEvent item,long cycle);
}
public sealed class MotionPlayer
{
    public MotionGraph Graph { get; }
    private readonly IMotionHost host;
    public string State { get; private set; }
    public double Frame { get; private set; }
    public double Timer { get; private set; }
    public long Cycle { get; private set; }
    private double previous=-1;
    public bool Playing { get; private set; } = true;
    public Dictionary<string,double> Parameters { get; }
    public MotionClip Clip=>Graph.Clips.Single(c=>c.Id==Graph.States.Single(s=>s.Id==State).Clip);
    public IReadOnlyDictionary<string,MotionPose> Pose=>Clip.Sample(Frame);
    public MotionPose Root=>Clip.RootPose(Frame);
    public MotionPlayer(MotionGraph graph,IMotionHost host)
    {
        graph.Validate();Graph=graph;this.host=host;State=graph.Entry;Parameters=new(graph.Parameters,StringComparer.Ordinal);
    }
    public void Stop()=>Playing=false;
    public void Seek(double frame){if(!double.IsFinite(frame))throw new ArgumentOutOfRangeException(nameof(frame));Frame=Math.Clamp(frame,0,Clip.Duration);previous=Frame;}
    public void Restart(){State=Graph.Entry;Frame=Timer=0;previous=-1;Cycle++;Playing=true;}
    public void Tick()
    {
        if(!Playing)return;
        var state=Graph.States.Single(s=>s.Id==State);
        var transition=state.Transitions.OrderByDescending(t=>t.Priority).FirstOrDefault(t=>t.Conditions.All(Matches));
        if(transition is not null){State=transition.Target;Frame=Timer=0;previous=-1;Cycle++;state=Graph.States.Single(s=>s.Id==State);}
        else if(previous>=0){Frame=Math.Min(Clip.Duration,Frame+1);Timer++;}
        if(Frame>=Clip.Duration&&Clip.Loop)
        {
            foreach(var e in Clip.Events.Where(e=>e.Start>previous&&e.Start<=Frame))host.Event(e,Cycle);
            Frame=0;previous=-1;Cycle++;
        }
        var facts=host.Facts;var m=state.Movement;
        double vx=m.InputSpeed is {} speed?((facts.Input.Down(m.RightKey)?1:0)-(facts.Input.Down(m.LeftKey)?1:0))*speed:m.VelocityX??facts.SpeedX;
        double vy=m.VelocityY??facts.SpeedY;
        vy=Math.Min(m.MaxFall,vy+m.Gravity);
        if(m.JumpSpeed is {} jump&&facts.Grounded&&facts.Input.JustPressed(m.JumpKey))vy=-Math.Abs(jump);
        host.Move(new(vx,vy,m.DisplaceX,m.DisplaceY));
        foreach(var e in Clip.Events)
            if(e.Contact?(Frame>=e.Start&&Frame<=(e.End??e.Start)||e.Start>previous&&e.Start<=Frame):e.Start>previous&&e.Start<=Frame)host.Event(e,Cycle);
        previous=Frame;
    }
    private bool Matches(MotionCondition c)
    {
        var f=host.Facts;
        double value=c.Kind switch
        {
            MotionConditionKind.InputHeld=>f.Input.Down(c.Key)?1:0,
            MotionConditionKind.InputPressed=>f.Input.JustPressed(c.Key)?1:0,
            MotionConditionKind.InputReleased=>f.Input.Released.Contains(c.Key)?1:0,
            MotionConditionKind.Grounded=>f.Grounded?1:0,MotionConditionKind.SpeedX=>f.SpeedX,MotionConditionKind.SpeedY=>f.SpeedY,
            MotionConditionKind.Health=>f.Health,MotionConditionKind.Hit=>f.Hit?1:0,MotionConditionKind.Dead=>f.Dead?1:0,
            MotionConditionKind.Finished=>Frame>=Clip.Duration?1:0,MotionConditionKind.Timer=>Timer,
            MotionConditionKind.Parameter=>Parameters.GetValueOrDefault(c.Parameter),_=>throw new InvalidDataException("Unknown condition.")
        };
        return c.Comparison switch{MotionComparison.Equal=>value==c.Value,MotionComparison.NotEqual=>value!=c.Value,MotionComparison.Greater=>value>c.Value,MotionComparison.GreaterOrEqual=>value>=c.Value,MotionComparison.Less=>value<c.Value,MotionComparison.LessOrEqual=>value<=c.Value,_=>false};
    }
}
