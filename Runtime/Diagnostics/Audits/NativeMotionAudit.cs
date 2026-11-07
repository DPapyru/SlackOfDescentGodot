using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeMotionAudit : Node
{
    public override async void _Ready()
    {
        string temp=Path.Combine(Path.GetTempPath(),"ccgame-motion-audit-"+Guid.NewGuid().ToString("N"));
        int checks=0;
        void Expect(bool condition,string message){checks++;if(!condition)throw new InvalidOperationException(message);}
        try
        {
            string root=ProjectSettings.GlobalizePath("res://GameData");var live=new GmWorld(root,Path.Combine(root,"assets"),Path.Combine(temp,"live"));var store=new NativeContentStore(Path.Combine(temp,"content"));var registry=new ContentRegistry();
            NativeLocalization.Initialize(root,live.UserRoot);
            foreach(string preset in new[]{"Nana","Ryoko","Yuki"})
            {
                using var preview=new NativeCharacterPreview(this,live,registry,new(store));await preview.InitializeAsync();
                var definition=new CharacterDefinition{BasePreset=preset,Appearance="Character-"+preset};await preview.ApplyAsync(definition);
                var w=preview.World;var actor=new ActorContext(w,preview.Player!);var motions=preview.Bridge.Services.Motions;
                Expect(motions.Skeleton(actor).Count==15,preset+" skeleton resolves");
                var imported=MotionImport.FromAst(w.Code["external/Chara/Motion/PlayerNana/Stand0_Damage.txt"],"audit.motion",preset);
                var document=new MotionGraphDocument(imported);document.Edit(g=>{g.Clips[0].Loop=true;g.Clips[0].Events.Add(new(){Kind=MotionEventKind.SdkEvent,Start=0,Value="audit.motion.frame"});});
                string file=Path.Combine(temp,preset+".motion.json");document.Save(file);var graph=MotionGraph.Load(file);graph.States[0].Movement=new(){VelocityX=1,Gravity=0,VelocityY=0};
                int events=0;registry.EventHandlers["audit.frame"]=new("audit","audit.motion.frame",NativeEventPhase.After,_=>events++);
                preview.Registry.EventHandlers["audit.frame"]=registry.EventHandlers["audit.frame"];preview.Registry.Refresh();
                actor.Write("DMG",0);actor.Write("BINDED",0);actor.Write("HALFBINDED",0);NativeActorActions.Motion(actor,"STAND_0");
                await preview.AdvanceAsync(new(false,false,false,false));motions.Play(actor,graph);
                double before=actor.X;
                for(int i=0;i<6;i++)await preview.AdvanceAsync(new(false,false,false,false));
                Expect(motions.State(actor) is {Suspended:false},preset+" graph active after physics");
                Expect(actor.X>before,preset+" movement reaches world integration");
                Expect(events==1,preset+" frame event fires once");
                var state=motions.State(actor)!;Expect(state.Frame>=3,preset+" 60Hz frame advances");
                var head=actor.Part("HEAD")!;var expected=graph.Clips[0].Sample(state.Frame)["HEAD"];Expect(Math.Abs(head.Read("image_angle")-expected.Angle-preview.Bridge.Services.Entities.Find(head.Instance.Scope.ReadOr("parent").Int)!.Read("image_angle"))<.01,preset+" final pose survives End Step");
                bool conflict=false;try{preview.Bridge.Services.Actors.Bind(actor,CharacterActionSlot.Dash,new NativeSampleBoost());}catch(InvalidDataException){conflict=true;}Expect(conflict,preset+" conflicting binding rejected");
                actor.Write("BINDED",1);await preview.AdvanceAsync(new(false,false,false,false));await preview.AdvanceAsync(new(false,false,false,false));Expect(motions.State(actor)?.Suspended==true,preset+" forced state suspends graph");
                actor.Write("BINDED",0);actor.Write("HALFBINDED",0);actor.Write("DMG",0);NativeActorActions.Motion(actor,"STAND_0");for(int i=0;i<4;i++)await preview.AdvanceAsync(new(false,false,false,false));Expect(motions.State(actor)?.Suspended==false,preset+" forced state recovers");
                motions.Stop(actor);
                preview.Registry.RegisterEntity(new NativeEntityDefinition{Id="audit.target",BehaviorId="audit.target",Width=32,Height=32},()=>new Target());preview.Registry.Refresh();
                int contacts=0;preview.Registry.EventHandlers["audit.contact"]=new("audit","audit.contact",NativeEventPhase.After,_=>contacts++);preview.Registry.Refresh();
                var target=preview.Bridge.Services.Entities.Spawn("audit.target",actor.X,actor.Y);
                var contactGraph=MotionGraph.Load(file);contactGraph.Clips[0].Loop=false;contactGraph.States[0].Movement=new(){VelocityX=0,VelocityY=0};
                contactGraph.Clips[0].Events=[new(){Contact=true,Target=MotionTarget.Entity,Kind=MotionEventKind.SdkEvent,Start=0,End=contactGraph.Clips[0].Duration,Width=128,Height=128,Value="audit.contact"}];
                motions.Play(actor,contactGraph);for(int i=0;i<6;i++)await preview.AdvanceAsync(new(false,false,false,false));
                Expect(contacts==1,preset+" contact interval deduplicates target");
                motions.Stop(actor);preview.Bridge.Services.Entities.Destroy(target.Id);
                preview.Registry.RegisterEntity(new NativeEntityDefinition{Id="audit.wall",BehaviorId="audit.wall",Width=32,Height=256,Solid=true},()=>new Target("audit.wall"));
                var wall=preview.Bridge.Services.Entities.Spawn("audit.wall",actor.X+64,actor.Y);
                var blocked=MotionGraph.Load(file);blocked.States[0].Movement=new(){VelocityX=8,VelocityY=0};double approach=actor.X;
                motions.Play(actor,blocked);for(int i=0;i<20;i++)await preview.AdvanceAsync(new(false,false,false,false));
                Expect(actor.X>approach&&actor.X<wall.X-16,preset+" graph movement stops at collision boundary: start="+approach+" x="+actor.X+" wall="+wall.X+" suspended="+motions.State(actor)?.Suspended+" bounds="+w.Bounds(actor.Instance));
                double stopped=actor.X;for(int i=0;i<4;i++)await preview.AdvanceAsync(new(false,false,false,false));Expect(Math.Abs(actor.X-stopped)<.01,preset+" graph does not integrate blocked movement twice");
                motions.Stop(actor);preview.Bridge.Services.Entities.Destroy(wall.Id);

                using(var comparison=new NativeCharacterPreview(this,live,registry,new(store)))
                {
                    await comparison.InitializeAsync();await comparison.ApplyAsync(new(){BasePreset=preset,Appearance="Character-"+preset});
                    var other=new ActorContext(comparison.World,comparison.Player!);preview.Reset();comparison.Reset();
                    actor.Write("DMG",0);actor.Write("BINDED",0);actor.Write("HALFBINDED",0);NativeActorActions.Motion(actor,"STAND_0");
                    NativeActorActions.Motion(other,"STAND_0");
                    var stable=new MotionGraph{Id="parity",Skeleton=preset,Clips=[new(){Duration=60,Tracks=[new(){Bone="HEAD",Keys=[new(){Angle=17}]}]}],States=[new(){Movement=new(){VelocityX=0,VelocityY=0}}]};
                    stable.Clips[0].Events=[new(){Kind=MotionEventKind.SdkEvent,Start=2,Value="audit.parity"}];
                    int previewEvents=0,gameEvents=0;
                    preview.Registry.EventHandlers["parity"]=new("audit","audit.parity",NativeEventPhase.After,_=>previewEvents++);preview.Registry.Refresh();
                    comparison.Registry.EventHandlers["parity"]=new("audit","audit.parity",NativeEventPhase.After,_=>gameEvents++);comparison.Registry.Refresh();
                    motions.Play(actor,stable);comparison.Bridge.Services.Motions.Play(other,stable);
                    for(int i=0;i<8;i++)
                    {
                        await preview.AdvanceAsync(new(false,false,false,false));
                        comparison.Clock.BeginTick();comparison.Bridge.Update(new(FrameContext.FixedDelta,comparison.Clock.GameTime,comparison.Clock.Tick,InputSnapshot.Empty));
                        if(comparison.Clock.LegacyDue(comparison.World.Engine.Read("room_speed").Number))await comparison.World.StepAsync();
                    }
                    Expect(motions.State(actor)?.Frame==comparison.Bridge.Services.Motions.State(other)?.Frame,preset+" preview and world state frames agree");
                    Expect(Math.Abs(actor.X-other.X)<.01&&Math.Abs(actor.Y-other.Y)<.01,preset+" preview and world positions agree");
                    Expect(previewEvents==gameEvents&&previewEvents==1,preset+" preview and world events agree");
                    Expect(Math.Abs(actor.Part("HEAD")!.Read("image_angle")-other.Part("HEAD")!.Read("image_angle"))<.01,preset+" preview and world poses agree");
                }
                motions.Stop(actor);Expect(motions.State(actor) is null,preset+" stop releases graph");
                motions.Play(actor,graph);await w.LoadRoomAsync(w.Constants["Title"].Int);Expect(motions.State(actor) is null,preset+" room change stops old playback");
                if(preset=="Nana")
                {
                    var map=new NativeMapService(w,preview.Registry).ImportOriginal(w.Constants["Title"].Int).Document;
                    map.Room["instances"]=new System.Text.Json.Nodes.JsonArray();map.Room["tiles"]=new System.Text.Json.Nodes.JsonArray();map.Room["code"]=w.Compile("return 0;");
                    map.Events=[new(){Id="stable.event",Once=true,Actions=[new(){Kind=NativeMapEventActionKind.SetSwitch,StateKey="motion.once",Enabled=true}]}];
                    using var maps=new NativeMapRuntime(w,preview.Registry,preview.Bridge.Services,[new("motion.map","audit",map)]);await w.LoadRoomAsync(maps.Room("motion.map"));
                    Expect(preview.Bridge.Services.Maps.Trigger("stable.event"),"SDK triggers stable map event id");
                    Expect(!preview.Bridge.Services.Maps.Trigger("stable.event")&&maps.State["motion.once"],"explicit map trigger preserves Once rule");
                }

            }
            using(var game=new NativeCharacterPreview(this,live,registry,new(store)))
            {
                await game.InitializeAsync();
                var editor=new NativeActionEditor(game.World,game.Registry,store,[]){Visible=false};AddChild(editor);
                await editor.AuditAsync(temp);
                if(System.Environment.GetEnvironmentVariable("SLACK_MOTION_IMPORT_PERF") is{Length:>0} importPerf)await editor.ImportForensicsAsync(importPerf);
                RemoveChild(editor);editor.QueueFree();
                string package=Path.Combine(temp,"motion-package");Directory.CreateDirectory(package);
                var packaged=new MotionGraph{Id="audit.package.motion",Clips=[new()],States=[new()]};packaged.Save(Path.Combine(package,"player.motion.json"));
                NativeContentStore.Save(Path.Combine(package,"mod.json"),new NativeModManifest{FormatVersion=2,Id="audit.motion.package",Name="Motion package",Version="1.0.0",Content=new(){Motions={{packaged.Id,"player.motion.json"}}}});
                using var manager=new NativeModManager(game.Registry);
                var loaded=manager.Reload(package);Expect(loaded.Success,"motion package registers: "+loaded.Diagnostics);
                var owner=new ActorContext(game.World,game.Player!);game.Bridge.Services.Motions.Play(owner,packaged.Id);
                var reloaded=manager.Reload(package);Expect(reloaded.Success&&game.Bridge.Services.Motions.State(owner) is null,"hot reload stops old owned graph playback");
                game.Bridge.Services.Motions.Play(owner,packaged.Id);var unloaded=manager.Unload("audit.motion.package");
                Expect(unloaded.Success&&!game.Registry.MotionGraphs.ContainsKey(packaged.Id)&&game.Bridge.Services.Motions.State(owner) is null,"unload removes graph and active owned session");

            }
            var graphical=DisplayServer.GetName()!="headless";
            if(graphical)await ComparePerformance(live,registry,store,temp);
            GD.Print("MOTION_AUDIT_OK checks=",checks);GetTree().Quit();
        }
        catch(Exception error){GD.PushError(error.ToString());GetTree().Quit(1);}
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    private sealed class Target(string id="audit.target") : IEntityBehavior
    {
        public string Id=>id;
        public void Update(ActorContext actor,FrameContext frame){}
    }
    private async Task ComparePerformance(GmWorld live,ContentRegistry registry,NativeContentStore store,string output)
    {
        var reports=new List<object>();
        foreach(bool enabled in new[]{false,true})
        {
            using var preview=new NativeCharacterPreview(this,live,registry,new(store));preview.World.Random.Seed=0;await preview.InitializeAsync();
            int rendered=0;var canvas=new GmCanvas{Paint=c=>{preview.Graphics.Render(c);rendered++;}};AddChild(canvas);
            GetWindow().Show();
            if(enabled)
            {
                var graph=MotionImport.FromAst(preview.World.Code["external/Chara/Motion/PlayerNana/Stand0_Damage.txt"],"perf","Nana");
                preview.Bridge.Services.Motions.Play(new(preview.World,preview.Player!),graph);
            }
            var samples=new List<double>();var renderSamples=new List<double>();long drawCalls=0;
            long builds=preview.Graphics.Builds;
            for(int frame=0;frame<180;frame++)
            {
                long started=System.Diagnostics.Stopwatch.GetTimestamp();await preview.AdvanceAsync(new(false,false,false,false));
                if(frame>=60)samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                canvas.QueueRedraw();await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                long renderStart=System.Diagnostics.Stopwatch.GetTimestamp();RenderingServer.ForceDraw(true);
                if(frame>=60)renderSamples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds);
                drawCalls=Math.Max(drawCalls,(long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));
            }
            if(rendered==0)throw new InvalidOperationException("Performance sample did not render its canvas.");
            samples.Sort();reports.Add(new{graph=enabled,meanMs=samples.Average(),p95Ms=samples[(int)(samples.Count*.95)],builds=preview.Graphics.Builds-builds,display=DisplayServer.GetName(),renderedFrames=rendered,drawCalls,renderMeanMs=renderSamples.Average()});
            RemoveChild(canvas);canvas.QueueFree();
        }
        string file=System.Environment.GetEnvironmentVariable("SLACK_MOTION_PERF")??Path.Combine(output,"motion-performance.json");NativeContentStore.Save(file,reports);GD.Print("MOTION_PERFORMANCE ",JsonSerializer.Serialize(reports));
    }

}
