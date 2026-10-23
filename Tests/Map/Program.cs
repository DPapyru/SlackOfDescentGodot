using SlackOfDescent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

string project = Environment.GetEnvironmentVariable("SLACK_MAP_PROJECT") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string output = Environment.GetEnvironmentVariable("SLACK_MAP_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "ccgame-map-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var world = new GmWorld(Path.Combine(project, "GameData"), Path.Combine(project, "GameData/assets"), Path.Combine(output, "user"));
var registry = new ContentRegistry(); var service = new NativeMapService(world, registry);
int assertions = 0;
void Expect(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
var original = service.ImportOriginal(service.TestRoomId).Document;
Expect(service.TestRoomId == world.Constants["EnemyTestRoom"].Int, "Title TestRoom resolves actual EnemyTestRoom");
Expect(original.Instances.Count == 1227 && original.Tiles.Count == 1647, "Complete original map counts");
Expect(JsonNode.DeepEquals(original.Room, JsonNode.Parse(world.Rooms[service.TestRoomId].GetRawText())), "Import preserves every raw field");
Expect(original.CreationCode.Count == 1228, "Room and per-entity initialization code retained");
var reconstructed = original.Clone();
reconstructed.Room["unknownRoom"] = new JsonObject { ["future"] = new JsonArray(17, "保留") };
reconstructed.Extra = new() { ["futureFormat"] = JsonSerializer.SerializeToElement(new { hello = "兼容" }) };
reconstructed.Instances[0]!["futureEntity"] = "保留";
reconstructed.Tiles[0]!["futureTile"] = 99;
var entity = service.AddEntity(reconstructed, world.Constants["objBlock"].Int, 512, 160);
entity["properties"] = new JsonObject { ["editorValue"] = 42, ["editorText"] = "中文与 空格", ["solid"] = true };
int addedId = Int(entity, "id");
string path = Path.Combine(output, "中文 空格", "完整测试.map.json"); service.Save(reconstructed, path);
var loaded = service.Load(path).Document;
Expect(loaded.Id == reconstructed.Id, "Stable document ID");
Expect(JsonNode.DeepEquals(loaded.Room, reconstructed.Room), "All original and unknown room/entity/tile fields round trip");
Expect(loaded.Extra!["futureFormat"].GetProperty("hello").GetString() == "兼容", "Unknown document fields round trip");
Expect(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Count() == 0, "Atomic save leaves no temporary files");
int installed = service.InstallForPlay(loaded); var playable = world.Rooms[installed];
Expect(installed == service.TestRoomId, "Playback retains original room-indexed script identity");
Expect(playable.GetProperty("instances").GetArrayLength() == 1228 && playable.GetProperty("tiles").GetArrayLength() == 1647, "Playback reconstructs complete content");
foreach (var pair in original.Room.Where(p => p.Key is not ("id" or "name" or "caption" or "instances" or "tiles"))) Expect(JsonNode.DeepEquals(pair.Value, JsonNode.Parse(playable.GetProperty(pair.Key).GetRawText())), "Preserved room metadata " + pair.Key);
foreach (var data in playable.GetProperty("instances").EnumerateArray())
{
    var actor = world.Create(data.GetProperty("object").GetInt32(), data.GetProperty("x").GetDouble(), data.GetProperty("y").GetDouble(), data.GetProperty("id").GetInt32(), false);
    actor.Scope.Write("image_xscale", data.GetProperty("xscale").GetDouble()); actor.Scope.Write("image_yscale", data.GetProperty("yscale").GetDouble()); actor.Scope.Write("image_angle", data.GetProperty("angle").GetDouble());
}
foreach (var data in original.Instances.OfType<JsonObject>())
{
    var actor = world.Instances.Single(i => i.Id == Int(data, "id"));
    Expect(actor.Object == Int(data, "object"), "Reconstructed entity type");
    Expect(actor.Scope.Read("x").Number == Number(data, "x") && actor.Scope.Read("y").Number == Number(data, "y"), "Reconstructed entity position");
    Expect(actor.Scope.Read("image_xscale").Number == Number(data, "xscale") && actor.Scope.Read("image_yscale").Number == Number(data, "yscale"), "Reconstructed entity transform");
}
service.ApplyProperties(loaded); var added = world.Instances.Single(i => i.Id == addedId);
Expect(added.Scope.Read("editorValue").Number == 42 && added.Scope.Read("editorText").ToString() == "中文与 空格", "Saved custom properties reach gameplay instances");
Expect(world.Call("collision_point", [513, 161, world.Constants["objBlock"], false, false], new(added)).Int != -4, "Edited collision block is query-ready");
// A character-shaped actor is moved through floor, raised platform, wall and room edge probes.
var collisionDocument = original.Clone(); collisionDocument.Instances.Clear(); collisionDocument.Tiles.Clear();
int blockType = world.Constants["objBlock"].Int;
for (int x = 0; x < 320; x += 32) service.AddEntity(collisionDocument, blockType, x, 256);
for (int x = 96; x < 192; x += 32) service.AddEntity(collisionDocument, blockType, x, 160);
for (int y = 0; y < 256; y += 32) service.AddEntity(collisionDocument, blockType, 288, y);
var collisionWorld = new GmWorld(Path.Combine(project, "GameData"), Path.Combine(project, "GameData/assets"), Path.Combine(output, "collision-user"));
foreach (var block in collisionDocument.Instances.OfType<JsonObject>()) collisionWorld.Create(blockType, Number(block,"x"), Number(block,"y"), Int(block,"id"), false);
var probe = collisionWorld.Create(blockType, 32, 200, run: false); probe.Scope.Write("solid",false);
var probeContext = new GmContext(probe);
bool Meets(double x,double y) => collisionWorld.Call("place_meeting", [x,y,blockType], probeContext).Truth;
Expect(!Meets(32,224) && Meets(32,225), "Floor top has exact 32px character contact boundary");
Expect(!Meets(96,128) && Meets(96,129), "Raised platform top contact boundary");
Expect(!Meets(256,32) && Meets(257,32), "Wall contact boundary");
Expect(!Meets(-32,256) && Meets(-31,256), "Room left edge collision boundary");
Expect(!Meets(320,256) && Meets(319,256), "Room right edge collision boundary");
// Preserve missing content through load-save; playback excludes it until the provider returns.
var missing = service.AddEntity(loaded, -1, 128, 64, "missing.vendor.entity"); missing["properties"] = new JsonObject { ["health"] = 777 };
loaded.Dependencies.Add("missing.asset.bundle"); service.Save(loaded, path); var missingLoaded = service.Load(path);
Expect(missingLoaded.Warnings.Count >= 2 && missingLoaded.Document.Instances.Count == 1229, "Missing dependencies warn and retain editable data");
Expect(Int(missingLoaded.Document.Instances.OfType<JsonObject>().Last()["properties"]!.AsObject(), "health") == 777, "Missing dependency props retained");
service.InstallForPlay(missingLoaded.Document); Expect(world.Rooms[installed].GetProperty("instances").GetArrayLength() == 1228, "Missing entity omitted only from playback");
string before = File.ReadAllText(path); var invalid = loaded.Clone(); invalid.FormatVersion = 99;
bool rejected = false; try { service.Save(invalid, path); } catch (InvalidDataException) { rejected = true; }
Expect(rejected && File.ReadAllText(path) == before, "Unsupported version cannot overwrite source");
Expect(Number(new JsonObject { ["number"] = 17 }, "number") == 17, "Integer JSON values remain numeric without serialization");
var malformed = original.Clone(); malformed.Instances[0]!["x"] = "bad"; rejected = false; try { service.Validate(malformed); } catch (InvalidDataException) { rejected = true; } Expect(rejected, "Malformed raw entity edit rejected before saving");
malformed = original.Clone(); malformed.Room.Remove("speed"); rejected = false; try { service.Validate(malformed); } catch (InvalidDataException) { rejected = true; } Expect(rejected, "Missing required room metadata rejected");
service.RestoreAfterPlaytest(); Expect(world.Rooms[service.TestRoomId].GetProperty("instances").GetArrayLength() == 1227, "Original room restored after playtest");
Console.WriteLine($"MAP_DATA_OK assertions={assertions} entities=1227 tiles=1647 output={output}");
File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { assertions, entities = 1227, tiles = 1647, checks = new[] { "lossless-import", "stable-ids", "atomic-unicode-save", "entity-reconstruction", "custom-properties", "collision-boundaries", "missing-dependencies", "version-protection" } }, NativeContentStore.Json));
