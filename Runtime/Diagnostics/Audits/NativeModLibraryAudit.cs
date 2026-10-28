using Godot;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public partial class NativeModLibraryAudit : Node
{
    private int assertions;
    private void Expect(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        assertions++;
    }
    private void Reject(Action action, string label)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { rejected = true; }
        Expect(rejected, label);
    }
    public override void _Ready()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "native-mod-library-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Run(temporary);
            GD.Print("NATIVE_MOD_LIBRARY_AUDIT_OK assertions=", assertions);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("NATIVE_MOD_LIBRARY_AUDIT_FAILED " + error); GetTree().Quit(1); }
        finally { Directory.Delete(temporary, true); }
    }
    private void Run(string temporary)
    {
        var store = new NativeContentStore(Path.Combine(temporary, "user"));
        var registry = new ContentRegistry();
        using var manager = new NativeModManager(registry);
        var library = new NativeModLibrary(store, manager);
        string project = library.CreateProject("audit.base", "Base package");
        Expect(library.Projects().Count == 1 && library.Installed().Count == 0 && manager.LoadedMods.Count == 0, "new project stays separate from installed and active packages");
        Reject(() => library.CreateProject("../escape", "bad"), "project directory traversal rejected");
        Reject(() => library.Export(project, Path.Combine(temporary, "empty.zip")), "empty authoring project cannot be exported as a runnable package");
        var manifest = library.OpenProject(project);
        manifest.Content!.Resources.Add("audit.base.file", "asset.txt");
        manifest.Content.Maps.Add("audit.map", "room.map.json");
        manifest.Content.Characters.Add("audit-character", "hero.character.json");
        manifest.Content.Languages.Add("audit.base.en", "en.json");
        manifest.MapPack = new() { RoomOrder = ["audit.map"], StartRoom = "audit.map" };
        File.WriteAllText(Path.Combine(project, "asset.txt"), "original");
        NativeContentStore.Save(Path.Combine(project, "room.map.json"), new NativeMapDocument {
            Id = "audit.map", Room = new JsonObject { ["width"] = 640, ["height"] = 480, ["instances"] = new JsonArray(), ["tiles"] = new JsonArray() }
        });
        var character = new CharacterDefinition { Id = "audit-character", Parts = [new NativeCharacterPart {
            Id = "body", Width = 1, Height = 1, Frames = [new NativeCharacterFrame { Id = "frame", Png = "frame.png" }]
        }] };
        File.WriteAllBytes(Path.Combine(project, "frame.png"), NativeCharacterPng.Encode(1, 1, [1, 2, 3, 255]));
        NativeContentStore.Save(Path.Combine(project, "hero.character.json"), character);
        NativeContentStore.Save(Path.Combine(project, "en.json"), new Dictionary<string, string> { ["audit.base.name"] = "Base translation" });
        library.SaveProjectManifest(project, manifest);
        string zip = Path.Combine(temporary, "base.zip"); library.Export(project, zip);
        var preview = library.InspectZip(zip);
        Expect(preview.Manifest.Id == "audit.base" && !preview.ContainsCode && !preview.ReplacesInstalled && preview.Files.Count == 7, "ZIP preview returns package metadata and complete relative file list");
        Expect(manager.LoadedMods.Count == 0 && registry.Resources.Count == 0, "preview does not activate content");
        var installed = library.Install(zip);
        Expect(!installed.Replaced && installed.Directory.StartsWith(store.Mods, StringComparison.Ordinal) && library.Installed().Count == 1, "install writes only installed library");
        Reject(() => library.SaveProjectManifest(installed.Directory, manifest), "installed copy cannot be edited through project save");
        Reject(() => library.Install(zip), "same ID replacement requires explicit option");
        Expect(library.InspectZip(zip).ReplacesInstalled && library.InspectZip(zip).InstalledVersion == "1.0.0", "replacement preview identifies installed version");
        Expect(manager.Reload(installed.Directory).Success, "typed content package activates");
        Expect(registry.Maps["audit.map"] == Path.Combine(installed.Directory, "room.map.json") && registry.Characters.ContainsKey("audit-character")
            && registry.CharacterDirectories["audit-character"] == installed.Directory && registry.Languages.ContainsKey("audit.base.en")
            && registry.Translations["en/audit.base.name"] == "Base translation", "maps characters roots languages and translations register their package contributions");
        string clone = library.CloneInstalled(installed.Directory, "working-copy");
        var changed = library.OpenProject(clone); changed.Version = "2.0.0";
        File.WriteAllText(Path.Combine(clone, "asset.txt"), "updated"); library.SaveProjectManifest(clone, changed);
        Expect(File.ReadAllText(registry.Resources["audit.base.file"]) == "original" && library.OpenProject(clone).Id == "audit.base", "cloned project retains stable IDs and cannot mutate installed files");
        string dependent = library.CreateProject("audit.dependent", "Dependent");
        var dependency = library.OpenProject(dependent);
        dependency.Content!.Resources.Add("audit.dependent.file", "asset.txt"); dependency.Dependencies.Add("audit.base", "1.0.0");
        File.WriteAllText(Path.Combine(dependent, "asset.txt"), "dependent"); library.SaveProjectManifest(dependent, dependency);
        Expect(manager.Reload(dependent).Success, "exact-version loaded dependency accepted");
        Expect(!manager.Unload("audit.base").Success, "unload rejects removing a required dependency");
        string update = Path.Combine(temporary, "update.zip"); library.Export(clone, update);
        Reject(() => library.Install(update, true), "incompatible active update rejected");
        Expect(NativeModManifest.Read(Path.Combine(installed.Directory, "mod.json")).Version == "1.0.0"
            && manager.LoadedMods.Single(value => value.Id == "audit.base").Manifest.Version == "1.0.0"
            && File.ReadAllText(registry.Resources["audit.base.file"]) == "original", "failed installed update restores disk and active registry");
        dependency.Dependencies["audit.base"] = "2.0.0"; library.SaveProjectManifest(dependent, dependency);
        var batch = manager.ReloadMany([dependent, clone]);
        Expect(batch.Success && manager.LoadedMods.Count == 2 && File.ReadAllText(registry.Resources["audit.base.file"]) == "updated", "batch dependency validation is independent of input order");
        changed.Version = "3.0.0"; library.SaveProjectManifest(clone, changed);
        Expect(!manager.ReloadMany([clone, dependent]).Success && manager.LoadedMods.Single(value => value.Id == "audit.base").Manifest.Version == "2.0.0", "one incompatible candidate preserves the entire active batch");
        changed.Version = "2.0.0"; changed.Dependencies.Add("audit.dependent", "*"); library.SaveProjectManifest(clone, changed);
        Expect(!manager.ReloadMany([clone, dependent]).Success, "dependency cycle rejected");
        changed.Dependencies.Clear(); changed.Version = "4.0.0"; dependency.Dependencies["audit.base"] = "*";
        library.SaveProjectManifest(clone, changed); library.SaveProjectManifest(dependent, dependency);
        Action observer = () => throw new InvalidOperationException("batch-observer"); registry.Changed += observer;
        try { Expect(!manager.ReloadMany([clone, dependent]).Success, "observer failure rejects whole batch"); }
        finally { registry.Changed -= observer; }
        Expect(manager.LoadedMods.Single(value => value.Id == "audit.base").Manifest.Version == "2.0.0" && registry.Translations.Count == 1, "batch rollback restores old metadata and owned translations");
        changed.Content!.Resources.Remove("audit.base.file"); dependency.Content!.Resources.Add("audit.base.file", "asset.txt");
        library.SaveProjectManifest(clone, changed); library.SaveProjectManifest(dependent, dependency);
        Expect(manager.ReloadMany([dependent, clone]).Success && registry.Resources["audit.base.file"] == Path.Combine(dependent, "asset.txt"),
            "a batch can transfer a resource ID between owners in either input order");
        Expect(!manager.ReloadMany([clone, clone]).Success && registry.Resources["audit.base.file"] == Path.Combine(dependent, "asset.txt")
            && manager.LoadedMods.Count == 2, "duplicate candidates preserve transferred resource ownership");
        Expect(manager.Unload("audit.dependent").Success && !registry.Resources.ContainsKey("audit.base.file") && registry.Maps.ContainsKey("audit.map")
            && registry.Translations.Count == 1, "unloading the new owner removes transferred resources and preserves the old owner's remaining content");
        Expect(manager.Unload("audit.base").Success, "dependency order permits complete unload");
        Expect(registry.Resources.Count + registry.Maps.Count + registry.Characters.Count + registry.CharacterDirectories.Count + registry.Languages.Count + registry.Translations.Count == 0,
            "unload clears every typed contribution");
        VerifyArchives(temporary, zip, library);
        VerifyArtifactCompleteness(temporary, zip, library, store);
        Expect(library.ExportDiagnostics().Contains("audit.base", StringComparison.Ordinal), "diagnostic export includes installed and project metadata");
    }
    private void VerifyArtifactCompleteness(string temporary, string original, NativeModLibrary library, NativeContentStore store)
    {
        byte[] originalZip = File.ReadAllBytes(original);
        foreach (string excluded in new[] { ".git", ".godot" })
        {
            string id = "audit." + excluded[1..];
            string project = library.CreateProject(id, "Excluded content");
            var manifest = library.OpenProject(project);
            manifest.Content!.Resources.Add(id + ".file", excluded + "/asset.txt");
            NativeContentStore.WriteAtomic(Path.Combine(project, excluded, "asset.txt"), [1]);
            library.SaveProjectManifest(project, manifest);
            Reject(() => library.Export(project, original, true), "export rejects a declared file omitted from " + excluded);
            Expect(File.ReadAllBytes(original).SequenceEqual(originalZip), "failed export preserves existing ZIP for " + excluded);
            string archivePath = Path.Combine(temporary, excluded[1..] + ".zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(Path.Combine(project, "mod.json"), "mod.json");
                archive.CreateEntryFromFile(Path.Combine(project, excluded, "asset.txt"), excluded + "/asset.txt");
            }
            var installed = library.Install(archivePath);
            Reject(() => library.CloneInstalled(installed.Directory, "clone-" + excluded[1..]), "clone rejects a declared file omitted from " + excluded);
            Expect(!Directory.Exists(Path.Combine(store.Projects, "clone-" + excluded[1..])), "failed clone leaves no partial project for " + excluded);
            manifest.Content.Resources[id + ".file"] = "asset.txt";
            File.WriteAllText(Path.Combine(project, "asset.txt"), "included"); library.SaveProjectManifest(project, manifest);
            library.Export(project, archivePath, true);
            Expect(library.InspectZip(archivePath).Files.SequenceEqual(new[] { "asset.txt", id + ".csproj", "mod.json" }), "unreferenced " + excluded + " metadata is omitted from a complete export");
        }
    }
    private void VerifyArchives(string temporary, string original, NativeModLibrary library)
    {
        string wrapper = Path.Combine(temporary, "wrapper.zip");
        using (var source = ZipFile.OpenRead(original))
        using (var target = ZipFile.Open(wrapper, ZipArchiveMode.Create))
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open(); using var output = target.CreateEntry("package/" + entry.FullName).Open(); input.CopyTo(output);
            }
        Expect(library.InspectZip(wrapper).Manifest.Id == "audit.base", "single wrapper directory accepted");
        string Bad(string name, params string[] paths)
        {
            string path = Path.Combine(temporary, name + ".zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (string value in paths) { using var writer = new StreamWriter(archive.CreateEntry(value).Open()); writer.Write("{}"); }
            return path;
        }
        Reject(() => library.InspectZip(Bad("traversal", "mod.json", "../escaped.txt")), "ZIP traversal rejected before extraction");
        Reject(() => library.InspectZip(Bad("windows", "mod.json", "C:/escape.txt")), "ZIP drive path rejected");
        Reject(() => library.InspectZip(Bad("duplicate", "mod.json", "asset.txt", "ASSET.txt")), "ZIP case-insensitive duplicate rejected");
        Reject(() => library.InspectZip(Bad("multiple", "mod.json", "nested/mod.json")), "multiple manifest roots rejected");
        Reject(() => library.InspectZip(Bad("outside", "wrapper/mod.json", "outside.txt")), "files outside wrapper rejected");
        Reject(() => library.InspectZip(Bad("deep", "a/b/mod.json")), "deep wrapper rejected");
        string link = Path.Combine(temporary, "link.zip");
        using (var archive = ZipFile.Open(link, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link.txt"); entry.ExternalAttributes = unchecked((int)0xa1ff0000);
        }
        Reject(() => library.InspectZip(link), "ZIP symbolic link rejected");
        Expect(!File.Exists(Path.Combine(temporary, "escaped.txt")) && library.Installed().Count == 1, "invalid archives leave installed library and outside paths untouched");
    }
}
