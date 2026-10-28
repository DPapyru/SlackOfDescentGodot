using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeCharacterEditor
{
    private void LoadAssets()
    {
        string path = Path.Combine(world.Root, "media.json"); if (!File.Exists(path)) return;
        var media = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(path))!;
        foreach (var pair in media.Where(p => p.Key.StartsWith("Chara/Doll/", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var frames = pair.Value.GetProperty("frames");
            if (frames.GetArrayLength() is < 1 or > 256 || frames.EnumerateArray().Any(f => f.GetProperty("width").GetInt32() > 256 || f.GetProperty("height").GetInt32() > 256)) continue;
            assets.Add((pair.Key, pair.Value));
        }
    }
    private void PopulateAssets(string query)
    {
        originalAssets.Clear();
        for (int i = 0; i < assets.Count; i++)
        {
            var asset = assets[i]; if (!NativeContentLocalization.Matches(registry, asset.Name, asset.Name, query)) continue;
            int index = originalAssets.AddItem(NativeContentLocalization.Display(registry, asset.Name, asset.Name)); originalAssets.SetItemMetadata(index, i); originalAssets.SetItemTooltip(index, asset.Name);
        }
    }
    private static NativePixelClipboard ReadPng(string path, bool original = false)
    {
        using var image = Image.LoadFromFile(path);
        if (image is null || image.IsEmpty()) throw new InvalidDataException("无法读取 PNG：" + path);
        if (image.GetWidth() is < 1 or > 256 || image.GetHeight() is < 1 or > 256) throw new InvalidDataException("PNG 尺寸必须为 1–256。");
        image.Convert(Image.Format.Rgba8); var data = image.GetData();
        if (original)
        {
            int offset = (image.GetHeight() - 1) * image.GetWidth() * 4;
            byte r = data[offset], g = data[offset + 1], b = data[offset + 2];
            for (int i = 0; i < data.Length; i += 4) if (data[i] == r && data[i + 1] == g && data[i + 2] == b) data[i + 3] = 0;
        }
        return new(image.GetWidth(), image.GetHeight(), data);
    }
    private void ImportPng(bool append)
    {
        if (CurrentPart is null) { ShowMessage(L("请先添加部位。", "Add a part first.")); return; }
        var dialog = new FileDialog { Name = "ImportCharacterPng", FileMode = FileDialog.FileModeEnum.OpenFiles, Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.png ; PNG"], Title = L("导入 PNG 动画帧（按文件名顺序）", "Import PNG frames (filename order)"), Size = new(800, 550) }; AddChild(dialog);
        dialog.FilesSelected += paths =>
        {
            try
            {
                var images = paths.Order(StringComparer.Ordinal).Select(p => ReadPng(p)).ToArray();
                Document.ImportFrames(CurrentPart!.Id, images, append); selectedFrame = append ? CurrentPart.Frames.Count - images.Length : 0; Refresh(); OnPixelsEdited();
            }
            catch (Exception error) { ShowMessage(error.Message); }
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
    private void ImportOriginal()
    {
        if (CurrentPart is null || originalAssets.GetSelectedItems().Length == 0) return;
        try
        {
            var asset = assets[originalAssets.GetItemMetadata(originalAssets.GetSelectedItems()[0]).AsInt32()];
            var images = asset.Data.GetProperty("frames").EnumerateArray().Select(f => ReadPng(Path.Combine(world.Root, f.GetProperty("path").GetString()!), true)).ToArray();
            int x = asset.Data.GetProperty("originX").GetInt32(), y = asset.Data.GetProperty("originY").GetInt32();
            if (world.Code.TryGetValue("external/" + Path.ChangeExtension(asset.Name, ".txt"), out var metadata))
                foreach (var node in metadata[1].EnumerateArray())
                    if (node.GetArrayLength() > 3 && node[0].GetString() == "binary" && node[1].GetString() == "Assign" && node[2][0].GetString() == "id" && node[3][0].GetString() == "num")
                    {
                        if (node[2][1].GetString() == "partx") x = (int)node[3][1].GetDouble();
                        if (node[2][1].GetString() == "party") y = (int)node[3][1].GetDouble();
                    }
            Document.ImportFrames(CurrentPart.Id, images, anchorX: x, anchorY: y); selectedFrame = 0; Refresh(); OnPixelsEdited();
        }
        catch (Exception error) { ShowMessage(error.Message); }
    }
    private void SaveToProject()
    {
        var library = new NativeModLibrary(repository.Store); var projects = library.Projects();
        if (projects.Count == 0) { ShowMessage(L("请先在 Mod 窗口创建项目。", "Create a project in the Mod window first.")); return; }
        var dialog = new ConfirmationDialog { Name = "SaveCharacterProject", Title = L("保存到 Mod 项目", "Save to Mod project"), Size = new(520, 180) }; AddChild(dialog);
        var choice = new OptionButton { CustomMinimumSize = new(440, 36), AutoTranslateMode = AutoTranslateModeEnum.Disabled }; dialog.AddChild(choice);
        foreach (var project in projects) choice.AddItem(project.Manifest.Name + " [" + project.Manifest.Id + "]");
        dialog.Confirmed += () =>
        {
            try
            {
                string directory = projects[choice.Selected].Directory;
                void SaveProject()
                {
                    var manifest = library.OpenProject(directory); manifest.Content ??= new();
                    manifest.Content.Characters[Document.Definition.Id] = repository.SavePackage(Document, directory);
                    library.SaveProjectManifest(directory, manifest); status.Text = L("角色已加入项目：", "Character added to project: ") + manifest.Name; dialog.QueueFree();
                }
                var current = library.OpenProject(directory);
                if (current.Content?.Characters.ContainsKey(Document.Definition.Id) == true) Confirm(L("替换项目中的同 ID 角色？", "Replace the character with the same ID in this project?"), SaveProject); else SaveProject();
            }
            catch (Exception error) { ShowMessage(error.Message); }
        };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
    private void RunAssetUiAudit(string output)
    {
        void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException("Character asset UI: " + message); }
        NewDocument();
        PopulateAssets("Character-Nana_Body_Top.gif");
        int original = Enumerable.Range(0, originalAssets.ItemCount).Single(i => assets[originalAssets.GetItemMetadata(i).AsInt32()].Name == "Chara/Doll/Body/Character-Nana_Body_Top.gif");
        originalAssets.Select(original); ImportOriginal();
        Expect(CurrentPart!.AnchorX == 11 && CurrentPart.AnchorY == 19 && CurrentPart.Frames.Count > 1, "original animation uses its skeleton anchor");
        string rgb = Path.Combine(output, "import-01-rgb.png"), palette = Path.Combine(output, "import-02-palette.png");
        File.WriteAllBytes(rgb, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP4L6fBLabIoPE/ICrgPwAcNQSoOnZbNgAAAABJRU5ErkJggg=="));
        File.WriteAllBytes(palette, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAMAAABFaP0WAAAADFBMVEX/HigLFiEo/1BaUP/utSiJAAAABHRSTlP/AID/excXrQAAAA5JREFUeJxjYGBkYGIGAAARAAeeoioSAAAAAElFTkSuQmCC"));
        ImportPng(false);
        var import = GetChildren().OfType<FileDialog>().Last(); import.Hide(); import.EmitSignal(FileDialog.SignalName.FilesSelected, new[] { palette, rgb });
        byte[] expected = [255, 30, 40, 255, 11, 22, 33, 0, 40, 255, 80, 128, 90, 80, 255, 255];
        Expect(CurrentPart!.Width == 2 && CurrentPart.Height == 2 && CurrentPart.Frames.Count == 2, "PNG selection imports animation frames in filename order");
        Expect(Document.Pixels(CurrentPart, 1).SequenceEqual(expected) && Document.Pixels(CurrentPart, 0)[7] == 255, "RGB and palette PNG convert to editable RGBA without losing transparent RGB");
        ImportPng(true); import = GetChildren().OfType<FileDialog>().Last(); import.Hide(); import.EmitSignal(FileDialog.SignalName.FilesSelected, new[] { rgb });
        Expect(CurrentPart.Frames.Count == 3 && Document.Pixels(CurrentPart, 2).SequenceEqual(Document.Pixels(CurrentPart, 0)), "append keeps independent animation frames");
        var library = new NativeModLibrary(repository.Store); string directory = library.CreateProject("audit.character.ui." + Guid.NewGuid().ToString("N"), "Character UI package");
        SaveToProject(); var save = GetChildren().OfType<ConfirmationDialog>().Single(d => d.Name == "SaveCharacterProject");
        save.GetChildren().OfType<OptionButton>().Single().Selected = library.Projects().ToList().FindIndex(p => p.Directory == directory);
        save.Hide(); save.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        var manifest = library.OpenProject(directory);
        Expect(manifest.Content!.Characters.ContainsKey(Document.Definition.Id), "project save writes the selected project manifest");
        var definition = NativeContentStore.Load<CharacterDefinition>(Path.Combine(directory, manifest.Content.Characters[Document.Definition.Id]));
        var loaded = repository.LoadDefinition(definition, directory);
        Expect(loaded.MissingFrames.Count == 0 && loaded.Pixels(CurrentPart.Frames[1].Id).SequenceEqual(expected), "project save retains imported pixels with package relative assets");
        GD.Print("CHARACTER_ASSET_UI_AUDIT_OK original=anchor,animation rgb=PASS palette=PASS append=PASS project=PASS");
    }
}
