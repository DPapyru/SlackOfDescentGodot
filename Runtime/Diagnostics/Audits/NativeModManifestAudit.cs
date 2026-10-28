using Godot;
using System.Runtime.Loader;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public partial class NativeModManifestAudit : Node
{
    private int assertions;
    private void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }
    public override void _Ready()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "native-manifest-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            VerifyResources(temporary);
            VerifyCode(ProjectSettings.GlobalizePath("res://"), temporary);
            GD.Print("NATIVE_MOD_MANIFEST_AUDIT_OK assertions=", assertions);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("NATIVE_MOD_MANIFEST_AUDIT_FAILED " + error); GetTree().Quit(1); }
        finally { Directory.Delete(temporary, true); }
    }
    private void VerifyResources(string temporary)
    {
        string references = Path.Combine(temporary, "invalid-references");
        Directory.CreateDirectory(references);
        File.WriteAllBytes(Path.Combine(references, "invalid.dll"), [0, 1, 2]);
        var (directory, manifest) = CreatePack(temporary, "资源 包", "audit.assets", "audit.assets.file", "first resource");
        File.WriteAllBytes(Path.Combine(directory, "cover.png"), NativeCharacterPng.Encode(1, 1, [1, 2, 3, 255]));
        File.WriteAllText(Path.Combine(directory, "replacement.txt"), "replacement resource");
        manifest["cover"] = "cover.png";
        Save(directory, manifest);
        var content = new ContentRegistry();
        using var manager = new NativeModManager(content, references);
        int contexts = AssemblyLoadContext.All.Count(context => context.IsCollectible);
        var loaded = manager.Reload(directory);
        Expect(loaded.Success, "v2 pure resource package loads without C# or compiler references: " + loaded.Diagnostics);
        string originalPath = Path.Combine(directory, "asset.txt");
        Expect(content.Resources["audit.assets.file"] == originalPath, "resource resolves relative to its owning package");
        Expect(AssemblyLoadContext.All.Count(context => context.IsCollectible) == contexts, "pure resources create no assembly context");
        var metadata = manager.LoadedMods.Single().Manifest!;
        Expect(metadata.Id == "audit.assets" && metadata.Name == "资源包" && metadata.Author == "作者"
            && metadata.Description == "纯资源无需 C#" && metadata.Version == "1.0.0" && metadata.Cover == "cover.png", "loaded package exposes its manifest metadata");
        metadata.Name = "external mutation";
        Expect(manager.LoadedMods.Single().Manifest!.Name == "资源包", "metadata snapshot cannot change the active package");
        var (otherDirectory, _) = CreatePack(temporary, "other", "audit.other", "AUDIT.ASSETS.FILE", "second resource");
        var other = manager.Reload(otherDirectory);
        Expect(other.Success && content.Resources.Count == 2, "stable resource IDs use ordinal comparison");
        string otherPath = Path.Combine(otherDirectory, "asset.txt");
        Expect(File.ReadAllText(content.Resources["audit.assets.file"]) == "first resource"
            && content.Resources["AUDIT.ASSETS.FILE"] == otherPath && File.ReadAllText(otherPath) == "second resource", "same filenames resolve independently for each package");
        var (conflictDirectory, _) = CreatePack(temporary, "conflict", "audit.conflict", "audit.assets.file", "conflict");
        var conflict = manager.Reload(conflictDirectory);
        Expect(!conflict.Success && manager.LoadedMods.Count == 2 && content.Resources["audit.assets.file"] == originalPath, "another package cannot silently replace a resource ID");
        void RejectText(string json, string label)
        {
            File.WriteAllText(Path.Combine(directory, "mod.json"), json);
            var failed = manager.Reload(directory);
            Expect(!failed.Success && !string.IsNullOrWhiteSpace(failed.Diagnostics), label + " reports a failure");
            Expect(manager.LoadedMods.Count == 2 && content.Resources.Count == 2 && content.Resources["audit.assets.file"] == originalPath
                && content.Resources["AUDIT.ASSETS.FILE"] == otherPath && manager.LoadedMods.Single(mod => mod.Id == "audit.assets").Manifest!.Version == "1.0.0", label + " preserves both active packages");
        }
        void Reject(Action<JsonObject> change, string label)
        {
            var invalid = manifest.DeepClone().AsObject(); change(invalid); RejectText(invalid.ToJsonString(), label);
        }
        Reject(value => value["formatVersion"] = 99, "unsupported manifest version");
        Reject(value => value["id"] = " ", "missing package ID");
        Reject(value => value["name"] = "", "missing package name");
        Reject(value => value["version"] = "", "missing package version");
        Reject(value => value["content"] = new JsonObject(), "empty package");
        Reject(value => value["content"] = null, "null content declaration");
        Reject(value => value["content"]!["resources"] = null, "null resource declaration");
        Reject(value => value["content"]!["code"] = new JsonObject(), "incomplete code declaration");
        Reject(value => value["content"]!["maps"] = new JsonArray(), "unsupported map declaration");
        Reject(value => value["sources"] = new JsonArray("ignored.cs"), "legacy code fields mixed into v2");
        Reject(value => value["content"]!["resources"]!["audit.assets.file"] = "missing.txt", "missing resource file");
        File.WriteAllText(Path.Combine(temporary, "outside.txt"), "outside package");
        Reject(value => value["content"]!["resources"]!["audit.assets.file"] = "../outside.txt", "resource path traversal");
        Reject(value => value["content"]!["resources"]!["audit.assets.file"] = Path.Combine(temporary, "outside.txt"), "absolute resource path");
        Reject(value => value["content"]!["resources"]!["audit.assets.file"] = null, "null resource path");
        Reject(value => value["content"]!["resources"] = new JsonObject { [" "] = "asset.txt" }, "empty stable resource ID");
        Reject(value => value["cover"] = "../outside.txt", "cover path traversal");
        Reject(value => value["cover"] = "missing.png", "missing cover file");
        string json = manifest.ToJsonString();
        RejectText(json[..^1] + ",\"id\":\"audit.assets\"}", "duplicate manifest field");
        RejectText(json[..^1] + ",\"NAME\":\"ambiguous\"}", "case-insensitive duplicate manifest field");
        RejectText(json.Replace("\"audit.assets.file\":\"asset.txt\"", "\"audit.assets.file\":\"asset.txt\",\"audit.assets.file\":\"replacement.txt\""), "duplicate stable resource ID in JSON");
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(directory, "link.txt"), originalPath);
            Reject(value => value["content"]!["resources"]!["audit.assets.file"] = "link.txt", "resource symlink");
            Directory.CreateSymbolicLink(Path.Combine(directory, "linked-directory"), otherDirectory);
            Reject(value => value["content"]!["resources"]!["audit.assets.file"] = "linked-directory/asset.txt", "resource directory symlink");
        }
        var replacement = manifest.DeepClone().AsObject();
        replacement["version"] = "2.0.0";
        replacement["content"]!["resources"] = new JsonObject { ["audit.assets.updated"] = "replacement.txt" };
        Save(directory, replacement);
        Action observer = () => throw new InvalidOperationException("manifest-observer-failed");
        content.Changed += observer;
        NativeModResult rollback;
        try { rollback = manager.Reload(directory); }
        finally { content.Changed -= observer; }
        Expect(!rollback.Success && rollback.Diagnostics.Contains("manifest-observer-failed") && content.Resources["audit.assets.file"] == originalPath
            && !content.Resources.ContainsKey("audit.assets.updated"), "failed activation restores the previous registry atomically");
        var reloaded = manager.Reload(directory);
        Expect(reloaded.Success && reloaded.PreviousContext is null && !content.Resources.ContainsKey("audit.assets.file")
            && content.Resources["audit.assets.updated"] == Path.Combine(directory, "replacement.txt") && content.Resources["AUDIT.ASSETS.FILE"] == otherPath, "resource reload replaces only its own contributions");
        Expect(manager.LoadedMods.Single(mod => mod.Id == "audit.assets").Manifest!.Version == "2.0.0", "successful reload replaces manifest metadata");
        var unloaded = manager.Unload("audit.assets");
        Expect(unloaded.Success && unloaded.PreviousContext is null && manager.RetiredContexts.Count == 0
            && AssemblyLoadContext.All.Count(context => context.IsCollectible) == contexts, "pure load reload rollback and unload create no assembly context");
        Expect(content.Resources.Count == 1 && content.Resources["AUDIT.ASSETS.FILE"] == otherPath, "unload preserves the other package");
        Expect(manager.Unload("audit.other").Success && content.Resources.Count == 0, "last pure package unload clears its resources");
    }
    private void VerifyCode(string project, string temporary)
    {
        var legacyInfo = new NativeModInfo("legacy", "directory", "diagnostics");
        Expect(legacyInfo.Id == "legacy" && legacyInfo.Directory == "directory" && legacyInfo.Diagnostics == "diagnostics", "original three-argument mod info constructor remains compatible");
        var content = new ContentRegistry();
        using var manager = new NativeModManager(content);
        var legacy = manager.Reload(Path.Combine(project, "Examples", "Mods", "Movement"));
        Expect(legacy.Success && content.Actions.Count == 3 && content.Characters.Count == 2, "unmodified v1 manifest still compiles its source mod: " + legacy.Diagnostics);
        Expect(manager.LoadedMods.Single().Manifest!.FormatVersion == 1, "legacy manifest remains inspectable");
        string legacyDirectory = Path.Combine(temporary, "legacy-extra-fields");
        string legacySource = Path.Combine(project, "Examples", "Mods", "Movement");
        foreach (string file in Directory.EnumerateFiles(legacySource, "*", SearchOption.AllDirectories))
            NativeContentStore.WriteAtomic(Path.Combine(legacyDirectory, Path.GetRelativePath(legacySource, file)), File.ReadAllBytes(file));
        var extendedLegacy = JsonNode.Parse(File.ReadAllText(Path.Combine(legacyDirectory, "mod.json")))!.AsObject();
        extendedLegacy["version"] = 1; extendedLegacy["content"] = "custom"; extendedLegacy["name"] = 17;
        Save(legacyDirectory, extendedLegacy);
        var compatible = manager.Reload(legacyDirectory);
        Expect(compatible.Success && content.Actions.Count == 3 && content.Characters.Count == 2, "v1 keeps ignoring formerly unknown fields with different JSON types: " + compatible.Diagnostics);
        Expect(manager.LoadedMods.Single().Manifest.Name == "example.movement", "v1 display metadata falls back to its ID instead of interpreting legacy extensions");
        var legacyUnload = manager.Unload("example.movement");
        Expect(legacyUnload.Success && legacyUnload.PreviousContext is not null && NativeModManager.VerifyUnloaded(legacyUnload.PreviousContext), "legacy code still releases its collectible context");
        var (directory, manifest) = CreatePack(temporary, "mixed", "audit.mixed", "audit.declared", "declared resource");
        File.WriteAllText(Path.Combine(directory, "code.txt"), "code-owned resource");
        string source = "using System; using System.IO; using SlackOfDescent; public sealed class ManifestAuditMod : IMod { public string Id => \"audit.mixed\"; public void Load(ModContext context) { context.Content.Resources[\"audit.from-code\"] = Path.Combine(context.Directory, \"code.txt\"); } public void Unload() { } }";
        File.WriteAllText(Path.Combine(directory, "Mod.cs"), source);
        manifest["content"]!["code"] = new JsonObject { ["entryType"] = "ManifestAuditMod", ["sources"] = new JsonArray("Mod.cs"), ["managedDependencies"] = new JsonArray() };
        Save(directory, manifest);
        var mixed = manager.Reload(directory);
        Expect(mixed.Success && content.Resources["audit.declared"] == Path.Combine(directory, "asset.txt")
            && content.Resources["audit.from-code"] == Path.Combine(directory, "code.txt"), "v2 code and declarative resource contributions load together: " + mixed.Diagnostics);
        var (otherDirectory, _) = CreatePack(temporary, "mixed-companion", "audit.companion", "audit.companion.file", "companion");
        Expect(manager.Reload(otherDirectory).Success, "pure resource and source packages coexist");
        void RejectSource(string invalid, string label)
        {
            File.WriteAllText(Path.Combine(directory, "Mod.cs"), invalid);
            var failed = manager.Reload(directory);
            Expect(!failed.Success && !string.IsNullOrWhiteSpace(failed.Diagnostics), label + " reports a failure");
            Expect(manager.LoadedMods.Count == 2 && content.Resources.Count == 3 && content.Resources.ContainsKey("audit.declared")
                && content.Resources.ContainsKey("audit.from-code") && content.Resources.ContainsKey("audit.companion.file"), label + " preserves all previous contributions");
        }
        RejectSource(source + "\ninvalid C# !!!", "v2 compile error");
        RejectSource(source.Replace("context.Content.Resources[\"audit.from-code\"] = Path.Combine(context.Directory, \"code.txt\");", "throw new InvalidOperationException(\"candidate-load-failed\");"), "v2 Load failure");
        RejectSource(source.Replace("audit.from-code", "audit.declared"), "code indexer and declarative resource ID collision");
        RejectSource(source.Replace("audit.from-code", "audit.declared").Replace("\"code.txt\"", "\"asset.txt\""), "same-value code and declarative resource ID collision");
        File.WriteAllText(Path.Combine(directory, "Mod.cs"), source);
        var codeOnly = manifest.DeepClone().AsObject(); codeOnly["content"]!.AsObject().Remove("resources");
        Save(directory, codeOnly);
        var reloaded = manager.Reload(directory);
        Expect(reloaded.Success && reloaded.PreviousContext is not null && !content.Resources.ContainsKey("audit.declared")
            && content.Resources.ContainsKey("audit.from-code") && content.Resources.ContainsKey("audit.companion.file"), "v2 source-only reload removes its old declared resources");
        var pure = manifest.DeepClone().AsObject(); pure["content"]!.AsObject().Remove("code"); Save(directory, pure);
        var toPure = manager.Reload(directory);
        Expect(toPure.Success && toPure.PreviousContext is not null && content.Resources.ContainsKey("audit.declared")
            && !content.Resources.ContainsKey("audit.from-code"), "source package can become pure resources and retires its assembly");
        Save(directory, manifest);
        var toCode = manager.Reload(directory);
        Expect(toCode.Success && toCode.PreviousContext is null && content.Resources.ContainsKey("audit.from-code"), "pure package can gain code without inventing a previous assembly");
        var unloaded = manager.Unload("audit.mixed");
        Expect(unloaded.Success && unloaded.PreviousContext is not null && content.Resources.Count == 1
            && content.Resources.ContainsKey("audit.companion.file"), "mixed unload clears both contribution sources and preserves the companion");
        Expect(manager.Unload("audit.companion").PreviousContext is null && content.Resources.Count == 0, "pure companion unload does not return a code context");
        Expect(manager.RetiredContexts.All(context => NativeModManager.VerifyUnloaded(context)), "failed candidates and successful code replacements release all contexts");
    }
    private static (string Directory, JsonObject Manifest) CreatePack(string temporary, string folder, string id, string resourceId, string text)
    {
        string directory = Path.Combine(temporary, folder); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "asset.txt"), text);
        var manifest = new JsonObject {
            ["formatVersion"] = 2, ["id"] = id, ["name"] = "资源包", ["author"] = "作者",
            ["description"] = "纯资源无需 C#", ["version"] = "1.0.0",
            ["content"] = new JsonObject { ["resources"] = new JsonObject { [resourceId] = "asset.txt" } }
        };
        Save(directory, manifest); return (directory, manifest);
    }
    private static void Save(string directory, JsonObject manifest) => NativeContentStore.Save(Path.Combine(directory, "mod.json"), manifest);
}
