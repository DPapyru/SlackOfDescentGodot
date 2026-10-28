using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed class NativeInputService
{
    private readonly ContentRegistry registry;
    private readonly string path;
    private readonly Dictionary<string, int> bindings;
    public NativeInputContext Context { get; set; } = NativeInputContext.Game;
    public NativeInputService(ContentRegistry registry, string userRoot)
    {
        this.registry = registry; path = Path.Combine(userRoot, "Content", "input-bindings.json");
        bindings = File.Exists(path) ? NativeContentStore.Load<Dictionary<string, int>>(path) : new(StringComparer.Ordinal);
    }
    public int GetKey(string id) => bindings.TryGetValue(id, out int key) && key is >= 2 and <= 255 ? key : Definition(id).DefaultKey;
    public void SetKey(string id, int key)
    {
        Definition(id);
        if (key is < 2 or > 255) throw new ArgumentOutOfRangeException(nameof(key));
        bindings[id] = key; NativeContentStore.Save(path, bindings);
    }
    private NativeInputAction Definition(string id) => registry.InputActions.TryGetValue(id, out var action) ? action : throw new KeyNotFoundException("输入操作未加载：" + id);
    private bool Enabled(string id) => registry.InputActions.TryGetValue(id, out var action) && (action.Contexts & Context) != 0;
    public bool Down(string id, InputSnapshot input) => Enabled(id) && input.Down(GetKey(id));
    public bool Pressed(string id, InputSnapshot input) => Enabled(id) && input.JustPressed(GetKey(id));
    public bool Released(string id, InputSnapshot input) => Enabled(id) && input.Released.Contains(GetKey(id));
}
