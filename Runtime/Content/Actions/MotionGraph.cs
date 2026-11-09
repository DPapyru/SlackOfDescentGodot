namespace SlackOfDescent;

public enum MotionConditionKind { InputHeld, InputPressed, InputReleased, Grounded, SpeedX, SpeedY, Health, Hit, Dead, Finished, Timer, Parameter }
public enum MotionComparison { Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual }
public enum MotionEventKind { Damage, Interact, Spawn, Destroy, SdkEvent, MapEvent }
public enum MotionTarget { Enemy, Entity, Any }
public sealed class MotionKey
{
    public double Frame { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Angle { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public int Picture { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public MotionPose Pose => new(X,Y,Angle,ScaleX,ScaleY,Picture);
}
public readonly record struct MotionPose(double X,double Y,double Angle,double ScaleX=1,double ScaleY=1,int Picture=0)
{
    public static MotionPose Lerp(MotionPose a,MotionPose b,double t)
    {
        double delta=((b.Angle-a.Angle)%360+540)%360-180;
        return new(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t,a.Angle+delta*t,a.ScaleX+(b.ScaleX-a.ScaleX)*t,a.ScaleY+(b.ScaleY-a.ScaleY)*t,t>=1?b.Picture:a.Picture);
    }
}
public sealed class MotionTrack
{
    public string Bone { get; set; } = "HEAD";
    public List<MotionKey> Keys { get; set; } = new();
    public MotionPose Sample(double frame)
    {
        if(Keys.Count==0)return new(0,0,0,1,1);
        if(frame<=Keys[0].Frame)return Keys[0].Pose;
        for(int i=1;i<Keys.Count;i++)if(frame<=Keys[i].Frame)return MotionPose.Lerp(Keys[i-1].Pose,Keys[i].Pose,(frame-Keys[i-1].Frame)/(Keys[i].Frame-Keys[i-1].Frame));
        return Keys[^1].Pose;
    }
}
public sealed class MotionPhase
{
    public string Name { get; set; } = "active";
    public double Start { get; set; }
    public double End { get; set; }
}
public sealed class MotionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MotionEventKind Kind { get; set; }
    public double Start { get; set; }
    public double? End { get; set; }
    public bool Contact { get; set; }
    public bool OncePerContact { get; set; } = true;
    public string Bone { get; set; } = "";
    public MotionTarget Target { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 32;
    public double Height { get; set; } = 32;
    public double Amount { get; set; } = 1;
    public string Value { get; set; } = "";
}
public sealed class MotionClip
{
    public string Id { get; set; } = "idle";
    public double Duration { get; set; } = 60;
    public bool Loop { get; set; } = true;
    public List<MotionTrack> Tracks { get; set; } = new();
    public List<MotionKey> Root { get; set; } = new();
    public List<MotionPhase> Phases { get; set; } = new();
    public List<MotionEvent> Events { get; set; } = new();
    public Dictionary<string,MotionPose> Sample(double frame)=>Tracks.Where(t=>t.Keys.Count>0).ToDictionary(t=>t.Bone,t=>t.Sample(frame),StringComparer.Ordinal);
    public MotionPose RootPose(double frame)=>new MotionTrack{Keys=Root}.Sample(frame);
}
public sealed class MotionCondition
{
    public MotionConditionKind Kind { get; set; }
    public MotionComparison Comparison { get; set; } = MotionComparison.Equal;
    public int Key { get; set; }
    public double Value { get; set; } = 1;
    public string Parameter { get; set; } = "";
}
public sealed class MotionTransition
{
    public string Target { get; set; } = "";
    public int Priority { get; set; }
    public List<MotionCondition> Conditions { get; set; } = new();
}
public sealed class MotionMovement
{
    public double? VelocityX { get; set; }
    public double? VelocityY { get; set; }
    public double Gravity { get; set; }
    public double MaxFall { get; set; } = 24;
    public int LeftKey { get; set; } = 37;
    public int RightKey { get; set; } = 39;
    public double? InputSpeed { get; set; }
    public double? JumpSpeed { get; set; }
    public int JumpKey { get; set; } = 32;
    public double DisplaceX { get; set; }
    public double DisplaceY { get; set; }
}
public sealed class MotionState
{
    public string Id { get; set; } = "idle";
    public string Clip { get; set; } = "idle";
    public MotionMovement Movement { get; set; } = new();
    public List<MotionTransition> Transitions { get; set; } = new();
}
public sealed class MotionGraph
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "motion";
    public string Skeleton { get; set; } = "Nana";
    public string Entry { get; set; } = "idle";
    public List<MotionClip> Clips { get; set; } = new();
    public List<MotionState> States { get; set; } = new();
    public Dictionary<string,double> Parameters { get; set; } = new(StringComparer.Ordinal);
    public List<MotionIkChain>? IkChains { get; set; }
    public Dictionary<string,string> EntitySlots { get; set; } = new(StringComparer.Ordinal);
    public void Validate()
    {
        void Require(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
        bool Finite(params double[] values)=>values.All(double.IsFinite);
        void Keys(List<MotionKey> keys,double duration)
        {
            double previous=-1;
            foreach(var k in keys){Require(Finite(k.Frame,k.X,k.Y,k.Angle,k.ScaleX,k.ScaleY)&&k.Frame>=0&&k.Frame<=duration&&k.Frame>previous&&k.Picture>=0,"Invalid or unordered motion key.");previous=k.Frame;}
        }
        Require(FormatVersion==1,"Unsupported motion format.");Require(!string.IsNullOrWhiteSpace(Id),"Motion id is required.");
        Require(Skeleton is "Nana" or "Ryoko" or "Yuki" || MotionSkeleton.IsLegacy(Skeleton),"Unsupported skeleton: "+Skeleton);
        Require(Clips.Count>0&&Clips.Select(c=>c.Id).Distinct().Count()==Clips.Count,"Motion clips must have unique ids.");
        Require(States.Count>0&&States.Select(s=>s.Id).Distinct().Count()==States.Count&&States.Any(s=>s.Id==Entry),"Motion states require unique ids and a valid entry.");
        Require(Parameters.Values.All(double.IsFinite),"Invalid parameter value.");
        foreach(var c in Clips)
        {
            Require(!string.IsNullOrWhiteSpace(c.Id)&&Finite(c.Duration)&&c.Duration>0,"Invalid clip duration.");Keys(c.Root,c.Duration);
            Require(c.Tracks.Select(t=>t.Bone).Distinct().Count()==c.Tracks.Count,"Duplicate bone track.");
            foreach(var track in c.Tracks){Require(MotionSkeleton.IsNodeName(track.Bone),"Unknown skeleton bone: "+track.Bone);Keys(track.Keys,c.Duration);}
            foreach(var p in c.Phases)Require(Finite(p.Start,p.End)&&p.Start>=0&&p.End>=p.Start&&p.End<=c.Duration,"Invalid phase interval.");
            Require(c.Events.Select(e=>e.Id).Distinct().Count()==c.Events.Count,"Duplicate event id.");
            foreach(var e in c.Events)Require(Enum.IsDefined(e.Kind)&&Enum.IsDefined(e.Target)&&!string.IsNullOrWhiteSpace(e.Id)&&Finite(e.Start,e.End??e.Start,e.X,e.Y,e.Width,e.Height,e.Amount)&&e.Start>=0&&(e.End??e.Start)>=e.Start&&(e.End??e.Start)<=c.Duration&&e.Width>0&&e.Height>0&&e.Amount>=0&&(e.Bone.Length==0||MotionSkeleton.IsNodeName(e.Bone)),"Invalid motion event.");
        }
        foreach(var s in States)
        {
            Require(!string.IsNullOrWhiteSpace(s.Id)&&Clips.Any(c=>c.Id==s.Clip),"State references a missing clip.");
            var m=s.Movement;Require(Finite(m.VelocityX??0,m.VelocityY??0,m.Gravity,m.MaxFall,m.InputSpeed??0,m.JumpSpeed??0,m.DisplaceX,m.DisplaceY)&&m.MaxFall>=0,"Invalid movement operation.");
            foreach(var t in s.Transitions)
            {
                Require(States.Any(state=>state.Id==t.Target),"Transition references a missing state.");
                foreach(var c in t.Conditions)Require(Enum.IsDefined(c.Kind)&&Enum.IsDefined(c.Comparison)&&Finite(c.Value)&&(c.Kind!=MotionConditionKind.Parameter||Parameters.ContainsKey(c.Parameter)),"Invalid transition condition.");
            }
        }
        var chainNames = new HashSet<string>(StringComparer.Ordinal);
        foreach(var chain in IkChains ?? [])
        {
            Require(chain.Enabled && MotionSkeleton.IsNodeName(chain.Root) && MotionSkeleton.IsNodeName(chain.Mid) && MotionSkeleton.IsNodeName(chain.End),"Invalid IK chain.");
            Require(chain.Root != chain.Mid && chain.Mid != chain.End && chain.Root != chain.End,"IK chain nodes must be distinct.");
            Require(chainNames.Add(chain.Root+"/"+chain.Mid+"/"+chain.End),"Duplicate IK chain.");
        }
        foreach(var slot in EntitySlots)
            Require(MotionSkeleton.IsNodeName(slot.Key) && !string.IsNullOrWhiteSpace(slot.Value),"Invalid entity slot: "+slot.Key);
        Require(EntitySlots.Values.Distinct(StringComparer.Ordinal).Count()==EntitySlots.Count,"Entity slot target is already occupied.");
    }
    public void CopyFrom(MotionGraph graph)
    {
        foreach (var property in typeof(MotionGraph).GetProperties())
            if (property.CanWrite) property.SetValue(this,property.GetValue(graph));
    }
    public void Save(string path){Validate();NativeContentStore.Save(path,this);}
    public static MotionGraph Load(string path){var graph=NativeContentStore.Load<MotionGraph>(path);graph.Validate();return graph;}
}
