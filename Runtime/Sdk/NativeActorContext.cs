using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class ActorContext
{
    public NativeModServices Services => World.NativeServices ?? throw new InvalidOperationException("原生模组服务尚未初始化。");
    public IReadOnlyList<NativeBone> Skeleton()=>Services.Motions.Skeleton(this);
    public void PlayMotion(string id)=>Services.Motions.Play(this,id);
    public void StopMotion()=>Services.Motions.Stop(this);
    public NativeMotionState? MotionState=>Services.Motions.State(this);
    public ActorContext? Part(string name)
    {
        var part=NativeCharacterBones.Resolve(World,Instance,name);
        return part is null?null:new(World,part);
    }
}
