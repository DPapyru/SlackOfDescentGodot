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
        var defaultChains=MotionIkPresets.For(new MotionGraph{Id="ik.default",Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]});
        Check(defaultChains.Select(chain=>chain.Root+"→"+chain.Mid+"→"+chain.End).SequenceEqual(new[]{"HAND1_ABOVE→HAND2_ABOVE→HAND3_ABOVE","HAND1_BELOW→HAND2_BELOW→HAND3_BELOW","FOOT1_ABOVE→FOOT2_ABOVE→FOOT3_ABOVE","FOOT1_BELOW→FOOT2_BELOW→FOOT3_BELOW"}),"default IK chains mirror left and right three bone limbs");
        var handGraph=new MotionGraph{Id="ik.hand",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=2}]},new(){Bone="HAND2_ABOVE",Keys=[new(){Frame=0,X=2}]},new(){Bone="HAND3_ABOVE",Keys=[new(){Frame=0,X=2}]}]}],States=[new(){Id="idle",Clip="idle"}]};
        var handDoc=new MotionGraphDocument(handGraph);var handDrag=handDoc.Drag("idle","HAND3_ABOVE",0,3,1);var handPose=handDoc.Graph.Clips[0].Sample(0);
        Check(handDrag.Success&&Math.Abs(Math.Sqrt(handPose["HAND2_ABOVE"].X*handPose["HAND2_ABOVE"].X+handPose["HAND2_ABOVE"].Y*handPose["HAND2_ABOVE"].Y)-2)<1e-9&&Math.Abs(Math.Sqrt(handPose["HAND3_ABOVE"].X*handPose["HAND3_ABOVE"].X+handPose["HAND3_ABOVE"].Y*handPose["HAND3_ABOVE"].Y)-2)<1e-9,"a default three bone chain drag preserves both segment lengths");
        var materialized=new MotionGraph{Id="ik.materialized",Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]};MotionIkPresets.Materialize(materialized);
        var materializedSaved=System.Text.Json.JsonSerializer.Deserialize<MotionGraph>(System.Text.Json.JsonSerializer.Serialize(materialized,NativeContentStore.Json),NativeContentStore.Json)!;materializedSaved.Validate();
        Check(materializedSaved.IkChains is {Count:4}&&materializedSaved.IkChains!.Select(chain=>chain.End).SequenceEqual(new[]{"HAND3_ABOVE","HAND3_BELOW","FOOT3_ABOVE","FOOT3_BELOW"}),"materialized default IK chains save and reload");
        var legacyChains=new MotionGraph{Id="ik.legacy",Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND2_ABOVE"},new(){Root="FOOT1_BELOW",Mid="FOOT2_BELOW",End="FOOT3_BELOW"}]};
        var legacySaved=System.Text.Json.JsonSerializer.Deserialize<MotionGraph>(System.Text.Json.JsonSerializer.Serialize(legacyChains,NativeContentStore.Json),NativeContentStore.Json)!;legacySaved.Validate();
        Check(MotionIkPresets.For(legacySaved).Count==2&&legacySaved.IkChains![0].Root=="BODY_TOP"&&legacySaved.IkChains[1].End=="FOOT3_BELOW","stored IK chains load unchanged");
        var implicitGraph=new MotionGraph{Id="ik.implicit",Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]};
        var implicitSaved=System.Text.Json.JsonSerializer.Deserialize<MotionGraph>(System.Text.Json.JsonSerializer.Serialize(implicitGraph,NativeContentStore.Json),NativeContentStore.Json)!;implicitSaved.Validate();
        Check(implicitSaved.IkChains is null&&MotionIkPresets.For(implicitSaved).Count==4,"files without IK chains keep implicit defaults");
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
        var rotateBefore=fkDoc.Graph.Clips[0].Sample(0)["HAND2_ABOVE"];fkDoc.Rotate("idle","HAND2_ABOVE",0,33);var rotated=fkDoc.Graph.Clips[0].Sample(0)["HAND2_ABOVE"];
        Check(Math.Abs(rotated.X-rotateBefore.X)<.001&&Math.Abs(rotated.Y-rotateBefore.Y)<.001&&Math.Abs(rotated.Angle-33)<.001,"rotation writes only the angle");
        fkDoc.Undo();Check(Math.Abs(fkDoc.Graph.Clips[0].Sample(0)["HAND2_ABOVE"].Angle-rotateBefore.Angle)<.001,"rotation is one undo operation");
        var spinGraph=new MotionGraph{Id="spin",Clips=[new(){Id="idle",Duration=10,Tracks=[new(){Bone="HEAD",Keys=[new(){Frame=0,X=3,Y=0}]}]}],States=[new(){Id="idle",Clip="idle"}]};
        var spinDoc=new MotionGraphDocument(spinGraph);var spinResult=spinDoc.Drag("idle","HEAD",0,0,5);
        var headPose=spinDoc.Graph.Clips[0].Sample(0)["HEAD"];var headPoint=Compose((0d,0d),0d,headPose.X,headPose.Y);
        Check(spinResult.Success&&Math.Abs(headPoint.X)<.001&&Math.Abs(headPoint.Y-3)<.001&&Math.Abs(Math.Sqrt(headPose.X*headPose.X+headPose.Y*headPose.Y)-3)<.001,"unbound bone drag keeps its length and points at the target");
        var dirtyDoc=new MotionGraphDocument(new MotionGraph{Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]});
        Check(!dirtyDoc.Dirty,"new document starts clean");
        dirtyDoc.Edit(g=>g.Clips[0].Duration=30);Check(dirtyDoc.Dirty,"edit marks the document dirty");
        string dirtyPath=Path.Combine(Path.GetTempPath(),"motion-dirty-"+Guid.NewGuid().ToString("N")+".motion.json");dirtyDoc.Save(dirtyPath);
        Check(!dirtyDoc.Dirty&&File.Exists(dirtyPath),"save clears the dirty flag");File.Delete(dirtyPath);
        var coalesceDoc=new MotionGraphDocument(new MotionGraph{Id="coalesce",Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]});
        long clock=0;MotionGraphDocument.TickProvider=()=>clock;
        coalesceDoc.Edit(g=>g.Clips[0].Duration=10,"duration:idle");coalesceDoc.Edit(g=>g.Clips[0].Duration=11,"duration:idle");
        Check(coalesceDoc.UndoDepth==1,"same-key edits inside the window keep a single undo step");
        clock+=500;coalesceDoc.Edit(g=>g.Clips[0].Duration=12,"duration:idle");
        Check(coalesceDoc.UndoDepth==2,"advancing the clock past the window starts a new undo step");
        coalesceDoc.Undo();coalesceDoc.Edit(g=>g.Clips[0].Duration=13,"duration:idle");
        Check(coalesceDoc.UndoDepth==2,"undo resets the coalescing window");
        coalesceDoc.Undo();coalesceDoc.Redo();coalesceDoc.Edit(g=>g.Clips[0].Duration=14,"duration:idle");
        Check(coalesceDoc.UndoDepth==3,"redo resets the coalescing window");
        string coalescePath=Path.Combine(Path.GetTempPath(),"motion-coalesce-"+Guid.NewGuid().ToString("N")+".motion.json");coalesceDoc.Save(coalescePath);
        coalesceDoc.Edit(g=>g.Clips[0].Duration=15,"duration:idle");
        Check(coalesceDoc.UndoDepth==4,"save resets the coalescing window");
        File.Delete(coalescePath);MotionGraphDocument.TickProvider=()=>Environment.TickCount64;
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
        Check(MotionImport.TryFirstRegistered(world.Code,"Nana","motion","Nana",out var initial,out var initialSource,out var initialError)&&initialError.Length==0&&initial!.Id=="motion"&&initial!.Skeleton=="Nana","the first registered original motion becomes the initial document");
        Check(initialSource==registered[0].Name+" → "+registered[0].Resource,"the initial document imports the first registered original motion");
        var initialPose=initial!.Clips[0].Sample(0);
        Check(initialPose.Count>0&&initialPose.Values.Any(pose=>pose.X!=0||pose.Y!=0),"the imported initial pose is not collapsed to the origin");
        var expectedPose=MotionImport.FromAst(world.Code["external/Chara/Motion/"+registered[0].Resource+".txt"],"motion","Nana").Clips[0].Sample(0);
        Check(initialPose.Count==expectedPose.Count&&initialPose.All(pair=>expectedPose[pair.Key]==pair.Value),"the imported initial pose equals frame zero of that original motion");
        object Registration(MotionRegistered item)=>new object[]{"call","PlayerMotionLoad",new object[]{new object[]{"str",""},new object[]{"str",item.Name},new object[]{"str",item.Resource}}};
        using(var ordered=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new object[]{"group",new object[]{Registration(registered[1]),Registration(registered[0])}})))
        {
            var orderCode=new Dictionary<string,System.Text.Json.JsonElement>(StringComparer.Ordinal){["external/Chara/Nana/motion_list.txt"]=ordered.RootElement};
            foreach(var item in registered.Take(2))orderCode["external/Chara/Motion/"+item.Resource+".txt"]=world.Code["external/Chara/Motion/"+item.Resource+".txt"];
            Check(MotionImport.TryFirstRegistered(orderCode,"Nana","order","Nana",out var orderedGraph,out var orderedSource,out _)&&orderedGraph!.Id=="order"&&orderedSource==registered[1].Name+" → "+registered[1].Resource,"the first registered action follows motion_list registration order");
        }
        var emptyCode=new Dictionary<string,System.Text.Json.JsonElement>();
        Check(!MotionImport.TryFirstRegistered(emptyCode,"Nana","motion","Nana",out var missing,out _,out var missingError)&&missing is null&&missingError.Contains("原版动作清单",StringComparison.Ordinal),"a missing motion list fails with a message instead of throwing");
        var brokenCode=new Dictionary<string,System.Text.Json.JsonElement>(StringComparer.Ordinal){["external/Chara/Nana/motion_list.txt"]=world.Code["external/Chara/Nana/motion_list.txt"]};
        Check(!MotionImport.TryFirstRegistered(brokenCode,"Nana","motion","Nana",out var broken,out _,out var brokenError)&&broken is null&&brokenError.Contains("找不到原版动作",StringComparison.Ordinal),"a missing original motion file fails with a message instead of throwing");
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
        var importName="PlayerNana/Stand0_Damage";var importedMotion=MotionImport.FromAst(world.Code["external/Chara/Motion/"+importName+".txt"],importName.Replace('/','.'),"Nana",clipId:importName);
        Check(importedMotion.Clips[0].Id==importName&&importedMotion.States[0].Clip==importName,"imported clip id follows the source motion name");
        string importPath=Path.Combine(Path.GetTempPath(),"motion-import-"+Guid.NewGuid().ToString("N")+".motion.json");importedMotion.Save(importPath);
        Check(MotionGraph.Load(importPath).Clips[0].Id==importName,"save reload keeps the imported clip name");File.Delete(importPath);
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
        string originDirectory=Path.Combine(Path.GetTempPath(),"motion-origin-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(originDirectory);
        try
        {
            string legacyFile=Path.Combine(originDirectory,"legacy.motion.json");
            File.WriteAllText(legacyFile,"{\"FormatVersion\":1,\"Id\":\"origin.legacy\",\"Skeleton\":\"Nana\",\"Entry\":\"idle\",\"Clips\":[{\"Id\":\"idle\"}],\"States\":[{\"Id\":\"idle\",\"Clip\":\"idle\"}]}");
            Check(MotionGraph.Load(legacyFile).Origin=="unknown","a motion graph without an origin field loads as unknown");
            var modGraph=MotionGraph.Load(legacyFile,"example.mod");
            Check(modGraph.Origin=="example.mod","the mod content loader records the mod id");
            string modFile=Path.Combine(originDirectory,"mod.motion.json");modGraph.Save(modFile);
            Check(MotionGraph.Load(modFile).Origin=="example.mod","saving and reloading keeps the origin");
            var roundTrip=System.Text.Json.JsonSerializer.Deserialize<MotionGraph>(System.Text.Json.JsonSerializer.Serialize(modGraph,NativeContentStore.Json),NativeContentStore.Json)!;
            Check(roundTrip.Origin=="example.mod","serialization round trip keeps the origin");
            var nativeGraph=MotionImport.FromAst(world.Code["external/Chara/Motion/PlayerNana/Stand0_Damage.txt"],"origin.native","Nana");
            Check(nativeGraph.Origin=="native","importing an original GameData action records the native layer");
        }
        finally{Directory.Delete(originDirectory,true);}
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
