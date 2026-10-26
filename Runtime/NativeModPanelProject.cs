using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeModPanel
{
    private TabContainer projectTabs = null!;
    private ItemList projectFiles = null!;
    private TextEdit fileContents = null!;
    private TextureRect fileImage = null!;
    private string? editingFile;
    private readonly Dictionary<string, LineEdit> metadataFields = new(StringComparer.Ordinal);
    private bool bindingMetadata;
    private void BuildProjectEditor(VBoxContainer parent)
    {
        projectTabs = new TabContainer { Name = "ProjectEditorTabs", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; parent.AddChild(projectTabs);
        var metadataScroll = new ScrollContainer { Name = "作品信息", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; projectTabs.AddChild(metadataScroll); projectTabs.SetTabTitle(0, L("作品信息", "Project details"));
        var metadata = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; metadataScroll.AddChild(metadata);
        void Field(string key, string title, Func<NativeModManifest, string> get, Action<NativeModManifest, string> set)
        {
            metadata.AddChild(new Label { Text = title }); var input = new LineEdit { Name = "Mod" + key, AutoTranslateMode = AutoTranslateModeEnum.Disabled }; metadata.AddChild(input); metadataFields[key] = input;
            input.TextChanged += value => { if (!bindingMetadata) Run(() => MutateManifest(manifest => set(manifest, value))); };
        }
        Field("Id", "ID", m => m.Id, (m, value) => m.Id = value); Field("Name", L("作品名称", "Name"), m => m.Name, (m, value) => m.Name = value);
        Field("Author", L("作者", "Author"), m => m.Author, (m, value) => m.Author = value); Field("Version", L("作品版本", "Version"), m => m.Version, (m, value) => m.Version = value);
        Field("Description", L("描述", "Description"), m => m.Description, (m, value) => m.Description = value); Field("Cover", L("封面相对路径", "Cover path"), m => m.Cover, (m, value) => m.Cover = value);
        var imports = new HBoxContainer(); metadata.AddChild(imports);
        AddButton(imports, L("导入资源…", "Import resource…"), "ImportProjectResource", () => ImportProjectAsset("resource"));
        AddButton(imports, L("加入角色…", "Add character…"), "ImportProjectCharacter", () => ImportProjectAsset("character"));
        AddButton(imports, L("加入地图…", "Add map…"), "ImportProjectMap", () => ImportProjectAsset("map"));
        AddButton(imports, L("加入语言…", "Add language…"), "ImportProjectLanguage", () => ImportProjectAsset("language"));
        var fileSplit = new HSplitContainer { Name = "项目文件", SplitOffsets = [180] }; projectTabs.AddChild(fileSplit); projectTabs.SetTabTitle(1, L("项目文件", "Project files"));
        projectFiles = new ItemList { AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new(170, 200) }; fileSplit.AddChild(projectFiles);
        var fileEditor = new VBoxContainer(); fileSplit.AddChild(fileEditor);
        fileContents = new TextEdit { Name = "ProjectFileContents", AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(330, 230) }; fileEditor.AddChild(fileContents);
        fileImage = new TextureRect { Name = "ProjectImagePreview", ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = CanvasItem.TextureFilterEnum.Nearest, CustomMinimumSize = new(250, 170), Visible = false }; fileEditor.AddChild(fileImage);
        var fileActions = new HBoxContainer(); fileEditor.AddChild(fileActions); AddButton(fileActions, L("保存文件", "Save file"), "SaveProjectFile", SaveProjectFile); AddButton(fileActions, L("刷新文件", "Refresh files"), "RefreshProjectFiles", RefreshProjectFiles);
        projectFiles.ItemSelected += index => Run(() => OpenProjectFile(projectFiles.GetItemMetadata((int)index).AsString()));
        var advanced = new VBoxContainer { Name = "高级清单" }; projectTabs.AddChild(advanced); projectTabs.SetTabTitle(2, L("高级清单", "Advanced manifest")); advanced.AddChild(manifestText);
        manifestText.TextChanged += BindMetadata;
    }
    private void BindMetadata()
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<NativeModManifest>(manifestText.Text, NativeContentStore.Json); if (manifest is null) return;
            bindingMetadata = true;
            foreach (var (key, value) in new[] { ("Id", manifest.Id), ("Name", manifest.Name), ("Author", manifest.Author), ("Version", manifest.Version), ("Description", manifest.Description), ("Cover", manifest.Cover) }) if (metadataFields[key].Text != value) metadataFields[key].Text = value;
        }
        catch (JsonException) { }
        finally { bindingMetadata = false; }
    }
    private void MutateManifest(Action<NativeModManifest> change)
    {
        var manifest = JsonSerializer.Deserialize<NativeModManifest>(manifestText.Text, NativeContentStore.Json) ?? throw new InvalidDataException("mod.json"); change(manifest); manifestText.Text = JsonSerializer.Serialize(manifest, NativeContentStore.Json);
    }
    private void RefreshProjectFiles()
    {
        projectFiles.Clear(); if (!Directory.Exists(directory.Text)) return;
        foreach (string path in Directory.EnumerateFiles(directory.Text, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        { string relative = Path.GetRelativePath(directory.Text, path).Replace('\\', '/'); int index = projectFiles.AddItem(relative); projectFiles.SetItemMetadata(index, relative); projectFiles.SetItemTooltip(index, relative); }
    }
    private void OpenProjectFile(string relative)
    {
        string path = NativeModManager.ResolvePackageFile(directory.Text, relative); editingFile = relative; fileImage.Texture = null;
        bool image = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase); fileImage.Visible = image; fileContents.Visible = !image;
        if (image) { var manifest = new NativeModManifest { Cover = relative }; fileImage.Texture = Cover(directory.Text, manifest); return; }
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException(L("文本文件超过 2 MB，请使用外部编辑器。", "Text files over 2 MB require an external editor."));
        string extension = Path.GetExtension(path).ToLowerInvariant(); fileContents.Editable = extension is ".cs" or ".json" or ".txt" or ".csv" or ".cfg";
        fileContents.Text = fileContents.Editable ? File.ReadAllText(path) : L("此文件可保留在项目中。", "This file is preserved in the project.");
    }
    private void SaveProjectFile()
    {
        if (editingFile is null || !fileContents.Editable || !fileContents.Visible) return;
        string path = NativeModManager.ResolvePackageFile(directory.Text, editingFile); NativeContentStore.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(fileContents.Text));
        if (editingFile == "mod.json") manifestText.Text = fileContents.Text; diagnostics.Text = L("文件已保存：", "File saved: ") + editingFile;
    }
    private void ImportProjectAsset(string kind)
    {
        if (directory.Text.Length == 0) throw new InvalidOperationException(L("请先打开项目。", "Open a project first."));
        Choose(FileDialog.FileModeEnum.OpenFile, kind == "map" ? "*.map.json ; Map" : kind == "character" ? "*.character.json ; Character" : kind == "language" ? "*.json ; JSON" : "", source =>
        {
            string defaultId = kind == "map" ? NativeContentStore.Load<NativeMapDocument>(source).Id : kind == "character" ? NativeContentStore.Load<CharacterDefinition>(source).Id : library.OpenProject(directory.Text).Id + "." + Path.GetFileNameWithoutExtension(source);
            var dialog = new ConfirmationDialog { Name = "ImportProjectAsset", Title = L("资源稳定 ID", "Stable asset ID"), Size = new(510, 230) }; AddChild(dialog);
            var box = new VBoxContainer(); dialog.AddChild(box); var id = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = defaultId }; box.AddChild(id);
            box.AddChild(new Label { Text = L("语言 ID 使用 .en 或 .zh_CN 后缀。", "Language IDs end with .en or .zh_CN."), Visible = kind == "language" });
            var cover = new CheckBox { Text = L("用作封面", "Use as cover"), Visible = kind == "resource" && Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase) }; box.AddChild(cover);
            dialog.Confirmed += () => Run(() =>
            {
                if (string.IsNullOrWhiteSpace(id.Text)) throw new InvalidDataException("ID");
                string relative = (kind == "map" ? "maps/" : kind == "language" ? "languages/" : "assets/") + Path.GetFileName(source); string target = Path.Combine(directory.Text, relative);
                void Import()
                {
                    if (kind == "character")
                    {
                        var character = NativeContentStore.Load<CharacterDefinition>(source); var repository = new NativeCharacterRepository(store);
                        string sourceRoot = Path.GetDirectoryName(source)!;
                        for (var parent = new DirectoryInfo(sourceRoot); parent is not null; parent = parent.Parent)
                            if (File.Exists(Path.Combine(parent.FullName, "mod.json"))) { sourceRoot = parent.FullName; break; }
                        var document = repository.LoadDefinition(character, sourceRoot); document.Definition.Id = id.Text;
                        string characterPath = repository.SavePackage(document, directory.Text);
                        MutateManifest(manifest => { manifest.Content ??= new(); manifest.Content.Characters[id.Text] = characterPath; }); SaveManifest(); RefreshProjectFiles(); dialog.QueueFree(); return;
                    }
                    NativeContentStore.WriteAtomic(target, File.ReadAllBytes(source)); MutateManifest(manifest => { manifest.Content ??= new(); var entries = kind == "map" ? manifest.Content.Maps : kind == "language" ? manifest.Content.Languages : manifest.Content.Resources; entries[id.Text] = relative; if (cover.ButtonPressed) manifest.Cover = relative; }); SaveManifest(); RefreshProjectFiles(); dialog.QueueFree();
                }
                if (File.Exists(target)) Confirm(L("替换项目中的同名资源文件？", "Replace the resource with the same filename?"), Import); else Import();
            }); dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
        });
    }
}
