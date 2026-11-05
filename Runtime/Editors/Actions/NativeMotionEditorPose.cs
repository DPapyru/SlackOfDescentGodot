using Godot;

namespace SlackOfDescent;

public partial class NativeMotionEditor
{
    private MotionView PreviewView=>PreviewActor is {} actor?new((int)actor.X,(int)actor.Y-40,320,240,640,480):new(0,0,320,240,640,480);

    private void SurfaceInput(InputEvent input)
    {
        if(input is InputEventKey{Pressed:true,Keycode:Key.Escape}&&dragging){dragging=false;draggedBone=null;status.Text="已取消拖动，文档未修改。";return;}
        if(input is InputEventMouseButton button&&button.ButtonIndex==MouseButton.Left)
        {
            if(button.Pressed){draggedBone=PickBone(button.Position);dragging=draggedBone is not null;if(dragging){(dragX,dragY)=CanvasPosition(button.Position);SelectBone(draggedBone!);}}
            else if(dragging&&draggedBone is {} name){dragging=false;var local=LocalTarget(name,dragX,dragY);var result=document.Drag(Clip.Id,name,frame.Value,local.X,local.Y);status.Text=result.Success?(result.Clamped?result.Reason:"节点已更新；一次拖动生成一个撤销记录。"):result.Reason;Refresh();ApplyDocumentPose();}
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

    private (double X,double Y) CanvasPosition(Vector2 position)=>MotionViewport.ToWorld(position.X,position.Y,surface.Size.X,surface.Size.Y,PreviewView);

    private void ApplyDocumentPose()
    {
        if(preview is null||PreviewActor is not {} actor)return;
        try
        {
            preview.Bridge.Services.Motions.Preview(actor,document.Graph,Clip.Id,frame.Value);
            preview.Graphics.Build();canvas.QueueRedraw();SyncOverlay();
        }
        catch(InvalidDataException error){status.Text=error.Message;}
    }

    private void SyncOverlay()
    {
        if(overlay is null||PreviewActor is not {} actor)return;
        overlay.Sync(actor.Services.Motions.Skeleton(actor),bone.Selected>=0?bone.GetItemText((int)bone.Selected):null,document.Graph.IkChains,PreviewView,new((float)actor.X,(float)actor.Y));
    }

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
}
