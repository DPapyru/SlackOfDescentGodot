using SlackOfDescent;

public static class MotionGraphTests
{
    public static int Run(GmWorld world)
    {
        int count=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        bool Throws(Action action,string text){try{action();return false;}catch(Exception error){return error.Message.Contains(text,StringComparison.Ordinal);}}
        (double X,double Y) Compose((double X,double Y) parent,double angle,double x,double y){var (offsetX,offsetY)=MotionRigCoordinates.Rotate(x,y,angle);return (parent.X+offsetX,parent.Y+offsetY);}
        double Wrap(double angle){angle%=360;return angle<=-180?angle+360:angle>180?angle-360:angle;}
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
        var drag=new MotionGraph{Id="ik.drag",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="BODY_TOP",Keys=[new(){Frame=0,X=0,Y=0}]},new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=3,Y=0}]},new(){Bone="HAND2_ABOVE",Keys=[new(){Frame=0,X=3,Y=0}]}]}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND2_ABOVE"}]};
        var dragDoc=new MotionGraphDocument(drag);var dragged=dragDoc.Drag("idle","HAND2_ABOVE",0,4,2);var midPose=dragDoc.Graph.Clips[0].Tracks.Single(t=>t.Bone=="HAND1_ABOVE").Keys[0];var endPose=dragDoc.Graph.Clips[0].Tracks.Single(t=>t.Bone=="HAND2_ABOVE").Keys[0];Check(dragged.Success&&Math.Abs(Math.Sqrt(midPose.X*midPose.X+midPose.Y*midPose.Y)-3)<.001&&Math.Abs(Math.Sqrt(endPose.X*endPose.X+endPose.Y*endPose.Y)-3)<.001,"IK drag preserves both segment lengths");dragDoc.Undo();Check(dragDoc.Graph.Clips[0].Tracks.Single(t=>t.Bone=="HAND2_ABOVE").Keys[0].X==3,"IK drag is one undo operation");
        var reachGraph=new MotionGraph{Id="ik.reach",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="BODY_TOP",Keys=[new(){Frame=0}]},new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=3,Y=0}]},new(){Bone="HAND2_ABOVE",Keys=[new(){Frame=0,X=3,Y=0}]}]}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND2_ABOVE"}]};
        var reachDoc=new MotionGraphDocument(reachGraph);int reachUndo=reachDoc.UndoDepth;var near=reachDoc.Drag("idle","HAND2_ABOVE",0,2,1);var nearPose=reachDoc.Graph.Clips[0].Sample(0);
        var nearMidPoint=Compose((nearPose["BODY_TOP"].X,nearPose["BODY_TOP"].Y),nearPose["BODY_TOP"].Angle,nearPose["HAND1_ABOVE"].X,nearPose["HAND1_ABOVE"].Y);
        var nearEndPoint=Compose(nearMidPoint,nearPose["BODY_TOP"].Angle+nearPose["HAND1_ABOVE"].Angle,nearPose["HAND2_ABOVE"].X,nearPose["HAND2_ABOVE"].Y);
        Check(near.Success&&!near.Clamped&&near.Reason.Length==0,"reachable IK drag reports no clamp");
        Check(Math.Abs(Math.Sqrt(nearPose["HAND1_ABOVE"].X*nearPose["HAND1_ABOVE"].X+nearPose["HAND1_ABOVE"].Y*nearPose["HAND1_ABOVE"].Y)-3)<1e-9&&Math.Abs(nearPose["HAND1_ABOVE"].Y)<1e-9&&Math.Abs(Math.Sqrt(nearPose["HAND2_ABOVE"].X*nearPose["HAND2_ABOVE"].X+nearPose["HAND2_ABOVE"].Y*nearPose["HAND2_ABOVE"].Y)-3)<1e-9&&Math.Abs(nearPose["HAND2_ABOVE"].Y)<1e-9,"IK writeback keeps both segments on the local X axis");
        Check(Math.Abs(nearEndPoint.X-2)<1e-9&&Math.Abs(nearEndPoint.Y-1)<1e-9,"reachable IK writeback lands the end on the target");
        Check(Math.Abs(Wrap(nearPose["BODY_TOP"].Angle+near.RootAngle))<1e-9,"IK writeback stores the rig angle convention for the chain root");
        Check(Math.Abs(Wrap(nearPose["HAND1_ABOVE"].Angle-(near.RootAngle-near.MidAngle)))<1e-9,"IK writeback stores the rig angle convention for the chain mid");
        Check(reachDoc.UndoDepth==reachUndo+1,"reachable IK drag is one undo step");
        var far=reachDoc.Drag("idle","HAND2_ABOVE",0,30,0);var farPose=reachDoc.Graph.Clips[0].Sample(0);
        var farMidPoint=Compose((farPose["BODY_TOP"].X,farPose["BODY_TOP"].Y),farPose["BODY_TOP"].Angle,farPose["HAND1_ABOVE"].X,farPose["HAND1_ABOVE"].Y);
        var farEndPoint=Compose(farMidPoint,farPose["BODY_TOP"].Angle+farPose["HAND1_ABOVE"].Angle,farPose["HAND2_ABOVE"].X,farPose["HAND2_ABOVE"].Y);
        Check(far.Success&&far.Clamped&&far.Reason=="目标超出链长，已夹到可达范围。","IK drag beyond reach clamps once with the reach message");
        Check(Math.Abs(farEndPoint.X-6)<1e-9&&Math.Abs(farEndPoint.Y)<1e-9,"IK clamp lands on the reachable boundary");
        Check(Math.Abs(Math.Sqrt(farPose["HAND1_ABOVE"].X*farPose["HAND1_ABOVE"].X+farPose["HAND1_ABOVE"].Y*farPose["HAND1_ABOVE"].Y)-3)<1e-9&&Math.Abs(Math.Sqrt(farPose["HAND2_ABOVE"].X*farPose["HAND2_ABOVE"].X+farPose["HAND2_ABOVE"].Y*farPose["HAND2_ABOVE"].Y)-3)<1e-9,"clamped IK drag keeps both segment lengths");
        Check(reachDoc.UndoDepth==reachUndo+2,"clamped IK drag is one undo step");
        var clear=reachDoc.Drag("idle","HAND2_ABOVE",0,0,2);
        Check(clear.Success&&!clear.Clamped&&clear.Reason.Length==0,"a follow-up reachable IK drag clears the clamp result");
        var framedGraph=new MotionGraph{Id="ik.framed",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="BODY_TOP",Keys=[new(){Frame=0}]},new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=3}]},new(){Bone="HAND2_ABOVE",Keys=[new(){Frame=0,X=3}]}]}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND2_ABOVE"}]};
        var framedDoc=new MotionGraphDocument(framedGraph);var framedResult=framedDoc.Drag("idle","HAND2_ABOVE",0,2,1,null,false,new MotionIkFrame(30,0,5));var framedPose=framedDoc.Graph.Clips[0].Sample(0);
        var framedMidPoint=Compose((0d,5d),framedPose["BODY_TOP"].Angle+30,framedPose["HAND1_ABOVE"].X,framedPose["HAND1_ABOVE"].Y);
        var framedEndPoint=Compose(framedMidPoint,framedPose["BODY_TOP"].Angle+30+framedPose["HAND1_ABOVE"].Angle,framedPose["HAND2_ABOVE"].X,framedPose["HAND2_ABOVE"].Y);
        Check(framedResult.Success&&Math.Abs(framedEndPoint.X-2)<1e-9&&Math.Abs(framedEndPoint.Y-1)<1e-9,"IK drag honours the chain root frame override");
        Check(Math.Abs(Wrap(framedPose["BODY_TOP"].Angle+framedResult.RootAngle+30))<1e-9,"IK writeback subtracts the chain parent angle from the root rotation");
        var viewport=new MotionView(100,200,320,240,640,480);
        var mapped=MotionViewport.ToWorld(400,300,640,480,viewport);var mappedBack=MotionViewport.ToSurface(300,350,640,480,viewport);
        Check(Math.Abs(mapped.X-300)<.001&&Math.Abs(mapped.Y-350)<.001&&Math.Abs(mappedBack.X-400)<.001&&Math.Abs(mappedBack.Y-300)<.001,"viewport maps a 1:1 surface and round trips");
        var zoomed=MotionViewport.ToWorld(400,300,400,300,new(0,0,320,240,640,480));
        Check(Math.Abs(zoomed.X-320)<.001&&Math.Abs(zoomed.Y-240)<.001,"viewport scales a smaller surface onto the port");
        var barStart=MotionViewport.ToWorld(200,0,800,300,new(0,0,320,240,640,480));var barEnd=MotionViewport.ToWorld(600,300,800,300,new(0,0,320,240,640,480));var barBack=MotionViewport.ToSurface(320,240,800,300,new(0,0,320,240,640,480));
        Check(Math.Abs(barStart.X)<.001&&Math.Abs(barStart.Y)<.001&&Math.Abs(barEnd.X-320)<.001&&Math.Abs(barEnd.Y-240)<.001&&Math.Abs(barBack.X-600)<.001&&Math.Abs(barBack.Y-300)<.001,"viewport honours letterbox bars and round trips");
        Check(Math.Abs(MotionViewport.WorldPerPixel(800,300,new(0,0,320,240,640,480))-.8)<.001&&Math.Abs(MotionViewport.WorldPerPixel(640,480,new(0,0,320,240,640,480))-.5)<.001,"viewport converts a 12px pick radius to world units");
        Check(Math.Abs(MotionViewport.WorldPerPixel(800,300,new(0,0,320,240,640,480))*12-9.6)<.001,"viewport turns the 12px pick radius into 9.6 world units");
        Check(MotionViewport.WorldPerPixel(0,0,new(0,0,320,240,640,480))==0,"viewport reports no scale for an empty surface");
        var composeGraph=new MotionGraph{Id="ik.compose",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="BODY_TOP",Keys=[new(){Frame=0,Angle=15}]},new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=3,Y=0}]},new(){Bone="HAND2_ABOVE",Keys=[new(){Frame=0,X=3,Y=0}]}]}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND2_ABOVE"}]};
        var composeDoc=new MotionGraphDocument(composeGraph);var composeResult=composeDoc.Drag("idle","HAND2_ABOVE",0,4,2);
        var composed=composeDoc.Graph.Clips[0].Sample(0);
        var rootPoint=(composed["BODY_TOP"].X,composed["BODY_TOP"].Y);
        var midPoint=Compose(rootPoint,composed["BODY_TOP"].Angle,composed["HAND1_ABOVE"].X,composed["HAND1_ABOVE"].Y);
        var endPoint=Compose(midPoint,composed["BODY_TOP"].Angle+composed["HAND1_ABOVE"].Angle,composed["HAND2_ABOVE"].X,composed["HAND2_ABOVE"].Y);
        Check(composeResult.Success&&Math.Abs(endPoint.X-4)<.01&&Math.Abs(endPoint.Y-2)<.01,"IK writeback composes to the dragged target");
        Check(Math.Abs(Math.Sqrt(composed["HAND1_ABOVE"].X*composed["HAND1_ABOVE"].X+composed["HAND1_ABOVE"].Y*composed["HAND1_ABOVE"].Y)-3)<.001&&Math.Abs(Math.Sqrt(composed["HAND2_ABOVE"].X*composed["HAND2_ABOVE"].X+composed["HAND2_ABOVE"].Y*composed["HAND2_ABOVE"].Y)-3)<.001,"IK writeback keeps both segment lengths");
        composeDoc.Undo();Check(composeDoc.Graph.Clips[0].Sample(0)["BODY_TOP"].Angle==15&&composeDoc.Graph.Clips[0].Sample(0)["HAND2_ABOVE"].X==3,"IK writeback is a single undo step");
        var fkGraph=new MotionGraph{Id="ik.fk",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="BODY_TOP",Keys=[new(){Frame=0,Angle=15}]},new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=3,Y=0}]},new(){Bone="HAND2_ABOVE",Keys=[new(){Frame=0,X=3,Y=0}]}]}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND2_ABOVE"}]};
        var fkDoc=new MotionGraphDocument(fkGraph);var fkDrag=fkDoc.Drag("idle","HAND2_ABOVE",0,0,5,null,true);var fkPose=fkDoc.Graph.Clips[0].Sample(0);
        Check(fkDrag.Success&&fkPose["BODY_TOP"].Angle==15&&Math.Abs(Math.Sqrt(fkPose["HAND2_ABOVE"].X*fkPose["HAND2_ABOVE"].X+fkPose["HAND2_ABOVE"].Y*fkPose["HAND2_ABOVE"].Y)-3)<.001&&Math.Abs(fkPose["HAND2_ABOVE"].Angle+90)<.001,"temporary FK bypasses the IK chain and keeps the segment length");
        fkDoc.Translate("idle","HAND2_ABOVE",0,7,-2);var translated=fkDoc.Graph.Clips[0].Sample(0)["HAND2_ABOVE"];
        Check(translated.X==7&&translated.Y==-2&&Math.Abs(translated.Angle-fkPose["HAND2_ABOVE"].Angle)<.001,"local translate writes X/Y and keeps the angle");
        fkDoc.Undo();Check(Math.Abs(fkDoc.Graph.Clips[0].Sample(0)["HAND2_ABOVE"].X-fkPose["HAND2_ABOVE"].X)<.001,"local translate is one undo operation");
        var spinGraph=new MotionGraph{Id="spin",Clips=[new(){Id="idle",Duration=10,Tracks=[new(){Bone="HEAD",Keys=[new(){Frame=0,X=3,Y=0}]}]}],States=[new(){Id="idle",Clip="idle"}]};
        var spinDoc=new MotionGraphDocument(spinGraph);var spinResult=spinDoc.Drag("idle","HEAD",0,0,5);
        var headPose=spinDoc.Graph.Clips[0].Sample(0)["HEAD"];var headPoint=Compose((0d,0d),0d,headPose.X,headPose.Y);
        Check(spinResult.Success&&Math.Abs(headPoint.X)<.001&&Math.Abs(headPoint.Y-3)<.001&&Math.Abs(Math.Sqrt(headPose.X*headPose.X+headPose.Y*headPose.Y)-3)<.001,"unbound bone drag keeps its length and points at the target");
        var dirtyDoc=new MotionGraphDocument(new MotionGraph{Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]});
        Check(!dirtyDoc.Dirty,"new document starts clean");
        dirtyDoc.Edit(g=>g.Clips[0].Duration=30);Check(dirtyDoc.Dirty,"edit marks the document dirty");
        string dirtyPath=Path.Combine(Path.GetTempPath(),"motion-dirty-"+Guid.NewGuid().ToString("N")+".motion.json");dirtyDoc.Save(dirtyPath);
        Check(!dirtyDoc.Dirty&&File.Exists(dirtyPath),"save clears the dirty flag");File.Delete(dirtyPath);
        var renameGraph=new MotionGraph{Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle",Transitions=[new(){Target="run"}]},new(){Id="run",Clip="idle"}],Entry="idle"};
        MotionGraphEdits.RenameState(renameGraph,"run","walk");
        Check(renameGraph.States[1].Id=="walk"&&renameGraph.States[0].Transitions[0].Target=="walk","rename state rewrites transitions");
        MotionGraphEdits.RenameState(renameGraph,"idle","stand");Check(renameGraph.Entry=="stand","rename entry state rewrites the entry");
        Check(Throws(()=>MotionGraphEdits.RenameState(renameGraph,"walk","stand"),"已存在"),"rename rejects duplicate ids");
        MotionGraphEdits.RemoveState(renameGraph,"walk");
        Check(renameGraph.States.Count==1&&renameGraph.States.All(s=>s.Transitions.All(t=>t.Target!="walk")),"remove state drops inbound transitions");
        Check(Throws(()=>MotionGraphEdits.RemoveState(renameGraph,"stand"),"至少保留"),"remove refuses the last state");
        var entryGraph=new MotionGraph{Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"},new(){Id="run",Clip="idle"}],Entry="idle"};
        MotionGraphEdits.RemoveState(entryGraph,"idle");Check(entryGraph.Entry=="run"&&entryGraph.States.Count==1,"removing the entry reassigns it");
        Check(MotionExport.ValidId("motion.dash-2_x")&&!MotionExport.ValidId("bad/id")&&!MotionExport.ValidId("")&&!MotionExport.ValidId("动作")&&!MotionExport.ValidId("../x"),"motion ids accept letters digits dot underscore hyphen");
        Check(MotionExport.MotionPath("dash")=="motions/dash.motion.json","motion export path lives under motions");
        var presetProject=new MotionProject(new(MotionTargetKind.PlayerPreset,"Ryoko"),new MotionGraphDocument(new MotionGraph{Id="p",Skeleton="Ryoko",Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]}));
        Check(!presetProject.Dirty&&presetProject.Target.Skeleton("Nana")=="Ryoko","player preset project targets the preset skeleton");
        presetProject.Document.Edit(g=>g.Clips[0].Duration=12);Check(presetProject.Dirty,"project dirty follows its document");
        Check(new MotionProjectTarget(MotionTargetKind.PlayerCharacter,"hero").Skeleton("Yuki")=="Yuki"&&new MotionProjectTarget(MotionTargetKind.Entity,"objEnemyDoll").Skeleton("Nana")=="legacy:objEnemyDoll","project targets resolve skeleton ids without schema changes");
        var registered=MotionImport.Registered(world.Code["external/Chara/Nana/motion_list.txt"]);
        Check(registered.Count>0&&registered.Any(item=>item.Name=="STAND_0"&&item.Resource=="PlayerCommon/StandFem0"),"motion list registration parses PlayerMotionLoad entries");
        var malformedAst=new object[]{"group",new object[]{
            new object[]{"call","PlayerMotionLoad",new object[]{new object[]{"str",""},new object[]{"str",""},new object[]{"str","Player/X"}}},
            new object[]{"call","PlayerMotionLoad",new object[]{new object[]{"str",""},new object[]{"str","A"},new object[]{"str",""}}},
            new object[]{"call","PlayerMotionLoad",new object[]{new object[]{"str",""},new object[]{"str","A"},new object[]{"str","Res/One"}}},
            new object[]{"call","PlayerMotionLoad",new object[]{new object[]{"str",""},new object[]{"str","A"},new object[]{"str","Res/Two"}}},
            new object[]{"call","Other",new object[0]},
            new object[]{"binary","Assign",1,2}}};
        using(var malformed=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(malformedAst)))
        {
            var filtered=MotionImport.Registered(malformed.RootElement);
            Check(filtered.Count==1&&filtered[0].Name=="A"&&filtered[0].Resource=="Res/One","motion registration skips empty, malformed and duplicate entries");
        }
        using(var empty=System.Text.Json.JsonDocument.Parse("{\"unused\":0}"))
            Check(MotionImport.Registered(empty.RootElement).Count==0,"non list nodes register nothing");
        var escapedAst=new object[]{"group",new object[]{new object[]{"call","PlayerMotionLoad",new object[]{new object[]{"str",""},new object[]{"str","名字 \"x\""},new object[]{"str","Res\\Path"}}}}};
        using(var escaped=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(escapedAst)))
        {
            var items=MotionImport.Registered(escaped.RootElement);
            Check(items.Count==1&&items[0].Name=="名字 \"x\""&&items[0].Resource=="Res\\Path","motion registration keeps escaped names and resources intact");
        }
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
