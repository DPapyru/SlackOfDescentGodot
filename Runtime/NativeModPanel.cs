using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeModPanel : Window
{
    private readonly NativeModManager manager;
    private readonly NativeContentStore store;
    private readonly ContentRegistry registry;
    private readonly NativeModLibrary library;
    private readonly string examples;
    private readonly ItemList list = new() { Name = "InstalledMods", AutoTranslateMode = AutoTranslateModeEnum.Disabled };
    private readonly ItemList projects = new() { Name = "ModProjects", AutoTranslateMode = AutoTranslateModeEnum.Disabled };
    private readonly TextEdit diagnostics = new() { Name = "ModDiagnostics", AutoTranslateMode = AutoTranslateModeEnum.Disabled, Editable = false, CustomMinimumSize = new(620, 110) };
    private readonly RichTextLabel details = new() { Name = "ModDetails", AutoTranslateMode = AutoTranslateModeEnum.Disabled, FitContent = true, CustomMinimumSize = new(280, 200) };
    private readonly LineEdit directory = new() { Name = "ModProjectDirectory", AutoTranslateMode = AutoTranslateModeEnum.Disabled, Editable = false };
    private readonly LineEdit filter = new() { Name = "ModSearch", AutoTranslateMode = AutoTranslateModeEnum.Disabled };
    private readonly TextEdit manifestText = new() { Name = "ModManifestEditor", AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(430, 250) };
    private readonly List<string> directories = new();
    private readonly List<string> projectDirectories = new();
    private readonly Dictionary<string, ImageTexture> covers = new();
    private FileDialog picker = null!;
    private ConfirmationDialog confirmation = null!;
    private TabContainer tabs = null!;
    private Action<string>? selectedFile;
    private Action? confirmAction;
    private string? selectedInstalled;
    private static string L(string zh, string en) => NativeLocalization.IsChinese ? zh : en;
    public NativeModPanel(NativeModManager manager, NativeContentStore store, string examplesDirectory, ContentRegistry? registry = null)
    {
        this.manager = manager; this.store = store; this.registry = registry ?? new(); examples = examplesDirectory; library = new(store, manager);
        Name = "NativeModPanel"; Title = "Mod"; Size = new(1040, 720); MinSize = new(780, 560);
        var margin = new MarginContainer(); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); AddChild(margin);
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 16);
        var body = new VBoxContainer(); margin.AddChild(body);
        tabs = new TabContainer { Name = "ModTabs", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; body.AddChild(tabs);
        var installed = new VBoxContainer { Name = "Mod列表" }; tabs.AddChild(installed); tabs.SetTabTitle(0, L("Mod列表", "Mod list"));
        filter.PlaceholderText = L("搜索名称、作者或 ID", "Search name, author or ID"); installed.AddChild(filter); filter.TextChanged += _ => Refresh();
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [460] }; installed.AddChild(split);
        list.CustomMinimumSize = new(300, 200); list.SizeFlagsVertical = Control.SizeFlags.ExpandFill; list.FixedIconSize = new(80, 64); split.AddChild(list);
        var detailScroll = new ScrollContainer { CustomMinimumSize = new(300, 0) }; split.AddChild(detailScroll); detailScroll.AddChild(details);
        var actions = new HBoxContainer(); installed.AddChild(actions);
        AddButton(actions, L("启用 / 重载", "Enable / reload"), "EnableMod", ReloadInstalled);
        AddButton(actions, L("停用", "Disable"), "DisableMod", UnloadSelected);
        AddButton(actions, L("导入 ZIP…", "Import ZIP…"), "ImportMod", () => Choose(FileDialog.FileModeEnum.OpenFile, "*.zip ; ZIP", ImportArchive));
        AddButton(actions, L("编辑副本", "Edit copy"), "CloneMod", CloneSelected);
        AddButton(actions, L("安装示例", "Install examples"), "InstallExamples", InstallExamples);
        AddButton(actions, L("刷新", "Refresh"), "RefreshMods", Refresh);
        var editor = new VBoxContainer { Name = "Mod编辑" }; tabs.AddChild(editor); tabs.SetTabTitle(1, L("Mod编辑", "Mod editor"));
        var projectBar = new HBoxContainer(); editor.AddChild(projectBar);
        AddButton(projectBar, L("新建项目", "New project"), "NewModProject", NewProject);
        AddButton(projectBar, L("打开项目…", "Open project…"), "OpenModProject", () => Choose(FileDialog.FileModeEnum.OpenDir, "", OpenProject));
        AddButton(projectBar, L("打开目录", "Open folder"), "OpenModFolder", () => { if (Directory.Exists(directory.Text)) OS.ShellOpen(directory.Text); });
        AddButton(projectBar, L("导出 ZIP…", "Export ZIP…"), "ExportMod", () => { if (directory.Text.Length > 0) Choose(FileDialog.FileModeEnum.SaveFile, "*.zip ; ZIP", ExportProject); });
        var projectSplit = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = [230] }; editor.AddChild(projectSplit);
        projects.CustomMinimumSize = new(210, 160); projectSplit.AddChild(projects);
        var projectContent = new VBoxContainer(); projectSplit.AddChild(projectContent); projectContent.AddChild(directory);
        BuildProjectEditor(projectContent);
        var projectActions = new HBoxContainer(); projectContent.AddChild(projectActions);
        AddButton(projectActions, L("保存清单", "Save manifest"), "SaveModManifest", SaveManifest);
        AddButton(projectActions, L("编译 / 加载 / 重载", "Compile / load / reload"), "ReloadModProject", ReloadSelected);
        AddButton(projectActions, L("查看文件", "Browse files"), "BrowseModFiles", BrowseProjectFiles);
        body.AddChild(diagnostics);
        var footer = new HBoxContainer(); body.AddChild(footer);
        AddButton(footer, L("导出诊断…", "Export diagnostics…"), "ExportModDiagnostics", () => Choose(FileDialog.FileModeEnum.SaveFile, "*.txt ; Text", path => { NativeContentStore.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(library.ExportDiagnostics())); diagnostics.Text = L("诊断已导出：", "Diagnostics exported: ") + path; }));
        AddButton(footer, L("打开安装目录", "Open installed folder"), "OpenModsFolder", () => OS.ShellOpen(store.Mods));
        picker = new FileDialog { Name = "ModFileDialog", Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Size = new(800, 560) }; AddChild(picker);
        picker.FileSelected += path => Run(() => selectedFile?.Invoke(path)); picker.DirSelected += path => Run(() => selectedFile?.Invoke(path));
        confirmation = new ConfirmationDialog { Name = "ModConfirmation", Title = L("请确认", "Confirm"), Size = new(650, 350) }; AddChild(confirmation);
        confirmation.Confirmed += () => { var action = confirmAction; confirmAction = null; Run(() => action?.Invoke()); }; confirmation.Canceled += () => confirmAction = null;
        list.ItemSelected += index => { selectedInstalled = directories[(int)index]; ShowDetails(selectedInstalled); };
        projects.ItemSelected += index => Run(() => OpenProject(projectDirectories[(int)index]));
        CloseRequested += Hide; manager.Changed += Refresh;
        Refresh();
    }
    public override void _Ready() { Transient = true; Exclusive = false; }
    public override void _ExitTree() { manager.Changed -= Refresh; foreach (var texture in covers.Values) texture.Dispose(); }
    private void AddButton(Node parent, string text, string name, Action action) { var button = new Button { Text = text, Name = name }; parent.AddChild(button); button.Pressed += () => Run(action); }
    private void Run(Action action) { try { action(); } catch (Exception error) { diagnostics.Text = NativeLocalization.Text(error.GetBaseException().Message); } }
    private void Choose(FileDialog.FileModeEnum mode, string fileFilter, Action<string> action)
    {
        selectedFile = action; picker.FileMode = mode; picker.Filters = fileFilter.Length == 0 ? [] : [fileFilter]; picker.CurrentDir = Directory.Exists(directory.Text) ? directory.Text : store.Root; picker.PopupCentered();
    }
    private void Confirm(string text, Action action) { confirmAction = action; confirmation.DialogText = text; confirmation.PopupCentered(); }
    public void Refresh()
    {
        list.Clear(); directories.Clear();
        foreach (string path in Directory.EnumerateDirectories(store.Mods).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(path, "mod.json"))) continue;
            try
            {
                var manifest = NativeModManifest.Read(Path.Combine(path, "mod.json"));
                string name = NativeContentLocalization.Display(registry, manifest.Id, manifest.Name);
                if (!NativeContentLocalization.Matches(registry, manifest.Id, manifest.Name, filter.Text) && !manifest.Author.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)) continue;
                bool active = manager.LoadedMods.Any(m => m.Id == manifest.Id);
                directories.Add(path); int index = list.AddItem((active ? "● " : "○ ") + name + "\n" + manifest.Version + "  " + manifest.Author, Cover(path, manifest));
                list.SetItemTooltip(index, $"{name}\nID: {manifest.Id}\n{manifest.Description}"); if (path == selectedInstalled) list.Select(index);
            }
            catch (Exception error) { directories.Add(path); list.AddItem("⚠ " + Path.GetFileName(path)); list.SetItemTooltip(list.ItemCount - 1, error.Message); }
        }
        projects.Clear(); projectDirectories.Clear();
        foreach (var project in library.Projects()) { projectDirectories.Add(project.Directory); int index = projects.AddItem(project.Manifest.Name); projects.SetItemTooltip(index, "ID: " + project.Manifest.Id + "\n" + project.Directory); if (project.Directory == directory.Text) projects.Select(index); }
        if (selectedInstalled is not null) ShowDetails(selectedInstalled);
    }
    private ImageTexture? Cover(string root, NativeModManifest manifest)
    {
        if (manifest.Cover.Length == 0) return null;
        try
        {
            string path = NativeModManager.ResolvePackageFile(root, manifest.Cover);
            if (covers.TryGetValue(path, out var cached)) return cached;
            using var image = Image.LoadFromFile(path); if (image is null || image.IsEmpty()) return null; image.Resize(80, 64, Image.Interpolation.Nearest);
            return covers[path] = ImageTexture.CreateFromImage(image);
        }
        catch { return null; }
    }
    private void ShowDetails(string path)
    {
        Run(() =>
        {
            var manifest = NativeModManifest.Read(Path.Combine(path, "mod.json"));
            bool active = manager.LoadedMods.Any(m => m.Id == manifest.Id);
            details.Text = $"{NativeContentLocalization.Display(registry, manifest.Id, manifest.Name)}\n{L("作者", "Author")}: {manifest.Author}\n{L("版本", "Version")}: {manifest.Version}\nID: {manifest.Id}\n{L("状态", "State")}: {L(active ? "已启用" : "已停用", active ? "Enabled" : "Disabled")}\n\n{NativeContentLocalization.Description(registry, manifest.Id, manifest.Description)}\n\n{L("内容", "Content")}:\n" + JsonSerializer.Serialize(manifest.Content, NativeContentStore.Json);
            string? warnings = manager.LoadedMods.FirstOrDefault(mod => mod.Id == manifest.Id)?.Diagnostics;
            if (!string.IsNullOrEmpty(warnings)) details.Text += "\n\n" + L("诊断：", "Diagnostics:") + "\n" + NativeLocalization.Text(warnings);
        });
    }
    public void ReloadSelected() => Run(() => { if (directory.Text.Length == 0) throw new InvalidOperationException(L("请先打开项目。", "Open a project first.")); SaveManifest(); ShowResult(manager.Reload(directory.Text)); });
    private void ReloadInstalled() => Run(() => { if (selectedInstalled is null) return; ShowResult(manager.Reload(selectedInstalled)); });
    private void ShowResult(NativeModResult result) { diagnostics.Text = L(result.Success ? "成功：" : "失败（保留当前版本）：", result.Success ? "Success: " : "Failed (current version retained): ") + result.Id + "\n" + NativeLocalization.Text(result.Diagnostics); Refresh(); }
    private void UnloadSelected() => Run(() => { if (selectedInstalled is null) return; ShowResult(manager.Unload(NativeModManifest.Read(Path.Combine(selectedInstalled, "mod.json")).Id)); });
    private void ImportArchive(string zip)
    {
        var preview = library.InspectZip(zip);
        Confirm($"{preview.Manifest.Name}\nID: {preview.Manifest.Id}\n{preview.Manifest.Author}  {preview.Manifest.Version}\n\n{preview.Manifest.Description}\n\n{L("文件数", "Files")}: {preview.Files.Count}\n" + L(preview.ContainsCode ? "包含 C# 代码。启用后会在游戏进程中运行。" : "资源与内容包。", preview.ContainsCode ? "Contains C# code that runs in the game process when enabled." : "Resource and content package.") + "\n" + L(preview.ReplacesInstalled ? "将替换已安装版本：" + preview.InstalledVersion : "导入后可在列表中启用。", preview.ReplacesInstalled ? "Replaces installed version: " + preview.InstalledVersion : "Enable it from the list after import."), () => { var result = library.Install(zip, preview.ReplacesInstalled); selectedInstalled = result.Directory; Refresh(); diagnostics.Text = L("Mod 已导入。", "Mod imported."); });
    }
    private void NewProject()
    {
        var dialog = new ConfirmationDialog { Title = L("新建 Mod 项目", "New Mod project"), Size = new(480, 210) }; AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box); var id = new LineEdit { PlaceholderText = "ID", AutoTranslateMode = AutoTranslateModeEnum.Disabled }; box.AddChild(id); var name = new LineEdit { PlaceholderText = L("作品名称", "Project name"), AutoTranslateMode = AutoTranslateModeEnum.Disabled }; box.AddChild(name);
        dialog.Confirmed += () => Run(() => { OpenProject(library.CreateProject(id.Text, name.Text)); dialog.QueueFree(); }); dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
    private void OpenProject(string path) { var manifest = library.OpenProject(path); directory.Text = Path.GetFullPath(path); manifestText.Text = JsonSerializer.Serialize(manifest, NativeContentStore.Json); RefreshProjectFiles(); tabs.CurrentTab = 1; Refresh(); }
    private void SaveManifest()
    {
        if (directory.Text.Length == 0) throw new InvalidOperationException(L("请先打开项目。", "Open a project first."));
        var manifest = JsonSerializer.Deserialize<NativeModManifest>(manifestText.Text, NativeContentStore.Json) ?? throw new InvalidDataException("mod.json"); library.SaveProjectManifest(directory.Text, manifest); diagnostics.Text = L("项目清单已保存。", "Project manifest saved."); Refresh();
    }
    private void CloneSelected() => Run(() => { if (selectedInstalled is null) return; string id = NativeModManifest.Read(Path.Combine(selectedInstalled, "mod.json")).Id + "-" + DateTime.Now.ToString("yyyyMMddHHmmss"); OpenProject(library.CloneInstalled(selectedInstalled, id)); });
    private void ExportProject(string path) { SaveManifest(); if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) path += ".zip"; if (File.Exists(path)) Confirm(L("覆盖已有 ZIP 文件？", "Replace the existing ZIP file?"), () => { library.Export(directory.Text, path, true); diagnostics.Text = L("已导出：", "Exported: ") + path; }); else { library.Export(directory.Text, path); diagnostics.Text = L("已导出：", "Exported: ") + path; } }
    private void BrowseProjectFiles() { RefreshProjectFiles(); projectTabs.CurrentTab = 1; }
    public void InstallExamples()
    {
        Run(() => { if (!Directory.Exists(examples)) throw new DirectoryNotFoundException(L("未找到随包示例目录：", "Bundled examples were not found: ") + examples); foreach (string path in Directory.EnumerateFiles(examples, "*", SearchOption.AllDirectories)) { string target = Path.Combine(store.Mods, Path.GetRelativePath(examples, path)); if (!File.Exists(target)) NativeContentStore.WriteAtomic(target, File.ReadAllBytes(path)); } diagnostics.Text = L("示例已安装，可选择并启用。", "Examples installed. Select one to enable it."); Refresh(); });
    }
}
