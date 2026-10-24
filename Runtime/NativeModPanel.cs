using Godot;

namespace SlackOfDescent;

public partial class NativeModPanel : Window
{
    private readonly NativeModManager manager;
    private readonly NativeContentStore store;
    private readonly string examples;
    private readonly ItemList list = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled };
    private readonly TextEdit diagnostics = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Editable = false, CustomMinimumSize = new(620, 180) };
    private readonly LineEdit directory = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, PlaceholderText = NativeLocalization.Text("模组目录（含 mod.json 和 C# 源文件）") };
    private readonly List<string> directories = new();
    public NativeModPanel(NativeModManager manager, NativeContentStore store, string examplesDirectory)
    {
        this.manager = manager; this.store = store; examples = examplesDirectory;
        Name = "NativeModPanel"; Title = "C# 模组"; Size = new(720, 600);
        var margin = new MarginContainer(); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); AddChild(margin);
        margin.AddThemeConstantOverride("margin_left", 16); margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 16); margin.AddThemeConstantOverride("margin_bottom", 16);
        var body = new VBoxContainer(); margin.AddChild(body);
        body.AddChild(new Label { Text = "选择模组后可编译、重新加载或卸载；编译失败会保留当前版本。", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        list.CustomMinimumSize = new(0, 160); list.SizeFlagsVertical = Control.SizeFlags.ExpandFill; body.AddChild(list);
        body.AddChild(directory);
        var buttons = new HBoxContainer(); body.AddChild(buttons);
        Button AddButton(string text, Action action) { var button = new Button { Text = text }; button.Pressed += action; buttons.AddChild(button); return button; }
        AddButton("编译 / 加载 / 重载", ReloadSelected);
        AddButton("卸载", UnloadSelected);
        AddButton("安装示例", InstallExamples);
        AddButton("刷新", Refresh);
        AddButton("打开目录", () => OS.ShellOpen(store.Mods));
        body.AddChild(diagnostics);
        list.ItemSelected += index => directory.Text = directories[(int)index];
        CloseRequested += Hide;
        Refresh();
    }
    public override void _Ready() { Transient = true; Exclusive = true; }
    public void Refresh()
    {
        list.Clear(); directories.Clear();
        foreach (string path in Directory.EnumerateDirectories(store.Mods).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(path, "mod.json"))) continue;
            directories.Add(path);
            string id;
            try { id = NativeContentStore.Load<NativeModManifest>(Path.Combine(path, "mod.json")).Id; }
            catch { id = Path.GetFileName(path); }
            bool active = manager.LoadedMods.Any(m => m.Id == id);
            list.AddItem((active ? "● " : "○ ") + id);
        }
    }
    public void ReloadSelected()
    {
        if (string.IsNullOrWhiteSpace(directory.Text)) { diagnostics.Text = NativeLocalization.Text("请先选择模组或输入目录。"); return; }
        var result = manager.Reload(directory.Text);
        diagnostics.Text = NativeLocalization.Text(result.Success ? "成功：" : "失败（保留当前版本）：") + result.Id + "\n" + NativeLocalization.Text(result.Diagnostics);
        Refresh();
    }
    private void UnloadSelected()
    {
        try
        {
            string id = NativeContentStore.Load<NativeModManifest>(Path.Combine(directory.Text, "mod.json")).Id;
            var result = manager.Unload(id); diagnostics.Text = NativeLocalization.Text(result.Diagnostics);
            if (result.PreviousContext is {} weak) diagnostics.Text += "\n" + NativeLocalization.Text("程序集回收：") + NativeLocalization.Text(NativeModManager.VerifyUnloaded(weak) ? "已完成" : "等待 Godot 在帧结束释放引用");
            Refresh();
        }
        catch (Exception error) { diagnostics.Text = NativeLocalization.Text(error.Message); }
    }
    public void InstallExamples()
    {
        if (!Directory.Exists(examples)) { diagnostics.Text = NativeLocalization.Text("未找到随包示例目录：") + examples; return; }
        foreach (string path in Directory.EnumerateFiles(examples, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(store.Mods, Path.GetRelativePath(examples, path));
            if (!File.Exists(target)) NativeContentStore.WriteAtomic(target, File.ReadAllBytes(path));
        }
        diagnostics.Text = NativeLocalization.Text("示例已安装，可选择并编译。"); Refresh();
    }
}
