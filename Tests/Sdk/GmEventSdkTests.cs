using SlackOfDescent;

public static class GmEventSdkTests
{
    public static int Run(GmWorld shared)
    {
        int assertions = 0;
        void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + message);
            assertions++;
        }
        var userRoot = Path.Combine(Path.GetTempPath(), "slack-event-sdk-" + Environment.ProcessId);
        var world = new GmWorld(shared.Root, shared.SourceRoot, userRoot);
        var registry = new ContentRegistry();
        using var services = new NativeModServices(world, registry);
        Expect(services.Events is not null, "NativeModServices exposes the event service");
        var events = services.Events;
        var self = new GmInstance { Id = 900001, Object = -1 };
        world.Instances.Add(self);
        var context = new GmContext(self);
        void Subscribe(string id, string name, NativeEventPhase phase, Action<NativeGameEvent> handler) { registry.EventHandlers.Add(id, new NativeEventHandler("event-tests", name, phase, handler)); registry.Refresh(); }
        void Unsubscribe(params string[] ids) { foreach (string id in ids) registry.EventHandlers.Remove(id); registry.Refresh(); }
        Expect(world.CallInterceptor is null && world.CallObserver is null, "1 an empty registry installs no call hook");
        Expect(world.CreatedObserver is null && world.DestroyedObserver is null, "1 an empty registry installs no lifecycle hook");
        Expect(world.StepBeginObserver is null && world.StepEndObserver is null && world.CollisionObserver is null && world.AlarmObserver is null && world.KeyObserver is null, "1 an empty registry installs no world hook");
        Expect(world.Events == events && events.HandlerCount(NativeEventNames.StepBegin) == 0 && !events.HasHandlers(NativeEventNames.InstanceCreated), "1 the event service owns the world while reporting no handlers");
        int intercepted = 0;
        GmValue nested = default;
        Subscribe("event-tests:call:before", NativeEventNames.Call("string_length"), NativeEventPhase.Before, value =>
        {
            intercepted++;
            nested = world.Call("string_length", ["abc"], context);
            value.Handled = true; value.Result = 7;
        });
        Expect(world.CallInterceptor is not null && world.CallObserver is null && world.Events is not null, "2 a call.before subscription installs the interceptor");
        Expect(world.Call("string_length", ["abcdef"], context).Int == 7, "2 Handled with Result short-circuits the builtin");
        int finished = 0;
        Subscribe("event-tests:call:finish", NativeEventNames.Call("string_length"), NativeEventPhase.After, value => { finished++; value.Result = value.Result.Number + 100; });
        Expect(world.Call("string_length", ["abcdef"], context).Int == 107, "2a a Before takeover still reaches the After handler");
        Expect(intercepted == 2 && finished == 1, "2a the After handler observes the intercepted result once");
        Unsubscribe("event-tests:call:finish");
        Expect(world.Call("string_length", ["abcdef"], context).Int == 7, "2b the takeover result is returned unchanged again after the After handler leaves");
        Expect(intercepted == 3, "4 the interceptor runs once per outer call");
        Expect(nested.Int == 3, "4 the nested identical call falls through to the original builtin");
        string? observed = null;
        Subscribe("event-tests:call:after", NativeEventNames.Call("string_lower"), NativeEventPhase.After, value => { observed = value.Result.Text; value.Result = (value.Result.Text ?? "") + "!"; });
        Expect(world.CallObserver is not null && world.CallInterceptor is not null, "3 a call.after subscription installs the observer beside the interceptor");
        Expect(world.Call("string_lower", ["ABC"], context).Text == "abc!" && observed == "abc", "3 the after handler sees the original result and rewrites it");
        Expect(world.Call("string_lower", ["ABC"], context).Text == "abc!", "3 every matching call reaches the after handler");
        Expect(world.Call("real", ["4.75"], context).Number == 4.75, "3 a non-subscribed builtin bypasses the call hooks entirely");
        Unsubscribe("event-tests:call:before", "event-tests:call:after");
        Expect(world.CallInterceptor is null && world.CallObserver is null, "11 removing every call handler clears both call hooks");
        Expect(world.Call("string_length", ["abcdef"], context).Int == 6 && world.Call("string_lower", ["ABC"], context).Text == "abc", "11 the builtins run unmodified after unsubscribing");
        int created = 0, destroyed = 0, createdId = -1, destroyedId = -1;
        Subscribe("event-tests:created", NativeEventNames.InstanceCreated, NativeEventPhase.Before, value => { created++; createdId = value.Arguments["id"].Int; });
        Subscribe("event-tests:destroyed", NativeEventNames.InstanceDestroyed, NativeEventPhase.Before, value => { destroyed++; destroyedId = value.Arguments["id"].Int; });
        Expect(world.CreatedObserver is not null && world.DestroyedObserver is not null, "5 lifecycle subscriptions install both lifecycle hooks");
        int plain = world.Call("object_add", [], context).Int;
        var spawned = world.Create(plain, 16, 16, run: false);
        Expect(created == 1 && createdId == spawned.Id, "5 instance.created fires once with the new instance id");
        spawned.Destroyed = true;
        Expect(destroyed == 1 && destroyedId == spawned.Id, "6 instance.destroyed fires once with the destroyed instance id");
        spawned.Destroyed = false; spawned.Destroyed = true;
        Expect(destroyed == 1, "6 a toggled instance still reports its destruction only once");
        world.Instances.Remove(spawned);
        int stepBegin = 0, stepEnd = 0;
        Subscribe("event-tests:step:begin", NativeEventNames.StepBegin, NativeEventPhase.Before, value => stepBegin++);
        Subscribe("event-tests:step:end", NativeEventNames.StepEnd, NativeEventPhase.After, value => stepEnd++);
        Expect(world.StepBeginObserver is not null && world.StepEndObserver is not null, "7 step subscriptions install both step hooks");
        world.Step();
        Expect(stepBegin == 1 && stepEnd == 1, "7 one step notifies step.begin and step.end once");
        world.Step();
        Expect(stepBegin == 2 && stepEnd == 2, "7 later steps keep notifying both hooks");
        int collisions = 0, collisionTarget = -1;
        Subscribe("event-tests:collision", NativeEventNames.Collision, NativeEventPhase.Before, value => { collisions++; collisionTarget = value.Arguments["target"].Int; });
        Expect(world.CollisionObserver is not null, "8 a collision subscription installs the collision hook");
        int block = world.RegisterSprite(System.Text.Json.JsonSerializer.SerializeToElement(new { originX = 0, originY = 0, frames = new[] { new { width = 8, height = 8, path = "unused" } }, masks = new[] { new { width = 8, height = 8, left = 0, right = 7, top = 0, bottom = 7, data = new string('1', 64) } } }));
        int impact = world.Call("object_add", [], context).Int;
        world.Call("object_set_sprite", [impact, block], context);
        world.Call("object_event_add", [impact, 4, impact, "hits += 1;"], context);
        var left = world.Create(impact, 40, 40, run: false); left.Scope.Write("hits", 0);
        var right = world.Create(impact, 44, 44, run: false); right.Scope.Write("hits", 0);
        for (int attempt = 0; attempt < 4 && collisions == 0; attempt++) world.Step();
        Expect(collisions > 0 && collisionTarget == impact, "8 overlapping instances report the collision with its target");
        left.Destroyed = true; right.Destroyed = true;
        int alarms = 0, alarmSlot = -1, alarmId = -1;
        Subscribe("event-tests:alarm", NativeEventNames.Alarm, NativeEventPhase.Before, value => { alarms++; alarmSlot = value.Arguments["alarm"].Int; alarmId = value.Arguments["id"].Int; });
        Expect(world.AlarmObserver is not null, "9 an alarm subscription installs the alarm hook");
        world.Call("object_event_add", [plain, 2, 7, "alarms += 1;"], context);
        var ringing = world.Create(plain, 24, 24, run: false);
        ringing.Scope.Write("alarms", 0); ringing.Scope.Write("alarm", 1, 7);
        world.Step();
        Expect(alarms == 1 && alarmSlot == 7 && alarmId == ringing.Id, "9 the alarm hook reports the expired slot for its instance");
        Expect(ringing.Scope.Read("alarm", 7).Int == 0, "9 the engine clears the expired alarm slot");
        var phases = new List<NativeKeyPhase>();
        Subscribe("event-tests:key:down", NativeEventNames.KeyDown, NativeEventPhase.Before, value => { phases.Add(NativeKeyPhase.Down); });
        Subscribe("event-tests:key:pressed", NativeEventNames.KeyPressed, NativeEventPhase.Before, value => { phases.Add(NativeKeyPhase.Pressed); });
        Subscribe("event-tests:key:released", NativeEventNames.KeyReleased, NativeEventPhase.Before, value => { phases.Add(NativeKeyPhase.Released); });
        Expect(world.KeyObserver is not null && events.HandlerCount(NativeEventNames.KeyDown) == 1, "10 key subscriptions install the key hook");
        var input = new GmInput();
        input.Key(world, 82, true);
        Expect(phases.SequenceEqual(new[] { NativeKeyPhase.Down, NativeKeyPhase.Pressed }), "10 a fresh press reports key.down then key.pressed");
        input.Key(world, 82, false);
        Expect(phases.SequenceEqual(new[] { NativeKeyPhase.Down, NativeKeyPhase.Pressed, NativeKeyPhase.Released }), "10 a release reports only key.released");
        input.Key(world, 82, true); input.Key(world, 82, false);
        Expect(phases.Count == 6 && phases[3] == NativeKeyPhase.Down && phases[4] == NativeKeyPhase.Pressed && phases[5] == NativeKeyPhase.Released, "10 a repeated keystroke reports every phase again");
        input.Key(world, 82, false);
        Expect(phases.Count == 6, "10 an idle release reports nothing");
        input.Key(world, 82, true);
        Expect(phases.Count == 8 && phases[6] == NativeKeyPhase.Down && phases[7] == NativeKeyPhase.Pressed, "10 a fresh press after release reports both edges");
        input.Key(world, 82, true);
        Expect(phases.Count == 9 && phases[8] == NativeKeyPhase.Down, "10 holding the key reports key.down without a second key.pressed");

        int roomDestroyed = 0;
        Expect(world.DestroyedObserver is not null, "12 the destroyed hook stays installed for the room test");
        int roomId = 900777;
        world.Rooms[roomId] = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            name = "event-test-room", width = 64, height = 64, speed = 30, colour = 0, viewsEnabled = false, clearScreen = false,
            tiles = Array.Empty<object>(), backgrounds = Array.Empty<object>(), views = Array.Empty<object>(),
            instances = Array.Empty<object>(), code = world.Compile("return 0;")
        });
        var transient = world.Create(plain, 8, 8, run: false);
        transient.Scope.Write("persistent", false);
        roomDestroyed = 0;
        int roomEvents = 0;
        Subscribe("event-tests:room", NativeEventNames.Room, NativeEventPhase.Before, value => { roomEvents++; value.Arguments["next"] = value.Arguments["next"]; });
        Expect(world.Events is not null && roomEvents == 0, "13 a room subscription starts silent");
        world.LoadRoomAsync(roomId).GetAwaiter().GetResult();
        Expect(destroyed >= 2 && destroyedId >= 0, "12 leaving a room reports instance.destroyed for non-persistent instances");
        Expect(!world.Instances.Contains(transient), "12 the transient instance is removed with the room");
        Expect(roomEvents == 1, "13 entering a room notifies the room event once");
        Unsubscribe("event-tests:room");
        events.Dispose();
        Expect(world.Events is null && world.KeyObserver is null && world.StepBeginObserver is null && world.CallInterceptor is null, "11 disposing the service releases every world hook");
        return assertions;
    }
}
