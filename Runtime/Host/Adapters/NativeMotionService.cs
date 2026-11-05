namespace SlackOfDescent;

public sealed record NativeBone(string Name,int Instance,int Parent,double X,double Y,double Angle);
public sealed record NativeMotionState(string State,double Frame,long Cycle,bool Suspended);
public sealed class NativeMotionService : IDisposable
{
    private readonly NativeModServices services;
    private readonly Dictionary<int,Session> sessions=new();
    private readonly Dictionary<int,(ActorContext Actor,CharacterDefinition Definition)> configured=new();
    private readonly Dictionary<int,double> gravities=new();
    private InputSnapshot input=InputSnapshot.Empty;
    private readonly HashSet<int> pressed=new(),released=new();
    private IReadOnlySet<int> held=new HashSet<int>();
    internal void CaptureInput(InputSnapshot snapshot){held=snapshot.Held;pressed.UnionWith(snapshot.Pressed);released.UnionWith(snapshot.Released);}
    private double accumulator;
    internal NativeMotionService(NativeModServices services)
    {
        this.services=services;services.Registry.OwnerRetired+=Retire;services.Registry.Committed+=Refresh;
    }
    public IReadOnlyList<NativeBone> Skeleton(ActorContext actor)=>Resolve(actor).Select(p=>new NativeBone(p.Key,p.Value.Id,p.Value.Scope.ReadOr("parent",actor.Id).Int,p.Value.Scope.ReadOr("x").Number,p.Value.Scope.ReadOr("y").Number,p.Value.Scope.ReadOr("image_angle").Number)).ToArray();
    private void CheckWorld(ActorContext actor){if(!ReferenceEquals(actor.World,services.World))throw new InvalidDataException("Motion actor belongs to a different world.");}
    internal Dictionary<string,GmInstance> Resolve(ActorContext actor,MotionGraph? graph=null)
    {
        CheckWorld(actor);
        var bones=new Dictionary<string,GmInstance>(StringComparer.Ordinal);
        var descriptor=MotionSkeletonDescriptor.FromActor(services.World,actor.Instance);
        foreach(var node in descriptor.Nodes) bones[node.Name]=services.World.Instances.First(i=>i.Id==node.Instance);
        if(graph is not null && graph.EntitySlots.Count>0)
        {
            foreach(var slot in graph.EntitySlots)
            {
                if(!TryResolveSlot(descriptor,slot.Value,out var node))throw new InvalidDataException("Entity slot target is missing: "+slot.Value);
                bones[slot.Key]=services.World.Instances.First(i=>i.Id==node.Instance);
            }
        }
        if(graph is not null)
            foreach(var track in graph.Clips.SelectMany(clip=>clip.Tracks).Select(track=>track.Bone).Distinct(StringComparer.Ordinal))
                if(!bones.ContainsKey(track))throw new InvalidDataException("Motion track is not bound to the target skeleton: "+track);
        return bones;

        static bool TryResolveSlot(MotionSkeletonDescriptor descriptor,string target,out MotionSkeletonNode node)
        {
            if(descriptor.ByName.TryGetValue(target,out node))return true;
            if(MotionSkeleton.TryProcess(target,out int process))
            {
                node=descriptor.Nodes.FirstOrDefault(item=>item.Process==process);return node.Instance>=0;
            }
            if(target.StartsWith("Process[",StringComparison.Ordinal)&&target.EndsWith(']')&&int.TryParse(target.AsSpan(8,target.Length-9),out process))
            {
                node=descriptor.Nodes.FirstOrDefault(item=>item.Process==process);return node.Instance>=0;
            }
            if(int.TryParse(target,out int id))
            {
                node=descriptor.Nodes.FirstOrDefault(item=>item.Instance==id);return node.Instance>=0;
            }
            node=default;return false;
        }
    }
    internal void Validate(ActorContext actor,MotionGraph graph,string? preset=null)
    {
        if(!ReferenceEquals(actor.World,services.World))throw new InvalidDataException("Motion actor belongs to a different world.");
        graph.Validate();Resolve(actor,graph);
        bool player=services.World.Constants.TryGetValue("objPlayerDoll",out var playerObject)&&services.World.IsObject(actor.Instance.Object,playerObject.Int);
        string actual=preset??services.World.Globals.ReadOr("Chara","Nana").ToString();
        if(player&&actual!=graph.Skeleton)throw new InvalidDataException("Motion skeleton mismatch: "+graph.Skeleton+" / "+actual);
        if(!player&&!MotionSkeleton.IsLegacy(graph.Skeleton))throw new InvalidDataException("Non-player motion requires a legacy skeleton id.");
        if(!player&&MotionSkeletonDescriptor.FromActor(services.World,actor.Instance).Id!=graph.Skeleton)throw new InvalidDataException("Motion skeleton mismatch: "+graph.Skeleton+" / "+MotionSkeletonDescriptor.FromActor(services.World,actor.Instance).Id);
        var bridge=NativeActorBridges.Find(services);
        if(bridge?.HasFullController(actor.Id)==true)throw new InvalidDataException("Character already has a full controller.");
    }
    public void Play(ActorContext actor,string graph)=>Play(actor,services.Registry.MotionGraphs.TryGetValue(graph,out var asset)?asset:throw new InvalidDataException("Motion graph is not registered: "+graph));
    public void Play(ActorContext actor,MotionGraph graph)
    {
        Validate(actor,graph);
        Replace(actor,graph,services.CurrentScope,true);
    }
    private void Replace(ActorContext actor,MotionGraph graph,NativeResourceScope scope,bool runtime)
    {
        ForgetSession(actor.Id,false);
        NativeActorBridges.Find(services)?.SuspendMovement(actor.Id);
        if(string.IsNullOrEmpty(scope.Contribution))scope=services.Registry.ScopeFor("motion",graph.Id,actor.Id);
        scope=scope with{Contribution=scope.Contribution+"/play:"+Guid.NewGuid().ToString("N"),Actor=actor.Id};
        sessions[actor.Id]=new(this,actor,graph,scope,runtime);
    }
    public void Stop(ActorContext actor)
    {
        CheckWorld(actor);
        bool restore=sessions.TryGetValue(actor.Id,out var current)&&current.Runtime;
        ForgetSession(actor.Id);
        if(restore&&configured.TryGetValue(actor.Id,out var binding)&&binding.Definition.MotionGraph is {} id&&services.Registry.MotionGraphs.TryGetValue(id,out var graph))
            Replace(actor,graph,services.Registry.ScopeFor("motion",id,actor.Id),false);
    }
    public NativeMotionState? State(ActorContext actor){CheckWorld(actor);return sessions.TryGetValue(actor.Id,out var s)?new(s.Player.State,s.Player.Frame,s.Player.Cycle,s.Suspended):null;}
    public void Seek(ActorContext actor,double frame){CheckWorld(actor);if(!sessions.TryGetValue(actor.Id,out var session))throw new InvalidOperationException("Motion is not playing.");session.Player.Seek(frame);session.Submit();}
    public void Parameter(ActorContext actor,string name,double value)
    {
        CheckWorld(actor);
        if(!double.IsFinite(value)||!sessions.TryGetValue(actor.Id,out var session)||!session.Player.Parameters.ContainsKey(name))throw new InvalidDataException("Motion parameter is missing or invalid.");
        session.Player.Parameters[name]=value;
    }
    internal bool Controls(int id)=>sessions.ContainsKey(id);
    internal bool Active(int id)=>sessions.TryGetValue(id,out var s)&&!s.Forced();
    internal void Configure(ActorContext actor,CharacterDefinition definition)
    {
        configured[actor.Id]=(actor,definition);
        if(definition.MotionGraph is not {} id){if(sessions.TryGetValue(actor.Id,out var previous)&&!previous.Runtime)ForgetSession(actor.Id);return;}
        if(!services.Registry.MotionGraphs.TryGetValue(id,out var graph))throw new InvalidDataException("Character motion graph is missing: "+id);
        Validate(actor,graph,definition.BasePreset);
        if(sessions.TryGetValue(actor.Id,out var existing)&&(existing.Runtime||ReferenceEquals(existing.Asset,graph)))return;
        Replace(actor,graph,services.Registry.ScopeFor("motion",id,actor.Id),false);
    }
    internal void Preview(ActorContext actor,MotionGraph graph,string clipId,double frame)
    {
        CheckWorld(actor);
        var clip=graph.Clips.SingleOrDefault(item=>item.Id==clipId)??throw new InvalidDataException("Motion clip is missing: "+clipId);
        ApplyPose(actor,Resolve(actor,graph),clip.Sample(frame),clip.RootPose(frame));
    }
    private static void ApplyPose(ActorContext actor,Dictionary<string,GmInstance> bones,IReadOnlyDictionary<string,MotionPose> pose,MotionPose root)
    {
        double direction=actor.Instance.Scope.ReadOr("Direction",1).Number;
        var done=new HashSet<int>();var visiting=new HashSet<int>();
        void Apply(string name)
        {
            var bone=bones[name];if(done.Contains(bone.Id))return;
            if(!visiting.Add(bone.Id))throw new InvalidDataException("Cyclic skeleton parents.");
            int parentId=bone.Scope.ReadOr("parent",actor.Id).Int;
            string? parentName=bones.FirstOrDefault(p=>p.Value.Id==parentId).Key;
            if(parentName is not null)Apply(parentName);
            double parentX=parentName is null?actor.X+root.X*direction:bones[parentName].Scope.ReadOr("x").Number;
            double parentY=parentName is null?actor.Y+root.Y:bones[parentName].Scope.ReadOr("y").Number;
            double parentAngle=parentName is null?actor.Read("image_angle"):bones[parentName].Scope.ReadOr("image_angle").Number;
            bool tracked=pose.TryGetValue(name,out var p);
            if(!tracked)
            {
                double localAngle=bone.Scope.ReadOr("PartPosDir").Number*Math.PI/180,localDistance=bone.Scope.ReadOr("PartPosDis").Number;
                p=new(Math.Cos(localAngle)*localDistance,-Math.Sin(localAngle)*localDistance,bone.Scope.ReadOr("Angle").Number,bone.Scope.ReadOr("ScaleX",1).Number,bone.Scope.ReadOr("ScaleY",1).Number,bone.Scope.ReadOr("PartPicture").Int);
            }
            double radians=parentAngle*Math.PI/180;
            actor.World.Write(bone,"x",parentX+(p.X*Math.Cos(radians)+p.Y*Math.Sin(radians))*direction);
            actor.World.Write(bone,"y",parentY-p.X*Math.Sin(radians)+p.Y*Math.Cos(radians));
            bone.Scope.Write("Direction",direction);bone.Scope.Write("image_angle",p.Angle+parentAngle);
            if(tracked){bone.Scope.Write("Angle",p.Angle);bone.Scope.Write("ScaleX",p.ScaleX);bone.Scope.Write("ScaleY",p.ScaleY);bone.Scope.Write("PartPicture",p.Picture);bone.Scope.Write("image_index",p.Picture);}
            visiting.Remove(bone.Id);done.Add(bone.Id);
        }
        foreach(string name in bones.Keys)Apply(name);
    }
    private void Refresh()
    {
        foreach(var pair in configured.Values.ToArray())
            if(pair.Definition.MotionGraph is {} id&&!services.Registry.MotionGraphs.ContainsKey(id))Forget(pair.Actor.Id);
            else Configure(pair.Actor,pair.Definition);
    }
    private void ForgetSession(int id,bool resume=true)
    {
        if(sessions.Remove(id,out var s)){s.Player.Stop();services.ClearScope(s.Scope);s.RestorePose();}
        if(resume)NativeActorBridges.Find(services)?.ResumeMovement(id);
    }
    internal void Forget(int id){configured.Remove(id);ForgetSession(id);}
    private void Retire(NativeModOwner owner)
    {
        foreach(var s in sessions.Values.Where(s=>s.Scope.Owner==owner).ToArray())Forget(s.Actor.Id);
    }
    internal void ClearScope(NativeResourceScope scope)
    {
        foreach(var s in sessions.Values.Where(s=>s.Scope==scope).ToArray())Forget(s.Actor.Id);
    }
    internal void LeaveRoom(){foreach(int id in sessions.Keys.ToArray())Forget(id);configured.Clear();accumulator=0;pressed.Clear();released.Clear();}
    internal void BeforePhysics()
    {
        double rate=Math.Max(1,services.World.Engine.Read("room_speed").Number);accumulator+=60/rate;
        int ticks=(int)accumulator;accumulator-=ticks;
        var frameInput=new InputSnapshot(held,new HashSet<int>(pressed),new HashSet<int>(released));
        input=frameInput;
        foreach(var s in sessions.Values.ToArray())
        {
            if(s.Actor.Instance.Destroyed||!services.World.Instances.Contains(s.Actor.Instance)){Forget(s.Actor.Id);continue;}
            if(!s.Actor.Instance.Active)continue;
            if(s.Forced()){s.Suspended=true;continue;}
            if(s.Suspended){s.Player.Restart();s.Suspended=false;s.Hits.Clear();}
            input=frameInput;
            s.SyncVelocity(rate/60);
            using var scope=services.EnterScope(s.Scope);
            for(int tick=0;tick<ticks;tick++)
            {
                s.Player.Tick();
                if(tick==0)input=new(frameInput.Held,new HashSet<int>(),new HashSet<int>());
            }
            s.Integrate(60/rate);
        }
        if(ticks>0){pressed.Clear();released.Clear();}
    }
    internal void AfterStep()
    {
        foreach(var pair in gravities)if(services.Entities.Find(pair.Key) is {} actor)actor.Write("gravity",pair.Value);
        gravities.Clear();
        foreach(var s in sessions.Values.ToArray())if(!s.Suspended&&!s.Forced())s.Submit();
    }
    internal bool Suppress(string key,GmContext context)
    {
        if(!key.StartsWith("objects/objController/3/",StringComparison.Ordinal))return false;
        int id=context.Self.Scope.ReadOr("CtrlTarget",-4).Int;
        if(!sessions.ContainsKey(id))id=sessions.Keys.FirstOrDefault(id=>services.World.IsObject(sessions[id].Actor.Instance.Object,context.Self.Scope.ReadOr("CtrlTarget",-4).Int));
        if(!Active(id))return false;
        return key is "objects/objController/3/0/13" or "objects/objController/3/0/17" or "objects/objController/3/0/18" or "objects/objController/3/0/22" or "objects/objController/3/0/23";
    }
    public void Dispose(){services.Registry.OwnerRetired-=Retire;services.Registry.Committed-=Refresh;LeaveRoom();}
    private sealed class Session : IMotionHost
    {
        private readonly NativeMotionService owner;
        public ActorContext Actor { get; }
        public MotionPlayer Player { get; }
        public MotionGraph Asset { get; }
        public NativeResourceScope Scope { get; }
        public bool Runtime { get; }
        public bool Suspended;
        public readonly HashSet<(long,string,int)> Hits=new();
        private readonly Dictionary<string,GmInstance> bones;
        private readonly Dictionary<int,BonePose> original = new();
        private double vx,vy,dx,dy;
        public Session(NativeMotionService owner,ActorContext actor,MotionGraph graph,NativeResourceScope scope,bool runtime)
        {this.owner=owner;Actor=actor;Scope=scope;Runtime=runtime;bones=owner.Resolve(actor,graph);foreach(var bone in bones.Values.GroupBy(value=>value.Id).Select(group=>group.First()))original[bone.Id]=new(bone.Scope.ReadOr("x").Number,bone.Scope.ReadOr("y").Number,bone.Scope.ReadOr("image_angle").Number,bone.Scope.ReadOr("Angle").Number,bone.Scope.ReadOr("ScaleX",1).Number,bone.Scope.ReadOr("ScaleY",1).Number,bone.Scope.ReadOr("PartPicture").Int,bone.Scope.ReadOr("image_index").Number,bone.Scope.ReadOr("Direction",1).Number);Asset=graph;var copy=System.Text.Json.JsonSerializer.Deserialize<MotionGraph>(System.Text.Json.JsonSerializer.Serialize(graph,NativeContentStore.Json),NativeContentStore.Json)!;Player=new(copy,this);}
        private readonly record struct BonePose(double X,double Y,double ImageAngle,double Angle,double ScaleX,double ScaleY,int Picture,double ImageIndex,double Direction);
        public void RestorePose()
        {
            foreach(var bone in bones.Values.GroupBy(value=>value.Id).Select(group=>group.First()))
                if(original.TryGetValue(bone.Id,out var pose)){Actor.World.Write(bone,"x",pose.X);Actor.World.Write(bone,"y",pose.Y);bone.Scope.Write("image_angle",pose.ImageAngle);bone.Scope.Write("Angle",pose.Angle);bone.Scope.Write("ScaleX",pose.ScaleX);bone.Scope.Write("ScaleY",pose.ScaleY);bone.Scope.Write("PartPicture",pose.Picture);bone.Scope.Write("image_index",pose.ImageIndex);bone.Scope.Write("Direction",pose.Direction);}
        }
        public bool Forced()
        {
            bool player=owner.services.World.Constants.TryGetValue("objPlayerDoll",out var playerObject)&&owner.services.World.IsObject(Actor.Instance.Object,playerObject.Int);
            if(!player)return Actor.Instance.Destroyed;
            return !NativeActorActions.CanAct(Actor)||Actor.Read("touchingQS")!=0||Actor.Read("touchingTablet")!=0||Actor.Read("DMG")>0;
        }
        public MotionFacts Facts=>new(owner.input,NativeActorActions.Grounded(Actor),vx,vy,Actor.World.Globals.ReadOr("PlayerHP",1).Number,Actor.Read("DMG")>0,Actor.World.Globals.ReadOr("PlayerHP",1).Number<=0);
        public void Move(MotionIntent intent){vx=intent.VelocityX;vy=intent.VelocityY;dx+=intent.DisplaceX;dy+=intent.DisplaceY;}
        public void SyncVelocity(double scale){vx=Actor.HorizontalSpeed*scale;vy=Actor.VerticalSpeed*scale;dx=dy=0;}
        public void Integrate(double scale)
        {
            var w=Actor.World;double x=Actor.X,y=Actor.Y;
            double MoveAxis(double distance,bool horizontal)
            {
                if(Math.Abs(distance)>4096)throw new InvalidDataException("Motion displacement exceeds world integration limit.");
                double moved=0,sign=Math.Sign(distance);
                while(Math.Abs(moved)<Math.Abs(distance))
                {
                    double step=sign*Math.Min(1,Math.Abs(distance-moved));
                    if(!Actor.Call("place_free",x+(horizontal?step:0),y+(horizontal?0:step)).Truth)break;
                    if(horizontal)x+=step;else y+=step;moved+=step;
                }
                return moved;
            }
            double desiredX=vx*scale+dx,desiredY=vy*scale+dy;
            double actualX=MoveAxis(desiredX,true),actualY=MoveAxis(desiredY,false);
            Actor.HorizontalSpeed=actualX;Actor.VerticalSpeed=actualY;
            owner.gravities[Actor.Id]=Actor.Read("gravity");Actor.Write("gravity",0);
            if(vx!=0)Actor.Write("Direction",Math.Sign(vx));
        }
        public void Submit()=>ApplyPose(Actor,bones,Player.Pose,Player.Root);
        public void Event(MotionEvent item,long cycle)
        {
            if(Hits.Count>0&&Hits.First().Item1!=cycle)Hits.Clear();
            if(!item.Contact){Execute(item,Actor);return;}
            Submit();
            double x=Actor.X,y=Actor.Y,direction=Actor.Instance.Scope.ReadOr("Direction",1).Number;
            if(item.Bone.Length>0){var bone=bones[item.Bone];x=bone.Scope.ReadOr("x").Number;y=bone.Scope.ReadOr("y").Number;}
            double offsetX=item.X*direction,offsetY=item.Y,width=item.Width,height=item.Height;
            if(item.Bone.Length>0)
            {
                var bone=bones[item.Bone];double a=bone.Scope.ReadOr("image_angle").Number*direction*Math.PI/180,sx=bone.Scope.ReadOr("ScaleX",1).Number,sy=bone.Scope.ReadOr("ScaleY",1).Number;
                offsetX=item.X*sx*direction*Math.Cos(a)+item.Y*sy*Math.Sin(a);offsetY=-item.X*sx*direction*Math.Sin(a)+item.Y*sy*Math.Cos(a);
                width=Math.Abs(item.Width*sx*Math.Cos(a))+Math.Abs(item.Height*sy*Math.Sin(a));height=Math.Abs(item.Width*sx*Math.Sin(a))+Math.Abs(item.Height*sy*Math.Cos(a));
            }
            var region=new GmBounds(x+offsetX-width/2,y+offsetY-height/2,x+offsetX+width/2,y+offsetY+height/2);
            foreach(var target in Actor.World.Instances.ToArray())
            {
                if(target.Id==Actor.Id||target.Destroyed||!target.Active||bones.Values.Contains(target))continue;
                if(item.Target==MotionTarget.Enemy&&!Actor.World.IsObject(target.Object,Actor.World.Constants["objEnemyDoll"].Int))continue;
                if(item.Target==MotionTarget.Entity&&!owner.services.Entities.IsNative(target.Id))continue;
                var bounds=Actor.World.Bounds(target);if(bounds.Empty||!region.Overlaps(bounds))continue;
                if(item.OncePerContact&&!Hits.Add((cycle,item.Id,target.Id)))continue;
                Execute(item,new(Actor.World,target));
            }
        }
        private void Execute(MotionEvent item,ActorContext target)
        {
            var services=owner.services;
            switch(item.Kind)
            {
                case MotionEventKind.Damage:services.Combat.Damage(Actor,new(target,target.X,target.Y,0,false),item.Amount,Actor.Read("Direction")<0?180:0);break;
                case MotionEventKind.Interact:services.Entities.Interact(target.Id,Actor);break;
                case MotionEventKind.Spawn:services.Entities.Spawn(item.Value,Actor.X+item.X,Actor.Y+item.Y);break;
                case MotionEventKind.Destroy:services.Entities.Destroy(target.Id);break;
                case MotionEventKind.SdkEvent:var value=new NativeGameEvent(item.Value,Actor);value.Arguments["target"]=target.Id;services.Dispatch(value,NativeEventPhase.Before);services.Dispatch(value,NativeEventPhase.After);break;
                case MotionEventKind.MapEvent:services.Maps.Trigger(item.Value,Actor);break;
            }
        }
    }
}
