using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    private enum DragMode { Root, Fk, Ik, Ball, Translate }
    private const int PortWidth=640,PortHeight=480;
    private double viewCenterX,viewCenterY,viewWidth=320;
    private MotionView PreviewView
    {
        get
        {
            var engine=preview?.World.Engine;
            if(engine is not null&&engine.ReadOr("view_enabled").Truth)
                return new(engine.Read("view_xview",[0]).Number,engine.Read("view_yview",[0]).Number,engine.Read("view_wview",[0]).Number,engine.Read("view_hview",[0]).Number,PortWidth,PortHeight);
            return new(viewCenterX-viewWidth/2,viewCenterY-viewWidth*.375,viewWidth,viewWidth*.75,PortWidth,PortHeight);
        }
    }
    private bool panning;
    private Vector2 panOrigin;
    private double panCenterX,panCenterY;
    private DragMode dragMode;
    private Vector2 dragOrigin,dragSurface;
    private double dragStartAngle,dragStartPointer;
    private (double X,double Y) dragLocal;
    private MotionPose dragStartPose;
    private bool dragBend,syncingBone;
    private string? highlightedBone;

    private void SurfaceInput(InputEvent input)
    {
        if(input is InputEventKey{Pressed:true,Keycode:Key.Escape}){CancelDrag();return;}
        if(input is InputEventMouseButton button){SurfaceButton(button);return;}
        if(input is InputEventMouseMotion motion)SurfaceMotion(motion);
    }

    private void SurfaceButton(InputEventMouseButton button)
    {
        if(button.Pressed)TakeCanvasFocus();
        if(button.ButtonIndex==MouseButton.Left)
        {
            if(button.Pressed){SurfacePress(button);return;}
            if(dragging&&draggedBone is {} name)
            {
                dragging=false;
                if(dragSurface.DistanceTo(dragOrigin)<3)
                {
                    overlay.SetRingAngle(null);ApplyDocumentPose();
                    status.Text=dragMode switch{DragMode.Translate=>"已选中骨骼；未拖动，未写入局部 X/Y。",DragMode.Ball=>"已选中骨骼；未拖动，未写入旋转。",_=>"已选中骨骼；未拖动，未写入关键帧。"};
                    return;
                }
                CommitDrag(name);
            }
            else if(panning)panning=false;
            return;
        }
        if(button.ButtonIndex==MouseButton.Middle){if(button.Pressed)BeginPan(button.Position);else panning=false;return;}
        if(button.ButtonIndex==MouseButton.WheelUp&&button.Pressed){ZoomView(button.Position,1/1.12);return;}
        if(button.ButtonIndex==MouseButton.WheelDown&&button.Pressed){ZoomView(button.Position,1.12);return;}
        if(button.ButtonIndex==MouseButton.Right&&button.Pressed&&dragging)CancelDrag();
    }

    private void SurfacePress(InputEventMouseButton button)
    {
        if(overlay.RingHit(button.Position)=="ball"&&highlightedBone is {} ball){draggedBone=ball;dragging=true;dragMode=DragMode.Ball;dragOrigin=dragSurface=button.Position;(dragX,dragY)=CanvasPosition(button.Position);return;}
        draggedBone=PickBoneAt(button.Position);
        if(draggedBone is null){ClearBoneSelection();BeginPan(button.Position);return;}
        SelectBone(draggedBone);
        dragging=true;(dragX,dragY)=CanvasPosition(button.Position);
        dragOrigin=dragSurface=button.Position;
        BeginDrag(button);
    }

    private void SurfaceMotion(InputEventMouseMotion motion)
    {
        if(!dragging){if(panning)UpdatePan(motion.Position);return;}
        (dragX,dragY)=CanvasPosition(motion.Position);dragSurface=motion.Position;
        if(dragMode==DragMode.Ball){var local=LocalTarget(draggedBone!,dragX,dragY,false);overlay.SetRingAngle(Math.Atan2(-local.Y,local.X)*180/Math.PI);}
        PreviewDrag(draggedBone!);
    }

    private void PreviewDrag(string name)
    {
        var graph=document.Preview(copy=>ApplyDrag(copy,name));
        if(graph is not null)ApplyPose(graph);
    }

    private MotionIkResult ApplyDrag(MotionGraph graph,string name)
    {
        if(dragMode==DragMode.Ball)
        {
            var local=LocalTarget(name,dragX,dragY,false);
            double angle=Math.Atan2(-local.Y,local.X)*180/Math.PI;
            MotionPoseEditor.Rotate(graph,Clip.Id,name,frame.Value,angle);
            return new(true,false,angle,angle,angle,0,"");
        }
        if(dragMode==DragMode.Translate)
        {
            var (localX,localY)=TranslateTarget(name);
            MotionPoseEditor.Translate(graph,Clip.Id,name,frame.Value,localX,localY);
            return new(true,false,0,0,0,0,"");
        }
        var (targetX,targetY)=DragTarget(name);
        return MotionPoseEditor.Drag(graph,Clip.Id,name,frame.Value,targetX,targetY,dragMode==DragMode.Ik?dragBend:null,dragMode==DragMode.Fk,dragMode==DragMode.Ik?IkFrame(name):null);
    }

    private (double X,double Y) TranslateTarget(string name)
    {
        var local=LocalTarget(name,dragX,dragY,false);
        return (dragStartPose.X+local.X-dragLocal.X,dragStartPose.Y+local.Y-dragLocal.Y);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if(@event is not InputEventKey{Pressed:true,Echo:false} key)return;
        if(!HandleEditorKey(key))return;
        GetViewport().SetInputAsHandled();
    }

    private void BeginDrag(InputEventMouseButton button)
    {
        string name=draggedBone!;
        var local=LocalTarget(name,dragX,dragY,false);
        dragStartAngle=LocalPose(name).Angle;
        dragStartPointer=Math.Atan2(-local.Y,local.X)*180/Math.PI;
        if(name=="root")dragMode=DragMode.Root;
        else if(button.AltPressed)dragMode=DragMode.Fk;
        else if(IsIkEnd(name)){dragMode=DragMode.Ik;dragBend=MotionPoseEditor.Bend(document.Graph,Clip.Id,name,frame.Value,IkFrame(name))??true;}
        else {dragMode=DragMode.Translate;dragLocal=local;dragStartPose=LocalPose(name);}
    }

    private void CommitDrag(string name)
    {
        try
        {
            MotionIkResult? outcome=null;
            if(dragMode==DragMode.Translate)
            {
                var (targetX,targetY)=TranslateTarget(name);
                if(Math.Abs(targetX-dragStartPose.X)<1e-9&&Math.Abs(targetY-dragStartPose.Y)<1e-9){overlay.SetRingAngle(null);status.Text="未移动，未写入局部 X/Y。";ApplyDocumentPose();return;}
            }
            document.Edit(graph=>{outcome=ApplyDrag(graph,name);if(!outcome.Value.Success)throw new InvalidDataException(outcome.Value.Reason);});
            overlay.SetRingAngle(null);
            status.Text=dragMode switch
            {
                DragMode.Ball=>"已写入旋转；一次拖动生成一个撤销记录。",
                DragMode.Translate=>"已写入局部 X/Y；一次拖动生成一个撤销记录。",
                _=>outcome is {Clamped:true}?outcome.Value.Reason:"节点已更新；一次拖动生成一个撤销记录。"
            };
            Refresh();ApplyDocumentPose();
        }
        catch(InvalidDataException error){overlay.SetRingAngle(null);status.Text=error.Message;Refresh();ApplyDocumentPose();}
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
        preview.FrameOn(viewCenterX,viewCenterY,Math.Max(1,viewWidth),Math.Max(1,viewWidth*.75),PortWidth,PortHeight);
        canvas.QueueRedraw();SyncOverlay();
    }
    private void ZoomView(Vector2 position,double factor)
    {
        if(surface.Size.X<=0||surface.Size.Y<=0)return;
        var (worldX,worldY)=CanvasPosition(position);
        var view=PreviewView;
        double before=view.ViewWidth,next=Math.Clamp(before*factor,32,4096);
        if(next==before)return;
        double u=(worldX-view.ViewX)/view.ViewWidth,v=(worldY-view.ViewY)/view.ViewHeight;
        viewWidth=next;viewCenterX=worldX-(u-.5)*next;viewCenterY=worldY-(v-.5)*next*.75;
        ApplyView();
    }
    private void BeginPan(Vector2 position){panning=true;panOrigin=position;var view=PreviewView;panCenterX=view.ViewX+view.ViewWidth/2;panCenterY=view.ViewY+view.ViewHeight/2;}
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
        dragging=false;draggedBone=null;overlay.SetRingAngle(null);status.Text="已取消拖动，文档未修改。";ApplyDocumentPose();
    }

    internal string? PickBoneAt(Vector2 position)=>overlay.PickBone(position);

    private (double X,double Y) CanvasPosition(Vector2 position)=>overlay.WorldPoint(position);

    private void ApplyDocumentPose()=>ApplyPose(document.Graph);

    private void ApplyPose(MotionGraph graph)
    {
        if(preview is null||PreviewActor is not {} actor)return;
        try
        {
            preview.Bridge.Services.Motions.Preview(actor,graph,Clip.Id,frame.Value);
            if(playing is not null&&!playing.ButtonPressed)sessionStale=true;
            preview.Graphics.Build();canvas.QueueRedraw();SyncOverlay();
        }
        catch(InvalidDataException error){status.Text=error.Message;}
    }

    private void SyncOverlay()
    {
        if(PreviewActor is not {} actor)return;
        var nodes=actor.Services.Motions.Skeleton(actor);
        overlay.Sync(nodes,highlightedBone,IkChains,PreviewView,new((float)actor.X,(float)actor.Y));
        UpdatePlaybackOverlay();
    }

    private MotionPose LocalPose(string name)=>name is "root" or "__root__"?Clip.RootPose(frame.Value):Clip.Sample(frame.Value).GetValueOrDefault(name);

    private MotionIkFrame? IkFrame(string bone)
    {
        if(PreviewActor is not {} actor)return null;
        var chain=IkChains.FirstOrDefault(item=>item.Enabled&&item.End==bone);
        if(chain is null)return null;
        var nodes=actor.Services.Motions.Skeleton(actor);
        if(nodes.FirstOrDefault(item=>item.Name==chain.Root) is not {} root)return null;
        if(nodes.Any(item=>item.Instance==root.Parent))return null;
        var rootPose=Clip.RootPose(frame.Value);
        return new(actor.Read("image_angle"),rootPose.X,rootPose.Y);
    }

    private (double X,double Y) LocalTarget(string node,double worldX,double worldY,bool chain)
    {
        if(PreviewActor is not {} actor)return (worldX,worldY);
        var player=actor.Instance;var nodes=actor.Services.Motions.Skeleton(actor);string parentName=node;
        if(node=="root")return MotionRigCoordinates.ToLocal(worldX,worldY,actor.X,actor.Y,0,player.Scope.ReadOr("Direction",1).Number);
        if(chain&&IkChains.FirstOrDefault(item=>item.Enabled&&item.End==node) is { } link)parentName=link.Root;
        if(!nodes.Any(item=>item.Name==parentName))return (worldX,worldY);var selected=nodes.Single(item=>item.Name==parentName);int parentId=selected.Parent;
        if(parentId==actor.Id)return MotionRigCoordinates.ToLocal(worldX,worldY,actor.X,actor.Y,0,player.Scope.ReadOr("Direction",1).Number);
        if(!nodes.Any(item=>item.Instance==parentId))return (worldX,worldY);var parent=nodes.Single(item=>item.Instance==parentId);
        return MotionRigCoordinates.ToLocal(worldX,worldY,parent.X,parent.Y,parent.Angle,player.Scope.ReadOr("Direction",1).Number);
    }
}
