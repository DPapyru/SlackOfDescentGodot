namespace SlackOfDescent;

public sealed class NativeActorService(NativeModServices services)
{
    public ICharacterAction? Bound(ActorContext actor, CharacterActionSlot slot) => Bridge.BoundAction(actor.Id, slot);
    public void Bind(ActorContext actor, CharacterActionSlot slot, ICharacterAction action) => Bridge.BindRuntime(actor, slot, action);
    public void Unbind(ActorContext actor, CharacterActionSlot slot) => Bridge.UnbindRuntime(actor, slot);
    public void Motion(ActorContext actor, string motion) => NativeActorActions.Motion(actor, motion);
    public bool MotionIs(ActorContext actor, string motion) => NativeActorActions.MotionMatches(actor, motion);
    public bool PlaceFree(ActorContext actor, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        return actor.Call("place_free", x, y).Truth;
    }
    public NativePartPose Pose(ActorContext part) => NativeAnimationService.Pose(part);
    public void ApplyPose(ActorContext actor, string part, NativePartPose pose) => NativeAnimationService.Apply(actor, part, pose);
    public NativePartPose Mount(ActorContext actor, string part, string resource, string id, double originX, double originY, double localX = 0, double localY = 0, double angleOffset = 0)
        => NativeAnimationService.Mount(actor, part, resource, id, originX, originY, localX, localY, angleOffset);
    internal void ClearScope(NativeResourceScope scope) => Bridge.ClearRuntimeScope(scope);
    internal void ClearOwner(NativeModOwner owner) => Bridge.ClearRuntimeOwner(owner);
    private GmNativeBridge Bridge => NativeActorBridges.Find(services) ?? throw new InvalidOperationException("原生角色服务尚未连接到运行期桥接层。");
}
