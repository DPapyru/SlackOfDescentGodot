using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SlackOfDescent.NativeMapService;

namespace SlackOfDescent;

public partial class NativeMapEditor
{
    private NativeMapAtlas atlas = null!;
    private TextureRect entityPreview = null!;
    private Label entityDescription = null!;
    private VBoxContainer propertyForm = null!;
    private LineEdit entityFilter = null!;
    private readonly Dictionary<string, ImageTexture> paletteTextures = new(StringComparer.Ordinal);
    private static string L(string zh, string en) => NativeLocalization.IsChinese ? zh : en;
    private string ContentName(string id, string name = "") => NativeContentLocalization.Display(registry, id, name);
    private bool ContentMatches(string id, string name, string query) => NativeContentLocalization.Matches(registry, id, name, query);
    private ImageTexture? PaletteTexture(string path)
    {
        path = Path.IsPathRooted(path) ? path : Path.Combine(world.Root, path);
        if (paletteTextures.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return null;
        using var image = Image.LoadFromFile(path); if (image is null || image.IsEmpty()) return null;
        return paletteTextures[path] = ImageTexture.CreateFromImage(image);
    }
    private void BuildAtlas(VBoxContainer parent)
    {
        var search = new LineEdit { Name = "TileSearch", AutoTranslateMode = AutoTranslateModeEnum.Disabled, PlaceholderText = L("搜索图集中英文名或 ID", "Search atlas name or ID") }; parent.AddChild(search);
        var assets = world.Manifest.GetProperty("backgrounds").EnumerateArray().Select(x => x.Clone()).ToArray();
        void Populate(string query)
        {
            int previous = backgrounds.GetSelectedId(); backgrounds.Clear();
            foreach (var asset in assets)
            {
                string name = asset.GetProperty("name").GetString()!; int id = asset.GetProperty("id").GetInt32();
                if (!ContentMatches(name, name, query) && !id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                backgrounds.AddItem(ContentName(name, name), id); backgrounds.GetPopup().SetItemTooltip(backgrounds.ItemCount - 1, name + "\nID: " + id); if (id == previous) backgrounds.Select(backgrounds.ItemCount - 1);
            }
            RefreshAtlas();
        }
        var scroll = new ScrollContainer { Name = "TileAtlasScroll", CustomMinimumSize = new(210, 160), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; parent.AddChild(scroll);
        atlas = new NativeMapAtlas { Name = "TileAtlas" }; scroll.AddChild(atlas);
        atlas.RegionSelected += region => { sourceX.Value = region.Position.X; sourceY.Value = region.Position.Y; tileWidth.Value = region.Size.X; tileHeight.Value = region.Size.Y; toolChoice.Select((int)NativeMapTool.Tile); if (MapCanvas is not null) MapCanvas.Tool = NativeMapTool.Tile; };
        backgrounds.ItemSelected += _ => RefreshAtlas(); search.TextChanged += Populate;
        foreach (var field in new[] { sourceX, sourceY, tileWidth, tileHeight }) field.ValueChanged += _ => { atlas.Region = new((int)sourceX.Value, (int)sourceY.Value, (int)tileWidth.Value, (int)tileHeight.Value); atlas.QueueRedraw(); };
        Populate("");
    }
    private void RefreshAtlas()
    {
        if (atlas is null) return;
        var background = world.Manifest.GetProperty("backgrounds").EnumerateArray().FirstOrDefault(x => x.GetProperty("id").GetInt32() == backgrounds.GetSelectedId());
        atlas.Atlas = background.ValueKind == JsonValueKind.Undefined ? null : PaletteTexture(background.GetProperty("path").GetString()!); atlas.Refresh();
    }
    private void BuildEntityPreview(VBoxContainer parent)
    {
        entities.FixedIconSize = new(36, 36);
        entityPreview = new TextureRect { Name = "EntityPreview", CustomMinimumSize = new(190, 90), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = CanvasItem.TextureFilterEnum.Nearest }; parent.AddChild(entityPreview);
        entityDescription = new Label { Name = "EntityDescription", AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new(190, 56) }; parent.AddChild(entityDescription);
        entities.ItemSelected += _ => UpdateEntityPreview();
    }
    private Texture2D? EntityTexture(int objectId)
    {
        if (!world.Objects.TryGetValue(objectId, out var obj) || !world.Sprites.TryGetValue(obj.GetProperty("sprite").GetInt32(), out var sprite) || sprite.GetProperty("frames").GetArrayLength() == 0) return null;
        return PaletteTexture(sprite.GetProperty("frames")[0].GetProperty("path").GetString()!);
    }
    private void PopulateEntities(string filter)
    {
        string previous = entities.GetSelectedItems().Length > 0 ? entities.GetItemMetadata(entities.GetSelectedItems()[0]).AsString() : "";
        entities.Clear();
        foreach (var pair in world.Objects.OrderBy(p => p.Value.GetProperty("name").GetString()))
        {
            string name = pair.Value.GetProperty("name").GetString()!; if (!ContentMatches(name, name, filter) && !pair.Key.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            int index = entities.AddItem((pair.Value.GetProperty("solid").GetBoolean() ? "▣ " : "● ") + ContentName(name, name), EntityTexture(pair.Key)); string id = "object:" + pair.Key; entities.SetItemMetadata(index, id); entities.SetItemTooltip(index, name + "\nID: " + pair.Key); if (id == previous) entities.Select(index);
        }
        foreach (string id in registry.EntityDefinitions.Keys.Concat(registry.Entities.Keys.Where(id => !registry.EntityDefinitions.Values.Any(d => d.BehaviorId == id))).Distinct().Order())
        {
            registry.EntityDefinitions.TryGetValue(id, out var definition); if (!ContentMatches(id, definition?.Name ?? "", filter)) continue;
            Texture2D? icon = definition is not null && registry.Resources.TryGetValue(definition.ResourceId, out string? resource) ? PaletteTexture(resource) : null;
            int index = entities.AddItem("◇ " + ContentName(id, definition?.Name ?? ""), icon); entities.SetItemMetadata(index, "native:" + id); entities.SetItemTooltip(index, "ID: " + id + "\n" + (definition?.Description ?? "")); if ("native:" + id == previous) entities.Select(index);
        }
        if (entities.ItemCount > 0 && entities.GetSelectedItems().Length == 0) entities.Select(0); UpdateEntityPreview();
    }
    private void UpdateEntityPreview()
    {
        if (entityPreview is null) return;
        var selectedItems = entities.GetSelectedItems(); if (selectedItems.Length == 0) { entityPreview.Texture = null; entityDescription.Text = ""; return; }
        int index = selectedItems[0]; entityPreview.Texture = entities.GetItemIcon(index); string id = entities.GetItemMetadata(index).AsString();
        entityDescription.Text = entities.GetItemText(index) + "\nID: " + id;
        if (id.StartsWith("native:", StringComparison.Ordinal) && registry.EntityDefinitions.TryGetValue(id[7..], out var definition)) entityDescription.Text += "\n" + NativeContentLocalization.Description(registry, id[7..], definition.Description);
    }
    private void ApplyEntityDefaults(JsonObject item)
    {
        string? type = item["entityType"]?.GetValue<string>();
        if (type is null || !registry.EntityDefinitions.TryGetValue(type, out var definition)) return;
        var values = item["properties"]!.AsObject();
        foreach (var parameter in definition.Parameters) values[parameter.Key] = DefaultProperty(parameter.Value.Type, parameter.Value.Default);
    }
    private static JsonNode DefaultProperty(string type, string value)
    {
        if (type == "bool" || type == "boolean") return JsonValue.Create(bool.TryParse(value, out bool parsed) && parsed)!;
        if (type == "number" || type == "int" || type == "integer") return JsonValue.Create(double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : 0)!;
        return JsonValue.Create(value)!;
    }
    private void RefreshPropertyForm()
    {
        foreach (var child in propertyForm.GetChildren()) { propertyForm.RemoveChild(child); child.QueueFree(); }
        if (selected is null || selectedTile) return;
        var values = selected["properties"] as JsonObject ?? new JsonObject();
        var definitions = new Dictionary<string, NativeEntityParameter>(StringComparer.Ordinal);
        string? type = selected["entityType"]?.GetValue<string>();
        if (type is not null && registry.EntityDefinitions.TryGetValue(type, out var schema)) foreach (var pair in schema.Parameters) definitions[pair.Key] = pair.Value;
        foreach (var pair in values) if (!definitions.ContainsKey(pair.Key)) definitions[pair.Key] = new() { Name = pair.Key, Type = pair.Value is JsonValue value && value.TryGetValue<bool>(out _) ? "bool" : pair.Value is JsonValue number && TryNumber(number, out _) ? "number" : "string" };
        foreach (var pair in definitions)
        {
            string key = pair.Key; var definition = pair.Value; JsonNode value = values[key] ?? DefaultProperty(definition.Type, definition.Default);
            var row = new HBoxContainer(); propertyForm.AddChild(row); var label = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = ContentName((type ?? "") + "." + key, definition.Name.Length > 0 ? definition.Name : key), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = key + "\n" + NativeContentLocalization.Description(registry, (type ?? "") + "." + key, definition.Description) }; row.AddChild(label);
            void Apply(JsonNode replacement)
            {
                if (updating || selected is null) return;
                Remember(); var properties = selected["properties"] as JsonObject; if (properties is null) selected["properties"] = properties = new(); properties[key] = replacement; Dirty = true; this.properties.Text = properties.ToJsonString(NativeContentStore.Json); UpdateStatus(); MapCanvas.QueueRedraw();
            }
            if (definition.Type is "bool" or "boolean")
            { var input = new CheckBox { ButtonPressed = value is JsonValue boolean && boolean.TryGetValue<bool>(out bool enabled) && enabled }; row.AddChild(input); input.Toggled += enabled => Apply(JsonValue.Create(enabled)!); }
            else if (definition.Type is "number" or "int" or "integer")
            { var input = new SpinBox { MinValue = definition.Min ?? -1000000, MaxValue = definition.Max ?? 1000000, Step = definition.Type == "number" ? .1 : 1, Value = value is JsonValue number && TryNumber(number, out double numeric) ? numeric : 0, UpdateOnTextChanged = false, CustomMinimumSize = new(95, 0) }; row.AddChild(input); input.ValueChanged += number => Apply(JsonValue.Create(number)!); }
            else { var input = new LineEdit { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = value.GetValue<string>(), CustomMinimumSize = new(100, 0) }; row.AddChild(input); input.TextSubmitted += text => Apply(JsonValue.Create(text)!); input.FocusExited += () => { if (input.Text != (values[key]?.GetValue<string>() ?? definition.Default)) Apply(JsonValue.Create(input.Text)!); }; }
        }
    }
    private bool IsInstalledMap(string path)
    {
        string full = Path.GetFullPath(path); string installed = Path.GetFullPath(store.Mods) + Path.DirectorySeparatorChar;
        return full.StartsWith(installed, StringComparison.Ordinal) || registry.Maps.Values.Any(x => Path.GetFullPath(x) == full);
    }
    private void OpenMapLibrary()
    {
        var dialog = new ConfirmationDialog { Name = "MapLibraryDialog", Title = L("地图目录", "Map library"), Size = new(650, 480), OkButtonText = L("打开副本", "Open copy") }; AddChild(dialog);
        var tree = new Tree { Name = "MapLibraryTree", HideRoot = true, CustomMinimumSize = new(610, 390), AutoTranslateMode = AutoTranslateModeEnum.Disabled }; dialog.AddChild(tree); var root = tree.CreateItem();
        var original = tree.CreateItem(root); original.SetText(0, L("原版", "Original"));
        foreach (var room in world.Rooms.OrderBy(x => x.Key)) { var item = tree.CreateItem(original); item.SetText(0, room.Value.GetProperty("name").GetString()); item.SetMetadata(0, "original:" + room.Key); item.SetTooltipText(0, "ID: " + room.Key); }
        var local = tree.CreateItem(root); local.SetText(0, L("个人地图", "My maps"));
        foreach (string path in Directory.EnumerateFiles(store.Maps, "*.map.json", SearchOption.AllDirectories).Order()) { var item = tree.CreateItem(local); item.SetText(0, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))); item.SetMetadata(0, path); item.SetTooltipText(0, path); }
        var mods = tree.CreateItem(root); mods.SetText(0, "Mod"); var groups = new Dictionary<string, TreeItem>();
        foreach (var map in registry.Maps.OrderBy(x => x.Key))
        {
            string group = Path.GetDirectoryName(map.Value)!; string installedRoot = Path.GetFullPath(store.Mods) + Path.DirectorySeparatorChar;
            if (map.Value.StartsWith(installedRoot, StringComparison.Ordinal)) group = Path.Combine(store.Mods, Path.GetRelativePath(store.Mods, map.Value).Split(Path.DirectorySeparatorChar)[0]);
            if (!groups.TryGetValue(group, out var parent)) { parent = tree.CreateItem(mods); string manifestPath = Path.Combine(group, "mod.json"); parent.SetText(0, File.Exists(manifestPath) ? NativeModManifest.Read(manifestPath).Name : Path.GetFileName(group)); groups.Add(group, parent); }
            var item = tree.CreateItem(parent); item.SetText(0, ContentName(map.Key, NativeContentStore.Load<NativeMapDocument>(map.Value).Name)); item.SetMetadata(0, map.Value); item.SetTooltipText(0, "ID: " + map.Key);
        }
        void Open() { string choice = tree.GetSelected()?.GetMetadata(0).AsString() ?? ""; if (choice.Length == 0) return; Guard(() => { if (choice.StartsWith("original:", StringComparison.Ordinal)) Import(int.Parse(choice[9..])); else LoadPath(choice); }); dialog.QueueFree(); }
        dialog.Confirmed += Open; tree.ItemActivated += Open; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
}
