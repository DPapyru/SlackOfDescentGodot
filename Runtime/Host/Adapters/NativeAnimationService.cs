namespace SlackOfDescent;

public static class NativeAnimationService
{
    public static NativePartPose Pose(ActorContext part) => new(part.X, part.Y, part.Read("image_angle") * part.Instance.Scope.ReadOr("Direction", 1).Number, part.Instance.Scope.ReadOr("ScaleX", 1).Number * part.Instance.Scope.ReadOr("Direction", 1).Number, part.Instance.Scope.ReadOr("ScaleY", 1).Number);
    public static void Apply(ActorContext actor, string name, NativePartPose pose)
    {
        var part = actor.Part(name) ?? throw new InvalidDataException("角色部位不存在：" + name);
        part.X = pose.X; part.Y = pose.Y; part.Write("image_angle", pose.Angle / part.Instance.Scope.ReadOr("Direction", 1).Number); part.Write("ScaleX", pose.ScaleX / part.Instance.Scope.ReadOr("Direction", 1).Number); part.Write("ScaleY", pose.ScaleY);
    }
    public static NativePartPose Mount(ActorContext actor, string partName, string resource, string id, double originX, double originY, double localX = 0, double localY = 0, double angleOffset = 0)
    {
        var part = actor.Part(partName) ?? actor;
        var pose = Pose(part); var point = pose.Transform(localX, localY);
        pose = pose with { X = point.X, Y = point.Y, Angle = pose.Angle + angleOffset };
        actor.Services.Presentation.Sprite(actor.Id, id, resource, pose.X, pose.Y, pose.Angle, pose.ScaleX, pose.ScaleY, originX, originY, part.Read("depth") - 1);
        return pose;
    }
}
