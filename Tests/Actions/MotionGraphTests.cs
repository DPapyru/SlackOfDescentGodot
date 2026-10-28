using SlackOfDescent;

public static class MotionGraphTests
{
    public static int Run(GmWorld world)
    {
        int count=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        bool Throws(Action action,string text){try{action();return false;}catch(Exception error){return error.Message.Contains(text,StringComparison.Ordinal);}}
        var clip=new MotionClip{Id="idle",Duration=4,Loop=false,Tracks=[new(){Bone="HEAD",Keys=[new(){Frame=0,Angle=350},new(){Frame=4,X=8,Angle=10,ScaleX=2,Picture=3}]}]};
        var pose=clip.Sample(2)["HEAD"];
        Check(pose.X==4&&pose.ScaleX==1.5&&Math.Abs(pose.Angle-360)<.001&&pose.Picture==0,"timeline interpolation and stepped pictures");
        var graph=new MotionGraph{Id="test",Entry="idle",Clips=[clip],States=[new(){Id="idle",Clip="idle",Transitions=[new(){Target="run",Priority=1,Conditions=[new(){Kind=MotionConditionKind.InputPressed,Key=39}]},new(){Target="jump",Priority=2,Conditions=[new(){Kind=MotionConditionKind.Grounded}]}]},new(){Id="run",Clip="idle"},new(){Id="jump",Clip="idle",Transitions=[new(){Target="run"}]}]};
        var host=new Host{Facts=new(new(new HashSet<int>{39},new HashSet<int>{39},new HashSet<int>()),true,0,0,100,false,false)};
        var player=new MotionPlayer(graph,host);player.Tick();
        Check(player.State=="jump","transition priority is explicit");
        Check(player.Frame==0,"one transition per tick starts target at frame zero");
        player.Tick();Check(player.State=="run","next transition waits for next tick");
        graph.States[2].Transitions.Clear();
        var restored=System.Text.Json.JsonSerializer.Deserialize<MotionGraph>(System.Text.Json.JsonSerializer.Serialize(graph,NativeContentStore.Json),NativeContentStore.Json)!;
        restored.Validate();Check(restored.Clips[0].Sample(2)["HEAD"]==pose,"save reload preserves poses");
        var document=new MotionGraphDocument(restored);document.Edit(g=>g.Clips[0].Tracks[0].Keys[0].X=12);document.Undo();Check(document.Graph.Clips[0].Tracks[0].Keys[0].X==0,"undo restores timeline");document.Redo();Check(document.Graph.Clips[0].Tracks[0].Keys[0].X==12,"redo restores edit");
        var ik=MotionIk.Solve((0d,0d),(3d,0d),(6d,0d),(20d,0d),true);Check(ik.Success&&ik.Clamped&&Math.Abs(ik.Reach-6)<.001,"IK clamps unreachable endpoint");
        Check(!MotionIk.Solve((0d,0d),(0d,0d),(2d,0d),(1d,0d)).Success,"IK rejects zero length segment");
        Check(!MotionIk.Solve((0d,0d),(2d,0d),(4d,0d),(0d,0d)).Success,"IK rejects coincident root target");
        var up=MotionIk.Solve((0d,0d),(2d,0d),(4d,0d),(2d,2d),true);var down=MotionIk.Solve((0d,0d),(2d,0d),(4d,0d),(2d,2d),false);Check(up.Success&&down.Success&&Math.Abs(up.RootAngle-down.RootAngle)>.1,"IK keeps bend direction");
        var local=MotionRigCoordinates.ToLocal(100,190,100,200,90,-1);Check(Math.Abs(local.X-10)<.001&&Math.Abs(local.Y)<.001,"world drag converts to local coordinates with actor mirror");
        var rig=new MotionGraph{Id="legacy.rig",Skeleton="legacy:objEnemy",Clips=[new(){Id="idle",Tracks=[new(){Bone="process:0",Keys=[new(){Frame=0}]}]}],States=[new(){Id="idle",Clip="idle"}],EntitySlots=new(){["process:0"]="process:0"}};rig.Validate();Check(rig.Skeleton=="legacy:objEnemy"&&rig.EntitySlots["process:0"]=="process:0","legacy process graph validates");
        int processObject=world.Constants["objEnemyDoll"].Int;var processActor=world.Create(processObject,0,0,run:false);var processPart=world.Create(processObject,4,0,run:false);processPart.Scope.Write("parent",processActor.Id);processPart.Scope.Write("PartPosDir",0);processPart.Scope.Write("PartPosDis",4);processActor.Scope.Write("Process",processPart.Object,0);var descriptor=MotionSkeletonDescriptor.FromActor(world,processActor);Check(descriptor.Contains("process:0")&&descriptor.Node("process:0").Parent==processActor.Id,"Process/parent skeleton resolves stable node");double originalAngle=processPart.Scope.ReadOr("image_angle").Number;var processGraph=new MotionGraph{Id="legacy.play",Skeleton="legacy:objEnemyDoll",Clips=[new(){Id="idle",Tracks=[new(){Bone="process:0",Keys=[new(){Frame=0,Angle=27}]}]}],States=[new(){Id="idle",Clip="idle"}],EntitySlots=new(){["process:0"]="process:0"}};using(var processBridge=new GmNativeBridge(world,new ContentRegistry())){var processContext=new ActorContext(world,processActor);processBridge.Services.Motions.Play(processContext,processGraph);processBridge.Update(new(FrameContext.FixedDelta,0,1,InputSnapshot.Empty));processBridge.Services.Motions.Stop(processContext);}Check(Math.Abs(processPart.Scope.ReadOr("image_angle").Number-originalAngle)<.001,"stopping legacy motion restores original pose");processActor.Destroyed=true;processPart.Destroyed=true;
        var duplicateSlots=new MotionGraph{Skeleton="legacy:objEnemyDoll",Clips=[new(){Tracks=[new(){Bone="process:0"},new(){Bone="process:1"}]}],States=[new(){Id="idle",Clip="idle"}],EntitySlots=new(){{"process:0","process:0"},{"process:1","process:0"}}};duplicateSlots.Clips[0].Id="idle";Check(Throws(() => duplicateSlots.Validate(),"occupied"),"duplicate entity slots report conflict");
        var drag=new MotionGraph{Id="ik.drag",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="BODY_TOP",Keys=[new(){Frame=0,X=0,Y=0}]},new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=3,Y=0}]},new(){Bone="HAND1_BELOW",Keys=[new(){Frame=0,X=3,Y=0}]}]}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND1_BELOW"}]};
        var dragDoc=new MotionGraphDocument(drag);var dragged=dragDoc.Drag("idle","HAND1_BELOW",0,4,2);var midPose=dragDoc.Graph.Clips[0].Tracks.Single(t=>t.Bone=="HAND1_ABOVE").Keys[0];var endPose=dragDoc.Graph.Clips[0].Tracks.Single(t=>t.Bone=="HAND1_BELOW").Keys[0];Check(dragged.Success&&Math.Abs(Math.Sqrt(midPose.X*midPose.X+midPose.Y*midPose.Y)-3)<.001&&Math.Abs(Math.Sqrt(endPose.X*endPose.X+endPose.Y*endPose.Y)-3)<.001,"IK drag preserves both segment lengths");dragDoc.Undo();Check(dragDoc.Graph.Clips[0].Tracks.Single(t=>t.Bone=="HAND1_BELOW").Keys[0].X==3,"IK drag is one undo operation");
        int imported=0,rejected=0;
        foreach(var pair in world.Code.Where(p=>p.Key.StartsWith("external/Chara/Motion/",StringComparison.Ordinal)))
            try{var motion=MotionImport.FromAst(pair.Value,pair.Key,"Nana");motion.Validate();imported++;}
            catch(InvalidDataException e){Check(e.Message.Contains("random",StringComparison.Ordinal)||e.Message.Contains("bone index",StringComparison.Ordinal)||e.Message.Contains("frame times",StringComparison.Ordinal),"unsupported motion reports exact random syntax: "+pair.Key+": "+e.Message);rejected++;}
        Check(imported>100&&imported+rejected==780,"entire original motion collection is accounted for");
        var eventClip=new MotionClip{Id="events",Duration=2,Loop=true,Events=[new(){Id="start",Start=0},new(){Id="fraction",Start=.5},new(){Id="end",Start=2}]};
        var eventHost=new Host{Facts=new(InputSnapshot.Empty,false,0,0,100,false,false)};
        var eventPlayer=new MotionPlayer(new(){Clips=[eventClip],States=[new(){Clip="events"}]},eventHost);
        for(int tick=0;tick<3;tick++)eventPlayer.Tick();
        Check(eventHost.Events.Count(e=>e=="end")==1&&eventHost.Events.Count(e=>e=="start")==2&&eventHost.Events.Count(e=>e=="fraction")==1,"loop boundary and fractional frame events are preserved");
        var sample=new NativeSampleGravity();var configRegistry=new ContentRegistry();
        configRegistry.RegisterAction("sample.gravity",sample.Definition,values=>{var action=new NativeSampleGravity();action.Configure(values);return action;});
        Check(((NativeSampleGravity)configRegistry.CreateAction("sample.gravity",new Dictionary<string,string>{{"strength","2"}})).Strength==2,"registered parameter factories consume saved values");
        using(var bridge=new GmNativeBridge(world,configRegistry))
        {
            var actor=world.CreateNativeEntity(0,0);var definition=new CharacterDefinition{Actions={{CharacterActionSlot.Jump,"sample.gravity"}},ActionConfigs={{CharacterActionSlot.Jump,new(){Action="sample.gravity",Slot=CharacterActionSlot.Jump,Mode=CharacterActionMode.Replace,Parameters={{"strength","3"}}}}}};
            bridge.BindCharacter(actor,definition);bridge.Update(new(FrameContext.FixedDelta,0,1,InputSnapshot.Empty));
            Check(actor.Scope.ReadOr("actionGravity").Number==3,"formal character binding consumes action configuration");
            configRegistry.RegisterEntity(new NativeEntityDefinition{Id="motion.wall",Width=16,Height=16,Solid=true},()=>new Entity());
            var wall=bridge.Services.Entities.Spawn("motion.wall",32,0);var mover=bridge.Services.Entities.Spawn("motion.wall",0,0);
            Check(mover.Call("place_free",32,0).Truth==false,"native entity bounds participate in solid world collisions");
            bridge.Services.Entities.Destroy(wall.Id);bridge.Services.Entities.Destroy(mover.Id);
            bridge.UnbindCharacter(actor.Id);world.Call("instance_destroy",[],new(actor));
        }
        Console.WriteLine("MOTION_IMPORT supported="+imported+" rejected="+rejected);
        bool invalid=false;try{graph.Clips[0].Tracks[0].Keys[0].X=double.NaN;graph.Validate();}catch(InvalidDataException){invalid=true;}
        Check(invalid,"non finite asset values are rejected");
        return count;
    }
    private sealed class Entity : IEntityBehavior
    {
        public string Id=>"motion.wall";
        public void Update(ActorContext actor,FrameContext frame){}
    }
    private sealed class Host : IMotionHost
    {
        public MotionFacts Facts{get;set;}
        public void Move(MotionIntent intent){}
        public readonly List<string> Events=new();
        public void Event(MotionEvent item,long cycle)=>Events.Add(item.Id);
    }
}
