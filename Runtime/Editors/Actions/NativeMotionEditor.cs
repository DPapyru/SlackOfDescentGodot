using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeMotionEditor : Window
{
    private readonly GmWorld world;
    private readonly ContentRegistry registry;
    private readonly NativeContentStore store;
    private MotionGraphDocument document;
    private readonly IEnumerable<string> packages;
    private OptionButton skeleton=null!,bone=null!,clipChoice=null!,stateChoice=null!,original=null!,entities=null!;
    private SpinBox frame=null!,x=null!,y=null!,angle=null!,sx=null!,sy=null!,picture=null!,duration=null!;
    private TextEdit stateText=null!,eventsText=null!,parametersText=null!;
    private Label status=null!,playState=null!;
    private LineEdit graphId=null!,stateId=null!,clipId=null!;
    private CheckButton playing=null!,loop=null!,rootTrack=null!;
    private TextureRect surface=null!;
    private HSlider timelineSlider=null!;
    private ItemList keyList=null!;
    private GraphEdit graphView=null!;
    private SubViewport viewport=null!;
    private GmCanvas canvas=null!;
    private NativeCharacterPreview? preview;
    private GmInstance? targetPreview;
    private bool busy,closing,refreshing;
    private bool dragging;
    private string? draggedBone;
    private NativeMotionOverlay? overlay;
    private double dragX,dragY;
    private double elapsed;
    private string? path;
    public NativeMotionEditor(GmWorld world,ContentRegistry registry,NativeContentStore store,IEnumerable<string> packages)
    {
        this.world=world;this.registry=registry;this.store=store;this.packages=packages;
        document=new(DefaultGraph());
    }
    internal MotionGraphDocument Document=>document;
    private static MotionGraph DefaultGraph()=>new(){Clips=[new()],States=[new(){Movement=new(){InputSpeed=4,Gravity=.6,JumpSpeed=10}}]};
    private MotionClip Clip=>document.Graph.Clips[(int)Math.Max(0,clipChoice.Selected)];
    private static HBoxContainer Row(Node parent){var row=new HBoxContainer();parent.AddChild(row);return row;}
    private static void Button(Node parent,string text,Action action){var b=new Button{Text=text};parent.AddChild(b);b.Pressed+=action;}
    private static SpinBox Number(Node parent,string label,double min=-4096,double max=4096,double value=0)
    {
        parent.AddChild(new Label{Text=label});var number=new SpinBox{MinValue=min,MaxValue=max,Step=.01,Value=value,CustomMinimumSize=new(90,0)};parent.AddChild(number);return number;
    }
    private static VBoxContainer Page(TabContainer tabs,string name){var page=new VBoxContainer{Name=name};tabs.AddChild(page);return page;}
    public override void _Ready()
    {
        Name="MotionGraphEditor";Title="骨架动作与状态图";Size=new(1250,820);MinSize=new(1080,700);Transient=true;
        CloseRequested+=()=>{closing=true;preview?.Dispose();QueueFree();};
        var root=new VBoxContainer();AddChild(root);root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var tools=Row(root);Button(tools,"导入原版",ImportOriginal);Button(tools,"保存",Save);Button(tools,"另存为",()=>ChooseFile(true));Button(tools,"加载",()=>ChooseFile(false));Button(tools,"撤销",()=>{document.Undo();Refresh();});Button(tools,"重做",()=>{document.Redo();Refresh();});
        tools.AddChild(new Label{Text="骨架"});skeleton=new();foreach(string preset in new[]{"Nana","Ryoko","Yuki"})skeleton.AddItem(preset);tools.AddChild(skeleton);
        skeleton.ItemSelected+=i=>Change(g=>g.Skeleton=skeleton.GetItemText((int)i));
        graphId=new LineEdit{Text="motion",PlaceholderText="动作图标识",CustomMinimumSize=new(140,0)};tools.AddChild(graphId);
        Button(tools,"设置标识",()=>Change(g=>g.Id=graphId.Text));
        original=new OptionButton{CustomMinimumSize=new(300,0)};root.AddChild(original);
        foreach(string key in world.Code.Keys.Where(k=>k.StartsWith("external/Chara/Motion/",StringComparison.Ordinal)).Order())original.AddItem(key["external/Chara/Motion/".Length..^4]);
        var content=Row(root);content.SizeFlagsVertical=Control.SizeFlags.ExpandFill;
        var left=new VBoxContainer{CustomMinimumSize=new(410,0)};content.AddChild(left);
        surface=new TextureRect{CustomMinimumSize=new(400,300),StretchMode=TextureRect.StretchModeEnum.KeepAspectCentered,ExpandMode=TextureRect.ExpandModeEnum.IgnoreSize};left.AddChild(surface);overlay=new NativeMotionOverlay();surface.AddChild(overlay);overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);surface.GuiInput+=SurfaceInput;surface.Resized+=SyncOverlay;
        viewport=new SubViewport{Size=new(640,480),RenderTargetUpdateMode=SubViewport.UpdateMode.Always,TransparentBg=false};AddChild(viewport);
        canvas=new GmCanvas();viewport.AddChild(canvas);surface.Texture=viewport.GetTexture();
        var playback=Row(left);Button(playback,"重载预览",()=>InitializePreview());playing=new CheckButton{Text="播放"};playback.AddChild(playing);Button(playback,"单帧",()=>Step());
        playState=new Label{AutowrapMode=TextServer.AutowrapMode.WordSmart};left.AddChild(playState);
        left.AddChild(new Label{Text="预览控制：方向键移动，空格跳跃"});
        entities=new OptionButton();left.AddChild(entities);
        foreach(var item in world.Objects.OrderBy(p=>p.Value.GetProperty("name").GetString()).Where(p=>p.Value.GetProperty("name").GetString()!.Contains("objEnemy",StringComparison.Ordinal)||p.Value.GetProperty("name").GetString()!.Contains("objBlock",StringComparison.Ordinal))){entities.AddItem(item.Value.GetProperty("name").GetString()!);entities.SetItemMetadata(entities.ItemCount-1,item.Key);}
        var spawn=Row(left);var spawnX=Number(spawn,"X",-10000,10000,180);var spawnY=Number(spawn,"Y",-10000,10000,0);
        Button(left,"放置测试实体",()=>Guard(()=>{if(preview is null)return;preview.World.Create(entities.GetItemMetadata((int)entities.Selected).AsInt32(),spawnX.Value,spawnY.Value);}));Button(left,"使用实体建立预览骨架",SelectLegacyTarget);
        Button(left,"应用到游戏角色",ApplyToGame);
        Button(left,"将动作图保存到角色",BindSavedCharacter);
        var tabs=new TabContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,SizeFlagsVertical=Control.SizeFlags.ExpandFill};content.AddChild(tabs);
        var timeline=Page(tabs,"时间轴");var select=Row(timeline);clipChoice=new();select.AddChild(clipChoice);clipChoice.ItemSelected+=_=>RefreshTimeline();clipId=new LineEdit{PlaceholderText="新片段标识",CustomMinimumSize=new(120,0)};select.AddChild(clipId);Button(select,"新增片段",()=>Change(g=>g.Clips.Add(new(){Id=clipId.Text})));
        var timing=Row(timeline);duration=Number(timing,"长度（60Hz）",1,100000,60);loop=new CheckButton{Text="循环",ButtonPressed=true};timing.AddChild(loop);Button(timing,"更新片段",()=>Change(_=>{Clip.Duration=duration.Value;Clip.Loop=loop.ButtonPressed;}));
        var track=Row(timeline);bone=new();bone.AddItem("root");foreach(string b in MotionSkeleton.Bones)bone.AddItem(b);track.AddChild(bone);bone.ItemSelected+=_=>ReadKey();rootTrack=new CheckButton{Text="姿态根偏移"};track.AddChild(rootTrack);rootTrack.Toggled+=_=>ReadKey();Button(track,"应用肢体预设",()=>Change(g=>MotionIkPresets.Ensure(g)));Button(track,"绑定所选末端",BindSelectedIk);Button(track,"解除所选 IK",UnbindSelectedIk);
        var time=Row(timeline);frame=Number(time,"动作帧",0,100000);frame.ValueChanged+=_=>ReadKey();Button(time,"读取运行帧",()=>{if(PreviewActor is {} actor&&actor.Services.Motions.State(actor) is {} state)frame.Value=state.Frame;});
        timelineSlider=new HSlider{MinValue=0,MaxValue=60,Step=.25};timeline.AddChild(timelineSlider);timelineSlider.ValueChanged+=value=>frame.Value=value;
        keyList=new ItemList{CustomMinimumSize=new(0,100)};timeline.AddChild(keyList);keyList.ItemSelected+=i=>frame.Value=keyList.GetItemMetadata((int)i).AsDouble();
        Button(timeline,"预览当前帧",()=>Guard(()=>{if(PreviewActor is not {} actor)return;playing.ButtonPressed=false;actor.Services.Motions.Seek(actor,frame.Value);preview!.Graphics.Build();canvas.QueueRedraw();}));
        var translate=Row(timeline);x=Number(translate,"X");y=Number(translate,"Y");angle=Number(translate,"角度",-36000,36000);
        var scale=Row(timeline);sx=Number(scale,"缩放X",-100,100,1);sy=Number(scale,"缩放Y",-100,100,1);picture=Number(scale,"贴图帧",0,10000);picture.Step=1;
        var keys=Row(timeline);Button(keys,"写入关键帧",WriteKey);Button(keys,"删除关键帧",()=>Change(_=>Keys().RemoveAll(k=>k.Frame==frame.Value)));
        timeline.AddChild(new Label{Text="位移与缩放线性插值；旋转取最短角度；贴图帧阶梯切换。\n根偏移只改变姿态，不移动实体。"});
        var phases=new TextEdit{CustomMinimumSize=new(0,120),SizeFlagsVertical=Control.SizeFlags.ExpandFill};timeline.AddChild(new Label{Text="阶段区间 JSON（Name / Start / End）"});timeline.AddChild(phases);Button(timeline,"读取阶段",()=>phases.Text=JsonSerializer.Serialize(Clip.Phases,NativeContentStore.Json));Button(timeline,"写入阶段",()=>Change(_=>Clip.Phases=JsonSerializer.Deserialize<List<MotionPhase>>(phases.Text,NativeContentStore.Json)??new()));
        var states=Page(tabs,"状态图");var stateRow=Row(states);stateChoice=new();stateRow.AddChild(stateChoice);stateChoice.ItemSelected+=_=>ReadState();stateId=new LineEdit{PlaceholderText="新状态标识",CustomMinimumSize=new(140,0)};stateRow.AddChild(stateId);Button(stateRow,"新增节点",()=>Change(g=>g.States.Add(new(){Id=stateId.Text,Clip=Clip.Id})));Button(stateRow,"设为入口",()=>Change(g=>g.Entry=g.States[(int)stateChoice.Selected].Id));
        states.AddChild(new Label{Text="节点、运动操作与转换条件；优先级从高到低，每 Tick 最多一次转换。"});
        graphView=new GraphEdit{CustomMinimumSize=new(0,240),SizeFlagsVertical=Control.SizeFlags.ExpandFill};states.AddChild(graphView);
        graphView.ConnectionRequest+=(from,fromPort,to,toPort)=>Change(g=>g.States.Single(s=>NodeName(s.Id)==from.ToString()).Transitions.Add(new(){Target=g.States.Single(s=>NodeName(s.Id)==to.ToString()).Id}));
        graphView.DisconnectionRequest+=(from,fromPort,to,toPort)=>Change(g=>g.States.Single(s=>NodeName(s.Id)==from.ToString()).Transitions.RemoveAll(t=>NodeName(t.Target)==to.ToString()));
        stateText=new TextEdit{CustomMinimumSize=new(0,180),SizeFlagsVertical=Control.SizeFlags.ExpandFill};states.AddChild(stateText);Button(states,"写入节点与转换",()=>Change(g=>g.States[(int)stateChoice.Selected]=JsonSerializer.Deserialize<MotionState>(stateText.Text,NativeContentStore.Json)??throw new InvalidDataException("状态为空")));
        var events=Page(tabs,"事件配置");events.AddChild(new Label{Text="指定帧或接触区间；Damage / Interact / Spawn / Destroy / SdkEvent / MapEvent"});eventsText=new TextEdit{SizeFlagsVertical=Control.SizeFlags.ExpandFill};events.AddChild(eventsText);Button(events,"读取事件",()=>eventsText.Text=JsonSerializer.Serialize(Clip.Events,NativeContentStore.Json));Button(events,"新增接触事件",()=>Change(_=>Clip.Events.Add(new(){Contact=true,Start=0,End=Clip.Duration,Kind=MotionEventKind.SdkEvent,Value="motion.contact"})));Button(events,"写入事件",()=>Change(_=>Clip.Events=JsonSerializer.Deserialize<List<MotionEvent>>(eventsText.Text,NativeContentStore.Json)??new()));
        var parameters=Page(tabs,"图参数");parametersText=new TextEdit{SizeFlagsVertical=Control.SizeFlags.ExpandFill};parameters.AddChild(parametersText);Button(parameters,"写入参数",()=>Change(g=>g.Parameters=JsonSerializer.Deserialize<Dictionary<string,double>>(parametersText.Text,NativeContentStore.Json)??new()));
        status=new Label{AutowrapMode=TextServer.AutowrapMode.WordSmart};root.AddChild(status);Refresh();InitializePreview();
    }
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
    private void ReadState(){if(stateChoice.Selected>=0)stateText.Text=JsonSerializer.Serialize(document.Graph.States[(int)stateChoice.Selected],NativeContentStore.Json);}
    private void RefreshTimeline(){timelineSlider.MaxValue=Clip.Duration;duration.Value=Clip.Duration;loop.ButtonPressed=Clip.Loop;eventsText.Text=JsonSerializer.Serialize(Clip.Events,NativeContentStore.Json);ReadKey();}
    private void Refresh()
    {
        refreshing=true;graphId.Text=document.Graph.Id;int skeletonIndex=Array.IndexOf(new[]{"Nana","Ryoko","Yuki"},document.Graph.Skeleton);if(skeletonIndex>=0)skeleton.Select(skeletonIndex);else skeleton.Select(0);
        int selected=(int)clipChoice.Selected;clipChoice.Clear();foreach(var c in document.Graph.Clips)clipChoice.AddItem(c.Id);clipChoice.Select(Math.Clamp(selected,0,clipChoice.ItemCount-1));
        selected=(int)stateChoice.Selected;stateChoice.Clear();foreach(var s in document.Graph.States)stateChoice.AddItem(s.Id);stateChoice.Select(Math.Clamp(selected,0,stateChoice.ItemCount-1));
        parametersText.Text=JsonSerializer.Serialize(document.Graph.Parameters,NativeContentStore.Json);refreshing=false;RefreshTimeline();ReadState();RefreshGraph();SyncOverlay();
    }
    private static string NodeName(string id)=>"state_"+Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(id));

    private void PopulateSkeleton()
    {
        if(PreviewActor is not {} active)return;
        var nodes=active.Services.Motions.Skeleton(active);
        var descriptor=MotionSkeletonDescriptor.FromActor(active.World,active.Instance,document.Graph.Skeleton);
        bool missing=document.Graph.Clips.Any(clip=>descriptor.Nodes.Any(node=>clip.Tracks.All(track=>track.Bone!=node.Name)||clip.Tracks.First(track=>track.Bone==node.Name).Keys.Count==0));
        if(missing||document.Graph.IkChains is null)document.Edit(graph=>{MotionRigTemplates.EnsureReferencePose(graph,descriptor);if(graph.IkChains is null){if(MotionSkeleton.IsLegacy(graph.Skeleton))graph.IkChains=[];else MotionIkPresets.Ensure(graph);}});
        int selected=bone.Selected;bone.Clear();bone.AddItem("root");foreach(var node in nodes)bone.AddItem(node.Name);bone.Select(Math.Clamp(selected,bone.ItemCount>0?0:0,bone.ItemCount-1));status.Text="预览骨架已加载："+nodes.Count+" 个节点。";
    }

    private void BindSelectedIk()
    {
        Guard(()=>
        {
            var active=PreviewActor;if(active is null)throw new InvalidOperationException("请先加载预览骨架。");
            string end=bone.GetItemText((int)bone.Selected);if(end=="root")throw new InvalidOperationException("根节点不能作为 IK 末端。");
            var nodes=active.Services.Motions.Skeleton(active);if(!nodes.Any(node=>node.Name==end))throw new InvalidOperationException("所选节点不在预览骨架中。");var current=nodes.Single(node=>node.Name==end);if(!nodes.Any(node=>node.Instance==current.Parent))throw new InvalidOperationException("所选节点缺少父节点。");var mid=nodes.Single(node=>node.Instance==current.Parent);if(!nodes.Any(node=>node.Instance==mid.Parent))throw new InvalidOperationException("所选节点没有连续的两骨父链。");var root=nodes.Single(node=>node.Instance==mid.Parent);
            Change(g=>{g.IkChains ??= new();g.IkChains.RemoveAll(chain=>chain.End==end);g.IkChains.Add(new(){Root=root.Name,Mid=mid.Name,End=end});});status.Text="已绑定 IK："+root.Name+" → "+mid.Name+" → "+end;
        });
    }

    private void UnbindSelectedIk()=>Guard(()=>{string end=bone.GetItemText((int)bone.Selected);Change(g=>{if(g.IkChains is not null)g.IkChains.RemoveAll(chain=>chain.Root==end||chain.Mid==end||chain.End==end);});status.Text="已解除所选节点的 IK。";});

    private void SurfaceInput(InputEvent input)
    {
        if(input is InputEventKey{Pressed:true,Keycode:Key.Escape}&&dragging){dragging=false;draggedBone=null;status.Text="已取消拖动，文档未修改。";return;}
        if(input is InputEventMouseButton button&&button.ButtonIndex==MouseButton.Left)
        {
            if(button.Pressed){draggedBone=PickBone(button.Position);dragging=draggedBone is not null;if(dragging){(dragX,dragY)=CanvasPosition(button.Position);SelectBone(draggedBone!);}}
            else if(dragging&&draggedBone is {} name){dragging=false;var local=LocalTarget(name,dragX,dragY);var result=document.Drag(Clip.Id,name,frame.Value,local.X,local.Y);status.Text=result.Success?(result.Clamped?result.Reason:"节点已更新；一次拖动生成一个撤销记录。") : result.Reason;Refresh();}
            return;
        }
        if(input is InputEventMouseButton{ButtonIndex:MouseButton.Right,Pressed:true}&&dragging){dragging=false;draggedBone=null;status.Text="已取消拖动，文档未修改。";return;}
        if(dragging&&input is InputEventMouseMotion motion){(dragX,dragY)=CanvasPosition(motion.Position);x.Value=dragX;y.Value=dragY;}
    }

    private string? PickBone(Vector2 position)=>PickBoneAt(position);

    internal string? PickBoneAt(Vector2 position)
    {
        if(PreviewActor is not {} actor||surface.Size.X<=0||surface.Size.Y<=0)return null;
        var (X,Y)=CanvasPosition(position);string? best=null;double distance=MotionViewport.WorldPerPixel(surface.Size.X,surface.Size.Y,PreviewView)*12;
        foreach(var node in actor.Services.Motions.Skeleton(actor)){double d=Math.Sqrt((node.X-X)*(node.X-X)+(node.Y-Y)*(node.Y-Y));if(d>distance)continue;distance=d;best=node.Name;}
        return Math.Sqrt((actor.X-X)*(actor.X-X)+(actor.Y-Y)*(actor.Y-Y))<distance?"root":best;
    }

    private MotionView PreviewView=>PreviewActor is {} actor?new((int)actor.X,(int)actor.Y-40,320,240,640,480):new(0,0,320,240,640,480);

    private void SyncOverlay()
    {
        if(overlay is null||PreviewActor is not {} actor)return;
        overlay.Sync(actor.Services.Motions.Skeleton(actor),bone.Selected>=0?bone.GetItemText((int)bone.Selected):null,document.Graph.IkChains,PreviewView,new((float)actor.X,(float)actor.Y));
    }

    private (double X,double Y) CanvasPosition(Vector2 position)=>MotionViewport.ToWorld(position.X,position.Y,surface.Size.X,surface.Size.Y,PreviewView);

    private (double X,double Y) LocalTarget(string node,double worldX,double worldY)
    {
        if(PreviewActor is not {} actor)return (worldX,worldY);
        var player=actor.Instance;var nodes=actor.Services.Motions.Skeleton(actor);string parentName=node;
        if(node=="root")return MotionRigCoordinates.ToLocal(worldX,worldY,actor.X,actor.Y,0,player.Scope.ReadOr("Direction",1).Number);
        if(document.Graph.IkChains?.FirstOrDefault(chain=>chain.Enabled&&chain.End==node) is { } chain)parentName=chain.Root;
        if(!nodes.Any(item=>item.Name==parentName))return (worldX,worldY);var selected=nodes.Single(item=>item.Name==parentName);int parentId=selected.Parent;
        if(parentId==actor.Id)return MotionRigCoordinates.ToLocal(worldX,worldY,actor.X,actor.Y,actor.Read("image_angle"),player.Scope.ReadOr("Direction",1).Number);
        if(!nodes.Any(item=>item.Instance==parentId))return (worldX,worldY);var parent=nodes.Single(item=>item.Instance==parentId);
        return MotionRigCoordinates.ToLocal(worldX,worldY,parent.X,parent.Y,parent.Angle,player.Scope.ReadOr("Direction",1).Number);
    }

    private void SelectBone(string name)
    {
        for(int i=0;i<bone.ItemCount;i++)if(bone.GetItemText(i)==name){bone.Select(i);return;}
    }

    private ActorContext? PreviewActor => preview is null ? null : targetPreview is { Destroyed: false } target ? new(preview.World,target) : preview.Player is { } player ? new(preview.World,player) : null;

    private void SelectLegacyTarget()
    {
        Guard(() =>
        {
            if(preview is null||entities.Selected<0)throw new InvalidOperationException("请先初始化预览并选择实体资源。");
            int objectId=entities.GetItemMetadata((int)entities.Selected).AsInt32();string resource=world.Objects[objectId].GetProperty("name").GetString()??objectId.ToString();
            document.Edit(graph=>
            {
                graph.Skeleton="legacy:"+resource;graph.EntitySlots.Clear();graph.IkChains=[];
                foreach(var clip in graph.Clips)clip.Tracks.Clear();
            });
            Refresh();InitializePreview();
        });
    }

    private GmInstance CreateLegacyPreview(string resource)
    {
        if(preview is null)throw new InvalidOperationException("预览尚未初始化。");
        int objectId=world.Objects.FirstOrDefault(pair=>pair.Value.GetProperty("name").GetString()==resource).Key;
        if(objectId==0&&!world.Objects.ContainsKey(objectId))throw new InvalidDataException("实体资源不存在："+resource);
        double x=preview.Player?.Scope.ReadOr("x").Number??96,y=preview.Player?.Scope.ReadOr("y").Number??0;
        var root=preview.World.Create(objectId,x,y,run:false);var child=preview.World.Create(objectId,x+24,y,run:false);child.Scope.Write("parent",root.Id);child.Scope.Write("PartPosDir",0);child.Scope.Write("PartPosDis",24);root.Scope.Write("Process",child.Object,0);return root;
    }
    private void RefreshGraph()
    {
        graphView.ClearConnections();foreach(var node in graphView.GetChildren().OfType<GraphNode>()){graphView.RemoveChild(node);node.QueueFree();}
        for(int i=0;i<document.Graph.States.Count;i++)
        {
            var state=document.Graph.States[i];var node=new GraphNode{Name=NodeName(state.Id),Title=(document.Graph.Entry==state.Id?"▶ ":"")+state.Id,PositionOffset=new(30+(i%3)*220,30+(i/3)*150),CustomMinimumSize=new(180,80)};
            node.AddChild(new Label{Text=state.Clip});node.SetSlot(0,true,0,Colors.SkyBlue,true,0,Colors.Orange);graphView.AddChild(node);
            node.GuiInput+=input=>{if(input is InputEventMouseButton{Pressed:true,ButtonIndex:MouseButton.Left}){stateChoice.Select(document.Graph.States.FindIndex(s=>s.Id==state.Id));ReadState();}};
        }
        foreach(var state in document.Graph.States)foreach(var transition in state.Transitions)graphView.ConnectNode(NodeName(state.Id),0,NodeName(transition.Target),0);
    }
    private void Guard(Action action){try{action();}catch(Exception e){status.Text=e.GetBaseException().Message;}}
    private void Change(Action<MotionGraph> action){if(refreshing)return;Guard(()=>{document.Edit(action);Refresh();status.Text="已编辑；重载预览以应用。";});}
    private void ImportOriginal()=>Guard(()=>{string name=original.GetItemText((int)original.Selected);document=new(MotionImport.FromAst(world.Code["external/Chara/Motion/"+name+".txt"],name.Replace('/','.'),document.Graph.Skeleton));path=null;Refresh();status.Text="已导入："+name;});
    private void Save(){if(path is null){ChooseFile(true);return;}Guard(()=>{document.Save(path);status.Text="已保存："+path;});}
    private void ChooseFile(bool save)
    {
        var dialog=new FileDialog{FileMode=save?FileDialog.FileModeEnum.SaveFile:FileDialog.FileModeEnum.OpenFile,Access=FileDialog.AccessEnum.Filesystem,UseNativeDialog=true};dialog.AddFilter("*.motion.json","Motion graph");AddChild(dialog);
        dialog.CurrentDir=Path.Combine(store.Root,"Actions");Directory.CreateDirectory(dialog.CurrentDir);dialog.CurrentFile=document.Graph.Id+".motion.json";
        dialog.FileSelected+=file=>Guard(()=>{if(save){document.Save(file);}else{document=new(MotionGraph.Load(file));Refresh();}path=file;status.Text=file;dialog.QueueFree();});dialog.Canceled+=()=>dialog.QueueFree();dialog.PopupCenteredRatio(.8f);
    }
    private async void InitializePreview()
    {
        if(busy)return;busy=true;playing.ButtonPressed=false;
        try
        {
            preview?.Dispose();targetPreview=null;preview=new(viewport,world,registry,new(store),packages);await preview.InitializeAsync();
            if(closing)return;
            string preset=document.Graph.Skeleton is "Nana" or "Ryoko" or "Yuki"?document.Graph.Skeleton:"Nana";
            var definition=new CharacterDefinition{BasePreset=preset,Appearance="Character-"+preset};await preview.ApplyAsync(definition);
            ActorContext actor;
            if(MotionSkeleton.IsLegacy(document.Graph.Skeleton)){string resource=document.Graph.Skeleton[7..];targetPreview=CreateLegacyPreview(resource);actor=new(preview.World,targetPreview);}
            else {targetPreview=null;actor=new(preview.World,preview.Player!);}
            PopulateSkeleton();preview.Bridge.Services.Motions.Play(actor,document.Graph);
            canvas.Paint=c=>preview.Graphics.Render(c);preview.FrameOn((int)actor.X,(int)actor.Y-40,320,240,640,480);preview.Graphics.Build();canvas.QueueRedraw();SyncOverlay();status.Text="预览已加载。";
        }
        catch(Exception e){status.Text=e.GetBaseException().Message;}
        finally{busy=false;}
    }
    public override void _Process(double delta)
    {
        if(!playing.ButtonPressed||busy||preview is null)return;elapsed+=Math.Min(delta,.1);
        if(elapsed>=FrameContext.FixedDelta){elapsed-=FrameContext.FixedDelta;Step();}
    }
    private async void Step()
    {
        var activePreview=preview;if(busy||activePreview is null||PreviewActor is not {} active)return;busy=true;
        try
        {
            foreach(var key in new[]{Key.Left,Key.Right,Key.Space})activePreview.Input.Key(activePreview.World,(int)key switch{(int)Key.Left=>37,(int)Key.Right=>39,_=>32},Input.IsKeyPressed(key));
            await activePreview.AdvanceAsync(new(Input.IsKeyPressed(Key.Left),Input.IsKeyPressed(Key.Right),Input.IsKeyPressed(Key.Space),false));
            var state=active.Services.Motions.State(active);playState.Text=$"{state?.State}  frame={state?.Frame:F1}  x={active.X:F2} y={active.Y:F2} suspended={state?.Suspended}";
            activePreview.FrameOn((int)active.X,(int)active.Y-40,320,240,640,480);canvas.QueueRedraw();SyncOverlay();
        }
        catch(Exception e){playing.ButtonPressed=false;status.Text=e.GetBaseException().Message;}
        finally{busy=false;}
    }
    private void ApplyToGame()=>Guard(()=>
    {
        var instance=world.Instances.FirstOrDefault(i=>!i.Destroyed&&world.IsObject(i.Object,world.Constants["objPlayerDoll"].Int));
        if(MotionSkeleton.IsLegacy(document.Graph.Skeleton))
        {
            string resource=document.Graph.Skeleton[7..];int objectId=world.Objects.FirstOrDefault(pair=>pair.Value.GetProperty("name").GetString()==resource).Key;instance=world.Instances.FirstOrDefault(i=>!i.Destroyed&&i.Object==objectId)??throw new InvalidOperationException("没有目标实体实例："+resource);
        }
        if(instance is null)throw new InvalidOperationException("没有游戏角色");
        var actor=new ActorContext(world,instance);actor.Services.Motions.Play(actor,document.Graph);status.Text=MotionSkeleton.IsLegacy(document.Graph.Skeleton)?"已应用到目标实体；关闭编辑器后继续运行。":"已应用到游戏角色；关闭编辑器后继续运行。";
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
