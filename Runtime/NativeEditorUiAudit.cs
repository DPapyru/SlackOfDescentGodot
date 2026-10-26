using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeEditorUiAudit : Node
{
    public override async void _Ready()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "native-editor-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            string root = ProjectSettings.GlobalizePath("res://GameData"); var store = new NativeContentStore(temporary); NativeLocalization.Initialize(root, temporary);
            var world = new GmWorld(root, Path.Combine(root, "assets"), temporary); var registry = new ContentRegistry();
            registry.RegisterEntity(new NativeEntityDefinition { Id = "audit.editor.entity", Name = "Audit entity", Parameters = new() { ["health"] = new() { Name = "Health", Type = "number", Default = "75", Min = 1, Max = 100 } } }, () => new AuditEntity());
            using var manager = new NativeModManager(registry);
            var map = new NativeMapEditor(world, registry, store) { Visible = false, ForceNative = true }; AddChild(map); map.PopupCentered();
            var mod = new NativeModPanel(manager, store, Path.Combine(temporary, "examples"), registry) { Visible = false, ForceNative = true }; AddChild(mod); mod.PopupCentered();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            int checks = map.AuditEditorFeatures(temporary) + mod.AuditEditorFeatures(temporary);
            mod.QueueFree(); map.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GD.Print("NATIVE_EDITOR_UI_AUDIT_OK checks=", checks); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("NATIVE_EDITOR_UI_AUDIT_FAILED " + error); GetTree().Quit(1); }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    private sealed class AuditEntity : IEntityBehavior
    {
        public string Id => "audit.editor.entity";
        public void Update(ActorContext entity, FrameContext frame) { }
    }
}

public partial class NativeMapEditor
{
    internal int AuditEditorFeatures(string temporary)
    {
        int checks = 0; void Expect(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Map editor: " + message); }
        registry.Translations["zh_CN/audit.editor.entity.name"] = "审计实体"; registry.Translations["en/audit.editor.entity.name"] = "Audit entity";
        registry.Translations["zh_CN/audit.editor.entity.health.name"] = "生命值"; registry.Translations["en/audit.editor.entity.health.name"] = "Health";
        registry.Translations["zh_CN/audit.editor.entity.health.description"] = "实体生命值"; registry.Translations["en/audit.editor.entity.health.description"] = "Entity health";
        NativeLocalization.SetLanguage("zh_CN", false);
        Expect(ContentName("audit.editor.entity", "Fallback") == "审计实体" && ContentMatches("audit.editor.entity", "", "Audit"), "localized display and English search");
        registry.Translations["en/audit.english.name"] = "English fallback";
        Expect(ContentName("audit.english", "Fallback") == "English fallback" && ContentName("missing", "Default") == "Default" && ContentName("missing") == "missing", "name fallback order");
        PopulateEntities("碰撞方块"); Expect(entities.ItemCount > 0 && Enumerable.Range(0, entities.ItemCount).Any(i => entities.GetItemMetadata(i).AsString() == "object:" + world.Constants["objBlock"].Int), "Chinese search preserves original object ID");
        PopulateEntities("audit.editor.entity"); Expect(entities.ItemCount == 1 && entities.GetItemText(0).Contains("审计实体", StringComparison.Ordinal), "ID search and Mod translation");
        Expect(atlas.Atlas is not null, "atlas texture loaded");
        sourceX.Value = sourceY.Value = 0; tileWidth.Value = tileHeight.Value = 32;
        atlas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = new(35, 67) });
        Expect(sourceX.Value == 32 && sourceY.Value == 64 && MapCanvas.Tool == NativeMapTool.Tile, "atlas click selects tile source");
        atlas._GuiInput(new InputEventMouseMotion { Position = new(99, 99), ButtonMask = MouseButtonMask.Left });
        atlas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = new(99, 99) });
        Expect(tileWidth.Value == 96 && tileHeight.Value == 64, "atlas drag keeps initial cell dimensions");
        var document = Document.Clone(); document.Instances.Clear(); document.Tiles.Clear(); SetDocument(document, null);
        var entity = Service.AddEntity(Document, -1, 0, 0, "audit.editor.entity"); ApplyEntityDefaults(entity); Select(entity, false);
        Expect(Number(entity["properties"]!.AsObject(), "health") == 75, "schema default applied");
        var parameterLabel = propertyForm.GetChild(0).GetChild<Label>(0); Expect(parameterLabel.Text == "生命值" && parameterLabel.TooltipText == "health\n实体生命值", "parameter label and description use Chinese translations with stable key");
        var spin = propertyForm.GetChild(0).GetChild<SpinBox>(1); Expect(spin.MinValue == 1 && spin.MaxValue == 100, "schema range in form"); spin.Value = 90;
        Expect(Number(entity["properties"]!.AsObject(), "health") == 90 && Dirty, "typed form updates stable property key");
        OpenEventEditor(); var eventDialog = GetNode<ConfirmationDialog>("MapEventEditor");
        ((Button)eventDialog.FindChild("AddMapEvent", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
        ((Button)eventDialog.FindChild("AddEventAction", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
        eventDialog.Hide();
        eventDialog.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        Expect(Document.Events.Count == 1 && Document.Events[0].Actions.Count == 1, "event form applies typed event and action");
        string eventId = Document.Events[0].Id; string path = Path.Combine(temporary, "edited.map.json"); Service.Save(Document, path); var loaded = Service.Load(path).Document;
        Expect(loaded.Events[0].Id == eventId && NativeMapEntityIdentity.Of(loaded.Instances[0]!.AsObject()) == NativeMapEntityIdentity.Of(entity), "events and logical entity IDs round trip");
        string installed = Path.Combine(store.Mods, "audit.maps", "room.map.json"); NativeContentStore.Save(installed, loaded); registry.Maps[loaded.Id] = installed; LoadPath(installed);
        Expect(FilePath is null, "installed map opens as editable copy");
        string before = File.ReadAllText(installed); SavePath(installed, true); Expect(File.ReadAllText(installed) == before && error.Visible, "installed source cannot be overwritten"); error.Hide();
        OpenMapLibrary(); Expect(GetNode<ConfirmationDialog>("MapLibraryDialog").FindChild("MapLibraryTree", true, false) is Tree, "room directory tree available");
        NativeLocalization.SetLanguage("en", false); Expect(ContentName("audit.editor.entity") == "Audit entity", "English display uses the same stable ID");
        Select(Document.Instances[0]!.AsObject(), false); parameterLabel = propertyForm.GetChild(0).GetChild<Label>(0); Expect(parameterLabel.Text == "Health" && parameterLabel.TooltipText == "health\nEntity health", "parameter label and description follow English with stable key");
        return checks;
    }
}

public partial class NativeModPanel
{
    internal int AuditEditorFeatures(string temporary)
    {
        int checks = 0; void Expect(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Mod editor: " + message); }
        Expect(Title == "Mod" && tabs.GetTabCount() == 2, "fixed Mod window and two pages");
        string project = library.CreateProject("audit.ui", "UI project"); OpenProject(project);
        metadataFields["Name"].Text = "Edited project"; metadataFields["Name"].EmitSignal(LineEdit.SignalName.TextChanged, "Edited project");
        metadataFields["Author"].Text = "Audit author"; metadataFields["Author"].EmitSignal(LineEdit.SignalName.TextChanged, "Audit author"); SaveManifest();
        Expect(library.OpenProject(project).Name == "Edited project" && library.OpenProject(project).Author == "Audit author", "metadata form saves manifest");
        File.WriteAllText(Path.Combine(project, "asset.txt"), "first"); MutateManifest(manifest => manifest.Content!.Resources["audit.ui.resource"] = "asset.txt"); SaveManifest(); RefreshProjectFiles();
        OpenProjectFile("asset.txt"); fileContents.Text = "edited"; SaveProjectFile(); Expect(File.ReadAllText(Path.Combine(project, "asset.txt")) == "edited", "project file editor saves text");
        string zip = Path.Combine(temporary, "ui.zip"); ExportProject(zip); Expect(File.Exists(zip), "project ZIP export");
        ImportArchive(zip); Expect(confirmation.Visible && confirmation.DialogText.Contains("audit.ui", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(store.Mods, "audit.ui")), "import previews metadata before installation");
        confirmation.EmitSignal(ConfirmationDialog.SignalName.Confirmed); confirmation.Hide(); Expect(Directory.Exists(Path.Combine(store.Mods, "audit.ui")) && list.ItemCount == 1, "confirmed import refreshes Mod list");
        ReloadInstalled(); Expect(manager.LoadedMods.Any(x => x.Id == "audit.ui"), "enable installed package");
        UnloadSelected(); Expect(manager.LoadedMods.Count == 0 && details.Text.Contains("audit.ui", StringComparison.Ordinal), "disable and keep ID in details");
        string report = library.ExportDiagnostics(); Expect(report.Contains("audit.ui", StringComparison.Ordinal), "diagnostics include package");
        return checks;
    }
}
