using System.Text.Json;

namespace SlackOfDescent;

public sealed class MotionGraphDocument(MotionGraph graph)
{
    public MotionGraph Graph { get; private set; } = graph;
    public bool Dirty { get; private set; }
    private readonly Stack<byte[]> undo=new();
    private readonly Stack<byte[]> redo=new();
    private byte[] Snapshot()=>JsonSerializer.SerializeToUtf8Bytes(Graph,NativeContentStore.Json);
    private void Restore(byte[] data)=>Graph=JsonSerializer.Deserialize<MotionGraph>(data,NativeContentStore.Json)!;
    public void Edit(Action<MotionGraph> change)
    {
        var before=Snapshot();
        try{change(Graph);Graph.Validate();undo.Push(before);redo.Clear();Dirty=true;}catch{Restore(before);throw;}
    }
    public bool Undo(){if(!undo.TryPop(out var data))return false;redo.Push(Snapshot());Restore(data);Dirty=true;return true;}
    public bool Redo(){if(!redo.TryPop(out var data))return false;undo.Push(Snapshot());Restore(data);Dirty=true;return true;}
    public MotionIkResult Drag(string clipId,string bone,double frame,double targetX,double targetY,bool? bendPositive=null,bool forceFk=false)
    {
        MotionIkResult result=MotionIkResult.Invalid("拖动未应用。");
        var before=Snapshot();
        try
        {
            result=MotionPoseEditor.Drag(Graph,clipId,bone,frame,targetX,targetY,bendPositive,forceFk);
            if(!result.Success){Restore(before);return result;}
            Graph.Validate();undo.Push(before);redo.Clear();Dirty=true;return result;
        }
        catch{Restore(before);throw;}
    }
    public void Translate(string clipId,string bone,double frame,double x,double y)
    {
        var before=Snapshot();
        try
        {
            MotionPoseEditor.Translate(Graph,clipId,bone,frame,x,y);
            Graph.Validate();undo.Push(before);redo.Clear();Dirty=true;
        }
        catch{Restore(before);throw;}
    }
    public void Save(string path){Graph.Save(path);Dirty=false;}
    internal void RestoreDirty(bool value)=>Dirty=value;
}
