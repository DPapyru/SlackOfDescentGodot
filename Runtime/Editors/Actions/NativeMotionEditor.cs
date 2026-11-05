using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeMotionEditor : Window
{
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private readonly NativeContentStore store;
    private readonly IEnumerable<string> packages;
    private readonly List<MotionProject> projects=new();
    private int activeProject;
    private OptionButton bone=null!,clipChoice=null!,stateChoice=null!,original=null!,transitionTarget=null!,conditionKind=null!,conditionComparison=null!,stateClip=null!;
    private SpinBox frame=null!,x=null!,y=null!,angle=null!,sx=null!,sy=null!,picture=null!,duration=null!,transitionPriority=null!,conditionKey=null!,conditionValue=null!,velocityX=null!,velocityY=null!,gravity=null!,maxFall=null!,inputSpeed=null!,jumpSpeed=null!,displaceX=null!,displaceY=null!;
    private CheckButton velocityXOn=null!,velocityYOn=null!,inputSpeedOn=null!,jumpSpeedOn=null!;
    private TextEdit eventsText=null!,parametersText=null!;
    private Label status=null!,playState=null!,targetLabel=null!;
    private LineEdit stateId=null!,stateRename=null!,clipId=null!,conditionParameter=null!;
    private CheckButton playing=null!,loop=null!,rootTrack=null!;
    private OptionButton speedChoice=null!;
    private TextureRect surface=null!;
    private HSlider timelineSlider=null!;
    private ItemList keyList=null!,transitionList=null!,conditionList=null!,boneList=null!,ikList=null!;
    private TabBar tabs=null!;
    private TabContainer inspector=null!;
    private GraphEdit graphView=null!;
    private SubViewport viewport=null!;
    private GmCanvas canvas=null!;
    private NativeMotionOverlay? overlay;
    private NativeCharacterPreview? preview;
    private GmInstance? targetPreview;
    private ConfirmationDialog confirmation=null!;
    private AcceptDialog message=null!;
    private Action? pendingConfirmation;
    private bool busy,closing,refreshing,refreshingTabs;
    private double playbackSpeed=1;
    private string? lastState,lastTransition;
    private double lastFrame;
    private readonly List<string> lastEvents=new();
    private bool rightHeld;
    private double lastRightTap;
    private bool dragging;
    private string? draggedBone;
    private double dragX,dragY;
    private double elapsed;
    private MotionGraphDocument document{get=>Project.Document;set=>Project.Document=value;}
    private string? path{get=>Project.Path;set=>Project.Path=value;}
    private MotionProject Project=>projects[Math.Clamp(activeProject,0,projects.Count-1)];
    public NativeMotionEditor(GmWorld world,ContentRegistry registry,NativeContentStore store,IEnumerable<string> packages)
    {
        this.world=world;this.registry=registry;this.store=store;this.packages=packages;
        projects.Add(new MotionProject(new(MotionTargetKind.PlayerPreset,"Nana"),new(DefaultGraph())));
    }
    internal MotionGraphDocument Document=>document;
    internal IReadOnlyList<MotionProject> Projects=>projects;
    private static MotionGraph DefaultGraph()=>new(){Clips=[new()],States=[new(){Movement=new(){InputSpeed=4,Gravity=.6,JumpSpeed=10}}]};
    private MotionClip Clip=>document.Graph.Clips[(int)Math.Max(0,clipChoice.Selected)];
    private string SelectedStateId=>stateChoice.Selected<0?document.Graph.States[0].Id:stateChoice.GetItemText((int)stateChoice.Selected);
    private MotionTransition? SelectedTransition=>stateChoice.Selected<0||SelectedIndex(transitionList)<0?null:document.Graph.States[(int)stateChoice.Selected].Transitions[SelectedIndex(transitionList)];
    private ActorContext? PreviewActor=>preview is null?null:targetPreview is {Destroyed:false} target?new(preview.World,target):preview.Player is { } player?new(preview.World,player):null;
    private List<MotionKey> Keys()
    {
        if(rootTrack.ButtonPressed||bone.GetItemText((int)bone.Selected)=="root")return Clip.Root;
        string name=bone.GetItemText((int)bone.Selected);var track=Clip.Tracks.SingleOrDefault(t=>t.Bone==name);
        if(track is null){track=new(){Bone=name};Clip.Tracks.Add(track);}return track.Keys;
    }
    private void ReadKey()
    {
        if(refreshing||x is null)return;
        var key=rootTrack.ButtonPressed||bone.GetItemText((int)bone.Selected)=="root"?new MotionTrack{Keys=Clip.Root}.Sample(frame.Value):Clip.Tracks.SingleOrDefault(t=>t.Bone==bone.GetItemText((int)bone.Selected))?.Sample(frame.Value)??new(0,0,0,1,1);
        x.Value=key.X;y.Value=key.Y;angle.Value=key.Angle;sx.Value=key.ScaleX;sy.Value=key.ScaleY;picture.Value=key.Picture;
        if(timelineSlider is not null){timelineSlider.SetValueNoSignal(frame.Value);keyList.Clear();var keys=rootTrack.ButtonPressed||bone.GetItemText((int)bone.Selected)=="root"?Clip.Root:Clip.Tracks.SingleOrDefault(t=>t.Bone==bone.GetItemText((int)bone.Selected))?.Keys??new();foreach(var k in keys){keyList.AddItem($"{k.Frame:F2}  ({k.X:F1}, {k.Y:F1})  {k.Angle:F1}°  picture={k.Picture}");keyList.SetItemMetadata(keyList.ItemCount-1,k.Frame);}}
    }
    private void WriteKey()=>Change(_=>{var keys=Keys();keys.RemoveAll(k=>k.Frame==frame.Value);keys.Add(new(){Frame=frame.Value,X=x.Value,Y=y.Value,Angle=angle.Value,ScaleX=sx.Value,ScaleY=sy.Value,Picture=(int)picture.Value});keys.Sort((a,b)=>a.Frame.CompareTo(b.Frame));});
    private void RefreshTimeline(){timelineSlider.MaxValue=Clip.Duration;duration.Value=Clip.Duration;loop.ButtonPressed=Clip.Loop;eventsText.Text=JsonSerializer.Serialize(Clip.Events,NativeContentStore.Json);ReadKey();}
    private void Refresh()
    {
        refreshing=true;
        int clipIndex=Project.ClipIndex;clipChoice.Clear();foreach(var c in document.Graph.Clips)clipChoice.AddItem(c.Id);clipChoice.Select(Math.Clamp(clipIndex,0,clipChoice.ItemCount-1));
        parametersText.Text=JsonSerializer.Serialize(document.Graph.Parameters,NativeContentStore.Json);
        RefreshTabs();RefreshStateInspector();RefreshGraph();
        refreshing=false;RefreshTimeline();SyncOverlay();
        if(targetLabel is not null)targetLabel.Text=TargetLabel();
        if(bone.ItemCount>0)bone.Select(Math.Clamp(Project.BoneIndex,0,bone.ItemCount-1));
    }
    private static string NodeName(string id)=>"state_"+Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(id));
    private void PopulateSkeleton()
    {
        if(PreviewActor is not {} active)return;
        var nodes=active.Services.Motions.Skeleton(active);
        highlightedBone=null;int selected=bone.Selected;bone.Clear();bone.AddItem("root");foreach(var node in nodes)bone.AddItem(node.Name);syncingBone=true;bone.Select(Math.Clamp(selected,0,bone.ItemCount-1));syncingBone=false;
        boneList.Clear();foreach(var node in nodes)boneList.AddItem(node.Name);
        RefreshIkList();status.Text="预览骨架已加载："+nodes.Count+" 个节点。";
    }
    private void RefreshIkList()
    {
        ikList.Clear();
        foreach(var chain in IkChains)ikList.AddItem(chain.Root+" → "+chain.Mid+" → "+chain.End);
    }
    private void BindSelectedIk()
    {
        Guard(()=>
        {
            var active=PreviewActor;if(active is null)throw new InvalidOperationException("请先加载预览骨架。");
            string end=bone.GetItemText((int)bone.Selected);if(end=="root")throw new InvalidOperationException("根节点不能作为 IK 末端。");
            var nodes=active.Services.Motions.Skeleton(active);if(!nodes.Any(node=>node.Name==end))throw new InvalidOperationException("所选节点不在预览骨架中。");var current=nodes.Single(node=>node.Name==end);if(!nodes.Any(node=>node.Instance==current.Parent))throw new InvalidOperationException("所选节点缺少父节点。");var mid=nodes.Single(node=>node.Instance==current.Parent);if(!nodes.Any(node=>node.Instance==mid.Parent))throw new InvalidOperationException("所选节点没有连续的两骨父链。");var root=nodes.Single(node=>node.Instance==mid.Parent);
            Change(g=>{MotionIkPresets.Materialize(g);g.IkChains!.RemoveAll(chain=>chain.End==end);g.IkChains.Add(new(){Root=root.Name,Mid=mid.Name,End=end});});status.Text="已绑定 IK："+root.Name+" → "+mid.Name+" → "+end;
        });
    }
    private void UnbindSelectedIk()=>Guard(()=>{string end=bone.GetItemText((int)bone.Selected);Change(g=>{MotionIkPresets.Materialize(g);g.IkChains!.RemoveAll(chain=>chain.Root==end||chain.Mid==end||chain.End==end);});status.Text="已解除所选节点的 IK。";});
    private void SelectBone(string name)
    {
        highlightedBone=name;
        for(int i=0;i<bone.ItemCount;i++)if(bone.GetItemText(i)==name){syncingBone=true;bone.Select(i);syncingBone=false;break;}
        if(boneList is not null)for(int i=0;i<boneList.ItemCount;i++)if(boneList.GetItemText(i)==name){syncingBone=true;boneList.Select(i);syncingBone=false;break;}
        ReadKey();SyncOverlay();
    }
    private void ClearBoneSelection()
    {
        highlightedBone=null;
        if(boneList is not null){syncingBone=true;boneList.DeselectAll();syncingBone=false;}
        SyncOverlay();
    }
    private IReadOnlyList<MotionIkChain> IkChains=>MotionIkPresets.For(document.Graph);
    private bool IsIkEnd(string name)=>IkChains.Any(chain=>chain.Enabled&&chain.End==name);
    private void GenerateReferencePose()=>Guard(()=>{
        if(PreviewActor is not {} actor)throw new InvalidOperationException("请先加载预览骨架。");
        var descriptor=MotionSkeletonDescriptor.FromActor(actor.World,actor.Instance,document.Graph.Skeleton);
        Change(graph=>MotionRigTemplates.EnsureReferencePose(graph,descriptor));
        status.Text="已生成参考姿态；一次拖动即可撤销。";
    });
    private void RefreshGraph()
    {
        graphView.ClearConnections();foreach(var node in graphView.GetChildren().OfType<GraphNode>()){graphView.RemoveChild(node);node.QueueFree();}
        for(int i=0;i<document.Graph.States.Count;i++)
        {
            var state=document.Graph.States[i];var node=new GraphNode{Name=NodeName(state.Id),Title=(document.Graph.Entry==state.Id?"▶ ":"")+state.Id,PositionOffset=new(30+(i%3)*220,30+(i/3)*150),CustomMinimumSize=new(180,80)};
            node.AddChild(new Label{Text=state.Clip});node.SetSlot(0,true,0,Colors.SkyBlue,true,0,Colors.Orange);graphView.AddChild(node);
            node.GuiInput+=input=>{if(input is InputEventMouseButton{Pressed:true,ButtonIndex:MouseButton.Left}){stateChoice.Select(document.Graph.States.FindIndex(s=>s.Id==state.Id));RefreshStateInspector();}};
        }
        foreach(var state in document.Graph.States)foreach(var transition in state.Transitions)graphView.ConnectNode(NodeName(state.Id),0,NodeName(transition.Target),0);
    }
    private void Guard(Action action){try{action();}catch(Exception e){status.Text=e.GetBaseException().Message;}}
    private void Change(Action<MotionGraph> action){if(refreshing)return;Guard(()=>{document.Edit(action);Refresh();ApplyDocumentPose();if(playing.ButtonPressed)RestartPlayback();status.Text="已编辑。";});}
    private void Confirm(string text,Action action){pendingConfirmation=action;confirmation.DialogText=text;confirmation.PopupCentered();}
    private void ShowMessage(string text){message.DialogText=text;message.PopupCentered();}
    private void RequestClose()
    {
        if(closing)return;
        void Close(){closing=true;preview?.Dispose();QueueFree();}
        ConfirmDirtyClose(Close);
    }
    private void ImportOriginal()=>Guard(()=>{string name=original.GetItemText((int)original.Selected);document=new(MotionImport.FromAst(world.Code["external/Chara/Motion/"+name+".txt"],name.Replace('/','.'),document.Graph.Skeleton));path=null;Refresh();InitializePreview();status.Text="已导入："+name;});
    private void Save(){if(path is null){ChooseFile(true);return;}Guard(()=>{document.Save(path);Refresh();status.Text="已保存："+path;});}
    private void ChooseFile(bool save)
    {
        var dialog=new FileDialog{FileMode=save?FileDialog.FileModeEnum.SaveFile:FileDialog.FileModeEnum.OpenFile,Access=FileDialog.AccessEnum.Filesystem,UseNativeDialog=true};dialog.AddFilter("*.motion.json","Motion graph");AddChild(dialog);
        dialog.CurrentDir=Path.Combine(store.Root,"Actions");Directory.CreateDirectory(dialog.CurrentDir);dialog.CurrentFile=document.Graph.Id+".motion.json";
        dialog.FileSelected+=file=>Guard(()=>{if(save){document.Save(file);}else{document=new(MotionGraph.Load(file));Project.Target=MotionSkeleton.IsLegacy(document.Graph.Skeleton)?new(MotionTargetKind.Entity,document.Graph.Skeleton[7..]):new(MotionTargetKind.PlayerPreset,document.Graph.Skeleton);}path=file;Refresh();InitializePreview();status.Text=file;dialog.QueueFree();});dialog.Canceled+=()=>dialog.QueueFree();dialog.PopupCenteredRatio(.8f);
    }
    private async void InitializePreview()
    {
        if(busy)return;busy=true;playing.ButtonPressed=false;
        try
        {
             preview?.Dispose();targetPreview=null;preview=new(viewport,world,registry,new(store),packages);await preview.InitializeAsync();
             if(closing)return;
            var target=Project.Target;
            ActorContext actor;
            if(target.Kind==MotionTargetKind.Entity){targetPreview=CreateLegacyPreview(target.Id);actor=new(preview.World,targetPreview);}
            else {await preview.ApplyAsync(CharacterDefinition(target));targetPreview=null;actor=new(preview.World,preview.Player!);}
             PopulateSkeleton();preview.Bridge.Services.Motions.Play(actor,document.Graph);
             canvas.Paint=c=>preview.Graphics.Render(c);FitView();status.Text="预览已加载。";
        }
        catch(Exception e){status.Text=e.GetBaseException().Message;}
        finally{busy=false;}
    }
    private CharacterDefinition CharacterDefinition(MotionProjectTarget target)
    {
        if(target.Kind==MotionTargetKind.PlayerPreset)return new(){BasePreset=target.Id,Appearance="Character-"+target.Id};
        if(!registry.Characters.TryGetValue(target.Id,out var definition))throw new InvalidDataException("角色不存在："+target.Id);
        var copy=JsonSerializer.Deserialize<CharacterDefinition>(JsonSerializer.SerializeToUtf8Bytes(definition,NativeContentStore.Json),NativeContentStore.Json)!;
        return copy;
    }
    private string CharacterPreset(MotionProjectTarget target)=>target.Kind==MotionTargetKind.PlayerCharacter&&registry.Characters.TryGetValue(target.Id,out var definition)?definition.BasePreset:"Nana";
    private GmInstance CreateLegacyPreview(string resource)
    {
        if(preview is null)throw new InvalidOperationException("预览尚未初始化。");
        int objectId=world.Objects.FirstOrDefault(pair=>pair.Value.GetProperty("name").GetString()==resource).Key;
        if(objectId==0&&!world.Objects.ContainsKey(objectId))throw new InvalidDataException("实体资源不存在："+resource);
        double x=preview.Player?.Scope.ReadOr("x").Number??96,y=preview.Player?.Scope.ReadOr("y").Number??0;
        var root=preview.World.Create(objectId,x,y,run:false);var child=preview.World.Create(objectId,x+24,y,run:false);child.Scope.Write("parent",root.Id);child.Scope.Write("PartPosDir",0);child.Scope.Write("PartPosDis",24);root.Scope.Write("Process",child.Object,0);return root;
    }
    public override void _Process(double delta)=>TickPlayback(delta);
    internal void TickPlayback(double delta)
    {
        if(!playing.ButtonPressed||busy||preview is null)return;
        elapsed+=Math.Min(delta,.5)*playbackSpeed;
        int steps=0;while(elapsed>=FrameContext.FixedDelta&&steps<30){elapsed-=FrameContext.FixedDelta;steps++;}
        if(steps>0)Advance(steps);
    }
    private void Step()=>Advance(1);
    private async void Advance(int steps)
    {
        var activePreview=preview;if(busy||activePreview is null||PreviewActor is not {} active)return;busy=true;
        try
        {
            for(int i=0;i<steps;i++)
            {
                await activePreview.AdvanceAsync(PreviewInput());
                var state=active.Services.Motions.State(active);
                if(state is null)continue;
                if(lastState is not null&&lastState!=state.State)lastTransition=lastState+" → "+state.State;
                lastEvents.Clear();
                var clip=document.Graph.Clips.FirstOrDefault(item=>item.Id==document.Graph.States.FirstOrDefault(s=>s.Id==state.State)?.Clip);
                if(clip is not null)foreach(var item in clip.Events.Where(e=>!e.Contact&&e.Start>lastFrame&&e.Start<=state.Frame))lastEvents.Add(item.Value.Length>0?item.Value:item.Id);
                lastState=state.State;lastFrame=state.Frame;
                playState.Text=$"{state.State}  frame={state.Frame:F1}  x={active.X:F2} y={active.Y:F2} suspended={state.Suspended}";
            }
            canvas.QueueRedraw();SyncOverlay();
        }
        catch(Exception e){playing.ButtonPressed=false;status.Text=e.GetBaseException().Message;}
        finally{busy=false;}
    }
    private NativeCharacterPreviewInput PreviewInput()
    {
        if(PreviewActor is not {} actor)return new(false,false,false,false);
        bool Held(int slot)=>Input.IsKeyPressed(GodotKey(NativeActorActions.Key(actor,slot)));
        bool right=Held(1),dash=false;
        if(right&&!rightHeld){double now=Time.GetTicksMsec();dash=now-lastRightTap<250;lastRightTap=now;}
        rightHeld=right;
        return new(Held(0),right,Held(5),dash);
    }
    private static Key GodotKey(int code)=>code switch
    {
        8=>Key.Backspace,9=>Key.Tab,13=>Key.Enter,16=>Key.Shift,17=>Key.Ctrl,18=>Key.Alt,27=>Key.Escape,32=>Key.Space,
        33=>Key.Pageup,34=>Key.Pagedown,35=>Key.End,36=>Key.Home,37=>Key.Left,38=>Key.Up,39=>Key.Right,40=>Key.Down,45=>Key.Insert,46=>Key.Delete,
        >=112 and <=123=>Key.F1+(code-112),_=>(Key)code
    };
    private string KeyLegend()
    {
        if(PreviewActor is not {} actor)return "";
        string Name(int slot)=>OS.GetKeycodeString(GodotKey(NativeActorActions.Key(actor,slot)));
        return "左 "+Name(0)+"   右 "+Name(1)+"   跳 "+Name(5)+"   冲刺 双击"+Name(1);
    }
    private void RestartPlayback()
    {
        Guard(()=>
        {
            if(preview is null||PreviewActor is not {} actor)return;
            preview.Bridge.Services.Motions.Play(actor,document.Graph);
            preview.Bridge.Services.Motions.Seek(actor,frame.Value);
            lastState=document.Graph.Entry;lastFrame=frame.Value;lastTransition=null;lastEvents.Clear();elapsed=0;
            UpdatePlaybackOverlay();
        });
    }
    private void UpdatePlaybackOverlay()
    {
        if(overlay is null)return;
        if(PreviewActor is not {} actor){overlay.Legend="";overlay.Playback="";return;}
        overlay.Legend="键位  "+KeyLegend();
        var state=actor.Services.Motions.State(actor);
        if(state is null){overlay.Playback="状态：未播放";return;}
        var clip=document.Graph.Clips.FirstOrDefault(item=>item.Id==document.Graph.States.FirstOrDefault(s=>s.Id==state.State)?.Clip);
        var text=new System.Text.StringBuilder();
        text.Append("状态 ").Append(state.State).Append("  帧 ").Append(state.Frame.ToString("F1"));
        if(clip is not null)text.Append('/').Append(clip.Duration.ToString("F0"));
        if(lastTransition is not null)text.Append("  转换 ").Append(lastTransition);
        if(lastEvents.Count>0)text.Append("  事件 ").Append(string.Join(",",lastEvents));
        overlay.Playback=text.ToString();
    }
    private void ApplyToGame()=>Guard(()=>
    {
        var instance=world.Instances.FirstOrDefault(i=>!i.Destroyed&&world.IsObject(i.Object,world.Constants["objPlayerDoll"].Int));
        if(MotionSkeleton.IsLegacy(document.Graph.Skeleton))
        {
            string resource=document.Graph.Skeleton[7..];int objectId=world.Objects.FirstOrDefault(pair=>pair.Value.GetProperty("name").GetString()==resource).Key;instance=world.Instances.FirstOrDefault(i=>!i.Destroyed&&i.Object==objectId)??throw new InvalidOperationException("没有目标实体实例："+resource);
        }
        if(instance is null)throw new InvalidOperationException("没有游戏角色");
        var actor=new ActorContext(world,instance);actor.Services.Motions.Play(actor,document.Graph);status.Text=(MotionSkeleton.IsLegacy(document.Graph.Skeleton)?"已应用到目标实体":"已应用到游戏角色")+"；关闭编辑器后继续运行；要持久化请保存到 Mod 项目或角色。";
    });
    private void BindSavedCharacter()=>Guard(()=>
    {
        if(path is null)throw new InvalidOperationException("请先保存动作图。");
        document.Save(path);registry.MotionGraphs[document.Graph.Id]=MotionGraph.Load(path);
        var dialog=new FileDialog{FileMode=FileDialog.FileModeEnum.OpenFile,Access=FileDialog.AccessEnum.Filesystem,UseNativeDialog=true};dialog.AddFilter("*.character.json","Character");AddChild(dialog);dialog.CurrentDir=store.Characters;
        dialog.FileSelected+=file=>Guard(()=>{var character=NativeContentStore.Load<CharacterDefinition>(file);if(character.BasePreset!=document.Graph.Skeleton)throw new InvalidDataException("角色骨架不匹配");character.MotionGraph=document.Graph.Id;NativeContentStore.Save(file,character);registry.RegisterCharacter(character);dialog.QueueFree();status.Text="角色已显式启用动作图。";});dialog.PopupCenteredRatio(.8f);
    });
    public override void _ExitTree(){closing=true;preview?.Dispose();}
}
