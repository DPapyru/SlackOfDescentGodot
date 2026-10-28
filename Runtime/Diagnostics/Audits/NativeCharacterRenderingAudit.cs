using Godot;

namespace SlackOfDescent;

public sealed partial class NativeCharacterRendering
{
    internal readonly List<(string Target, string Path, Vector2 Position, Vector2 Scale, float Angle)> Audit = new();
    partial void RecordPart(string target,string path,Vector2 position,Vector2 scale,float angle)=>Audit.Add((target,path,position,scale,angle));
    partial void ClearAudit()=>Audit.Clear();
}
