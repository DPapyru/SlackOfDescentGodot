using Godot;

namespace SlackOfDescent;

public partial class NativeCharacterAuditRunner
{
    private async Task<int> AuditRuntimeAsync(GmWorld live, ContentRegistry registry, NativeContentStore store, string output)
    {
        int checks = 0; void Expect(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Character runtime: " + message); }
        var repository = new NativeCharacterRepository(store);
        var document = NativeCharacterDocument.Create(); document.Definition.Id = "audit.character";
        var head = document.Definition.Parts[0]; head.Target = "HEAD"; head.ReplaceOriginal = true;
        byte[] rgba = [255, 30, 40, 255, 11, 22, 33, 0, 40, 255, 80, 128, 90, 80, 255, 255];
        document.ImportFrames(head.Id, [new(2, 2, rgba), new(2, 2, rgba.Reverse().ToArray())]);
        head.AnchorX = 1; head.AnchorY = 1;
        var decoration = document.Definition.Parts[1]; decoration.Target = "HAND2_ABOVE"; decoration.OffsetX = 3; decoration.OffsetY = -2;
        document.ImportFrames(decoration.Id, [new(2, 2, rgba)]);
        document.Definition.Parts[2].Visible = false;
        Expect(document.Pixels(head, 0).SequenceEqual(rgba) && head.Frames.Count == 2, "PNG images become independently editable animation frames");
        var library = new NativeModLibrary(store); string author = library.CreateProject("audit.character.package", "Character migration");
        var manifest = library.OpenProject(author); manifest.Content!.Characters[document.Definition.Id] = repository.SavePackage(document, author); library.SaveProjectManifest(author, manifest);
        var original = NativeContentStore.Load<CharacterDefinition>(Path.Combine(author, manifest.Content.Characters[document.Definition.Id]));
        Expect(original.Parts.SelectMany(p => p.Frames).All(f => !Path.IsPathRooted(f.Png) && f.Png.StartsWith("characters/", StringComparison.Ordinal)), "project frames use package relative paths");
        var anotherProject = library.CreateProject("audit.character.copy", "Another project");
        var fromAuthor = repository.LoadDefinition(original, author);
        string copied = repository.SavePackage(fromAuthor, anotherProject);
        Expect(repository.LoadDefinition(NativeContentStore.Load<CharacterDefinition>(Path.Combine(anotherProject, copied)), anotherProject).Pixels(head.Frames[0].Id).SequenceEqual(rgba), "copy from another project resolves its own root");
        string zip = Path.Combine(output, "character-migration.zip"); library.Export(author, zip); Directory.Delete(author, true);
        var installed = library.Install(zip); var installedManifest = NativeModManifest.Read(Path.Combine(installed.Directory, "mod.json"));
        var installedDefinition = NativeContentStore.Load<CharacterDefinition>(Path.Combine(installed.Directory, installedManifest.Content!.Characters[document.Definition.Id]));
        var migrated = repository.LoadDefinition(installedDefinition, installed.Directory);
        Expect(migrated.MissingFrames.Count == 0 && migrated.Pixels(head.Frames[0].Id).SequenceEqual(rgba), "ZIP retains transparent RGB after author directory is removed");
        registry.Characters[installedDefinition.Id] = installedDefinition; registry.CharacterDirectories[installedDefinition.Id] = installed.Directory;
        live.Globals.Write("character_preview_sentinel", 93271); live.Engine.Write("character_preview_sentinel", 8123);
        int liveRoom = live.CurrentRoom, liveNext = live.NextId; long liveFrame = live.Frame; string? dataRoot = registry.ModDataRoot;
        string sentinel = Path.Combine(live.UserRoot, "preview-sentinel.txt"); File.WriteAllText(sentinel, "unchanged");
        string[] liveFiles = Directory.GetFiles(live.UserRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            var previewOwner = new Node(); AddChild(previewOwner);
            var preview = new NativeCharacterPreview(previewOwner, live, registry, repository);
            string temporary = preview.TemporaryDirectory;
            try
            {
                await preview.InitializeAsync(); await preview.ApplyAsync(installedDefinition);
                Expect(preview.Player is not null && preview.Bridge.BoundCharacter(preview.Player.Id)?.Id == installedDefinition.Id, "independent world binds the complete character");
                for (int step = 0; step < (cycle == 0 ? 160 : 6); step++) await preview.AdvanceAsync(new(false, false, false, false));
                if (cycle == 0)
                {
                    var actor = preview.Player!;
                    var bone = preview.World.Instances.Single(i => !i.Destroyed && i.Id == actor.Scope.ReadOr("HEAD").Int);
                    preview.World.Write(actor, "Direction", -1); preview.World.Write(bone, "Direction", -1); preview.World.Write(bone, "ScaleX", 1.25); preview.World.Write(bone, "ScaleY", 0.75); preview.World.Write(bone, "image_angle", 31);
                    preview.Graphics.Build();
                    var replaced = preview.Rendering.Audit.Where(a => a.Target == "HEAD").ToArray();
                    Expect(replaced.Length == 1 && replaced[0].Scale.X < 0 && replaced[0].Position.IsEqualApprox(new((float)bone.Scope.ReadOr("x").Number, (float)bone.Scope.ReadOr("y").Number)), "one head replaces original draws at the real bone with facing");
                    Expect(Math.Abs(replaced[0].Angle - Mathf.DegToRad(31)) < 0.001 && Math.Abs(replaced[0].Scale.Y - 0.75) < 0.001, "replacement inherits original rotation and nonuniform scale");
                    Expect(preview.Rendering.Audit.Any(a => a.Target == "HAND2_ABOVE"), "decoration attaches to the selected limb");
                    Expect(preview.Rendering.Audit.All(a => a.Target != "BODY_TOP"), "unassigned body retains original rendering");
                    var jumps = new List<double>(); preview.World.CharacterJumped += item => { if (item == actor) jumps.Add(item.Scope.ReadOr("vspeed").Number); };
                    for (int phase = 0; phase < 2; phase++)
                    {
                        preview.Clock.RoomChanged(); if (phase == 1) preview.Clock.LegacyDue(preview.World.Engine.Read("room_speed").Number);
                        await preview.AdvanceAsync(new(false, false, true, false));
                        Expect(jumps.Count == phase + 1 && jumps[^1] < 0, "one jump press at fixed clock phase " + phase);
                        int offset = preview.World.Globals.ReadOr("controllMode").Equals(preview.World.Constants["CTRL_KEY"]) ? 12 : 0;
                        int jump = preview.World.Globals.Read("key", [offset + 5]).Int;
                        Expect(!preview.Input.TakeNativeInput().Held.Contains(jump), "jump is released at fixed clock phase " + phase);
                    }
                    double before = actor.Scope.ReadOr("x").Number; for (int step = 0; step < 12; step++) await preview.AdvanceAsync(new(false, true, false, false));
                    Expect(actor.Scope.ReadOr("x").Number != before, "preview controls execute original movement");
                    string firstFrame = preview.Rendering.Audit.Single(a => a.Target == "HEAD").Path;
                    for (int step = 0; step < 8 && preview.Rendering.Audit.Single(a => a.Target == "HEAD").Path == firstFrame; step++) { preview.Clock.BeginTick(); preview.Graphics.Build(); }
                    Expect(preview.Rendering.Audit.Single(a => a.Target == "HEAD").Path != firstFrame, "replacement animation advances using the preview clock");
                    int surface = preview.Graphics.Call("surface_create", [8, 8], new(actor))!.Value.Int;
                    preview.Rendering.Decorations(new());
                    try
                    {
                        preview.Graphics.Call("surface_set_target", [surface], new(actor));
                        preview.World.Call("draw_sprite_blur", [preview.World.Manifest.GetProperty("sprites")[0].GetProperty("id").GetInt32(), 0, bone.Scope.ReadOr("x"), bone.Scope.ReadOr("y"), 1, 1, 0, 16777215, 1, 5, 0, 2], new(bone));
                        Expect(preview.Rendering.Audit.All(a => a.Target != "HEAD"), "offscreen original sprite processing skips replacement hooks");
                    }
                    finally { preview.Graphics.Call("surface_reset_target", [], new(actor)); preview.Graphics.Call("surface_free", [surface], new(actor)); }
                    var varied = repository.LoadDefinition(installedDefinition, installed.Directory).Definition;
                    await preview.ApplyAsync(varied); for (int step = 0; step < 6; step++) await preview.AdvanceAsync(new(false, false, false, false));
                    preview.Graphics.SpriteAudit = new();
                    var replacementPaths = varied.Parts[0].Frames.Select(f => repository.FramePath(f, installed.Directory)).ToHashSet(StringComparer.Ordinal);
                    foreach (string target in NativeCharacterTargets.Names.Where(t => t != "TAIL"))
                    {
                        varied.Parts[0].Target = target; varied.Parts[0].Visible = false; preview.Graphics.Build(); int originalDraws = preview.Graphics.SpriteAudit.Count;
                        varied.Parts[0].Visible = true; preview.Graphics.Build();
                        Expect(preview.Rendering.Audit.Count(a => a.Target == target && replacementPaths.Contains(a.Path)) == 1 && preview.Graphics.SpriteAudit.Count < originalDraws, "replaces only the selected original body region: " + target);
                    }
                    varied.BasePreset = "Ryoko"; varied.Appearance = "Character-Ryoko"; varied.Parts[0].Target = "TAIL";
                    await preview.ApplyAsync(varied); for (int step = 0; step < 6; step++) await preview.AdvanceAsync(new(false, false, false, false));
                    Expect(preview.Player!.Scope.ReadOr("NEW_TAIL").Int >= 100000 && preview.Rendering.Audit.Count(a => a.Target == "TAIL") == 1, "tail replacement uses the original Ryoko tail bone");
                    Vector2 tailPosition = preview.Rendering.Audit.Single(a => a.Target == "TAIL").Position;
                    varied.Parts[0].ReplaceOriginal = false; preview.Graphics.Build();
                    Expect(preview.Rendering.Audit.Single(a => a.Target == "TAIL").Position.IsEqualApprox(tailPosition), "tail decoration uses the original attachment transform");
                    preview.Registry.Refresh(); preview.Graphics.Build();
                    Expect(ReferenceEquals(preview.Bridge.BoundCharacter(preview.Player!.Id), varied) && preview.Rendering.Audit.Count(a => a.Target == "TAIL") == 1, "unsaved preview definition survives an unrelated registry refresh");
                    var reloadLibrary = new NativeModLibrary(new NativeContentStore(preview.TemporaryDirectory));
                    string reloadPackage = reloadLibrary.CreateProject("audit.character.reload", "Reload character");
                    var reloadDocument = repository.LoadDefinition(installedDefinition, installed.Directory); reloadDocument.Definition.Id = "audit.character.reload";
                    reloadDocument.Definition.Parts[0].Target = "HEAD";
                    foreach (var part in reloadDocument.Definition.Parts.Skip(1)) part.Visible = false;
                    var reloadManifest = reloadLibrary.OpenProject(reloadPackage);
                    reloadManifest.Content!.Characters[reloadDocument.Definition.Id] = repository.SavePackage(reloadDocument, reloadPackage); reloadLibrary.SaveProjectManifest(reloadPackage, reloadManifest);
                    using var reloadManager = new NativeModManager(preview.Registry);
                    var reloadResult = reloadManager.Reload(reloadPackage);
                    Expect(reloadResult.Success, "character package binds before hot reload: " + reloadResult.Diagnostics);
                    await preview.ApplyAsync(preview.Registry.Characters[reloadDocument.Definition.Id]);
                    for (int step = 0; step < 6; step++) await preview.AdvanceAsync(new(false, false, false, false));
                    var previousFrames = preview.Rendering.Audit.Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
                    reloadDocument.Definition.Parts[0].Target = "BODY_TOP"; reloadDocument.Definition.Parts[0].Layer = 77; reloadDocument.Definition.Parts[0].ReplaceOriginal = false;
                    repository.SavePackage(reloadDocument, reloadPackage); reloadResult = reloadManager.Reload(reloadPackage); preview.Graphics.Build();
                    Expect(reloadResult.Success && preview.Rendering.Audit.Count == 1 && preview.Rendering.Audit[0].Target == "BODY_TOP" && !previousFrames.Contains(preview.Rendering.Audit[0].Path), "successful character reload replaces bound frame paths and body target");
                    var reloadDraws = new List<GmGraphics.DepthDraw>(); preview.Rendering.Decorations(reloadDraws);
                    var reloadBone = preview.World.Instances.Single(i => !i.Destroyed && i.Id == preview.Player!.Scope.ReadOr("BODY_TOP").Int);
                    Expect(reloadDraws.Count == 1 && Math.Abs(reloadDraws[0].Depth - (reloadBone.Scope.ReadOr("depth").Number - 77 - 0.01)) < 0.001, "successful character reload draws using the new layer");
                    string reloadManifestPath = Path.Combine(reloadPackage, "mod.json"), validManifest = File.ReadAllText(reloadManifestPath);
                    string committedFrame = preview.Rendering.Audit[0].Path;
                    File.WriteAllText(reloadManifestPath, "invalid candidate"); reloadResult = reloadManager.Reload(reloadPackage); preview.Graphics.Build();
                    Expect(!reloadResult.Success && preview.Rendering.Audit.Single().Path == committedFrame && preview.Rendering.Audit[0].Target == "BODY_TOP", "failed character candidate keeps the committed visual binding");
                    File.WriteAllText(reloadManifestPath, validManifest);
                    Expect(reloadManager.Unload(reloadDocument.Definition.Id).Success, "character package unload succeeds"); preview.Graphics.Build();
                    Expect(preview.Bridge.BoundCharacter(preview.Player!.Id) is null && preview.Rendering.Audit.Count == 0, "unloaded character stops drawing old external frames");
                }
                Expect(live.CurrentRoom == liveRoom && live.Frame == liveFrame && live.NextId == liveNext && live.Globals.ReadOr("character_preview_sentinel").Int == 93271 && live.Engine.ReadOr("character_preview_sentinel").Int == 8123, "live world and global state remain unchanged, cycle " + cycle);
                Expect(registry.ModDataRoot == dataRoot && File.ReadAllText(sentinel) == "unchanged" && Directory.GetFiles(live.UserRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).SequenceEqual(liveFiles), "live storage and registry remain unchanged, cycle " + cycle);
            }
            finally { preview.Dispose(); preview.Dispose(); previewOwner.QueueFree(); }
            Expect(!Directory.Exists(temporary), "preview storage is removed after repeated disposal, cycle " + cycle);
            Expect(System.Text.Json.JsonSerializer.SerializeToElement(preview.Graphics.Statistics()).GetProperty("textures").GetInt32() == 0, "preview releases cached textures on disposal, cycle " + cycle);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return checks;
    }
    private async Task<int> AuditCanvasContractsAsync(GmWorld live, ContentRegistry registry, NativeContentStore store)
    {
        int checks = 0;
        void Expect(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Character canvas: " + message); }
        var editor = new NativeCharacterEditor(live, registry, store); AddChild(editor); editor.PopupCentered();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var document = editor.Document;
        var canvas = editor.FindChild("像素画布", true, false) as NativeCharacterPixelCanvas;
        Expect(canvas is not null, "the editor exposes its pixel canvas");
        var workspace = editor.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>().FirstOrDefault(tab => tab.GetTabCount() > 1 && tab.GetTabTitle(1).Contains("部位"));
        Expect(workspace is not null, "the editor exposes the part-pixel tab");
        workspace!.CurrentTab = 1;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        canvas!.Mode = NativeCharacterCanvasMode.Part;
        canvas.Selection = new NativePixelRect(1, 2, 3, 2);
        canvas.Mode = NativeCharacterCanvasMode.Overall;
        Expect(canvas.Selection is null, "switching to the overall canvas drops the part-space selection, it kept " + canvas.Selection);
        canvas.Selection = new NativePixelRect(4, 5, 1, 1);
        canvas.Mode = NativeCharacterCanvasMode.Overall;
        Expect(canvas.Selection is { } kept && kept.X == 4 && kept.Y == 5 && kept.Width == 1 && kept.Height == 1, "assigning the mode that is already active is a no-op and keeps the selection, it kept " + canvas.Selection);
        canvas.Mode = NativeCharacterCanvasMode.Part;
        Expect(canvas.Selection is null, "switching back to the part canvas drops the overall-space selection, it kept " + canvas.Selection);
        int zoomEvents = 0; canvas.ZoomChanged += () => zoomEvents++;
        canvas.Zoom = 1;
        Expect(canvas.Zoom == 2, "a zoom below the minimum clamps to 2, got " + canvas.Zoom);
        canvas.Zoom = 99;
        Expect(canvas.Zoom == 24, "a zoom above the maximum clamps to 24, got " + canvas.Zoom);
        canvas.Zoom = 10; int reported = zoomEvents; canvas.Zoom = 10;
        Expect(zoomEvents == reported, "assigning the zoom that is already active raises no change event, it raised " + (zoomEvents - reported));
        canvas.Zoom = 11;
        Expect(canvas.Zoom == 11 && zoomEvents == reported + 1, "a real zoom change is reported exactly once, got zoom " + canvas.Zoom + " and " + (zoomEvents - reported) + " events");
        var part = document.Definition.Parts[0];
        Expect(Math.Abs(canvas.CustomMinimumSize.X - part.Width * canvas.Zoom) < .01f && Math.Abs(canvas.CustomMinimumSize.Y - part.Height * canvas.Zoom) < .01f, "the part canvas is sized to the pixel grid at the current zoom, got " + canvas.CustomMinimumSize + " for " + part.Width + "x" + part.Height + " at " + canvas.Zoom);
        canvas.Mode = NativeCharacterCanvasMode.Overall;
        var scroll = canvas.GetParent() as ScrollContainer;
        Expect(scroll is not null, "the pixel canvas lives inside the scroll area");
        var area = scroll!.GetRect().Size;
        float spaceX = area.X - 16, spaceY = area.Y - 16;
        canvas.FitZoom(spaceX, spaceY);
        var bounds = NativeCharacterLayout.Bounds(document.Definition);
        float fitted = canvas.Zoom;
        Expect(bounds.Width * fitted <= spaceX + .01f && bounds.Height * fitted <= spaceY + .01f, "fitting the overall canvas keeps the whole character inside the scroll area, got " + bounds + " at " + fitted + " in " + area);
        Expect(fitted is 12 or 2 || bounds.Width * (fitted + 1) > spaceX || bounds.Height * (fitted + 1) > spaceY, "the fitted zoom is the largest whole zoom that still fits, got " + fitted + " for " + bounds + " in " + area);
        editor.SetAutoFitForAudit(true);
        var saved = editor.Size;
        editor.Size = new Vector2I(saved.X + 140, saved.Y + 140);
        for (int frame = 0; frame < 8; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var grown = scroll.GetRect().Size;
        Expect(grown.X > area.X + .5f, "resizing the editor really grows the scroll area, it stayed " + area + " -> " + grown);
        Expect(canvas.Zoom != fitted, "resizing with auto fit on refits the overall canvas, zoom stayed " + canvas.Zoom + " after " + area + " -> " + grown);
        Expect(NativeCharacterLayout.Bounds(document.Definition).Width * canvas.Zoom <= grown.X - 16 + .01f && NativeCharacterLayout.Bounds(document.Definition).Height * canvas.Zoom <= grown.Y - 16 + .01f, "the refitted zoom still keeps the whole character inside the new area, got " + NativeCharacterLayout.Bounds(document.Definition) + " at " + canvas.Zoom + " in " + grown);
        editor.SetAutoFitForAudit(false);
        float pinned = canvas.Zoom;
        editor.Size = saved;
        for (int frame = 0; frame < 8; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Expect(canvas.Zoom == pinned, "with auto fit switched off a resize leaves the zoom alone, it moved from " + pinned + " to " + canvas.Zoom);
        var composite = editor.FindChildren("*", "Control", true, false).OfType<NativeCharacterComposite>().FirstOrDefault();
        Expect(composite is not null, "the editor exposes its composite preview");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        float scale = composite!.PaintedScale;
        var painted = NativeCharacterLayout.Bounds(document.Definition);
        int visible = document.Definition.Parts.Count(item => item.Visible);
        Expect(scale >= 1 && Math.Abs(scale - MathF.Floor(scale)) < .0001f, "the composite scales the character by whole pixels, got " + scale);
        Expect(painted.Width * scale <= composite.Size.X + .01f && painted.Height * scale <= composite.Size.Y + .01f, "the composite keeps the whole character inside its own box, got " + painted + " at " + scale + " in " + composite.Size);
        Expect(composite.PaintedParts == visible, "the composite paints every visible part once, got " + composite.PaintedParts + " of " + visible);
        Expect(composite.ClipContents, "the composite clips drawing to its own box so an oversized character cannot bleed over the editor");
        GD.Print("PLAY_AUDIT canvas-contract zoom=", fitted, " refit=", canvas.Zoom, " area=", area, " grown=", grown, " scale=", scale, " parts=", composite.PaintedParts);
        editor.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        return checks;
    }
}
