namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private static readonly bool emptyInstanceDiagnostics=System.Environment.GetEnvironmentVariable("SLACK_EMPTY_INSTANCE_DEBUG")=="1";
    private readonly Dictionary<int,int> emptyInstanceReads=new();
    private readonly Dictionary<int,int> emptyInstanceWrites=new();
    public int EmptyInstanceReadCount(int target)=>emptyInstanceReads.GetValueOrDefault(target);
    public int EmptyInstanceWriteCount(int target)=>emptyInstanceWrites.GetValueOrDefault(target);
    public IEnumerable<KeyValuePair<int,int>> EmptyInstanceTargets=>emptyInstanceReads.Concat(emptyInstanceWrites).GroupBy(pair=>pair.Key).Select(group=>new KeyValuePair<int,int>(group.Key,group.Sum(pair=>pair.Value))).OrderBy(pair=>pair.Key);
    internal void NoteEmptyInstanceRead(int target,string name)
    {
        emptyInstanceReads[target]=emptyInstanceReads.GetValueOrDefault(target)+1;
        if(emptyInstanceDiagnostics)Console.Error.WriteLine("EMPTY_INSTANCE read target="+target+" field="+name+" room="+CurrentRoom+" frame="+Frame);
    }
    internal void NoteEmptyInstanceWrite(int target,string name)
    {
        emptyInstanceWrites[target]=emptyInstanceWrites.GetValueOrDefault(target)+1;
        if(emptyInstanceDiagnostics)Console.Error.WriteLine("EMPTY_INSTANCE write target="+target+" field="+name+" room="+CurrentRoom+" frame="+Frame);
    }
}
