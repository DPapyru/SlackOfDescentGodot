using System.Text.Json;

namespace SlackOfDescent;

public sealed class MotionGraphDocument(MotionGraph graph)
{
    public MotionGraph Graph { get; private set; } = graph;
    public bool Dirty { get; private set; }
    private readonly Stack<byte[]> undo=new();
    private readonly Stack<byte[]> redo=new();
    private string? coalesceKey;
    private long coalesceStamp;
    private byte[] Snapshot()=>JsonSerializer.SerializeToUtf8Bytes(Graph,NativeContentStore.Json);
    private void Restore(byte[] data)=>Graph=JsonSerializer.Deserialize<MotionGraph>(data,NativeContentStore.Json)!;
    public void Edit(Action<MotionGraph> change,string? coalesce=null)
    {
        var before=Snapshot();
        try{change(Graph);Graph.Validate();long now=Environment.TickCount64;if(coalesce is null||coalesce!=coalesceKey||now-coalesceStamp>=500)undo.Push(before);coalesceKey=coalesce;coalesceStamp=now;redo.Clear();Dirty=true;}catch{Restore(before);throw;}
    }
    public bool Undo(){coalesceKey=null;if(!undo.TryPop(out var data))return false;redo.Push(Snapshot());Restore(data);Dirty=true;return true;}
    public bool Redo(){coalesceKey=null;if(!redo.TryPop(out var data))return false;undo.Push(Snapshot());Restore(data);Dirty=true;return true;}
    public MotionIkResult Drag(string clipId,string bone,double frame,double targetX,double targetY,bool? bendPositive=null,bool forceFk=false)
    {
        MotionIkResult result=MotionIkResult.Invalid("拖动未应用。");
        var before=Snapshot();
        try
        {
            result=MotionPoseEditor.Drag(Graph,clipId,bone,frame,targetX,targetY,bendPositive,forceFk);
            if(!result.Success){Restore(before);return result;}
            Graph.Validate();undo.Push(before);redo.Clear();Dirty=true;coalesceKey=null;return result;
        }
        catch{Restore(before);throw;}
    }
    public void Translate(string clipId,string bone,double frame,double x,double y)
    {
        var before=Snapshot();
        try
        {
            MotionPoseEditor.Translate(Graph,clipId,bone,frame,x,y);
            Graph.Validate();undo.Push(before);redo.Clear();Dirty=true;coalesceKey=null;
        }
        catch{Restore(before);throw;}
    }
    public void Rotate(string clipId,string bone,double frame,double angle)
    {
        var before=Snapshot();
        try
        {
            MotionPoseEditor.Rotate(Graph,clipId,bone,frame,angle);
            Graph.Validate();undo.Push(before);redo.Clear();Dirty=true;coalesceKey=null;
        }
        catch{Restore(before);throw;}
    }
    public void Save(string path){Graph.Save(path);Dirty=false;coalesceKey=null;}
    internal int UndoDepth=>undo.Count;
    internal void RestoreDirty(bool value)=>Dirty=value;
}
