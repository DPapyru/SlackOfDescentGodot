using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

public static class NativeCharacterLayout
{
    public static (int X, int Y) Place(NativeCharacterPart part) { var rest = NativeCharacterBones.RestOffset(part.Target); return (rest.X + part.OffsetX, rest.Y + part.OffsetY); }
    public static (int X, int Y) Corner(NativeCharacterPart part) { var place = Place(part); return (place.X - part.AnchorX, place.Y - part.AnchorY); }
    public static NativePixelRect Bounds(NativeCharacterPart part) { var corner = Corner(part); return new(corner.X, corner.Y, part.Width, part.Height); }
    public static List<NativeCharacterPart> Ordered(CharacterDefinition definition) => definition.Parts.Where(p => p.Visible && p.Frames.Count > 0).OrderBy(p => p.Layer).ThenBy(definition.Parts.IndexOf).ToList();
    public static NativePixelRect Bounds(CharacterDefinition definition)
    {
        var parts = Ordered(definition);
        if (parts.Count == 0) return new(0, 0, 0, 0);
        var rects = parts.Select(Bounds).ToArray();
        int x = rects.Min(r => r.X), y = rects.Min(r => r.Y);
        return new(x, y, rects.Max(r => r.X + r.Width) - x, rects.Max(r => r.Y + r.Height) - y);
    }
    public static string? Hit(CharacterDefinition definition, Func<NativeCharacterPart, byte[]> frame, int x, int y) => Hit(Ordered(definition), frame, x, y);
    public static string? Hit(List<NativeCharacterPart> parts, Func<NativeCharacterPart, byte[]> frame, int x, int y)
    {
        for (int index = parts.Count - 1; index >= 0; index--)
        {
            var part = parts[index]; var corner = Corner(part);
            int localX = x - corner.X, localY = y - corner.Y;
            if (localX < 0 || localY < 0 || localX >= part.Width || localY >= part.Height) continue;
            var rgba = frame(part); int offset = (localY * part.Width + localX) * 4;
            if (offset + 3 < rgba.Length && rgba[offset + 3] != 0) return part.Id;
        }
        return null;
    }
}

public static class NativeCharacterAnimation
{
    public static int FrameIndex(NativeCharacterPart part, int selectedFrame, bool playing, double elapsed, double fps)
    {
        if (part.Frames.Count == 0) return 0;
        if (!playing) return Math.Clamp(selectedFrame, 0, part.Frames.Count - 1);
        int total = part.Frames.Sum(f => Math.Max(1, f.Duration));
        int tick = Tick(elapsed, fps, total);
        for (int index = 0; index < part.Frames.Count; index++)
        {
            int duration = Math.Max(1, part.Frames[index].Duration);
            if (tick < duration) return index;
            tick -= duration;
        }
        return part.Frames.Count - 1;
    }
    public static int BodyFrameIndex(int count, int selectedFrame, bool playing, double elapsed, double fps) => count <= 0 ? 0 : playing ? Tick(elapsed, fps, count) : Math.Clamp(selectedFrame, 0, count - 1);
    private static int Tick(double elapsed, double fps, int count) => (int)(Math.Floor(Math.Max(0, elapsed) * fps) % Math.Max(1, count));
}

public sealed record NativeCharacterBaseSlot(string Target, int Layer, string Folder, string[] Files);

public sealed record NativeCharacterBasePlacement(NativeCharacterBaseSlot Slot, string Asset, string Part, string? Existing);

public static class NativeCharacterBase
{
    public static readonly string[] Presets = ["Nana", "Ryoko", "Yuki"];
    private static readonly NativeCharacterBaseSlot[] Table =
    [
        new("FOOT1_BELOW", -43, "Foot_Below", ["Socks0_below"]),
        new("FOOT2_BELOW", -42, "Foot_Below", ["Socks1_below"]),
        new("FOOT3_BELOW", -41, "Foot_Below", ["Socks2_below"]),
        new("FOOT3_BELOW", -40, "Foot_Below", ["Shoes_below"]),
        new("TAIL", -10, "Tail", ["Tail"]),
        new("BODY_BOTTOM", 0, "Body", ["Body_Bottom", "Body_Bottom_Pants"]),
        new("BODY_TOP", 10, "Body", ["Body_Top"]),
        new("HEAD", 30, "Head", ["Hair"]),
        new("HEAD", 31, "Head", ["Eye"]),
        new("HEAD", 32, "Head", ["Mouth"]),
        new("FOOT1_ABOVE", 38, "Foot_Above", ["Socks0_above"]),
        new("FOOT2_ABOVE", 39, "Foot_Above", ["Socks1_above"]),
        new("FOOT3_ABOVE", 40, "Foot_Above", ["Socks2_above"]),
        new("FOOT3_ABOVE", 41, "Foot_Above", ["Shoes_above"]),
        new("HAND2_ABOVE", 50, "Hand_Above", ["Hand2_above"]),
        new("HAND1_ABOVE", 51, "Hand_Above", ["Hand1_above"]),
    ];
    public static string Asset(string preset, NativeCharacterBaseSlot slot, string file) => "Chara/Doll/" + slot.Folder + "/Character-" + preset + "_" + file + ".gif";
    public static IReadOnlyList<(NativeCharacterBaseSlot Slot, string Asset, string Part)> Resolve(string preset, IReadOnlyCollection<string> available)
    {
        var result = new List<(NativeCharacterBaseSlot, string, string)>();
        foreach (var slot in Table)
            foreach (string file in slot.Files)
            {
                string asset = Asset(preset, slot, file);
                if (!available.Contains(asset)) continue;
                result.Add((slot, asset, file)); break;
            }
        return result;
    }
    public static IReadOnlyList<NativeCharacterBasePlacement> Plan(CharacterDefinition definition, string preset, IReadOnlyCollection<string> available) =>
        Resolve(preset, available).Select(item => new NativeCharacterBasePlacement(item.Slot, item.Asset, item.Part, definition.Parts.FirstOrDefault(p => p.Name == item.Part)?.Id)).ToList();
}
