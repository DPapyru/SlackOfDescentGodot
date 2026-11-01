using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public sealed class NativeCharacterBody : IDisposable
{
    public sealed record Entry(string Target, JsonElement Sprite, int Layer)
    {
        public int Count => Sprite.GetProperty("frames").GetArrayLength();
        public NativePixelRect Bounds
        {
            get
            {
                var rest = NativeCharacterBones.RestOffset(Target);
                var frames = Sprite.GetProperty("frames");
                return new(rest.X - Sprite.GetProperty("originX").GetInt32(), rest.Y - Sprite.GetProperty("originY").GetInt32(), frames[0].GetProperty("width").GetInt32(), frames[0].GetProperty("height").GetInt32());
            }
        }
    }
    private readonly GmWorld world;
    private readonly List<Entry> entries = new();
    private readonly Dictionary<string, ImageTexture> textures = new(StringComparer.Ordinal);
    private static readonly (string Target, string Sprite, int Layer)[] Table =
    [
        ("HAND3_BELOW", "sprHand3_Below", -55), ("HAND2_BELOW", "sprHand2_Below", -54), ("HAND1_BELOW", "sprHand1_Below", -53),
        ("FOOT3_BELOW", "sprFoot3_Below", -52), ("FOOT2_BELOW", "sprFoot2_Below", -51), ("FOOT1_BELOW", "sprFoot1_Below", -50),
        ("BODY_BOTTOM", "sprBodyBottom_2", 0), ("BODY_TOP", "sprBodyTop_2", 10), ("HEAD", "sprHead", 30),
        ("FOOT3_ABOVE", "sprFoot3_Above", 37), ("FOOT2_ABOVE", "sprFoot2_Above", 38), ("FOOT1_ABOVE", "sprFoot1_Above", 39),
        ("HAND3_ABOVE", "sprHand3_Above", 48), ("HAND2_ABOVE", "sprHand2_Above", 49), ("HAND1_ABOVE", "sprHand1_Above", 50)
    ];
    public NativeCharacterBody(GmWorld world)
    {
        this.world = world;
        foreach (var item in Table)
        {
            var sprite = world.Sprites.Values.FirstOrDefault(s => s.GetProperty("name").GetString() == item.Sprite);
            if (sprite.ValueKind != JsonValueKind.Undefined && sprite.GetProperty("frames").GetArrayLength() > 0) entries.Add(new(item.Target, sprite, item.Layer));
        }
    }
    public IEnumerable<Entry> Visible(CharacterDefinition definition) => entries.Where(entry => !definition.Parts.Any(part => part.Visible && part.ReplaceOriginal && part.Target == entry.Target));
    public NativePixelRect Bounds(CharacterDefinition definition)
    {
        var rects = Visible(definition).Select(entry => entry.Bounds).Append(NativeCharacterLayout.Bounds(definition)).Where(rect => rect.Width > 0 && rect.Height > 0).ToArray();
        if (rects.Length == 0) return new(0, 0, 0, 0);
        int x = rects.Min(rect => rect.X), y = rects.Min(rect => rect.Y);
        return new(x, y, rects.Max(rect => rect.X + rect.Width) - x, rects.Max(rect => rect.Y + rect.Height) - y);
    }
    public ImageTexture? Texture(Entry entry, int selectedFrame, bool playing, double elapsed, double fps)
    {
        int index = NativeCharacterAnimation.BodyFrameIndex(entry.Count, selectedFrame, playing, elapsed, fps);
        string path = entry.Sprite.GetProperty("frames")[index].GetProperty("path").GetString()!;
        if (textures.TryGetValue(path, out var texture)) return texture;
        using var image = Image.LoadFromFile(Path.Combine(world.Root, path));
        if (image is null || image.IsEmpty()) return null;
        textures.Add(path, texture = ImageTexture.CreateFromImage(image));
        return texture;
    }
    public void Dispose() { foreach (var texture in textures.Values) texture.Dispose(); textures.Clear(); }
}
