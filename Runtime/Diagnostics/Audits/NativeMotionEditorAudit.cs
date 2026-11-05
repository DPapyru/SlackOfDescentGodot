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
        foreach(var node in skeleton)
        {
            var (surfaceX,surfaceY)=MotionViewport.ToSurface(node.X,node.Y,surface.Size.X,surface.Size.Y,PreviewView);
            var picked=PickBoneAt(new((float)surfaceX,(float)surfaceY));var hit=skeleton.FirstOrDefault(item=>item.Name==picked);
            Expect(hit is not null&&Math.Abs(hit.X-node.X)<.001&&Math.Abs(hit.Y-node.Y)<.001,"skeleton pick hits "+node.Name);
        }
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
