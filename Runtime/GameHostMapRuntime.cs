using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    private NativeMapRuntime? mapRuntime;
    private ConfirmationDialog? mapSelector;
    private bool mapContentChanged;
    private bool mapMessageBusy;
    private readonly Queue<string> mapMessages = new();
    private IReadOnlyList<NativePlayableMap> EnabledMaps()
    {
        var result = new List<NativePlayableMap>();
        foreach (var mod in mods.LoadedMods)
        {
            var declared = mod.Manifest.Content?.Maps;
            if (declared is null) continue;
            var order = (mod.Manifest.MapPack?.RoomOrder ?? []).Concat(declared.Keys).Distinct(StringComparer.Ordinal);
            foreach (string id in order)
                if (content.Maps.TryGetValue(id, out string? path)) result.Add(new(id, mod.Id, NativeContentStore.Load<NativeMapDocument>(path)));
        }
        return result;
    }
    private string MapStart(string owner, IReadOnlyList<NativePlayableMap> available)
    {
        var mod = mods.LoadedMods.First(m => m.Id == owner);
        string start = mod.Manifest.MapPack?.StartRoom ?? "";
        return available.Any(m => m.Owner == owner && m.Id == start) ? start : available.First(m => m.Owner == owner).Id;
    }
    private void OpenMapSelector()
    {
        if (mapSelector is not null) return;
        ResetNativeInput(); modalDepth++;
        var available = EnabledMaps();
        var dialog = new ConfirmationDialog { Name = "NativeMapSelector", Title = NativeLocalization.Text("选择地图"), Size = new(650, 490), OkButtonText = NativeLocalization.Text("开始") };
        mapSelector = dialog; AddChild(dialog);
        var tree = new Tree { Name = "PlayableMapTree", HideRoot = true, CustomMinimumSize = new(610, 390), AutoTranslateMode = AutoTranslateModeEnum.Disabled }; dialog.AddChild(tree);
        BuildMapSelectionTree(tree, available);
        void Close()
        {
            if (mapSelector != dialog) return;
            mapSelector = null; modalDepth--; ResetNativeInput(); dialog.QueueFree();
        }
        async void Start()
        {
            string choice = tree.GetSelected()?.GetMetadata(0).AsString() ?? "";
            if (choice.Length == 0) { dialog.PopupCentered(); return; }
            if (choice.StartsWith("mod:", StringComparison.Ordinal)) choice = MapStart(choice[4..], available);
            Close();
            try
            {
                modalDepth++;
                if (choice == "original") { EndMapRuntime(); await PrepareTestingRoom(); }
                else await StartNativeMap(available, choice);
                graphics.Build(); ResetNativeInput();
            }
            catch (Exception error) { EndMapRuntime(); await ShowDialog(NativeLocalization.Text("地图加载失败") + ": " + error.Message, ["OK"]); }
            finally { modalDepth--; ResetNativeInput(); }
        }
        dialog.Confirmed += Start; tree.ItemActivated += Start; dialog.Canceled += Close; dialog.CloseRequested += Close;
        dialog.PopupCentered();
    }
    private void BuildMapSelectionTree(Tree tree, IReadOnlyList<NativePlayableMap> available)
    {
        var root = tree.CreateItem();
        var original = tree.CreateItem(root); original.SetText(0, NativeLocalization.Text("原版")); original.SetSelectable(0, false);
        var test = tree.CreateItem(original); test.SetText(0, "Test Room"); test.SetMetadata(0, "original");
        var modRoot = tree.CreateItem(root); modRoot.SetText(0, "Mod"); modRoot.SetSelectable(0, false);
        foreach (var mod in mods.LoadedMods)
        {
            var rooms = available.Where(m => m.Owner == mod.Id).ToArray(); if (rooms.Length == 0) continue;
            var parent = tree.CreateItem(modRoot); parent.SetText(0, NativeContentLocalization.Display(content, mod.Id, mod.Manifest.Name)); parent.SetMetadata(0, "mod:" + mod.Id); parent.SetTooltipText(0, "ID: " + mod.Id);
            foreach (var room in rooms)
            {
                var item = tree.CreateItem(parent); item.SetText(0, NativeContentLocalization.Display(content, room.Id, room.Document.Name)); item.SetMetadata(0, room.Id); item.SetTooltipText(0, "ID: " + room.Id);
            }
        }
    }
    private async Task StartNativeMap(IEnumerable<NativePlayableMap> maps, string first)
    {
        EndMapRuntime();
        mapRuntime = new(world, content, nativeBridge.Services, maps);
        nativeBridge.Services.Maps.ShowMessage = text => mapMessages.Enqueue(text);
        int room = mapRuntime.Room(first);
        await PrepareTestingRoom();
        NativeMapService.CollectUnusedObjects(world);
        await world.LoadRoomAsync(room);
        graphics.Build(); nativeClock.RoomChanged();
    }
    private void EndMapRuntime()
    {
        mapRuntime?.Dispose(); mapRuntime = null; mapMessages.Clear(); mapContentChanged = false;
    }
    private void OnMapModsChanged()
    {
        if (mapRuntime is not null) mapContentChanged = true;
    }
    private async void SyncMapRuntime()
    {
        if (mapContentChanged && pendingFrame is null && !world.IsLoadingRoom)
        {
            mapContentChanged = false;
            if (mapRuntime is not null)
            {
                modalDepth++;
                try { await world.LoadRoomAsync(world.Constants["Title"].Int); EndMapRuntime(); graphics.Build(); }
                finally { modalDepth--; ResetNativeInput(); }
            }
        }
        if (mapMessageBusy || mapMessages.Count == 0) return;
        mapMessageBusy = true;
        try { await ShowDialog(mapMessages.Dequeue(), ["OK"]); }
        finally { mapMessageBusy = false; }
    }
}
