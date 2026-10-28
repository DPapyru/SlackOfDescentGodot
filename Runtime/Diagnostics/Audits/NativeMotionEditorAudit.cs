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
        await Ready();
        Expect(graphView.GetChildren().OfType<GraphNode>().Count()==1,"entry state has a graph node");
        document=new(MotionImport.FromAst(world.Code["external/Chara/Motion/PlayerNana/Stand0_Damage.txt"],"audit.editor","Nana"));Refresh();
        bone.Select(2);frame.Value=2;x.Value=4;y.Value=-16;angle.Value=17;sx.Value=1.25;sy.Value=.8;picture.Value=1;WriteKey();
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
        ApplyToGame();var player=world.Instances.First(i=>!i.Destroyed&&world.IsObject(i.Object,world.Constants["objPlayerDoll"].Int));
        Expect(world.NativeServices!.Motions.State(new(world,player)) is not null,"edited graph applies to the game actor");
        GD.Print("MOTION_EDITOR_AUDIT_OK import=PASS edit=PASS undo=PASS save=PASS reload=PASS preview=PASS game=PASS");
    }
}
