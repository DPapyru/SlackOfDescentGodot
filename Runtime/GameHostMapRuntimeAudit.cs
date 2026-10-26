using Godot;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public partial class GameHost
{
    private async void AuditNativeMapRuntime()
    {
        int checks = 0;
        void Expect(bool condition, string text) { checks++; if (!condition) throw new InvalidOperationException(text); }
        async Task FinishTransitions()
        {
            for (int frame = 0; frame < 240 && world.Instances.Any(i => !i.Destroyed && (world.IsObject(i.Object, world.Constants["objChangeRoom"].Int) || world.IsObject(i.Object, world.Constants["objMissionClear"].Int))); frame++) await StepFrameAsync();
        }
        var trace = new List<string>(); var messages = new List<string>();
        string package = Path.Combine(Path.GetTempPath(), "native-map-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (int frame = 0; frame < 180; frame++) await StepFrameAsync();
            content.RegisterEntity(new NativeEntityDefinition { Id = "audit.map.entity", BehaviorId = "audit.map.behavior", Width = 30, Height = 20,
                Parameters = new() { ["health"] = new() { Default = "75" }, ["enabled"] = new() { Type = "bool", Default = "true" } } }, () => new MapRuntimeAuditEntity(trace));
            var editorService = new NativeMapService(world, content);
            var first = editorService.ImportOriginal(editorService.TestRoomId).Document; first.Id = "audit.first";
            var keep = new HashSet<int>(new[] { "objSetRespawn", "objDisplay", "objController", "objMultiScroll" }.Select(n => world.Constants[n].Int));
            foreach (var item in first.Instances.OfType<JsonObject>().Where(i => !keep.Contains(NativeMapService.Int(i, "object"))).ToArray()) first.Instances.Remove(item);
            first.Tiles.Clear();
            for (int x = 0; x < 640; x += 32) editorService.AddEntity(first, world.Constants["objBlock"].Int, x, 320);
            var marker = first.Instances.OfType<JsonObject>().First(i => NativeMapService.Int(i, "object") == world.Constants["objSetRespawn"].Int); marker["x"] = 96; marker["y"] = 256;
            var target = editorService.AddEntity(first, -1, 112, 256, "audit.map.entity"); target["logicalId"] = "target"; target["properties"] = new JsonObject { ["health"] = 90 };
            first.Events = [
                new() { Id = "enter", Trigger = NativeMapTrigger.EnterArea, X = 0, Y = 0, Width = 640, Height = 480, Actions = [new() { Kind = NativeMapEventActionKind.SetSwitch, StateKey = "entered" }, new() { Kind = NativeMapEventActionKind.Message, Text = "Map message" }, new() { Kind = NativeMapEventActionKind.Spawn, EntityType = "object:objLine", TargetId = "spawned", X = 32, Y = 32 }] },
                new() { Id = "interact", Trigger = NativeMapTrigger.Interact, TargetId = "target", Actions = [new() { Kind = NativeMapEventActionKind.SetSwitch, StateKey = "interacted", TargetId = "target", Enabled = false }] },
                new() { Id = "destroy", Trigger = NativeMapTrigger.TargetDestroyed, TargetId = "target", Actions = [new() { Kind = NativeMapEventActionKind.SetSwitch, StateKey = "destroyed" }] },
                new() { Id = "clear", Trigger = NativeMapTrigger.RoomCleared, Actions = [new() { Kind = NativeMapEventActionKind.SetSwitch, StateKey = "cleared" }] }
            ];
            var second = first.Clone(); second.Id = "audit.second"; second.Events.Clear();
            second.Instances.OfType<JsonObject>().Single(i => i["logicalId"]?.ToString() == "target")["properties"]!["health"] = 44;
            var originalObjects = world.Objects.ToDictionary(pair => pair.Key, pair => pair.Value);
            var originalRooms = world.Rooms.ToDictionary(pair => pair.Key, pair => pair.Value);
            var originalCode = world.Code.ToDictionary(pair => pair.Key, pair => pair.Value);
            bool Restored() => world.Objects.Count == originalObjects.Count && world.Rooms.Count == originalRooms.Count && world.Code.Count == originalCode.Count
                && originalObjects.All(pair => world.Objects.TryGetValue(pair.Key, out var value) && value.Equals(pair.Value))
                && originalRooms.All(pair => world.Rooms.TryGetValue(pair.Key, out var value) && value.Equals(pair.Value))
                && originalCode.All(pair => world.Code.TryGetValue(pair.Key, out var value) && value.Equals(pair.Value));
            NativePlayableMap[] emptyMaps = [new(first.Id, "audit.maps", first), new(second.Id, "audit.maps", second)];
            for (int session = 0; session < 3; session++)
            {
                using (var empty = new NativeMapRuntime(world, content, nativeBridge.Services, emptyMaps))
                {
                    Expect(world.Objects.Count == originalObjects.Count + 1 && world.Rooms.Count == originalRooms.Count + 2, "empty session installs one shared native definition and two rooms");
                    if (session == 0)
                    {
                        var invalid = second.Clone(); invalid.Room["speed"] = "invalid";
                        int objects = world.Objects.Count, rooms = world.Rooms.Count, code = world.Code.Count;
                        bool rejected = false;
                        try { _ = new NativeMapRuntime(world, content, nativeBridge.Services, [emptyMaps[0], new(invalid.Id, "audit.maps", invalid)]); }
                        catch (InvalidDataException) { rejected = true; }
                        Expect(rejected && ReferenceEquals(nativeBridge.Services.Maps.Runtime, empty) && world.Objects.Count == objects && world.Rooms.Count == rooms && world.Code.Count == code, "later map deep validation failure preserves prior session and all contribution counts");
                        var interactInput = content.InputActions["native.map.interact"]; content.InputActions.Remove(interactInput.Id);
                        Action reject = () => throw new InvalidOperationException("expected map activation rejection"); content.Changed += reject;
                        rejected = false;
                        try { _ = new NativeMapRuntime(world, content, nativeBridge.Services, emptyMaps); }
                        catch (InvalidOperationException) { rejected = true; }
                        finally { content.Changed -= reject; content.InputActions[interactInput.Id] = interactInput; }
                        Expect(rejected && ReferenceEquals(nativeBridge.Services.Maps.Runtime, empty) && world.Objects.Count == objects && world.Rooms.Count == rooms && world.Code.Count == code, "activation failure removes installed rooms code and definitions while retaining prior service pointer");
                    }
                }
                Expect(Restored() && nativeBridge.Services.Maps.Runtime is null, "empty session restores all original definitions and contribution counts");
            }
            var persistentSession = new NativeMapRuntime(world, content, nativeBridge.Services, emptyMaps);
            int stub = world.Rooms[persistentSession.Room(first.Id)].GetProperty("instances").EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == NativeMapService.Int(target, "id")).GetProperty("object").GetInt32();
            var survivor = world.Create(stub, 0, 0, run: false); survivor.Scope.Write("persistent", true);
            persistentSession.Dispose();
            Expect(world.Objects.ContainsKey(stub) && !survivor.Destroyed && world.Rooms.Count == originalRooms.Count && world.Code.Count == originalCode.Count, "session cleanup preserves a definition still referenced by a persistent live instance");
            survivor.Destroyed = true; await StepFrameAsync();
            Expect(Restored(), "retired definition is reclaimed after its last live instance is destroyed");
            GD.Print("NATIVE_MAP_CLEANUP_OK sessions=3 objects=", world.Objects.Count, " rooms=", world.Rooms.Count, " code=", world.Code.Count);
            NativeContentStore.Save(Path.Combine(package, "first.map.json"), first); NativeContentStore.Save(Path.Combine(package, "second.map.json"), second);
            var manifest = new NativeModManifest { FormatVersion = 2, Id = "audit.maps", Name = "Audit maps", Version = "1.0.0", Content = new() { Maps = new() { ["audit.first"] = "first.map.json", ["audit.second"] = "second.map.json" } }, MapPack = new() { RoomOrder = ["audit.first", "audit.second"], StartRoom = "audit.second" } };
            NativeContentStore.Save(Path.Combine(package, "mod.json"), manifest);
            var loaded = mods.Reload(package); Expect(loaded.Success, "map package loads: " + loaded.Diagnostics);
            var available = EnabledMaps();
            Expect(available.Select(m => m.Id).SequenceEqual(new[] { "audit.first", "audit.second" }) && MapStart("audit.maps", available) == "audit.second", "selection uses declared author order and designated start room");
            await StartNativeMap(available, "audit.first");
            nativeBridge.Services.Maps.ShowMessage = messages.Add;
            var player = Player()!; var originalPlayer = player; world.Globals.Write("mapAuditPersistent", 123);
            Expect(trace.SequenceEqual(new[] { "created:90:1", "entered:90" }), "schema properties precede Created and RoomEntered fires once: " + string.Join(",", trace));
            var nativeSpawn = nativeBridge.Services.Maps.Spawn("native:audit.map.entity", 64, 64, "nativeSpawn");
            Expect(nativeSpawn.Read("health") == 75 && trace.Last() == "entered:75" && nativeBridge.Services.Maps.Find("nativeSpawn")?.Id == nativeSpawn.Id, "map spawn resolves entity definition ID to its distinct behavior ID");
            var actor = nativeBridge.Services.Maps.Find("target")!;
            Expect(world.Bounds(actor.Instance).Right - world.Bounds(actor.Instance).Left == 30, "definition collision size binds existing map entity");
            nativeBridge.Update(new(FrameContext.FixedDelta, 0, 1, InputSnapshot.Empty)); await StepFrameAsync();
            Expect(mapRuntime!.State.GetValueOrDefault("entered") && mapRuntime.State.GetValueOrDefault("cleared") && messages.Count == 1 && nativeBridge.Services.Maps.Find("spawned") is not null, "area, room clear, message and spawn actions share Maps service");
            await StepFrameAsync(); Expect(messages.Count == 1, "once event does not repeat while inside area");
            nativeBridge.Services.Input.SetKey("native.map.interact", 70);
            var input = new InputSnapshot(new HashSet<int> { 70 }, new HashSet<int> { 70 }, new HashSet<int>());
            nativeBridge.Update(new(FrameContext.FixedDelta, 1, 2, input)); await StepFrameAsync();
            Expect(mapRuntime.State.ContainsKey("interacted") && !actor.Instance.Active, "interaction uses remapped named input and target switch");
            nativeBridge.Services.Maps.SetSwitch("enabled", true, "target"); nativeBridge.Services.Maps.Destroy("target");
            nativeBridge.Update(new(FrameContext.FixedDelta, 2, 3, InputSnapshot.Empty)); await StepFrameAsync();
            Expect(mapRuntime.State.GetValueOrDefault("destroyed") && nativeBridge.Services.Maps.Find("target") is null, "destroy and target destruction event share stable logical ID");
            int secondRoom = mapRuntime.Room("audit.second"); world.PendingRoom = secondRoom; await StepFrameAsync();
            Expect(ReferenceEquals(originalPlayer, Player()) && world.Globals.Read("mapAuditPersistent").Int == 123, "ordinary room switch preserves original persistent actor and global state");
            Expect(trace.Count(t => t == "created:44:1") == 1 && trace.Count(t => t == "entered:44") == 1, "new room initializes parameters and behavior once");
            world.Call("room_restart", [], new(Player()!)); await StepFrameAsync();
            Expect(trace.Count(t => t == "created:44:1") == 2 && trace.Count(t => t == "entered:44") == 2 && trace.Contains("exited:44"), "original room restart detaches and rebinds map behavior");
            nativeBridge.Services.Maps.Restart(); await StepFrameAsync();
            Expect(trace.Count(t => t == "created:44:1") == 3 && ReferenceEquals(originalPlayer, Player()), "Maps restart uses original Respawn and room lifecycle");
            await FinishTransitions();
            nativeBridge.Services.Maps.ChangeRoom("audit.first");
            var transition = world.Instances.Last(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objChangeRoom"].Int)); transition.Scope.Write("alpha", 1.95); await StepFrameAsync();
            Expect(nativeBridge.Services.Maps.CurrentRoom == "audit.first", "Maps room change uses original room fade object");
            await FinishTransitions();
            nativeBridge.Services.Maps.CompleteLevel();
            var clear = world.Instances.Last(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objMissionClear"].Int));
            Expect(clear.Scope.Read("nextRoom").Int == secondRoom, "completion uses author room order through original settlement object"); clear.Scope.Write("alpha", 9.95); await StepFrameAsync();
            Expect(nativeBridge.Services.Maps.CurrentRoom == "audit.second" && world.Globals.Read("mapAuditPersistent").Int == 123, "settlement loads next Mod room and preserves globals");
            await FinishTransitions();
            nativeBridge.Services.Maps.CompleteLevel(); clear = world.Instances.Last(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objMissionClear"].Int)); clear.Scope.Write("alpha", 9.95); await StepFrameAsync();
            Expect(world.CurrentRoom == world.Constants["Title"].Int && nativeBridge.Services.Maps.CurrentRoom.Length == 0, "last room settlement returns through original title lifecycle");
            var roomIds = mapRuntime.Rooms.Values.ToArray(); EndMapRuntime(); Expect(roomIds.All(id => !world.Rooms.ContainsKey(id)), "session cleanup removes all temporary rooms");
            await StartNativeMap(available, "audit.second"); roomIds = mapRuntime!.Rooms.Values.ToArray();
            var running = mapRuntime; File.WriteAllText(Path.Combine(package, "mod.json"), "{");
            Expect(!mods.Reload(package).Success && !mapContentChanged && ReferenceEquals(running, mapRuntime) && nativeBridge.Services.Maps.CurrentRoom == "audit.second", "failed package reload preserves active map session");
            Expect(mods.Unload("audit.maps").Success, "active map package unload succeeds"); SyncMapRuntime();
            for (int frame = 0; frame < 120 && mapRuntime is not null; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Expect(mapRuntime is null && world.CurrentRoom == world.Constants["Title"].Int && nativeBridge.Services.Maps.CurrentRoom.Length == 0 && roomIds.All(id => !world.Rooms.ContainsKey(id)), "successful unload returns through title and removes map session");
            GD.Print("NATIVE_MAP_RUNTIME_OK checks=", checks, " lifecycle=", string.Join(",", trace)); audio.Shutdown(); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("NATIVE_MAP_RUNTIME_FAILED " + error); audio.Shutdown(); GetTree().Quit(1); }
        finally { if (Directory.Exists(package)) Directory.Delete(package, true); }
    }
    private sealed class MapRuntimeAuditEntity(List<string> trace) : IEntityBehavior
    {
        public string Id => "audit.map.behavior";
        public void Created(ActorContext entity) => trace.Add("created:" + entity.Read("health") + ":" + entity.Read("enabled"));
        public void RoomEntered(ActorContext entity) => trace.Add("entered:" + entity.Read("health"));
        public void RoomExited(ActorContext entity) => trace.Add("exited:" + entity.Read("health"));
        public void Update(ActorContext entity, FrameContext frame) { }
    }
}
