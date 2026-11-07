using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private enum DragMode { Root, Fk, Ik, Ball }
    private const int PortWidth=640,PortHeight=480;
    private double viewCenterX,viewCenterY,viewWidth=320;
    private MotionView PreviewView=>new(viewCenterX-viewWidth/2,viewCenterY-viewWidth*.375,viewWidth,viewWidth*.75,PortWidth,PortHeight);
    private bool panning;
    private Vector2 panOrigin;
    private double panCenterX,panCenterY;
    private DragMode dragMode;
    private Vector2 dragOrigin,dragSurface;
    private double dragStartAngle,dragStartPointer;
    private bool dragBend,syncingBone;
    private string? highlightedBone;

    private void SurfaceInput(InputEvent input)
    {
        if(input is InputEventKey{Pressed:true,Keycode:Key.Escape}){CancelDrag();return;}
        if(input is InputEventMouseButton button)
        {
            if(button.ButtonIndex==MouseButton.Left)
            {
                if(button.Pressed)
                {
                    if(overlay?.RingHit(button.Position) is {} hit)
                    {
                        if(hit=="ball"&&highlightedBone is {} ball){draggedBone=ball;dragging=true;dragMode=DragMode.Ball;dragOrigin=dragSurface=button.Position;(dragX,dragY)=CanvasPosition(button.Position);return;}
                        if(hit.StartsWith("arrow:")&&int.TryParse(hit[6..],out int arrow)&&highlightedBone is {} stepped){StepBone(stepped,arrow,button.ShiftPressed?10:1);return;}
                    }
                    draggedBone=PickBoneAt(button.Position);
                    if(draggedBone is null){ClearBoneSelection();BeginPan(button.Position);return;}
                    SelectBone(draggedBone);
                    dragging=true;(dragX,dragY)=CanvasPosition(button.Position);
                    dragOrigin=dragSurface=button.Position;
                    BeginDrag(button);
                    return;
                }
                if(dragging&&draggedBone is {} name)
                {
                    dragging=false;
                    if(dragSurface.DistanceTo(dragOrigin)<3){overlay?.SetRingAngle(null);status.Text="已选中骨骼；未拖动，未写入关键帧。";return;}
                    CommitDrag(name);
                }
                else if(panning)panning=false;
                return;
            }
            if(button.ButtonIndex==MouseButton.Middle){if(button.Pressed)BeginPan(button.Position);else panning=false;return;}
            if(button.ButtonIndex==MouseButton.WheelUp&&button.Pressed){ZoomView(button.Position,1/1.12);return;}
            if(button.ButtonIndex==MouseButton.WheelDown&&button.Pressed){ZoomView(button.Position,1.12);return;}
            if(button.ButtonIndex==MouseButton.Right&&button.Pressed&&dragging){CancelDrag();return;}
            return;
        }
        if(input is InputEventMouseMotion motion)
        {
            if(dragging)
            {
                (dragX,dragY)=CanvasPosition(motion.Position);dragSurface=motion.Position;
                if(dragMode==DragMode.Ball){var local=LocalTarget(draggedBone!,dragX,dragY,false);overlay?.SetRingAngle(Math.Atan2(-local.Y,local.X)*180/Math.PI);}
            }
            else if(panning)UpdatePan(motion.Position);
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event){if(@event is InputEventKey{Pressed:true,Keycode:Key.Escape})CancelDrag();}

    private void BeginDrag(InputEventMouseButton button)
    {
        string name=draggedBone!;
        var local=LocalTarget(name,dragX,dragY,false);
        dragStartAngle=LocalPose(name).Angle;
        dragStartPointer=Math.Atan2(-local.Y,local.X)*180/Math.PI;
        if(name=="root")dragMode=DragMode.Root;
        else if(button.AltPressed)dragMode=DragMode.Fk;
        else if(IsIkEnd(name)){dragMode=DragMode.Ik;dragBend=MotionPoseEditor.Bend(document.Graph,Clip.Id,name,frame.Value)??true;}
        else dragMode=DragMode.Fk;
    }

    private void CommitDrag(string name)
    {
        try
        {
            if(dragMode==DragMode.Ball)
            {
                overlay?.SetRingAngle(null);
                var local=LocalTarget(name,dragX,dragY,false);
                double radians=Math.Atan2(-local.Y,local.X);
                var result=document.Drag(Clip.Id,name,frame.Value,Math.Cos(radians),-Math.Sin(radians),null,true);
                status.Text=result.Success?(result.Clamped?result.Reason:"已写入旋转；一次拖动生成一个撤销记录。"):result.Reason;
            }
            else
            {
                var (targetX,targetY)=DragTarget(name);
                var result=document.Drag(Clip.Id,name,frame.Value,targetX,targetY,dragMode==DragMode.Ik?dragBend:null,dragMode==DragMode.Fk);
                status.Text=result.Success?(result.Clamped?result.Reason:"节点已更新；一次拖动生成一个撤销记录。"):result.Reason;
            }
            Refresh();ApplyDocumentPose();
        }
        catch(InvalidDataException error){status.Text=error.Message;Refresh();ApplyDocumentPose();}
    }

    private (double X,double Y) DragTarget(string name)
    {
        if(dragMode==DragMode.Fk)
        {
            var local=LocalTarget(name,dragX,dragY,false);
            double pointer=Math.Atan2(-local.Y,local.X)*180/Math.PI;
            double angle=dragStartAngle+pointer-dragStartPointer,radians=angle*Math.PI/180;
            return (Math.Cos(radians),-Math.Sin(radians));
        }
        return LocalTarget(name,dragX,dragY,dragMode==DragMode.Ik);
    }

    internal void FitView()
    {
        if(PreviewActor is not {} actor)return;
        double left=actor.X,top=actor.Y,right=actor.X,bottom=actor.Y;
        foreach(var node in actor.Services.Motions.Skeleton(actor))
        {
            var part=actor.World.Instances.FirstOrDefault(item=>item.Id==node.Instance);
            if(part is null||part.Destroyed||!part.Active)continue;
            var bounds=actor.World.Bounds(part);
            if(bounds.Empty)continue;
            left=Math.Min(left,bounds.Left);top=Math.Min(top,bounds.Top);right=Math.Max(right,bounds.Right);bottom=Math.Max(bottom,bounds.Bottom);
        }
        double width=Math.Max(32,(right-left)*1.4),height=Math.Max(24,(bottom-top)*1.4);
        viewWidth=Math.Clamp(Math.Max(width,height*4/3),32,4096);
        viewCenterX=(left+right)/2;viewCenterY=(top+bottom)/2;
        ApplyView();
    }
    private void ApplyView()
    {
        if(preview is null)return;
        preview.FrameOn((int)viewCenterX,(int)viewCenterY,Math.Max(1,(int)viewWidth),Math.Max(1,(int)(viewWidth*.75)),PortWidth,PortHeight);
        canvas.QueueRedraw();SyncOverlay();
    }
    private void ZoomView(Vector2 position,double factor)
    {
        if(surface.Size.X<=0||surface.Size.Y<=0)return;
        var (worldX,worldY)=CanvasPosition(position);
        double before=viewWidth,next=Math.Clamp(before*factor,32,4096);
        if(next==before)return;
        double u=(worldX-(viewCenterX-before/2))/before,v=(worldY-(viewCenterY-before*.375))/(before*.75);
        viewWidth=next;viewCenterX=worldX-(u-.5)*next;viewCenterY=worldY-(v-.5)*next*.75;
        ApplyView();
    }
    private void BeginPan(Vector2 position){panning=true;panOrigin=position;panCenterX=viewCenterX;panCenterY=viewCenterY;}
    private void UpdatePan(Vector2 position)
    {
        double worldPerPixel=MotionViewport.WorldPerPixel(surface.Size.X,surface.Size.Y,PreviewView);
        viewCenterX=panCenterX-(position.X-panOrigin.X)*worldPerPixel;
        viewCenterY=panCenterY-(position.Y-panOrigin.Y)*worldPerPixel;
        ApplyView();
    }

    private void CancelDrag()
    {
        if(!dragging)return;
        dragging=false;draggedBone=null;overlay?.SetRingAngle(null);status.Text="已取消拖动，文档未修改。";
    }

    internal string? PickBoneAt(Vector2 position)
    {
        if(PreviewActor is not {} actor||surface.Size.X<=0||surface.Size.Y<=0)return null;
        var (X,Y)=CanvasPosition(position);var instances=actor.World.Instances;
        string? best=null;double bestDepth=double.MaxValue;int bestOrder=-1;
        foreach(var node in actor.Services.Motions.Skeleton(actor))
        {
            var part=instances.FirstOrDefault(item=>item.Id==node.Instance);
            if(part is null||part.Destroyed||!part.Active||!part.Scope.ReadOr("visible").Truth)continue;
            var bounds=actor.World.Bounds(part);
            if(bounds.Empty||X<bounds.Left||X>bounds.Right||Y<bounds.Top||Y>bounds.Bottom)continue;
            double depth=part.Scope.ReadOr("depth").Number;int order=instances.IndexOf(part);
            if(depth<bestDepth||(depth==bestDepth&&order>bestOrder)){best=node.Name;bestDepth=depth;bestOrder=order;}
        }
        return best;
    }

    private (double X,double Y) CanvasPosition(Vector2 position)=>MotionViewport.ToWorld(position.X,position.Y,surface.Size.X,surface.Size.Y,PreviewView);

    private void ApplyDocumentPose()
    {
        if(preview is null||PreviewActor is not {} actor)return;
        try
        {
            preview.Bridge.Services.Motions.Preview(actor,document.Graph,Clip.Id,frame.Value);
            if(playing is not null&&!playing.ButtonPressed)sessionStale=true;
            preview.Graphics.Build();canvas.QueueRedraw();SyncOverlay();
        }
        catch(InvalidDataException error){status.Text=error.Message;}
    }

    private void SyncOverlay()
    {
        if(overlay is null||PreviewActor is not {} actor)return;
        overlay.Sync(actor.Services.Motions.Skeleton(actor),highlightedBone,IkChains,PreviewView,new((float)actor.X,(float)actor.Y));
        SyncIkSelection();
        UpdatePlaybackOverlay();
    }

    private void SyncIkSelection()
    {
        if(ikList is null)return;
        var chains=IkChains;int row=-1;
        if(highlightedBone is {} bone)
            for(int i=0;i<chains.Count;i++)
                if(chains[i].Enabled&&(chains[i].Root==bone||chains[i].Mid==bone||chains[i].End==bone)){row=i;break;}
        if(row>=0&&row<ikList.ItemCount)ikList.Select(row);else ikList.DeselectAll();
    }

    private void StepBone(string name,int index,int step)
    {
        if(PreviewActor is not {} actor)return;
        var node=actor.Services.Motions.Skeleton(actor).FirstOrDefault(item=>item.Name==name);
        if(node is null)return;
        try
        {
            var local=LocalTarget(name,node.X,node.Y,false);
            double radians=(90-index*45)*Math.PI/180;
            var reference=LocalTarget(name,node.X+Math.Cos(radians),node.Y-Math.Sin(radians),false);
            double dx=reference.X-local.X,dy=reference.Y-local.Y,length=Math.Sqrt(dx*dx+dy*dy);
            if(length<1e-9)return;
            dx/=length;dy/=length;var current=LocalPose(name);
            document.Translate(Clip.Id,name,frame.Value,current.X+dx*step,current.Y+dy*step);
            status.Text="已步进局部 X/Y；一次点击生成一个撤销记录。";
            Refresh();ApplyDocumentPose();
        }
        catch(InvalidDataException error){status.Text=error.Message;}
    }

    private MotionPose LocalPose(string name)=>name is "root" or "__root__"?Clip.RootPose(frame.Value):Clip.Sample(frame.Value).GetValueOrDefault(name);

    private (double X,double Y) LocalTarget(string node,double worldX,double worldY,bool chain)
    {
        if(PreviewActor is not {} actor)return (worldX,worldY);
        var player=actor.Instance;var nodes=actor.Services.Motions.Skeleton(actor);string parentName=node;
        if(node=="root")return MotionRigCoordinates.ToLocal(worldX,worldY,actor.X,actor.Y,0,player.Scope.ReadOr("Direction",1).Number);
        if(chain&&IkChains.FirstOrDefault(item=>item.Enabled&&item.End==node) is { } link)parentName=link.Root;
        if(!nodes.Any(item=>item.Name==parentName))return (worldX,worldY);var selected=nodes.Single(item=>item.Name==parentName);int parentId=selected.Parent;
        if(parentId==actor.Id)return MotionRigCoordinates.ToLocal(worldX,worldY,actor.X,actor.Y,actor.Read("image_angle"),player.Scope.ReadOr("Direction",1).Number);
        if(!nodes.Any(item=>item.Instance==parentId))return (worldX,worldY);var parent=nodes.Single(item=>item.Instance==parentId);
        return MotionRigCoordinates.ToLocal(worldX,worldY,parent.X,parent.Y,parent.Angle,player.Scope.ReadOr("Direction",1).Number);
    }
}
