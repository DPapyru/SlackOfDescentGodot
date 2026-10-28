using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeCharacterEditor
{
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
