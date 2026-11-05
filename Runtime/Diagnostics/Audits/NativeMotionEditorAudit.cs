using Godot;

namespace SlackOfDescent;

public partial class NativeMotionEditor
{
    public async Task AuditAsync(string output)
    {
        void Expect(bool ok,string message){if(!ok)throw new InvalidOperationException("Motion editor: "+message);}
        async Task Ready()
        {
            long start=System.Diagnostics.Stopwatch.GetTimestamp();
            while(busy){if(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds>90)throw new TimeoutException("Motion preview initialization");await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
            Expect(preview?.Player is not null,"independent preview is ready: "+status.Text);
        }
        async Task Shot(string name)
        {
            string? directory=System.Environment.GetEnvironmentVariable("SLACK_MOTION_SHOTS");
            if(string.IsNullOrEmpty(directory)||DisplayServer.GetName()=="headless")return;
            Directory.CreateDirectory(directory);PopupCentered();
            for(int i=0;i<4;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            using var image=GetTexture().GetImage();string file=Path.Combine(directory,name+".png");image.SavePng(file);GD.Print("MOTION_EDITOR_SHOT ",file);
            Hide();
        }
        await Ready();
        var skeleton=preview!.Bridge.Services.Motions.Skeleton(new(preview.World,preview.Player!));
        Expect(skeleton.Count==15,"preview exposes the 15 part skeleton");
        document=new(new MotionGraph{Id="audit.ik",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="BODY_TOP",Keys=[new(){Frame=0}]},new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=3}]},new(){Bone="HAND1_BELOW",Keys=[new(){Frame=0,X=3}]}]}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND1_BELOW"}]});
        Refresh();
        var ikDragged=document.Drag("idle","HAND1_BELOW",0,4,2);
        Expect(ikDragged.Success,"IK drag applies from the editor document");
        Expect(Clip.Tracks.Single(t=>t.Bone=="BODY_TOP").Keys.Single().Angle!=0,"IK drag writes the chain root angle");
        document.Undo();Expect(Clip.Tracks.Single(t=>t.Bone=="BODY_TOP").Keys.Single().Angle==0,"IK drag undo restores the chain root");
        Expect(graphView.GetChildren().OfType<GraphNode>().Count()==1,"entry state has a graph node");
        int projectIndex=activeProject;
        NewProject(new(MotionTargetKind.PlayerPreset,"Ryoko"),"audit.project",false);
        Expect(projects.Count==projectIndex+2&&document.Graph.Skeleton=="Ryoko"&&Project.Target.Kind==MotionTargetKind.PlayerPreset,"new project targets the preset skeleton");
        Change(g=>g.Clips[0].Tracks.Add(new(){Bone="HEAD",Keys=[new(){Frame=0}]}));
        Expect(Project.Dirty,"editing a project marks it dirty");
        CloseProject(projects.Count-1);
        Expect(pendingConfirmation is not null&&confirmation.DialogText.Contains("未保存"),"closing a dirty project asks for confirmation");
        pendingConfirmation=null;confirmation.Hide();
        SwitchProject(projectIndex,false);
        Expect(activeProject==projectIndex&&document.Graph.Id!="audit.project","switching tabs rebinds the document");
        RemoveProject(projects.Count-1,false);
        Expect(projects.Count==projectIndex+1,"closing the extra project keeps the active one");
        NewProject(new(MotionTargetKind.Entity,"objEnemyDoll"),"audit.entity",false);
        Expect(document.Graph.Skeleton=="legacy:objEnemyDoll"&&targetLabel.Text.Contains("临时骨架"),"entity project uses a labelled legacy skeleton");
        Change(g=>{g.States.Add(new(){Id="next",Clip=g.Clips[0].Id});g.States[0].Transitions.Add(new(){Target="next"});});
        stateChoice.Select(1);RefreshStateInspector();
        stateRename.Text="walk";RenameSelectedState();
        Expect(document.Graph.States[1].Id=="walk"&&document.Graph.States[0].Transitions[0].Target=="walk","structured rename rewrites transitions");
        RemoveSelectedState();
        Expect(document.Graph.States.Count==1&&document.Graph.States[0].Transitions.Count==0,"structured delete drops transitions");
        velocityXOn.ButtonPressed=true;velocityX.Value=3;
        Expect(document.Graph.States[0].Movement.VelocityX==3,"structured movement writes the state");
        string projectFile=Path.Combine(output,"editor.project.motion.json");path=projectFile;Save();
        document=new(MotionGraph.Load(projectFile));Refresh();
        Expect(document.Graph.States[0].Movement.VelocityX==3,"structured state survives reload");
        Change(g=>g.Id="audit.export");
        string package=Path.Combine(store.Projects,"audit-motion-project");Directory.CreateDirectory(package);
        NativeContentStore.Save(Path.Combine(package,"mod.json"),new NativeModManifest{FormatVersion=2,Id="audit.motion.project",Name="Audit project",Version="1.0.0",Content=new()});
        ExportToProject(package);
        Expect(File.Exists(Path.Combine(package,"motions","audit.export.motion.json")),"motion export writes the project file");
        var exportedManifest=NativeModManifest.Read(Path.Combine(package,"mod.json"));
        Expect(exportedManifest.Content!.Motions.TryGetValue("audit.export",out var exportedPath)&&exportedPath=="motions/audit.export.motion.json","motion export updates the manifest");
        var exportedContent=new ContentRegistry();using(var exportedManager=new NativeModManager(exportedContent)){var loaded=exportedManager.Reload(package);Expect(loaded.Success&&exportedContent.MotionGraphs.ContainsKey("audit.export"),"exported motion registers through an isolated reload");}
        Expect(MotionExport.ValidId(document.Graph.Id)&&!MotionExport.ValidId("bad id"),"motion export validates ids");
        Change(g=>g.Clips[0].Duration=31);
        bool exportFailed=false;
        var writable=UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute;
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(package,writable&~UnixFileMode.UserWrite&~UnixFileMode.GroupWrite&~UnixFileMode.OtherWrite);
        try{ExportToProject(package);}catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException){exportFailed=true;}
        finally{if(!OperatingSystem.IsWindows())File.SetUnixFileMode(package,writable);}
        Expect(exportFailed&&Project.Dirty,"failed export keeps the project dirty");
        int closed=0;
        ConfirmDirtyClose(()=>closed++);
        Expect(pendingConfirmation is not null&&confirmation.DialogText.Contains(projects[0].Document.Graph.Id),"window close confirms the first dirty project");
        var firstConfirm=pendingConfirmation;pendingConfirmation=null;confirmation.Hide();firstConfirm!();
        Expect(pendingConfirmation is not null&&confirmation.DialogText.Contains(projects[1].Document.Graph.Id),"window close confirms each dirty project in turn");
        pendingConfirmation=null;confirmation.Hide();
        Expect(closed==0,"cancelling a dirty confirmation stops the close");
        ConfirmDirtyClose(()=>closed++);
        var confirmFirst=pendingConfirmation;pendingConfirmation=null;confirmation.Hide();confirmFirst!();
        var confirmSecond=pendingConfirmation;pendingConfirmation=null;confirmation.Hide();confirmSecond!();
        Expect(closed==1,"confirming every dirty project closes the window");
        SwitchProject(projectIndex,false);RemoveProject(projects.Count-1,false);
        Expect(projects.Count==projectIndex+1&&activeProject==projectIndex,"project tabs return to the first document");
        InitializePreview();await Ready();
        var previewBefore=preview!;var previewWorld=previewBefore.World;
        document=new(new MotionGraph{Id="audit.follow",Clips=[new(){Id="idle",Duration=20,Root=[new(){Frame=0,X=5,Y=-3}],Tracks=[new(){Bone="HEAD",Keys=[new(){Frame=0,X=2,Y=-4,Angle=10}]}]}],States=[new(){Id="idle",Clip="idle"}]});
        Refresh();var followActor=new ActorContext(preview.World,preview.Player!);followActor.Write("Direction",-1);
        frame.Value=0;long buildsBefore=preview.Graphics.Builds;
        Change(g=>g.Clips[0].Tracks[0].Keys[0].Angle=35);
        Expect(ReferenceEquals(preview,previewBefore)&&ReferenceEquals(preview.World,previewWorld),"editing reuses the preview world");
        Expect(preview.Graphics.Builds>buildsBefore,"editing redraws the canvas");
        var followClip=document.Graph.Clips[0];var followPose=followClip.Sample(frame.Value)["HEAD"];var followRoot=followClip.RootPose(frame.Value);
        double followX=followActor.X+followRoot.X*-1,followY=followActor.Y+followRoot.Y,followRadians=followActor.Read("image_angle")*Math.PI/180;
        double followExpectedX=followX+(followPose.X*Math.Cos(followRadians)+followPose.Y*Math.Sin(followRadians))*-1,followExpectedY=followY-followPose.X*Math.Sin(followRadians)+followPose.Y*Math.Cos(followRadians);
        Expect(Math.Abs(followActor.Part("HEAD")!.X-followExpectedX)<.001&&Math.Abs(followActor.Part("HEAD")!.Y-followExpectedY)<.001,"edited pose matches the canvas with root offset and mirror");
        RunAction("undo");
        Expect(document.Graph.Clips[0].Tracks[0].Keys[0].Angle==10,"undo restores the edited key");
        followPose=document.Graph.Clips[0].Sample(frame.Value)["HEAD"];
        followExpectedX=followX+(followPose.X*Math.Cos(followRadians)+followPose.Y*Math.Sin(followRadians))*-1;followExpectedY=followY-followPose.X*Math.Sin(followRadians)+followPose.Y*Math.Cos(followRadians);
        Expect(Math.Abs(followActor.Part("HEAD")!.X-followExpectedX)<.001&&Math.Abs(followActor.Part("HEAD")!.Y-followExpectedY)<.001,"undo immediately updates the canvas pose");
        RunAction("redo");
        followPose=document.Graph.Clips[0].Sample(frame.Value)["HEAD"];
        followExpectedX=followX+(followPose.X*Math.Cos(followRadians)+followPose.Y*Math.Sin(followRadians))*-1;followExpectedY=followY-followPose.X*Math.Sin(followRadians)+followPose.Y*Math.Cos(followRadians);
        Expect(Math.Abs(followActor.Part("HEAD")!.X-followExpectedX)<.001&&Math.Abs(followActor.Part("HEAD")!.Y-followExpectedY)<.001,"redo immediately updates the canvas pose");
        Expect(ReferenceEquals(preview,previewBefore),"undo and redo do not rebuild the preview");
        await Shot("motion-follow");
        document=new(MotionImport.FromAst(world.Code["external/Chara/Motion/PlayerNana/Stand0_Damage.txt"],"audit.editor","Nana"));Refresh();
        SelectBone("HEAD");frame.Value=2;x.Value=4;y.Value=-16;angle.Value=17;sx.Value=1.25;sy.Value=.8;picture.Value=1;WriteKey();
        Expect(Clip.Tracks.Single(t=>t.Bone=="HEAD").Keys.Any(k=>k.Frame==2&&k.Angle==17&&k.Picture==1),"timeline writes a real bone key");
        document.Undo();Expect(!Clip.Tracks.Single(t=>t.Bone=="HEAD").Keys.Any(k=>k.Frame==2),"undo removes timeline key");document.Redo();
        document.Edit(g=>g.IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND1_BELOW"}]);
        Refresh();var canvasActor=PreviewActor!;canvasActor.Write("Direction",1);ApplyDocumentPose();
        var canvasNodes=canvasActor.Services.Motions.Skeleton(canvasActor);
        string? Topmost(double x,double y)
        {
            string? best=null;double bestDepth=double.MaxValue;int bestOrder=-1;
            foreach(var node in canvasNodes)
            {
                var part=canvasActor.World.Instances.FirstOrDefault(item=>item.Id==node.Instance);
                if(part is null||part.Destroyed||!part.Active||!part.Scope.ReadOr("visible").Truth)continue;
                var bounds=canvasActor.World.Bounds(part);
                if(bounds.Empty||x<bounds.Left||x>bounds.Right||y<bounds.Top||y>bounds.Bottom)continue;
                double depth=part.Scope.ReadOr("depth").Number;int order=canvasActor.World.Instances.IndexOf(part);
                if(depth<bestDepth||(depth==bestDepth&&order>bestOrder)){best=node.Name;bestDepth=depth;bestOrder=order;}
            }
            return best;
        }
        Vector2 SurfacePoint(double x,double y){var (sx,sy)=MotionViewport.ToSurface(x,y,surface.Size.X,surface.Size.Y,PreviewView);return new((float)sx,(float)sy);}
        Vector2 PointFor(string name)
        {
            var node=canvasNodes.Single(item=>item.Name==name);var bounds=canvasActor.World.Bounds(canvasActor.World.Instances.First(item=>item.Id==node.Instance));
            for(double y=bounds.Top;y<=bounds.Bottom;y+=2)for(double x=bounds.Left;x<=bounds.Right;x+=2)if(Topmost(x,y)==name)return SurfacePoint(x,y);
            throw new InvalidOperationException("No unoccluded canvas point for "+name);
        }
        int pickable=0;
        foreach(var node in canvasNodes){var expectedName=Topmost(node.X,node.Y);if(expectedName is null)continue;pickable++;Expect(PickBoneAt(SurfacePoint(node.X,node.Y))==expectedName,"part rect hit picks the topmost bone at "+node.Name);}
        Expect(pickable>0,"visible part rects are pickable");
        bool overlaps=false;
        for(int a=0;a<canvasNodes.Count&&!overlaps;a++)for(int b=a+1;b<canvasNodes.Count&&!overlaps;b++)
        {
            var first=canvasActor.World.Bounds(canvasActor.World.Instances.First(item=>item.Id==canvasNodes[a].Instance));
            var second=canvasActor.World.Bounds(canvasActor.World.Instances.First(item=>item.Id==canvasNodes[b].Instance));
            double left=Math.Max(first.Left,second.Left),right=Math.Min(first.Right,second.Right),top=Math.Max(first.Top,second.Top),bottom=Math.Min(first.Bottom,second.Bottom);
            if(right<left||bottom<top)continue;
            double x=(left+right)/2,y=(top+bottom)/2;
            var expectedName=Topmost(x,y);Expect(expectedName is not null&&expectedName==PickBoneAt(SurfacePoint(x,y)),"overlapping part rects pick the topmost bone");
            overlaps=true;
        }
        Expect(overlaps,"character part rects overlap somewhere");
        string Serialized()=>System.Text.Json.JsonSerializer.Serialize(document.Graph,NativeContentStore.Json);
        var emptyBefore=Serialized();var blankPoint=SurfacePoint(canvasActor.X+400,canvasActor.Y+400);
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=blankPoint});
        Expect(!dragging&&highlightedBone is null&&boneList.GetSelectedItems().Length==0,"clicking empty canvas clears the selection");
        Expect(Serialized()==emptyBefore,"clicking empty canvas leaves the document untouched");
        var headNode=canvasNodes.Single(item=>item.Name=="HEAD");var headPoint=PointFor("HEAD");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=headPoint});
        Expect(dragging&&highlightedBone==Topmost(CanvasPosition(headPoint).X,CanvasPosition(headPoint).Y)&&boneList.GetSelectedItems().Length==1,"clicking a part selects and highlights the bone");
        int clickUndo=document.UndoDepth;
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=headPoint});
        Expect(!dragging&&Serialized()==emptyBefore&&document.UndoDepth==clickUndo,"a click without movement writes no key and no undo step");
        Expect(highlightedBone is not null,"a click without movement keeps the selection");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=headPoint});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Right,Pressed=true,Position=headPoint});
        Expect(!dragging&&Serialized()==emptyBefore,"right click cancels the drag without editing");
        var fkBefore=Clip.Sample(frame.Value)["HEAD"];int fkUndo=document.UndoDepth;
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=headPoint});
        Expect(dragging&&Math.Abs(Clip.Sample(frame.Value)["HEAD"].Angle-fkBefore.Angle)<.001,"pressing an FK bone does not jump");
        var fkTarget=SurfacePoint(CanvasPosition(headPoint).X+24,CanvasPosition(headPoint).Y-8);
        SurfaceInput(new InputEventMouseMotion{Position=fkTarget});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=fkTarget});
        var fkPose=Clip.Sample(frame.Value)["HEAD"];double fkLength=Math.Sqrt(fkBefore.X*fkBefore.X+fkBefore.Y*fkBefore.Y);
        Expect(Math.Abs(Math.Sqrt(fkPose.X*fkPose.X+fkPose.Y*fkPose.Y)-fkLength)<.001,"FK drag keeps the bone length");
        Expect(Clip.Tracks.Single(item=>item.Bone=="HEAD").Keys.Any(key=>key.Frame==frame.Value),"FK drag writes the current frame key");
        Expect(document.UndoDepth==fkUndo+1,"FK drag adds exactly one undo step");
        RunAction("undo");Expect(Math.Abs(Clip.Sample(frame.Value)["HEAD"].Angle-fkBefore.Angle)<.001,"FK drag is one undo step");RunAction("redo");
        var shiftBefore=Clip.Sample(frame.Value)["HEAD"];
        var shiftStart=PointFor("HEAD");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,ShiftPressed=true,Position=shiftStart});
        var shiftTarget=SurfacePoint(CanvasPosition(shiftStart).X-12,CanvasPosition(shiftStart).Y+10);
        var (shiftWorldX,shiftWorldY)=CanvasPosition(shiftTarget);var shiftExpected=LocalTarget("HEAD",shiftWorldX,shiftWorldY,false);
        SurfaceInput(new InputEventMouseMotion{Position=shiftTarget});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,ShiftPressed=true,Position=shiftTarget});
        var shiftPose=Clip.Sample(frame.Value)["HEAD"];
        Expect(Math.Abs(shiftPose.X-shiftExpected.X)<.001&&Math.Abs(shiftPose.Y-shiftExpected.Y)<.001,"shift drag writes local X/Y");
        Expect(Math.Abs(shiftPose.Angle-shiftBefore.Angle)<.001,"shift drag keeps the angle");
        RunAction("undo");Expect(Math.Abs(Clip.Sample(frame.Value)["HEAD"].X-fkPose.X)<.001,"shift drag is one undo step");RunAction("redo");
        var chainBefore=Clip.Sample(frame.Value);
        double midLength=Math.Sqrt(chainBefore["HAND1_ABOVE"].X*chainBefore["HAND1_ABOVE"].X+chainBefore["HAND1_ABOVE"].Y*chainBefore["HAND1_ABOVE"].Y);
        double endLength=Math.Sqrt(chainBefore["HAND1_BELOW"].X*chainBefore["HAND1_BELOW"].X+chainBefore["HAND1_BELOW"].Y*chainBefore["HAND1_BELOW"].Y);
        var endPoint=PointFor("HAND1_BELOW");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=endPoint});
        Expect(dragging&&dragMode==DragMode.Ik,"dragging an IK chain end uses IK by default");
        var farPoint=SurfacePoint(canvasActor.X+120,canvasActor.Y-60);
        SurfaceInput(new InputEventMouseMotion{Position=farPoint});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=farPoint});
        var chainPose=Clip.Sample(frame.Value);
        Expect(Math.Abs(Math.Sqrt(chainPose["HAND1_ABOVE"].X*chainPose["HAND1_ABOVE"].X+chainPose["HAND1_ABOVE"].Y*chainPose["HAND1_ABOVE"].Y)-midLength)<.001&&Math.Abs(Math.Sqrt(chainPose["HAND1_BELOW"].X*chainPose["HAND1_BELOW"].X+chainPose["HAND1_BELOW"].Y*chainPose["HAND1_BELOW"].Y)-endLength)<.001,"IK drag keeps both segment lengths");
        Expect(status.Text.Contains("夹"),"unreachable IK target is clamped to the chain reach");
        Expect(Math.Abs(chainPose["BODY_TOP"].Angle-chainBefore["BODY_TOP"].Angle)>.001,"IK drag rotates the chain root");
        var altBefore=Clip.Sample(frame.Value)["BODY_TOP"].Angle;var altPoint=PointFor("HAND1_BELOW");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,AltPressed=true,Position=altPoint});
        Expect(dragging&&dragMode==DragMode.Fk,"alt switches an IK end to temporary FK");
        var altTarget=SurfacePoint(CanvasPosition(altPoint).X+14,CanvasPosition(altPoint).Y-14);
        SurfaceInput(new InputEventMouseMotion{Position=altTarget});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,AltPressed=true,Position=altTarget});
        var altPose=Clip.Sample(frame.Value)["HAND1_BELOW"];
        Expect(Math.Abs(Clip.Sample(frame.Value)["BODY_TOP"].Angle-altBefore)<.001,"temporary FK leaves the IK chain root untouched");
        Expect(Math.Abs(Math.Sqrt(altPose.X*altPose.X+altPose.Y*altPose.Y)-endLength)<.001,"temporary FK keeps the end length");
        RunAction("undo");Expect(Math.Abs(Clip.Sample(frame.Value)["HAND1_BELOW"].X-chainPose["HAND1_BELOW"].X)<.001,"FK override is one undo step");RunAction("redo");
        SelectBone("HAND1_ABOVE");
        Expect(highlightedBone=="HAND1_ABOVE"&&bone.GetItemText((int)bone.Selected)=="HAND1_ABOVE"&&boneList.GetSelectedItems().Length==1&&boneList.GetItemText(boneList.GetSelectedItems()[0])=="HAND1_ABOVE","bone selection highlights the canvas and the left list");
        var cancelBefore=Serialized();var cancelStart=PointFor("HEAD");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=cancelStart});
        SurfaceInput(new InputEventMouseMotion{Position=SurfacePoint(CanvasPosition(cancelStart).X+30,CanvasPosition(cancelStart).Y-30)});
        SurfaceInput(new InputEventKey{Keycode=Key.Escape,Pressed=true});
        Expect(!dragging&&Serialized()==cancelBefore,"Escape cancels the drag without editing");
        await Shot("motion-drag");
        Change(g=>{g.States.Add(new(){Id="next",Clip=Clip.Id});g.States[0].Transitions.Add(new(){Target="next",Conditions=[new(){Kind=MotionConditionKind.Timer,Comparison=MotionComparison.GreaterOrEqual,Value=4}]});});
        Expect(graphView.GetChildren().OfType<GraphNode>().Count()==2&&graphView.GetConnectionList().Count==1,"state nodes and transition edge are visible");
        Change(_=>Clip.Events.Add(new(){Start=2,Kind=MotionEventKind.SdkEvent,Value="editor.frame"}));
        path=Path.Combine(output,"editor.motion.json");Save();Expect(File.Exists(path),"motion file saved");
        document=new(MotionGraph.Load(path));Refresh();Expect(Clip.Events.Single().Value=="editor.frame","event configuration survives reload");
        InitializePreview();await Ready();
        for(int i=0;i<12;i++){Step();await Ready();}
        Expect(preview!.Bridge.Services.Motions.State(new(preview.World,preview.Player!))?.State=="next","saved state graph runs in preview");
        var help=HelpText();
        Expect(help.Contains("MotionChange")&&help.Contains("PlayerMotionLoad"),"help text names the original motion hooks");
        var registered=MotionImport.Registered(world.Code["external/Chara/Nana/motion_list.txt"]);
        Expect(registered.Count>0&&help.Contains(registered[0].Name+" → "+registered[0].Resource),"help lists registered original motions");
        ApplyToGame();var player=world.Instances.First(i=>!i.Destroyed&&world.IsObject(i.Object,world.Constants["objPlayerDoll"].Int));
        Expect(world.NativeServices!.Motions.State(new(world,player)) is not null,"edited graph applies to the game actor");
        GD.Print("MOTION_EDITOR_AUDIT_OK import=PASS edit=PASS undo=PASS save=PASS reload=PASS preview=PASS game=PASS");
    }
}
