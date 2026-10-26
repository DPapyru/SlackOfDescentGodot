using System.Reflection;
using SlackOfDescent;

public static class GmInstanceSdkTests
{
    public static int Run(GmWorld world)
    {
        int assertions = 0;
        void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        using var services = new NativeModServices(world, new ContentRegistry());
        Expect(services.Instances is not null, "NativeModServices exposes the instance service");
        var instances = services.Instances;
        int bullet = instances.ObjectId("objBullet");
        Expect(bullet == world.Constants["objBullet"].Int, "ObjectId maps objBullet to the runtime constant");
        foreach (string name in new[] { "objBullet", "objPlayerDoll", "objEnemyDoll", "objController" })
            Expect(instances.ObjectName(instances.ObjectId(name)) == name, "ObjectName round-trips " + name);
        Expect(instances.ObjectName(-100000) is null, "ObjectName returns null for an unloaded id");
        bool missingObject = false;
        try { instances.ObjectId("objMissing"); }
        catch (InvalidOperationException error) { missingObject = error.Message.Contains("对象未加载："); }
        Expect(missingObject, "ObjectId reports 对象未加载 for an unknown name");
        bool blankObject = false;
        try { instances.ObjectId(" "); }
        catch (InvalidOperationException) { blankObject = true; }
        Expect(blankObject, "ObjectId rejects a blank name");

        int bulletsBefore = instances.Count("objBullet");
        int activeBulletsBefore = instances.Count("objBullet", true);
        int instancesBefore = world.Instances.Count;
        var first = instances.Spawn("objBullet", 320, 240, runEvents: false);
        Expect(world.Instances.Count == instancesBefore + 1, "Spawn(string) adds one world instance");
        Expect(first.X == 320 && first.Y == 240, "Spawn(string) places the instance at the requested coordinates");
        Expect(first.Instance.Object == bullet && first.Id > 0, "Spawn(string) creates the requested object type");
        Expect(instances.Find(first.Id)?.Id == first.Id, "Find returns the instance created by Spawn");
        var second = instances.Spawn(bullet, 32, 64, runEvents: false);
        Expect(world.Instances.Count == instancesBefore + 2, "Spawn(int) adds one world instance");
        Expect(second.Id != first.Id && second.X == 32 && second.Y == 64, "Spawn(int) creates a distinct instance at the requested coordinates");
        Expect(instances.Count("objBullet") == bulletsBefore + 2, "Count sees both spawned instances");
        Expect(instances.All("objBullet").Count == bulletsBefore + 2, "All filters by object name");
        Expect(instances.All().Any(actor => actor.Id == first.Id) && instances.All().Any(actor => actor.Id == second.Id), "All without a filter sees both spawned instances");
        Expect(instances.Find(-1234567) is null, "Find returns null for an unknown id");
        Expect(instances.FindObject("objBullet", bulletsBefore)?.Id == first.Id, "FindObject returns the first spawned instance");
        Expect(instances.FindObject("objBullet", bulletsBefore + 1)?.Id == second.Id, "FindObject honours its index");
        Expect(instances.FindObject("objBullet", bulletsBefore + 9) is null, "FindObject returns null past the last instance");
        bool negativeIndex = false;
        try { instances.FindObject("objBullet", -1); }
        catch (ArgumentOutOfRangeException) { negativeIndex = true; }
        Expect(negativeIndex, "FindObject rejects a negative index");
        Expect(instances.IsObjectOf(first, "objBullet") && instances.IsObjectOf(second, "objBullet"), "IsObjectOf accepts the matching object");
        Expect(!instances.IsObjectOf(first, "objController"), "IsObjectOf rejects a different object");
        Expect(instances.Exists(first) && instances.Exists(second), "Exists reports live spawned instances");
        Expect(!instances.Exists(new ActorContext(world, new GmInstance())), "Exists rejects an instance that is not in the world");
        second.Instance.Active = false;
        Expect(instances.Count("objBullet", activeOnly: true) == activeBulletsBefore + 1, "Count(activeOnly) skips inactive instances");
        Expect(instances.All("objBullet", activeOnly: true).Count == activeBulletsBefore + 1, "All(activeOnly) skips inactive instances");
        Expect(instances.FindObject("objBullet", bulletsBefore + 1) is null, "FindObject only returns active instances");
        second.Instance.Active = true;
        Expect(instances.Count("objBullet", activeOnly: true) == activeBulletsBefore + 2, "reactivated instances are counted again");

        instances.Alarm(first, 4, 9);
        Expect(first.Instance.Scope.Read("alarm", 4).Int == 9, "Alarm writes the requested alarm slot");
        instances.Alarm(first, 11, 0);
        Expect(first.Instance.Scope.Read("alarm", 11).Int == 0, "Alarm accepts the last valid slot");
        Expect(first.Instance.Scope.Read("alarm", 0).Int == -1, "untouched alarm slots keep their initial value");
        bool highAlarm = false;
        try { instances.Alarm(first, 12, 1); }
        catch (ArgumentOutOfRangeException) { highAlarm = true; }
        Expect(highAlarm, "Alarm rejects an index above 11");
        bool lowAlarm = false;
        try { instances.Alarm(first, -1, 1); }
        catch (ArgumentOutOfRangeException) { lowAlarm = true; }
        Expect(lowAlarm, "Alarm rejects a negative index");

        int probeObject = world.Call("object_add", [], new GmContext(new GmInstance())).Int;
        try
        {
            var probeContext = new GmContext(new GmInstance());
            world.Call("object_event_add", [probeObject, 3, 0, "probeSteps += 1;"], probeContext);
            world.Call("object_event_add", [probeObject, 4, bullet, "probeContact = other.id;"], probeContext);
            world.Call("object_event_add", [probeObject, 7, 10, "probeUsers += 1;"], probeContext);
            var probe = instances.Spawn(probeObject, 16, 32, runEvents: false);
            probe.Instance.Scope.Write("probeSteps", 0);
            probe.Instance.Scope.Write("probeUsers", 0);
            instances.Fire(probe, 3, 0);
            Expect(probe.Instance.Scope.Read("probeSteps").Int == 1, "Fire dispatches the addressed event");
            instances.Fire(probe, 3, 0);
            Expect(probe.Instance.Scope.Read("probeSteps").Int == 2, "repeated Fire dispatches the same event");
            instances.Fire(probe, 4, bullet, first);
            Expect(probe.Instance.Scope.Read("probeContact").Int == first.Id, "Fire forwards the other instance to the event");
            instances.UserEvent(probe, 0);
            Expect(probe.Instance.Scope.Read("probeUsers").Int == 1, "UserEvent dispatches ev_user0");
            bool unhandled = true;
            try { instances.Fire(probe, 5, 0); instances.UserEvent(probe, 15); }
            catch (Exception) { unhandled = false; }
            Expect(unhandled, "Fire and UserEvent ignore events without handlers");
            bool highUser = false;
            try { instances.UserEvent(probe, 16); }
            catch (ArgumentOutOfRangeException) { highUser = true; }
            Expect(highUser, "UserEvent rejects a number above 15");
            bool lowUser = false;
            try { instances.UserEvent(probe, -1); }
            catch (ArgumentOutOfRangeException) { lowUser = true; }
            Expect(lowUser, "UserEvent rejects a negative number");
            instances.Destroy(probe);
            Expect(!instances.Exists(probe) && probe.Instance.Destroyed, "Destroy removes the spawned probe instance");
        }
        finally { world.Call("object_delete", [probeObject], new GmContext(new GmInstance())); }

        instances.Destroy(second);
        Expect(second.Instance.Destroyed, "Destroy marks the instance destroyed");
        Expect(!instances.Exists(second), "Destroy clears Exists");
        Expect(instances.Find(second.Id) is null, "Destroy removes the instance from Find");
        Expect(instances.Count("objBullet") == bulletsBefore + 1, "Destroy reduces Count");

        int previousRoom = world.CurrentRoom;
        int? previousPending = world.PendingRoom;
        try
        {
            int firstRoom = world.Constants["room_first"].Int;
            int lastRoom = world.Rooms.Keys.Max();
            world.PendingRoom = null;
            instances.GoToRoom(firstRoom);
            Expect(world.PendingRoom == firstRoom, "GoToRoom writes the resolved room to PendingRoom");
            instances.GoToRoom(lastRoom);
            Expect(world.PendingRoom == lastRoom, "GoToRoom switches to another loaded room");
            world.CurrentRoom = firstRoom;
            world.PendingRoom = null;
            instances.RestartRoom();
            Expect(world.PendingRoom == firstRoom, "RestartRoom re-enters the current room");
            Expect(instances.CurrentRoom == firstRoom, "CurrentRoom mirrors the world room");
            string roomName = world.Rooms[firstRoom].GetProperty("name").GetString() ?? "";
            Expect(instances.CurrentRoomName == roomName, "CurrentRoomName resolves the loaded room name");
            bool missingRoom = false;
            try { instances.GoToRoom(-777777); }
            catch (InvalidOperationException error) { missingRoom = error.Message.Contains("房间未加载："); }
            Expect(missingRoom, "GoToRoom reports 房间未加载 for an unknown room");
            Expect(world.PendingRoom == firstRoom, "a failed GoToRoom leaves PendingRoom untouched");
        }
        finally { world.CurrentRoom = previousRoom; world.PendingRoom = previousPending; }

        using var ownerServices = new NativeModServices(world, new ContentRegistry());
        var ownerInstances = new NativeInstanceService(ownerServices);
        var scopeType = InternalType(typeof(NativeInstanceService), "NativeResourceScope");
        var ownerType = InternalType(typeof(NativeInstanceService), "NativeModOwner");
        var defaultScope = DefaultValue(scopeType);
        var otherScope = ScopeValue(scopeType, ownerType, "elsewhere", 0);
        var defaultOwner = DefaultValue(ownerType);
        var otherOwner = OwnerValue(ownerType, "other-owner", "generation");
        var byScope = ownerInstances.Spawn(bullet, 21, 22, runEvents: false);
        var keptByScope = world.Create(bullet, 23, 24, run: false);
        InternalCall(ownerInstances, "ClearScope", otherScope);
        Expect(!byScope.Instance.Destroyed, "ClearScope ignores instances spawned under another scope");
        InternalCall(ownerInstances, "ClearScope", defaultScope);
        Expect(byScope.Instance.Destroyed, "ClearScope reclaims the instances it spawned");
        Expect(!keptByScope.Destroyed && world.Instances.Contains(keptByScope), "ClearScope keeps instances created outside the service");
        var byOwner = ownerInstances.Spawn(bullet, 25, 26, runEvents: false);
        var keptByOwner = world.Create(bullet, 27, 28, run: false);
        InternalCall(ownerInstances, "ClearOwner", otherOwner);
        Expect(!byOwner.Instance.Destroyed, "ClearOwner ignores instances spawned under another owner");
        InternalCall(ownerInstances, "ClearOwner", defaultOwner);
        Expect(byOwner.Instance.Destroyed, "ClearOwner reclaims the instances it spawned");
        Expect(!keptByOwner.Destroyed && world.Instances.Contains(keptByOwner), "ClearOwner keeps instances created outside the service");
        var byDispose = ownerInstances.Spawn(bullet, 29, 30, runEvents: false);
        InternalCall(ownerInstances, "Dispose");
        Expect(byDispose.Instance.Destroyed, "Dispose reclaims every instance the service spawned");
        Expect(!keptByScope.Destroyed && !keptByOwner.Destroyed, "Dispose keeps instances created outside the service");
        Expect(world.Instances.Contains(keptByScope) && world.Instances.Contains(keptByOwner), "reclaimed ownership leaves foreign instances registered in the world");
        instances.Destroy(new ActorContext(world, keptByScope));
        instances.Destroy(new ActorContext(world, keptByOwner));
        Expect(keptByScope.Destroyed && keptByOwner.Destroyed, "Destroy removes foreign instances on request");
        InternalCall(instances, "Dispose");
        Expect(first.Instance.Destroyed && !instances.Exists(first), "Dispose reclaims the remaining instance spawned by the tested service");
        return assertions;
    }

    private static Type InternalType(Type owner, string name)
    {
        string fullName = (owner.Namespace is null ? "" : owner.Namespace + ".") + name;
        return owner.Assembly.GetType(fullName) ?? throw new InvalidOperationException("缺少内部类型：" + fullName);
    }
    private static object DefaultValue(Type type) => Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法构造内部类型：" + type.Name);
    private static object OwnerValue(Type ownerType, string id, string generation) => Activator.CreateInstance(ownerType, id, generation) ?? throw new InvalidOperationException("无法构造 NativeModOwner");
    private static object ScopeValue(Type scopeType, Type ownerType, string contribution, int actor) => Activator.CreateInstance(scopeType, DefaultValue(ownerType), contribution, actor) ?? throw new InvalidOperationException("无法构造 NativeResourceScope");
    private static void InternalCall(NativeInstanceService service, string name, params object?[] arguments)
    {
        var method = typeof(NativeInstanceService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null) throw new InvalidOperationException("NativeInstanceService 缺少内部成员：" + name);
        try { method.Invoke(service, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
